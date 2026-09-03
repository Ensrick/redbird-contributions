using System;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.X64.Tests.Infrastructure;

namespace RedBird.X64.Tests;

/// <summary>
/// The behaviour every <see cref="IHookBackend"/> has to exhibit, run against each engine.
/// </summary>
/// <remarks>
/// These are the tests that would catch a regression in either engine, or a divergence between them. Anything engine-specific lives in <see cref="NativeDetourTests"/>.
/// </remarks>
public sealed class DetourContractTests
{
    public static TheoryData<string> Backends => BackendCatalog.All;

    // -------------------------------------------------------------------------
    // Preparation
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void CreateDetour_DoesNotTouchTheTarget(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt original = target.GetDelegate<TestDelegates.BinaryInt>();

        Assert.Equal(7, original(3, 4));

        using IDetour<TestDelegates.BinaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0));

        // Preparation is supposed to be free to abandon: nothing may be written until Enable.
        Assert.True(target.MatchesOriginal(target.CodeLength), "Preparing a detour modified the target's code.");
        Assert.False(detour.IsInstalled);
        Assert.Equal(HookState.Prepared, detour.State);
        Assert.Equal(7, original(3, 4));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void CreateDetour_ProducesATrampoline(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();

        using IDetour<TestDelegates.BinaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0));

        detour.Enable();

        Assert.NotEqual(IntPtr.Zero, detour.TrampolineAddress);
        Assert.NotEqual((UInt64)detour.TrampolineAddress, target.Address);
    }

    // -------------------------------------------------------------------------
    // Redirection
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void Enable_RedirectsCallsToTheCallback(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        int observedA = 0;
        int observedB = 0;

        using IDetour<TestDelegates.BinaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) =>
            {
                observedA = a;
                observedB = b;
                return 1234;
            }));

        detour.Enable();

        Assert.True(detour.IsInstalled);
        Assert.Equal(HookState.Installed, detour.State);
        Assert.Equal(1234, call(3, 4));

        // Arguments have to survive the jump: a scheme that clobbers an argument register, or a callback marshalled under the wrong ABI, shows up right here.
        Assert.Equal(3, observedA);
        Assert.Equal(4, observedB);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Original_RunsTheUnhookedFunction(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        IDetour<TestDelegates.BinaryInt>? detour = null;

        detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => detour!.Original(a, b) * 10));

        using (detour)
        {
            detour.Enable();

            // Reaching the original through the trampoline is the whole point of a detour, and it is where prologue relocation either works or does not.
            Assert.Equal(70, call(3, 4));
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Original_HandlesFourRegisterArguments(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.AddFour();
        TestDelegates.QuadInt call = target.GetDelegate<TestDelegates.QuadInt>();

        Assert.Equal(10, call(1, 2, 3, 4));

        IDetour<TestDelegates.QuadInt>? detour = null;

        detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.QuadInt>(target.Address, (a, b, c, d) => detour!.Original(a, b, c, d) + 100));

        using (detour)
        {
            detour.Enable();
            Assert.Equal(110, call(1, 2, 3, 4));
        }
    }

    // -------------------------------------------------------------------------
    // Uninstall
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void Disable_RestoresTheOriginalBytesExactly(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        using IDetour<TestDelegates.BinaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0));

        detour.Enable();
        Assert.Equal(0, call(3, 4));

        detour.Disable();

        // Byte-for-byte, not merely "behaves the same": an engine that reassembles what it thought  was there can produce equivalent code of a different length and leave a fragment behind.
        Assert.True(target.MatchesOriginal(target.CodeLength), "Disable did not restore the original bytes exactly.");
        Assert.Equal(7, call(3, 4));
        Assert.False(detour.IsInstalled);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void EnableAndDisable_AreIdempotent(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        using IDetour<TestDelegates.BinaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 99));

        detour.Enable();
        detour.Enable();

        // A second Enable that re-snapshots would capture the patch as the "original" and make the hook permanent.
        Assert.Equal(99, call(1, 1));

        detour.Disable();
        detour.Disable();

        Assert.Equal(2, call(1, 1));
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Enable_AfterDisable_WorksAgain(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        using IDetour<TestDelegates.BinaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 55));

        detour.Enable();
        detour.Disable();
        detour.Enable();

        Assert.Equal(55, call(1, 1));

        detour.Disable();
        Assert.Equal(2, call(1, 1));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Dispose_RestoresTheTarget(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        IDetour<TestDelegates.BinaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0));

        detour.Enable();
        detour.Dispose();

        Assert.Equal(HookState.Disposed, detour.State);
        Assert.True(target.MatchesOriginal(target.CodeLength), "Dispose left the target patched.");
        Assert.Equal(7, call(3, 4));
    }

    // -------------------------------------------------------------------------
    // Independence
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void TwoDetours_DoNotInterfere(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction first = TestFunctions.Constant(1);
        using ExecutableFunction second = TestFunctions.Constant(2);

        TestDelegates.NullaryInt callFirst = first.GetDelegate<TestDelegates.NullaryInt>();
        TestDelegates.NullaryInt callSecond = second.GetDelegate<TestDelegates.NullaryInt>();

        using IDetour<TestDelegates.NullaryInt> firstDetour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.NullaryInt>(first.Address, () => 10, "first"));

        using IDetour<TestDelegates.NullaryInt> secondDetour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.NullaryInt>(second.Address, () => 20, "second"));

        firstDetour.Enable();
        secondDetour.Enable();

        Assert.Equal(10, callFirst());
        Assert.Equal(20, callSecond());

        // Unhooking one must leave the other alone, they may well share a trampoline slab.
        firstDetour.Disable();

        Assert.Equal(1, callFirst());
        Assert.Equal(20, callSecond());
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Detour_PreservesTheRequestedName(string backendName)
    {
        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();

        using IDetour<TestDelegates.BinaryInt> detour = backend.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0, "gSomeNamedHook"));

        Assert.Equal("gSomeNamedHook", detour.Name);
    }
}
