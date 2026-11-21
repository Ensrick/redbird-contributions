using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Assembly;
using RedBird.X64.Assembly.InstructionWalker;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using RedBird.X64.Memory.Scanners;
using RedBird.X64.Tests.Infrastructure;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Tests;

/// <summary>
/// Tests for the transaction system: the four commit phases, per-hook failure reporting, rollback, and the inline hooks that only reach the target through it.
/// </summary>
public sealed class HookTransactionTests
{
    private static IHookBackend Backend() => BackendCatalog.Resolve(BackendCatalog.NativeX64);

    private static HookTransaction Transaction(ExecutableFunction function, HookTransactionOptions? options = null)
    {
        HookTransactionOptions testOptions = (options ?? new HookTransactionOptions
        {
            Backend = Backend(),
        }) with
        {
            OwnsHooks = true,
        };

        return new HookTransaction(
            ScanRegion.FromPointer((IntPtr)function.Address, function.CodeLength),
            loggerFactory: null,
            testOptions);
    }

    // -------------------------------------------------------------------------
    // Commit
    // -------------------------------------------------------------------------

    [Fact]
    public void Commit_ResolvesInstallsAndPublishes()
    {
        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        using HookTransaction tx = Transaction(target);

        DetourHandle<TestDelegates.BinaryInt> handle = new();
        tx.AddDetour(handle, HookTarget.FromAddress(target.Address), (a, b) => 42);

        // Registration alone must not touch the target.
        Assert.False(handle.Success);
        Assert.Equal(7, call(3, 4));

        CommitResult result = tx.Commit();

        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(1, result.RegisteredCount);
        Assert.Single(result.Installed);
        Assert.Empty(result.Failures);

        Assert.True(handle.IsInstalled);
        Assert.Equal(42, call(3, 4));
    }

    [Fact]
    public void Commit_ResolvesAPatternTarget()
    {
        using ExecutableFunction target = TestFunctions.Constant(0x1234);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        using HookTransaction tx = Transaction(target);

        DetourHandle<TestDelegates.NullaryInt> handle = new();
        tx.AddDetour(handle, "B8 34 12 00 00", () => 99);

        tx.Commit();

        Assert.Equal(target.Address, handle.ResolvedAddress);
        Assert.Equal(99, call());
    }

    [Fact]
    public void Commit_ResolvesTheCalleeOfARelativeCallPattern()
    {
        using ExecutableFunction caller = ExecutableFunction.Create(assembler =>
        {
            Label callee = assembler.CreateLabel();

            assembler.nop();
            assembler.sub(rsp, 0x28);
            assembler.call(callee);
            assembler.add(rsp, 0x28);
            assembler.ret();

            assembler.Label(ref callee);
            assembler.mov(eax, 7);
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        TestDelegates.NullaryInt call = caller.GetDelegate<TestDelegates.NullaryInt>();
        Assert.Equal(7, call());

        using HookTransaction tx = Transaction(caller);
        DetourHandle<TestDelegates.NullaryInt> handle = new();
        HookTarget target = HookTarget.FromRelativeCall(
            "E8 ?? ?? ?? ?? 48 83 C4 28 C3 B8 07 00 00 00");

        tx.AddDetour(handle, target, () => 99);
        CommitResult result = tx.Commit();

        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(HookTargetKind.RelativeCall, target.Kind);
        Assert.True(target.RequiresScan);
        Assert.Equal(caller.Address + 15, handle.ResolvedAddress);
        Assert.Equal((byte)0xE8, Marshal.ReadByte((IntPtr)(caller.Address + 5)));
        Assert.Equal(99, call());
        Assert.Equal(7, handle.Original());
    }

    [Fact]
    public void Commit_RejectsARelativeCallTargetWhenTheMatchedInstructionIsNotACall()
    {
        using ExecutableFunction target = TestFunctions.Constant(0x1234);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();
        using HookTransaction tx = Transaction(target);

        DetourHandle<TestDelegates.NullaryInt> handle = new();
        tx.AddDetour(
            handle,
            HookTarget.FromRelativeCall("B8 34 12 00 00"),
            () => 99,
            "notACall");

        CommitResult result = tx.Commit();

        HookFailure failure = Assert.Single(result.Failures);
        Assert.Equal(HookFailurePhase.Resolve, failure.Phase);
        Assert.Contains("Expected a direct relative CALL", failure.Message, StringComparison.Ordinal);
        Assert.False(handle.Success);
        Assert.Equal(0x1234, call());
    }

    [Fact]
    public void Commit_ReportsAMissingPatternWithoutAffectingTheRest()
    {
        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        using HookTransaction tx = Transaction(target);

        DetourHandle<TestDelegates.BinaryInt> good = new();
        DetourHandle<TestDelegates.BinaryInt> missing = new();

        tx.AddDetour(good, HookTarget.FromAddress(target.Address), (a, b) => 42, "goodHook");
        tx.AddDetour(missing, "DE AD BE EF CA FE BA BE", (a, b) => 0, "missingHook");

        CommitResult result = tx.Commit();

        Assert.False(result.IsCompleteSuccess);
        Assert.Single(result.Installed);
        Assert.Single(result.Failures);

        HookFailure failure = result.Failures[0];
        Assert.Equal("missingHook", failure.Name);
        Assert.Equal(HookFailurePhase.Resolve, failure.Phase);
        Assert.Contains("DE AD BE EF", failure.Message, StringComparison.Ordinal);

        Assert.True(good.IsInstalled);
        Assert.False(missing.Success);
        Assert.Equal(42, call(3, 4));
    }

    [Fact]
    public void Handle_RequireThrowsWithContextWhenTheHookIsMissing()
    {
        using ExecutableFunction target = TestFunctions.Add();
        using HookTransaction tx = Transaction(target);

        DetourHandle<TestDelegates.BinaryInt> handle = new();
        tx.AddDetour(handle, "DE AD BE EF CA FE BA BE", (a, b) => 0, "absentHook");
        tx.Commit();

        Assert.NotNull(handle.Failure);
        Assert.Throws<InvalidOperationException>(() => handle.Require());
        Assert.Throws<InvalidOperationException>(() => handle.Original);
        Assert.False(handle.TryGetOriginal(out _));
    }

    [Fact]
    public void RollbackAndThrow_LeavesNothingInstalled()
    {
        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        using HookTransaction tx = Transaction(target, new HookTransactionOptions
        {
            Backend = Backend(),
            FailureMode = TransactionFailureMode.RollbackAndThrow,
        });

        DetourHandle<TestDelegates.BinaryInt> good = new();
        DetourHandle<TestDelegates.BinaryInt> missing = new();

        tx.AddDetour(good, HookTarget.FromAddress(target.Address), (a, b) => 42, "goodHook");
        tx.AddDetour(missing, "DE AD BE EF CA FE BA BE", (a, b) => 0, "missingHook");

        Assert.Throws<HookTransactionException>(() => tx.Commit());

        // Rollback has to undo the hooks that did install, not just stop.
        Assert.Equal(7, call(3, 4));
        Assert.True(target.MatchesOriginal(target.CodeLength));
        Assert.Equal(TransactionState.RolledBack, tx.State);
    }

    [Fact]
    public void InstallOnCommitFalse_PreparesWithoutPatching()
    {
        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        using HookTransaction tx = Transaction(target, new HookTransactionOptions
        {
            Backend = Backend(),
            InstallOnCommit = false,
        });

        DetourHandle<TestDelegates.BinaryInt> handle = new();
        tx.AddDetour(handle, HookTarget.FromAddress(target.Address), (a, b) => 42);

        tx.Commit();

        Assert.True(handle.Success);
        Assert.False(handle.IsInstalled);
        Assert.Equal(7, call(3, 4));

        tx.EnableAll();
        Assert.Equal(42, call(3, 4));

        tx.DisableAll();
        Assert.Equal(7, call(3, 4));
    }

    [Fact]
    public void Commit_ClosesRegistrationAndCannotRepeat()
    {
        using ExecutableFunction target = TestFunctions.Add();
        using HookTransaction tx = Transaction(target);

        tx.AddDetour(new DetourHandle<TestDelegates.BinaryInt>(), HookTarget.FromAddress(target.Address), (a, b) => 0);
        tx.Commit();

        Assert.Equal(TransactionState.Committed, tx.State);
        Assert.Throws<InvalidOperationException>(() => tx.Commit());
        Assert.Throws<InvalidOperationException>(() =>
            tx.AddDetour(new DetourHandle<TestDelegates.BinaryInt>(), HookTarget.FromAddress(target.Address), (a, b) => 0));
    }

    [Fact]
    public void Register_RejectsAMalformedPatternAtTheCallSite()
    {
        using ExecutableFunction target = TestFunctions.Add();
        using HookTransaction tx = Transaction(target);

        // Parsed at registration so the stack trace points at the offending line.
        Assert.Throws<ArgumentException>(() =>
            tx.AddDetour(new DetourHandle<TestDelegates.BinaryInt>(), "48 ZZ 5C", (a, b) => 0));
    }

    [Fact]
    public void Register_NullDetourHandleExplainsThatTargetResolutionHasNotRun()
    {
        using ExecutableFunction target = TestFunctions.Add();
        using HookTransaction tx = Transaction(target);

        DetourHandle<TestDelegates.BinaryInt> missingHandle = null!;
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            tx.AddDetour(
                missingHandle,
                HookTarget.FromExport("AnExportWhichMustNotBeResolved", (IntPtr)target.Address),
                (a, b) => 0));

        Assert.Equal("handle", exception.ParamName);
        Assert.Contains("Initialize the field", exception.Message, StringComparison.Ordinal);
        Assert.Contains("has not been looked up yet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_CommitsSeveralTransactionsInOneSweep()
    {
        using ExecutableFunction first = TestFunctions.Constant(1);
        using ExecutableFunction second = TestFunctions.Constant(2);

        TestDelegates.NullaryInt callFirst = first.GetDelegate<TestDelegates.NullaryInt>();
        TestDelegates.NullaryInt callSecond = second.GetDelegate<TestDelegates.NullaryInt>();

        // One region spanning both fixtures, the way a real consumer spans one module.
        UInt64 low = Math.Min(first.Address, second.Address);
        UInt64 high = Math.Max(first.Address, second.Address);
        int span = (int)(high - low) + 4096;

        using ScanRegion region = ScanRegion.FromPointer((IntPtr)low, span);
        using HookSession session = new(region, loggerFactory: null, new HookTransactionOptions { Backend = Backend() });

        HookTransaction txA = session.BeginTransaction("first");
        HookTransaction txB = session.BeginTransaction("second");

        DetourHandle<TestDelegates.NullaryInt> handleA = new();
        DetourHandle<TestDelegates.NullaryInt> handleB = new();

        txA.AddDetour(handleA, HookTarget.FromAddress(first.Address), () => 10);
        txB.AddDetour(handleB, HookTarget.FromAddress(second.Address), () => 20);

        CommitResult result = session.Commit();

        Assert.Equal(2, result.RegisteredCount);
        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(10, callFirst());
        Assert.Equal(20, callSecond());
    }

    // -------------------------------------------------------------------------
    // Inline hooks
    // -------------------------------------------------------------------------

    [Fact]
    public void AddInlineFactory_ReceivesTheResolvedOwnerBeforeGenerationAndPublishesOnlyAfterPreparation()
    {
        using ExecutableFunction target = TestFunctions.Constant(0x1234);
        byte[] original = target.ReadCurrentBytes(target.CodeLength);
        using HookTransaction tx = Transaction(target, new HookTransactionOptions
        {
            Backend = Backend(),
            InstallOnCommit = false,
        });
        HookHandle<X64InlineHook> handle = new();
        X64InlineHook? factoryOwner = null;
        WeakReference<TestDelegates.NullaryInt>? callbackReference = null;
        int factoryCalls = 0, generatorCalls = 0;
        int commitThread = Environment.CurrentManagedThreadId;

        tx.AddInlineFactory(handle, HookTarget.FromPattern("B8 34 12 00 00"), owner =>
        {
            factoryCalls++;
            factoryOwner = owner;
            Assert.Equal(commitThread, Environment.CurrentManagedThreadId);
            Assert.Equal(target.Address, owner.TargetAddress);
            Assert.Equal(HookState.Created, owner.State);
            Assert.Equal(IntPtr.Zero, owner.StubAddress);
            Assert.False(handle.Success);
            Assert.Null(handle.Hook);
            Assert.Equal<byte>(original, target.ReadCurrentBytes(target.CodeLength));
            IntPtr callback = PinFactoryCallback(owner, out callbackReference);
            Assert.NotEqual(IntPtr.Zero, callback);
            return (assembler, overwritten, returnAddress) =>
            {
                generatorCalls++;
                Assert.Equal(commitThread, Environment.CurrentManagedThreadId);
                Assert.Same(factoryOwner, owner);
                Assert.Equal(HookState.Created, owner.State);
                Assert.Equal(IntPtr.Zero, owner.StubAddress);
                Assert.False(handle.Success);
                Assert.Equal(target.Address + (ulong)owner.DisplacedByteCount, returnAddress);
                Assert.False(overwritten.IsEmpty);
                Assert.Equal(target.Address, overwritten[0].IP);
                Assert.Equal<byte>(original, target.ReadCurrentBytes(target.CodeLength));
                // Use the pinned pointer as generated data, without invoking the
                // callback or target. The library remains the sole Generate caller.
                assembler.mov(r11, unchecked((ulong)callback.ToInt64()));
                foreach (Instruction instruction in overwritten)
                    assembler.AddInstruction(instruction);
            };
        });

        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, generatorCalls);
        Assert.False(handle.Success);
        Assert.Equal("handle", handle.Name);
        CommitResult result = tx.Commit();
        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, generatorCalls);
        X64InlineHook hook = handle.Require();
        Assert.Same(factoryOwner, hook);
        Assert.Same(hook, Assert.Single(result.Installed));
        Assert.Same(hook, Assert.Single(tx.Hooks));
        Assert.Equal(target.Address, handle.ResolvedAddress);
        Assert.Equal(HookState.Prepared, hook.State);
        Assert.False(handle.IsInstalled);
        Assert.NotEqual(IntPtr.Zero, hook.StubAddress);
        Assert.Equal<byte>(original, target.ReadCurrentBytes(target.CodeLength));

        WeakReference<TestDelegates.NullaryInt> reference = Assert.IsType<WeakReference<TestDelegates.NullaryInt>>(callbackReference);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.True(reference.TryGetTarget(out _), "The actual hook must root its factory-pinned delegate.");

        tx.EnableAll();
        Assert.Equal(HookState.Installed, hook.State);
        Assert.False(target.MatchesOriginal(target.CodeLength));
        tx.DisableAll();
        Assert.Equal<byte>(original, target.ReadCurrentBytes(target.CodeLength));
        tx.Dispose();
        Assert.Equal(HookState.Disposed, hook.State);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, generatorCalls);
    }

    [Fact]
    public void AddInlineFactory_RejectsNullInputsDuringRegistration()
    {
        using ExecutableFunction target = TestFunctions.Constant(7);
        using HookTransaction tx = Transaction(target);
        HookHandle<X64InlineHook> handle = new();
        HookTarget site = HookTarget.FromAddress(target.Address);

        ArgumentNullException factoryFailure = Assert.Throws<ArgumentNullException>(() =>
            tx.AddInlineFactory(handle, site, null!));
        Assert.Equal("generatorFactory", factoryFailure.ParamName);
        ArgumentNullException handleFailure = Assert.Throws<ArgumentNullException>(() =>
            tx.AddInlineFactory(null!, site, _ => static (assembler, _, _) => assembler.nop()));
        Assert.Equal("handle", handleFailure.ParamName);
        Assert.False(handle.Success);
        Assert.Null(handle.Failure);
        Assert.Equal(TransactionState.Building, tx.State);
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Theory]
    [InlineData("factory-throws")]
    [InlineData("factory-returns-null")]
    [InlineData("generator-throws")]
    public void AddInlineFactory_ContinueOnErrorDisposesTheFailedOwnerAndPreparesTheNextSite(string failureKind)
    {
        using ExecutableFunction target = CreateFactorySites();
        byte[] original = target.ReadCurrentBytes(target.CodeLength);
        using HookTransaction tx = Transaction(target, new HookTransactionOptions
        {
            Backend = Backend(),
            InstallOnCommit = false,
            FailureMode = TransactionFailureMode.ContinueOnError,
        });
        HookHandle<X64InlineHook> failed = new(), good = new();
        X64InlineHook? failedOwner = null;
        InvalidOperationException expected = new("Controlled inline factory preparation failure.");
        int factoryCalls = 0, generatorCalls = 0, goodGeneratorCalls = 0;
        tx.AddInlineFactory(failed, HookTarget.FromAddress(target.Address), owner =>
        {
            factoryCalls++;
            failedOwner = owner;
            Assert.Equal(HookState.Created, owner.State);
            Assert.False(failed.Success);
            if (failureKind == "factory-throws") throw expected;
            if (failureKind == "factory-returns-null") return null!;
            return (_, _, _) => { generatorCalls++; throw expected; };
        }, name: "failed-factory");
        // The established generator overload continues to work beside the new API.
        tx.AddInline(good, HookTarget.FromAddress(target.Address + 32), (assembler, _, _) =>
        { goodGeneratorCalls++; assembler.nop(); }, name: "later-site");

        CommitResult result = tx.Commit();
        Assert.False(result.IsCompleteSuccess);
        Assert.Equal(2, result.RegisteredCount);
        HookFailure failure = Assert.Single(result.Failures);
        Assert.Equal(HookFailurePhase.Prepare, failure.Phase);
        Assert.Equal("failed-factory", failure.Name);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(failureKind == "generator-throws" ? 1 : 0, generatorCalls);
        Assert.Equal(1, goodGeneratorCalls);
        X64InlineHook discarded = Assert.IsType<X64InlineHook>(failedOwner);
        Assert.Equal(HookState.Disposed, discarded.State);
        Assert.Equal(IntPtr.Zero, discarded.StubAddress);
        Assert.Null(failed.Hook);
        Assert.False(failed.Success);
        Assert.NotNull(failed.Failure);
        if (failureKind != "factory-returns-null") Assert.Same(expected, failure.Exception);
        else Assert.IsType<InvalidOperationException>(failure.Exception);
        Assert.Same(good.Require(), Assert.Single(result.Installed));
        Assert.Equal(HookState.Prepared, good.Require().State);
        Assert.False(good.IsInstalled);
        Assert.Equal<byte>(original, target.ReadCurrentBytes(target.CodeLength));
    }

    [Theory]
    [InlineData("factory-throws")]
    [InlineData("factory-returns-null")]
    [InlineData("generator-throws")]
    public void AddInlineFactory_RollbackDisposesEarlierPreparationAndNeverPublishesEitherHandle(string failureKind)
    {
        using ExecutableFunction target = CreateFactorySites();
        byte[] original = target.ReadCurrentBytes(target.CodeLength);
        using HookTransaction tx = Transaction(target, new HookTransactionOptions
        {
            Backend = Backend(),
            FailureMode = TransactionFailureMode.RollbackAndThrow,
        });
        HookHandle<X64InlineHook> good = new(), failed = new();
        X64InlineHook? preparedOwner = null, failedOwner = null;
        InvalidOperationException expected = new("Controlled inline factory rollback failure.");
        int goodFactoryCalls = 0, goodGeneratorCalls = 0, failedFactoryCalls = 0, failedGeneratorCalls = 0;
        tx.AddInlineFactory(good, HookTarget.FromAddress(target.Address), owner =>
        {
            goodFactoryCalls++;
            preparedOwner = owner;
            return (assembler, _, _) => { goodGeneratorCalls++; assembler.nop(); };
        }, name: "prepared-site");
        tx.AddInlineFactory(failed, HookTarget.FromAddress(target.Address + 32), owner =>
        {
            failedFactoryCalls++;
            failedOwner = owner;
            Assert.Equal(HookState.Prepared, preparedOwner!.State);
            Assert.False(good.Success);
            Assert.False(failed.Success);
            Assert.Equal<byte>(original, target.ReadCurrentBytes(target.CodeLength));
            if (failureKind == "factory-throws") throw expected;
            if (failureKind == "factory-returns-null") return null!;
            return (_, _, _) => { failedGeneratorCalls++; throw expected; };
        }, name: "failed-factory");

        HookTransactionException failure = Assert.Throws<HookTransactionException>(() => tx.Commit());
        Assert.Equal(TransactionState.RolledBack, tx.State);
        Assert.Equal(1, goodFactoryCalls);
        Assert.Equal(1, goodGeneratorCalls);
        Assert.Equal(1, failedFactoryCalls);
        Assert.Equal(failureKind == "generator-throws" ? 1 : 0, failedGeneratorCalls);
        Assert.Equal(HookState.Disposed, Assert.IsType<X64InlineHook>(preparedOwner).State);
        Assert.Equal(HookState.Disposed, Assert.IsType<X64InlineHook>(failedOwner).State);
        Assert.False(good.Success);
        Assert.False(failed.Success);
        Assert.Null(good.Hook);
        Assert.Null(failed.Hook);
        Assert.NotNull(failed.Failure);
        Assert.Empty(tx.Hooks);
        if (failureKind != "factory-returns-null") Assert.Same(expected, failure.InnerException);
        else Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal<byte>(original, target.ReadCurrentBytes(target.CodeLength));
    }

    private static ExecutableFunction CreateFactorySites() => ExecutableFunction.Create(assembler =>
    {
        // Two disjoint 14-byte sites in one owned code page; neither is executed.
        for (int index = 0; index < 64; index++) assembler.nop();
        assembler.ret();
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IntPtr PinFactoryCallback(X64InlineHook owner, out WeakReference<TestDelegates.NullaryInt> reference)
    {
        TestDelegates.NullaryInt callback = new FactoryCallbackTarget().Invoke;
        reference = new(callback);
        return owner.PinDelegate(callback);
    }

    private sealed class FactoryCallbackTarget
    {
        internal int Invoke() => 37;
    }

    [Fact]
    public void AddInline_RunsGeneratedCodeAndReturnsToTheFunction()
    {
        // The inline hook fires mid-function and adds 100 to eax, then control resumes normally.
        using ExecutableFunction target = ExecutableFunction.Create((asm, _) =>
        {
            asm.mov(eax, 1);
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.add(eax, 2);
            asm.ret();
        });

        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();
        Assert.Equal(3, call());

        using HookTransaction tx = Transaction(target);

        HookHandle<X64InlineHook> handle = new();

        // Hook at offset 5, just past the mov: 16 nops give the 14 bytes a jump needs.
        tx.AddInline(handle, HookTarget.FromAddress(target.Address + 5), static (asm, overwritten, returnAddress) =>
        {
            asm.add(eax, 100);
        });

        CommitResult result = tx.Commit();

        Assert.True(result.IsCompleteSuccess);
        Assert.True(handle.IsInstalled);
        Assert.Equal(103, call());

        handle.Require().Disable();
        Assert.Equal(3, call());
    }

    [Fact]
    public void AddInline_CallsManagedCodeThroughFastcall()
    {
        Assert.SkipUnless(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "X64FastcallSafe emits the Microsoft x64 ABI; Linux managed callbacks use System V AMD64.");

        // The shape the consumer paste uses: an inline hook whose stub calls back into managed code with register values pulled out of the interrupted function.
        using ExecutableFunction target = ExecutableFunction.Create((asm, _) =>
        {
            asm.mov(ecx, 0x1234);
            asm.mov(edx, 0x5678);
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.mov(eax, 0);
            asm.ret();
        });

        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        using HookTransaction tx = Transaction(target);
        HookHandle<X64InlineHook> handle = new();

        tx.AddInline(handle, HookTarget.FromAddress(target.Address + 10), static (asm, overwritten, returnAddress) =>
        {
            asm.X64FastcallSafe(
                (UInt64)Marshal.GetFunctionPointerForDelegate(ObservedArguments),
                totalArgumentCount: 2,
                preserveRAX: false,
                prepareArgumentsAction: static a =>
                {
                    a.mov(rcx, rcx);
                    a.mov(rdx, rdx);
                });
        });

        tx.Commit();
        Assert.True(handle.IsInstalled);

        _firstArgument = 0;
        _secondArgument = 0;

        call();

        // If the ABI, the stack alignment or the shadow space were wrong, these would be garbage or the process would already have died.
        Assert.Equal(0x1234u, _firstArgument);
        Assert.Equal(0x5678u, _secondArgument);
    }

    /// <summary>
    /// Rooted in a static field on purpose.
    /// </summary>
    /// <remarks>
    /// Nothing else holds a managed reference once the generator lambda returns, and the stub calls through the raw pointer for the lifetime of the hook. 
    /// Passing an inline lambda straight to <see cref="Marshal.GetFunctionPointerForDelegate{TDelegate}"/> happens to survive because the compiler caches static lambdas, but that is an implementation detail rather than a guarantee.
    /// </remarks>
    private static readonly TwoArgumentCallback ObservedArguments = static (first, second) =>
    {
        _firstArgument = first;
        _secondArgument = second;
        return 0;
    };

    private static UInt64 _firstArgument;
    private static UInt64 _secondArgument;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UInt64 TwoArgumentCallback(UInt64 first, UInt64 second);

    [Fact]
    public void AddContextHook_WithoutCallerOwnedHandle_ReturnsAFreshHandle()
    {
        using ExecutableFunction target = TestFunctions.Add();
        using HookTransaction tx = Transaction(target);

        ContextHookDelegate callback = static _ => { };
        HookHandle<X64InlineHook> handle = tx.AddContextHook(
            HookTarget.FromAddress(target.Address),
            callback,
            name: "transient-context-hook");

        Assert.Equal("transient-context-hook", handle.Name);
        Assert.False(handle.Success);
    }

    // -------------------------------------------------------------------------
    // The full consumer pattern
    // -------------------------------------------------------------------------

    [Fact]
    public void ScanWalkAndInlineHook_WorksEndToEnd()
    {
        // Scan for a call site, read its target, walk into the callee for an anchor instruction, and hook there, the manoeuvre used when a function has no stable pattern of its own.
        using ExecutableFunction callee = ExecutableFunction.Create((asm, _) =>
        {
            asm.mov(eax, 5);
            asm.movzx(ecx, __word_ptr[rsp + 0x20]);    // the anchor
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.nop(); asm.nop(); asm.nop(); asm.nop();
            asm.ret();
        });

        UInt64 calleeAddress = callee.Address;

        using ExecutableFunction caller = ExecutableFunction.Create((asm, _) =>
        {
            asm.push(rbx);
            asm.AddInstruction(Instruction.CreateBranch(Code.Call_rel32_64, calleeAddress));
            asm.pop(rbx);
            asm.ret();
        });

        TestDelegates.NullaryInt call = caller.GetDelegate<TestDelegates.NullaryInt>();
        Assert.Equal(5, call());

        // 1. Locate the call and read where it goes.
        using ScanRegion callerRegion = ScanRegion.FromPointer((IntPtr)caller.Address, caller.CodeLength);
        DataScanner scanner = DataScanner.Create(callerRegion).Scan("53 E8", offset: 1);

        Assert.True(scanner.TryReadFlowControlTarget(out UInt64 resolved));
        Assert.Equal(calleeAddress, resolved);

        // 2. Walk the callee for the anchor.
        InstructionWalker walker = new(resolved, new InstructionWalkerConfig
        {
            WalkRules =
            [
                new InstructionWalkRule
                {
                    Predicate = InstructionWalkerHelpers.IsMnemonic(Mnemonic.Movzx),
                    Extractor = InstructionWalkerHelpers.ExtractInstructionAddress,
                },
            ],
        });

        Assert.True(walker.TryWalk(out UInt64 anchor));
        Assert.Equal(resolved + 5, anchor);

        // 3. Hook the anchor.
        using ScanRegion calleeRegion = ScanRegion.FromPointer((IntPtr)callee.Address, callee.CodeLength);
        using HookTransaction tx = new(calleeRegion, null, new HookTransactionOptions { Backend = Backend() });

        HookHandle<X64InlineHook> handle = new();
        tx.AddInline(handle, HookTarget.FromAddress(anchor), static (asm, overwritten, returnAddress) =>
        {
            asm.add(eax, 60);
        });

        CommitResult result = tx.Commit();

        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(65, call());

        handle.Require().Disable();
        Assert.Equal(5, call());
    }
}
