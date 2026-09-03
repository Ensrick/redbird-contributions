using System;

namespace RedBird.Abstractions.Hooks.Transaction;

/// <summary>
/// Handle to a function detour that exposes the original function directly.
/// </summary>
/// <typeparam name="TFunction">Delegate matching the target function signature.</typeparam>
public sealed class DetourHandle<TFunction> : HookHandle<IDetour<TFunction>> where TFunction : Delegate
{
    private TFunction? _original;

    /// <summary>
    /// Calls the original, un-hooked function.
    /// </summary>
    /// <exception cref="InvalidOperationException">The detour is not installed.</exception>
    public TFunction Original => _original ?? throw new InvalidOperationException($"Detour '{Name}' is not installed, so the original function cannot be called. {(Failure is null ? "Was the transaction committed?" : "Registration failed: " + Failure.Message)}", Failure);

    /// <summary>
    /// Non-throwing variant for call sites that must keep running when a pattern goes stale
    /// after a game patch.
    /// </summary>
    public bool TryGetOriginal(out TFunction? original)
    {
        original = _original;
        return original is not null;
    }

    /// <inheritdoc/>
    internal override void Attach(IDetour<TFunction> hook, UInt64 resolvedAddress)
    {
        base.Attach(hook, resolvedAddress);
        _original = hook.Original;
    }
}
