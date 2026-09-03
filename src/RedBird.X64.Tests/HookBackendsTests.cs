using System;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace RedBird.X64.Tests;

/// <summary>
/// Tests for <see cref="HookBackends"/>, the process-wide default-engine registry.
/// </summary>
/// <remarks>
/// It is global mutable state, which is why it gets its own collection: these tests set and clear the default, and anything else reading it concurrently would see whichever value happened to be
/// there.
/// </remarks>
[Collection("HookBackends")]
public sealed class HookBackendsTests : IDisposable
{
    private readonly bool _hadDefault;
    private readonly IHookBackend? _previous;

    public HookBackendsTests()
    {
        _hadDefault = HookBackends.HasDefault;
        _previous = _hadDefault ? HookBackends.Default : null;
    }

    [Fact]
    public void Resolve_PrefersTheExplicitBackend()
    {
        HookBackends.Default = NativeDetourBackend.Instance;

        StubBackend explicitBackend = new();

        Assert.Same(explicitBackend, HookBackends.Resolve(explicitBackend));
        Assert.Same(NativeDetourBackend.Instance, HookBackends.Resolve(null));
    }

    [Fact]
    public void Default_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => HookBackends.Default = null!);
    }

    [Fact]
    public void TrySetDefault_DoesNotOverrideAnExplicitChoice()
    {
        HookBackends.Default = NativeDetourBackend.Instance;

        Assert.False(HookBackends.TrySetDefault(new StubBackend()));
        Assert.Same(NativeDetourBackend.Instance, HookBackends.Default);
    }

    [Fact]
    public void HasDefault_ReflectsRegistration()
    {
        HookBackends.Default = NativeDetourBackend.Instance;
        Assert.True(HookBackends.HasDefault);
    }

    public void Dispose()
    {
        if (_previous is not null)
            HookBackends.Default = _previous;
    }

    /// <summary>A backend that exists only to be compared by reference.</summary>
    private sealed class StubBackend : IHookBackend
    {
        public string Name => "Stub";

        public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request)
            where TFunction : Delegate
            => throw new NotSupportedException("The stub backend never creates detours.");
    }
}
