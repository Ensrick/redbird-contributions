using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MinHook;
using RedBird.Abstractions.Hooks;

namespace RedBird.Backends.MinHook;

/// <summary>
/// A detour installed through MinHook.NET.
/// </summary>
/// <typeparam name="TFunction">Delegate matching the target function signature.</typeparam>
/// <remarks>
/// <para>
/// MinHook.NET maps onto the prepare/install split cleanly, which not every engine does.
/// <c>HookEngine.CreateHook</c> allocates a buffer near the target, disassembles the prologue and builds the trampoline, but writes nothing to the target, the jump only goes in on <c>EnableHook</c>. 
/// So construction here reaches <see cref="HookState.Prepared"/> with nothing to roll back, and <see cref="Original"/> is callable immediately.
/// </para>
/// </remarks>
internal sealed unsafe class MinHookDetour<TFunction> : IDetour<TFunction> where TFunction : Delegate
{
    private readonly HookEngine _engine;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    // Rooted
    private readonly TFunction _callback;

    private TFunction? _original;

    internal MinHookDetour(HookEngine engine, in DetourRequest<TFunction> request, bool refuseJumpPrologues)
    {
        _engine = engine;
        Name = request.Name;
        TargetAddress = request.TargetAddress;
        _callback = request.Callback;
        _logger = request.Logger ?? NullLogger.Instance;

        if (refuseJumpPrologues)
        {
            byte first = *(byte*)TargetAddress;

            if (first is 0xE9 or 0xEB)
            {
                State = HookState.Failed;
                throw new NotSupportedException(
                    $"0x{TargetAddress:X16} already begins with a jump (0x{first:X2}), and MinHook.NET decodes a near JMP's rel32 displacement as a rel8 while building its trampoline, so this detour's Original would " +
                    "point at an arbitrary address rather than at whatever the jump really targets. If you think this is an error, set refuseJumpPrologues to false when creating the MinHookBackend, or use a different engine.");
            }
        }

        try
        {
            _original = _engine.CreateHook((IntPtr)TargetAddress, _callback);
        }
        catch (ArgumentException ex)
        {
            State = HookState.Failed;
            throw new NotSupportedException($"MinHook refused the detour for [{Name}] at 0x{TargetAddress:X16}. If the same callback delegate instance is used for more than one hook, give each hook its own instance, MinHook indexes callbacks by delegate identity.", ex);
        }
        catch (Exception ex)
        {
            State = HookState.Failed;
            throw new NotSupportedException($"MinHook could not build a trampoline for [{Name}] at 0x{TargetAddress:X16}: {ex.Message}", ex);
        }

        State = HookState.Prepared;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public UInt64 TargetAddress { get; }

    /// <inheritdoc/>
    public HookState State { get; private set; } = HookState.Created;

    /// <inheritdoc/>
    public bool IsInstalled => State == HookState.Installed;

    /// <inheritdoc/>
    /// <remarks>
    /// Recovered by round-tripping the delegate MinHook returned. A delegate produced by <see cref="Marshal.GetDelegateForFunctionPointer{TDelegate}"/> hands the same pointer back
    /// out of <see cref="Marshal.GetFunctionPointerForDelegate{TDelegate}"/>, so this is the trampoline address rather than a fresh marshalling stub. MinHook exposes no other way to reach it.
    /// </remarks>
    public IntPtr TrampolineAddress
    {
        get
        {
            TFunction? original = _original;

            if (original is null)
                return IntPtr.Zero;

            try
            {
                return Marshal.GetFunctionPointerForDelegate(original);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }
    }

    /// <inheritdoc/>
    public TFunction Original => _original ?? throw new InvalidOperationException(
        $"Detour [{Name}] at 0x{TargetAddress:X16} has no trampoline.");

    /// <inheritdoc/>
    public void Enable()
    {
        lock (_gate)
        {
            if (State is HookState.Installed or HookState.Disposed or HookState.Failed)
                return;

            if (_original is null)
                throw new ObjectDisposedException(nameof(MinHookDetour<TFunction>));

            _engine.EnableHook(_original);

            State = HookState.Installed;
            _logger.LogDebug("Detour [{Name}] installed at 0x{TargetAddress:X16}.", Name, TargetAddress);
        }
    }

    /// <inheritdoc/>
    public void Disable()
    {
        lock (_gate)
        {
            if (State != HookState.Installed || _original is null)
                return;

            _engine.DisableHook(_original);

            State = HookState.Disabled;
            _logger.LogDebug("Detour [{Name}] removed from 0x{TargetAddress:X16}.", Name, TargetAddress);
        }
    }

    /// <summary>
    /// Restores the target and drops the trampoline reference.
    /// </summary>
    /// <remarks>
    /// The trampoline buffer itself stays allocated: MinHook has no per-hook removal, only enable and disable, and its <c>MemoryAllocator</c> releases everything at once when the engine is disposed.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (State == HookState.Disposed)
                return;

            try
            {
                Disable();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Name}] failed to uninstall during dispose.", Name);
            }
            finally
            {
                // The engine still holds this delegate as a dictionary key, so releasing our reference does not free anything, it only stops this detour being usable.
                _original = null;
                State = HookState.Disposed;
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} @ 0x{TargetAddress:X16} [MinHook, {State}]";
}
