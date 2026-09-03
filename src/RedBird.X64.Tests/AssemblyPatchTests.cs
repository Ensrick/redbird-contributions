using System;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using RedBird.X64.Tests.Infrastructure;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Tests;

/// <summary>
/// Tests for in-place assembly patching: the <see cref="X64AssemblyPatch"/> primitive that overwrites the target's instructions (rather than detouring around them), its instruction-boundary
/// padding, its optional max size constraint, and its integration into the transaction system.
/// </summary>
public sealed class AssemblyPatchTests
{
    private static IHookBackend Backend() => BackendCatalog.Resolve(BackendCatalog.NativeX64);

    private static HookTransaction Transaction(ExecutableFunction function, HookTransactionOptions? options = null)
        => new(
            ScanRegion.FromPointer((IntPtr)function.Address, function.CodeLength),
            loggerFactory: null,
            options ?? new HookTransactionOptions { Backend = Backend() });

    // =========================================================================
    // Standalone primitive
    // =========================================================================

    [Fact]
    public void Generate_AssemblesAndSnapshotsWithoutTouchingTheTarget()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        X64AssemblyPatch patch = new(target.Address);
        patch.Generate((asm, _) => asm.mov(eax, 42));

        // Prepared means everything is assembled and the original bytes are snapshotted, but the target is still untouched: the same guarantee the transaction relies on.
        Assert.Equal(HookState.Prepared, patch.State);
        Assert.False(patch.IsInstalled);
        Assert.Equal(5, call());
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Fact]
    public void Enable_OverwritesTheTargetInPlace()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();
        Assert.Equal(5, call());

        X64AssemblyPatch patch = new(target.Address);
        patch.Generate((asm, _) => asm.mov(eax, 42));   // B8 2A 00 00 00, same 5 bytes as the original mov
        patch.Enable();

        Assert.True(patch.IsInstalled);
        Assert.Equal(5, patch.BodyByteCount);
        Assert.Equal(5, patch.OverwrittenByteCount);   // exact instruction boundary, no padding
        Assert.Equal(42, call());
    }

    [Fact]
    public void Enable_PadsToTheInstructionBoundaryWhenTheBodyIsShorter()
    {
        // xor eax, eax is 2 bytes; it lands on the 5-byte "mov eax, 5", so the overwrite has to round up to 5 and NOP the trailing 3 bytes rather than leave a truncated instruction.
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        X64AssemblyPatch patch = new(target.Address);
        patch.Generate((asm, _) => asm.xor(eax, eax));
        patch.Enable();

        Assert.Equal(2, patch.BodyByteCount);
        Assert.Equal(5, patch.OverwrittenByteCount);
        Assert.Equal(0, call());   // xor eax, eax; nop; nop; nop; ... => 0
    }

    [Fact]
    public void Disable_RestoresTheExactOriginalBytes()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        X64AssemblyPatch patch = new(target.Address);
        patch.Generate((asm, _) => asm.mov(eax, 42));
        patch.Enable();
        Assert.Equal(42, call());

        patch.Disable();

        Assert.Equal(HookState.Disabled, patch.State);
        Assert.Equal(5, call());
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Fact]
    public void Dispose_RestoresTheTargetWhenStillInstalled()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        X64AssemblyPatch patch = new(target.Address);
        patch.Generate((asm, _) => asm.mov(eax, 42));
        patch.Enable();

        patch.Dispose();

        Assert.Equal(HookState.Disposed, patch.State);
        Assert.Equal(5, call());
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    // -------------------------------------------------------------------------
    // Max size constraint
    // -------------------------------------------------------------------------

    [Fact]
    public void MaxByteCount_LetsAPatchThatFitsThrough()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        // The body needs 5 bytes; a limit of 8 accommodates it.
        X64AssemblyPatch patch = new(target.Address, maxByteCount: 8);
        patch.Generate((asm, _) => asm.mov(eax, 42));
        patch.Enable();

        Assert.Equal(42, call());
    }

    [Fact]
    public void MaxByteCount_RejectsAnOverLongBodyBeforeTouchingTheTarget()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        // mov eax, 42 is 5 bytes but the constraint only allows 4.
        X64AssemblyPatch patch = new(target.Address, maxByteCount: 4);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => patch.Generate((asm, _) => asm.mov(eax, 42)));

        Assert.Contains("max size constraint is 4", ex.Message, StringComparison.Ordinal);
        Assert.Equal(HookState.Failed, patch.State);

        // The failure happened while preparing, so the target is exactly as it was.
        Assert.Equal(5, call());
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Fact]
    public void MaxByteCount_RejectsWhenBoundaryRoundingWouldExceedIt()
    {
        // The body is only 2 bytes and would fit under a limit of 3, but it lands on a 5-byte instruction, so honouring the limit would mean stopping mid-instruction.
        // The patch refuses as expected.
        using ExecutableFunction target = TestFunctions.Constant(5);

        X64AssemblyPatch patch = new(target.Address, maxByteCount: 3);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => patch.Generate((asm, _) => asm.xor(eax, eax)));

        Assert.Contains("instruction boundary", ex.Message, StringComparison.Ordinal);
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Fact]
    public void MaxByteCount_ZeroMeansNoLimit()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        X64AssemblyPatch patch = new(target.Address, maxByteCount: 0);
        patch.Generate((asm, _) => asm.mov(eax, 42));
        patch.Enable();

        Assert.Equal(42, call());
    }

    [Fact]
    public void Constructor_RejectsANegativeMaxByteCount()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new X64AssemblyPatch(0x1000, maxByteCount: -1));

    [Fact]
    public void Generate_RejectsAnEmptyBody()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);

        X64AssemblyPatch patch = new(target.Address);

        Assert.Throws<InvalidOperationException>(() => patch.Generate((asm, _) => { }));
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Fact]
    public void NullTargetAddress_IsInert()
    {
        X64AssemblyPatch patch = new(0);

        Assert.Equal(HookState.Failed, patch.State);

        // Generate and Enable are no-ops rather than throwing, matching the inline hook's null path.
        patch.Generate((asm, _) => asm.mov(eax, 42));
        patch.Enable();

        Assert.Equal(HookState.Failed, patch.State);
        Assert.False(patch.IsInstalled);
    }

    // =========================================================================
    // Transaction integration
    // =========================================================================

    [Fact]
    public void AddAssemblyPatch_ResolvesInstallsAndPublishes()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        using HookTransaction tx = Transaction(target);

        HookHandle<X64AssemblyPatch> handle = new();
        tx.AddAssemblyPatch(handle, HookTarget.FromAddress(target.Address), (asm, _) => asm.mov(eax, 42));

        // Registration alone must not touch the target.
        Assert.False(handle.Success);
        Assert.Equal(5, call());

        CommitResult result = tx.Commit();

        Assert.True(result.IsCompleteSuccess);
        Assert.Single(result.Installed);
        Assert.True(handle.IsInstalled);
        Assert.Equal(42, call());
    }

    [Fact]
    public void AddAssemblyPatch_ResolvesAPatternTarget()
    {
        using ExecutableFunction target = TestFunctions.Constant(0x1234);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        using HookTransaction tx = Transaction(target);

        HookHandle<X64AssemblyPatch> handle = new();
        tx.AddAssemblyPatch(handle, "B8 34 12 00 00", (asm, _) => asm.mov(eax, 99));

        tx.Commit();

        Assert.Equal(target.Address, handle.ResolvedAddress);
        Assert.Equal(99, call());
    }

    [Fact]
    public void AddAssemblyPatch_SurfacesAMaxSizeViolationAsAPrepareFailureWithoutAffectingTheRest()
    {
        using ExecutableFunction good = TestFunctions.Constant(1);
        using ExecutableFunction bad = TestFunctions.Constant(2);

        TestDelegates.NullaryInt callGood = good.GetDelegate<TestDelegates.NullaryInt>();
        TestDelegates.NullaryInt callBad = bad.GetDelegate<TestDelegates.NullaryInt>();

        // One region spanning both fixtures.
        UInt64 low = Math.Min(good.Address, bad.Address);
        UInt64 high = Math.Max(good.Address, bad.Address);
        int span = (int)(high - low) + 4096;

        using ScanRegion region = ScanRegion.FromPointer((IntPtr)low, span);
        using HookTransaction tx = new(region, loggerFactory: null, new HookTransactionOptions { Backend = Backend() });

        HookHandle<X64AssemblyPatch> okHandle = new();
        HookHandle<X64AssemblyPatch> tooBig = new();

        tx.AddAssemblyPatch(okHandle, HookTarget.FromAddress(good.Address), (asm, _) => asm.mov(eax, 7), name: "okPatch");
        tx.AddAssemblyPatch(tooBig, HookTarget.FromAddress(bad.Address), (asm, _) => asm.mov(eax, 7), maxByteCount: 2, name: "tooBigPatch");

        CommitResult result = tx.Commit();

        // The over-long patch fails on its own; the other one still installs.
        Assert.False(result.IsCompleteSuccess);
        Assert.Single(result.Installed);
        Assert.Single(result.Failures);

        HookFailure failure = result.Failures[0];
        Assert.Equal("tooBigPatch", failure.Name);
        Assert.Equal(HookFailurePhase.Prepare, failure.Phase);

        Assert.True(okHandle.IsInstalled);
        Assert.False(tooBig.Success);
        Assert.Equal(7, callGood());
        Assert.Equal(2, callBad());                       // untouched
        Assert.True(bad.MatchesOriginal(bad.CodeLength));
    }

    [Fact]
    public void AddAssemblyPatch_RollbackAndThrowLeavesNothingInstalled()
    {
        using ExecutableFunction good = TestFunctions.Constant(1);
        using ExecutableFunction bad = TestFunctions.Constant(2);

        UInt64 low = Math.Min(good.Address, bad.Address);
        UInt64 high = Math.Max(good.Address, bad.Address);
        int span = (int)(high - low) + 4096;

        using ScanRegion region = ScanRegion.FromPointer((IntPtr)low, span);
        using HookTransaction tx = new(region, loggerFactory: null, new HookTransactionOptions
        {
            Backend = Backend(),
            FailureMode = TransactionFailureMode.RollbackAndThrow,
        });

        TestDelegates.NullaryInt callGood = good.GetDelegate<TestDelegates.NullaryInt>();

        HookHandle<X64AssemblyPatch> okHandle = new();
        HookHandle<X64AssemblyPatch> tooBig = new();

        tx.AddAssemblyPatch(okHandle, HookTarget.FromAddress(good.Address), (asm, _) => asm.mov(eax, 7), name: "okPatch");
        tx.AddAssemblyPatch(tooBig, HookTarget.FromAddress(bad.Address), (asm, _) => asm.mov(eax, 7), maxByteCount: 2, name: "tooBigPatch");

        Assert.Throws<HookTransactionException>(() => tx.Commit());

        Assert.Equal(TransactionState.RolledBack, tx.State);
        Assert.False(okHandle.IsInstalled);
        Assert.Equal(1, callGood());
        Assert.True(good.MatchesOriginal(good.CodeLength));
    }

    [Fact]
    public void AddAssemblyPatch_HonoursInstallOnCommitFalse()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        using HookTransaction tx = Transaction(target, new HookTransactionOptions
        {
            Backend = Backend(),
            InstallOnCommit = false,
        });

        HookHandle<X64AssemblyPatch> handle = new();
        tx.AddAssemblyPatch(handle, HookTarget.FromAddress(target.Address), (asm, _) => asm.mov(eax, 42));

        tx.Commit();

        // Prepared, but the target is still original until EnableAll flips it.
        Assert.True(handle.Success);
        Assert.False(handle.IsInstalled);
        Assert.Equal(5, call());

        tx.EnableAll();
        Assert.Equal(42, call());

        tx.DisableAll();
        Assert.Equal(5, call());
    }

    [Fact]
    public void AddBytePatch_WritesRawBytesAndRoundsToTheInstructionBoundary()
    {
        // AddFour returns a+b+c+d. Offset 4 is the start of the third add ("add eax, <arg2>"), which is 2 bytes on the System V ABI and 3 on Windows. A single 0x90 rounds up to the
        // whole instruction either way, NOP-ing it out so the third argument drops.
        using ExecutableFunction target = TestFunctions.AddFour();
        TestDelegates.QuadInt call = target.GetDelegate<TestDelegates.QuadInt>();
        Assert.Equal(10, call(1, 2, 3, 4));

        using HookTransaction tx = Transaction(target);

        HookHandle<X64AssemblyPatch> handle = new();
        tx.AddBytePatch(handle, HookTarget.FromAddress(target.Address + 4), new byte[] { 0x90 });

        tx.Commit();

        Assert.True(handle.IsInstalled);
        Assert.Equal(7, call(1, 2, 3, 4));   // third argument no longer added

        handle.Require().Disable();
        Assert.Equal(10, call(1, 2, 3, 4));
    }

    [Fact]
    public void AddBytePatch_RejectsAnEmptyPatch()
    {
        using ExecutableFunction target = TestFunctions.Constant(5);
        using HookTransaction tx = Transaction(target);

        Assert.Throws<ArgumentException>(() =>
            tx.AddBytePatch(new HookHandle<X64AssemblyPatch>(), HookTarget.FromAddress(target.Address), ReadOnlySpan<byte>.Empty));
    }
}
