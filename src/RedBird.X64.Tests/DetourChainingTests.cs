using System;
using System.Collections.Generic;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Tests.Infrastructure;

namespace RedBird.X64.Tests;

/// <summary>
/// Layered hooks: several detours on one function, each able to see and modify what the layer below it produces.
/// </summary>
/// <remarks>
/// <c>IDXGISwapChain::Present</c> in a shipped game is routinely carrying hooks from the overlay, the anti-cheat, the streaming client and a profiler at once, and each of them expects to run and to be able to call through to the next.
/// </remarks>
public sealed class DetourChainingTests
{
    public static TheoryData<string> Backends => BackendCatalog.All;

    // -------------------------------------------------------------------------
    // The chain
    // -------------------------------------------------------------------------


    [Theory]
    [MemberData(nameof(Backends))]
    public void TwoLayers_BothRunAndSeeEachOther(string backendName)
    {
        BackendCatalog.RequireLeadingJumpRelocation(backendName);

        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        List<string> order = [];

        IDetour<TestDelegates.BinaryInt>? inner = null;
        inner = backend.CreateDetour(BackendCatalog.Request<TestDelegates.BinaryInt>(
            target.Address,
            (a, b) =>
            {
                order.Add("inner");
                return inner!.Original(a, b) + 1;
            },
            "inner"));

        using (inner)
        {
            inner.Enable();
            Assert.Equal(8, call(3, 4));

            IDetour<TestDelegates.BinaryInt>? outer = null;
            outer = backend.CreateDetour(BackendCatalog.Request<TestDelegates.BinaryInt>(
                target.Address,
                (a, b) =>
                {
                    order.Add("outer");
                    return outer!.Original(a, b) * 10;
                },
                "outer"));

            using (outer)
            {
                outer.Enable();
                order.Clear();

                // 3 + 4 = 7, inner adds one, outer multiplies by ten.
                Assert.Equal(80, call(3, 4));

                // The layer installed last runs first: it owns the entry point, and its Original reaches the layer below rather than the raw function.
                Assert.Equal(["outer", "inner"], order);

                outer.Disable();
            }

            // Removing the top layer leaves the one below it intact.
            Assert.Equal(8, call(3, 4));
        }

        Assert.Equal(7, call(3, 4));
        Assert.True(target.MatchesOriginal(target.CodeLength), "Unwinding the chain did not restore the function.");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void ThreeLayers_ComposeInOrder(string backendName)
    {
        BackendCatalog.RequireLeadingJumpRelocation(backendName);

        IHookBackend backend = BackendCatalog.Resolve(backendName);

        using ExecutableFunction target = TestFunctions.Constant(1);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        IDetour<TestDelegates.NullaryInt>? first = null;
        IDetour<TestDelegates.NullaryInt>? second = null;
        IDetour<TestDelegates.NullaryInt>? third = null;

        first = backend.CreateDetour(BackendCatalog.Request<TestDelegates.NullaryInt>(
            target.Address, () => first!.Original() + 10, "first"));
        first.Enable();

        second = backend.CreateDetour(BackendCatalog.Request<TestDelegates.NullaryInt>(
            target.Address, () => second!.Original() + 100, "second"));
        second.Enable();

        third = backend.CreateDetour(BackendCatalog.Request<TestDelegates.NullaryInt>(
            target.Address, () => third!.Original() + 1000, "third"));
        third.Enable();

        Assert.Equal(1111, call());

        // Reverse order, one layer at a time.
        third.Dispose();
        Assert.Equal(111, call());

        second.Dispose();
        Assert.Equal(11, call());

        first.Dispose();
        Assert.Equal(1, call());

        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Fact]
    public void PolyHook2_ManagedChainSupportsRemovingAndReenablingAMiddleLayer()
    {
        IHookBackend backend = BackendCatalog.Resolve(BackendCatalog.PolyHook2);

        using ExecutableFunction target = TestFunctions.Constant(1);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();

        IDetour<TestDelegates.NullaryInt>? first = null;
        IDetour<TestDelegates.NullaryInt>? second = null;
        IDetour<TestDelegates.NullaryInt>? third = null;

        first = backend.CreateDetour(BackendCatalog.Request<TestDelegates.NullaryInt>(
            target.Address, () => first!.Original() + 1, "first"));
        second = backend.CreateDetour(BackendCatalog.Request<TestDelegates.NullaryInt>(
            target.Address, () => second!.Original() * 10, "second"));
        third = backend.CreateDetour(BackendCatalog.Request<TestDelegates.NullaryInt>(
            target.Address, () => third!.Original() + 100, "third"));

        first.Enable();
        second.Enable();
        third.Enable();
        Assert.Equal(120, call());

        // Only the managed chain changes; PolyHook2's one physical entry patch stays installed.
        second.Disable();
        Assert.Equal(102, call());

        // Re-enabling installs this logical layer at the top of the current chain.
        second.Enable();
        Assert.Equal(1020, call());

        // Unlike independently patched layers, removing a lower logical layer is safe here.
        first.Dispose();
        Assert.Equal(1010, call());

        second.Dispose();
        Assert.Equal(101, call());
        third.Dispose();
        Assert.Equal(1, call());
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    // -------------------------------------------------------------------------
    // Native engine specifics
    // -------------------------------------------------------------------------

    [Fact]
    public void ChainDepth_CountsLayers()
    {
        NativeDetourBackend backend = new(NativeDetourOptions.Default);

        using ExecutableFunction target = TestFunctions.Add();

        using NativeDetour<TestDelegates.BinaryInt> first =
            (NativeDetour<TestDelegates.BinaryInt>)backend.CreateDetour(
                BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0, "first"));

        first.Enable();

        using NativeDetour<TestDelegates.BinaryInt> second =
            (NativeDetour<TestDelegates.BinaryInt>)backend.CreateDetour(
                BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0, "second"));

        Assert.Equal(1, first.ChainDepth);
        Assert.Equal(2, second.ChainDepth);
    }

    [Fact]
    public void SecondLayer_HooksTheFunctionRatherThanTheFirstCallback()
    {
        NativeDetourBackend backend = new(NativeDetourOptions.Default);

        using ExecutableFunction target = TestFunctions.Add();

        using NativeDetour<TestDelegates.BinaryInt> first =
            (NativeDetour<TestDelegates.BinaryInt>)backend.CreateDetour(
                BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0, "first"));

        first.Enable();

        using NativeDetour<TestDelegates.BinaryInt> second =
            (NativeDetour<TestDelegates.BinaryInt>)backend.CreateDetour(
                BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0, "second"));

        // The installed patch is jmp qword ptr [rip+disp32], the same shape as an import thunk.
        // Following it would resolve to the first callback's marshalling stub and hook that instead, which is what makes the module check in PrologueAnalyzer load-bearing.
        Assert.Equal(target.Address, second.TargetAddress);
        Assert.Equal(first.TargetAddress, second.TargetAddress);
    }

    [Fact]
    public void RefusePolicy_RejectsASecondLayer()
    {
        NativeDetourBackend permissive = new(NativeDetourOptions.Default);
        NativeDetourBackend strict = new(new NativeDetourOptions { Chaining = HookChainingPolicy.Refuse });

        using ExecutableFunction target = TestFunctions.Add();

        using IDetour<TestDelegates.BinaryInt> first = permissive.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0, "first"));

        first.Enable();

        Assert.Throws<NotSupportedException>(() => strict.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0, "second")));
    }

    // -------------------------------------------------------------------------
    // Out-of-order unhook
    // -------------------------------------------------------------------------

    [Fact]
    public void SafeUnhook_RefusesToRemoveALayerThatIsNotOnTop()
    {
        NativeDetourBackend backend = new(NativeDetourOptions.Default);

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        IDetour<TestDelegates.BinaryInt>? inner = null;
        inner = backend.CreateDetour(BackendCatalog.Request<TestDelegates.BinaryInt>(
            target.Address, (a, b) => inner!.Original(a, b) + 1, "inner"));
        inner.Enable();

        IDetour<TestDelegates.BinaryInt>? outer = null;
        outer = backend.CreateDetour(BackendCatalog.Request<TestDelegates.BinaryInt>(
            target.Address, (a, b) => outer!.Original(a, b) * 10, "outer"));
        outer.Enable();

        Assert.Equal(80, call(3, 4));

        // The inner layer's saved bytes are the original prologue.
        // Writing them back now would overwrite the outer layer's jump and silently unhook it while it still reports itself as installed
        inner.Disable();

        Assert.True(inner.IsInstalled, "The inner layer should have refused to uninstall from under the outer one.");
        Assert.Equal(80, call(3, 4));

        outer.Dispose();
        inner.Dispose();

        Assert.Equal(7, call(3, 4));
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Fact]
    public void SafeUnhookDisabled_RestoresRegardless()
    {
        NativeDetourBackend chained = new(NativeDetourOptions.Default);
        NativeDetourBackend reckless = new(new NativeDetourOptions { SafeUnhook = false });

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();

        IDetour<TestDelegates.BinaryInt>? inner = reckless.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 0, "inner"));
        inner.Enable();

        using IDetour<TestDelegates.BinaryInt> outer = chained.CreateDetour(
            BackendCatalog.Request<TestDelegates.BinaryInt>(target.Address, (a, b) => 1, "outer"));
        outer.Enable();

        // Opting out means the original bytes go back and the outer layer goes with them.
        inner.Disable();
        inner.Dispose();

        Assert.Equal(7, call(3, 4));
        Assert.True(target.MatchesOriginal(target.CodeLength));
    }
}
