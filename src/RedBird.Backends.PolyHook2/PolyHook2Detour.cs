using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Abstractions.Hooks;

namespace RedBird.Backends.PolyHook2;

/// <summary>
/// One logical layer in a PolyHook2 chain. The chain owns one physical native detour and composes logical layers through strongly typed managed delegates.
/// </summary>
/// <typeparam name="TFunction">Delegate matching the target function signature.</typeparam>
internal sealed class PolyHook2Detour<TFunction> : IDetour<TFunction> where TFunction : Delegate
{
    private readonly ILogger _logger;
    private readonly PolyHook2Chain<TFunction> _chain;
    private TFunction? _original;

    public PolyHook2Detour(in DetourRequest<TFunction> request)
    {
        Name = request.Name;
        TargetAddress = request.TargetAddress;
        Callback = request.Callback;
        _logger = request.Logger ?? NullLogger.Instance;
        _chain = PolyHook2ChainRegistry.Register(this);
        State = HookState.Prepared;
    }

    public string Name { get; }
    public UInt64 TargetAddress { get; }
    public HookState State { get; private set; }
    public bool IsInstalled => State == HookState.Installed;
    public IntPtr TrampolineAddress => State == HookState.Disposed ? IntPtr.Zero : _chain.TrampolineAddress;

    public TFunction Original => _original ?? throw new InvalidOperationException($"Detour [{Name}] at 0x{TargetAddress:X16} has no active lower chain layer or trampoline.");

    internal TFunction Callback { get; }

    public void Enable()
    {
        if (State is HookState.Installed or HookState.Disposed)
            return;

        _chain.Enable(this);
        _logger.LogDebug("Detour [{Name}] installed at 0x{TargetAddress:X16} (shared trampoline 0x{Trampoline:X16}).", Name, TargetAddress, (UInt64)TrampolineAddress);
    }

    public void Disable()
    {
        if (State != HookState.Installed)
            return;

        _chain.Disable(this);
        _logger.LogDebug("Detour [{Name}] removed from 0x{TargetAddress:X16}.", Name, TargetAddress);
    }

    public void Dispose()
    {
        if (State == HookState.Disposed)
            return;

        _chain.DisposeLayer(this);
        GC.SuppressFinalize(this);
    }

    internal void SetOriginal(TFunction original) => _original = original;
    internal void SetState(HookState state) => State = state;
}
