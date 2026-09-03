using System;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.X64.Tests.Infrastructure;

namespace RedBird.X64.Tests;

/// <summary>
/// Prologue shapes that an engine has to cope with, run against each engine.
/// </summary>
/// <remarks>
/// These are the tests worth having. The contract tests would pass against an engine that only handles a textbook prologue; these are the shapes that turn up in real compiler output and
/// break naive relocation.
/// </remarks>
public sealed class DetourPrologueShapeTests
{
    public static TheoryData<string> Backends => BackendCatalog.All;

    public static TheoryData<string, int> BackendsAndPrologueSizes
    {
        get
        {
            TheoryData<string, int> data = new();

            foreach (string backend in new[] { BackendCatalog.NativeX64, BackendCatalog.PolyHook2 })
            {
                foreach (int size in new[] { 5, 6, 8, 12, 14, 20 })
                    data.Add(backend, size);
            }

            return data;
        }
    }

    // -------------------------------------------------------------------------
    // Relocation
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void RipRelativeOperand_SurvivesRelocation(string backendName)
    {
        BackendCatalog.RequireRipRelativeRelocation(backendName);

        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.RipRelativeRead(0xBEEF);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        Assert.Equal(0xBEEF, call());

        IDetour<TestDelegates.NullaryInt>? detour = null;
        detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.NullaryInt>(target.Address, () => detour!.Original()));

        using (detour)
        {
            detour.Enable();

            // The displaced instruction reads through RIP, so relocating it without re-encoding the displacement makes it read from somewhere else entirely. A wrong value here means the
            // trampoline is silently corrupt.
            Assert.Equal(0xBEEF, call());
        }

        Assert.Equal(0xBEEF, call());
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void BranchIntoTheProloguePrefix_IsHandled(string backendName)
    {
        BackendCatalog.RequireBranchIntoPrologueSupport(backendName);

        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.SelfBranchingLoop();
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        Assert.Equal(3, call());

        IDetour<TestDelegates.NullaryInt>? detour = null;
        detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.NullaryInt>(target.Address, () => detour!.Original()));

        using (detour)
        {
            detour.Enable();

            // The loop branches back to offset 2, which sits inside the bytes the engine wants to overwrite. Unless the displaced region is widened to include the branch itself, the
            // relocated loop jumps into the middle of the installed patch.
            Assert.Equal(3, call());
        }

        Assert.Equal(3, call());
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    // -------------------------------------------------------------------------
    // Thunks
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void Thunk_IsHookedThroughToItsBody(string backendName)
    {
        BackendCatalog.RequireLeadingJumpRelocation(backendName);

        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.ThunkTo(0x42, out UInt64 bodyOffset);
        TestDelegates.NullaryInt callThroughThunk = target.GetDelegate<TestDelegates.NullaryInt>();

        Assert.Equal(0x42, callThroughThunk());

        using IDetour<TestDelegates.NullaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.NullaryInt>(target.Address, () => 0x99));

        detour.Enable();

        // Whether the engine patched the thunk or followed it to the body, a call through the thunk has to reach the callback.
        Assert.Equal(0x99, callThroughThunk());

        detour.Disable();
        Assert.Equal(0x42, callThroughThunk());

        _ = bodyOffset;
    }

    // -------------------------------------------------------------------------
    // Refusals
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void FunctionTooSmall_IsRefusedWithoutCorruptingMemory(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.TooSmall();
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        Assert.Equal(0, call());

        // The engines disagree about when they refuse: the managed one validates during construction, PolyHook only finds out inside Hook(). Either is fine; installing a
        // 14-byte patch over a 4-byte function is not.
        Exception? failure = Record.Exception(() =>
        {
            IDetour<TestDelegates.NullaryInt> detour = backend.CreateDetour(
                BackendCatalog.Request<TestDelegates.NullaryInt>(target.Address, () => 1));

            using (detour)
            {
                detour.Enable();
            }
        });

        Assert.NotNull(failure);
        Assert.Equal(0, call());
        Assert.True(target.MatchesOriginal(target.CodeLength), "A refused hook still modified the target.");
    }

    [Theory]
    [MemberData(nameof(BackendsAndPrologueSizes))]
    public void ShortPrologue_EitherWorksOrIsRefused_ButNeverCorrupts(string backendName, int prologueBytes)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add(prologueBytes);
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        Assert.Equal(7, call(3, 4));

        IDetour<TestDelegates.BinaryInt>? detour = null;

        Exception? failure = Record.Exception(() =>
        {
            detour = backend.CreateDetour(
                BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 1000));

            detour.Enable();
        });

        try
        {
            if (failure is null)
            {
                Assert.Equal(1000, call(3, 4));
                detour!.Disable();
            }

            // Whichever way it went, the function has to be intact afterwards. This is the property that matters: a prologue too short for the chosen scheme must produce a refusal, not
            // a patch that runs past the end of the function.
            Assert.Equal(7, call(3, 4));
            Assert.True(
                target.MatchesOriginal(target.CodeLength),
                $"Target corrupted with a {prologueBytes}-byte prologue (outcome: {(failure is null ? "hooked" : failure.GetType().Name)}).");
        }
        finally
        {
            detour?.Dispose();
        }
    }
}
