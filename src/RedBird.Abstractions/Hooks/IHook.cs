using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// The universal contract every hook implementation satisfies, regardless of architecture, hooking strategy (inline JMP, full detour, function clone) or third-party backend.
/// <para>
/// Implementations must be truly idempotent: calling <see cref="Enable"/> on an already-installed hook, or <see cref="Disable"/> on one that is not installed, is a no-op rather than an error.
/// </para>
/// </summary>
public interface IHook : IDisposable
{
    /// <summary>
    /// Human-readable identifier used in logs and failure reports. Usually the name of the field or variable the hook was registered against.
    /// </summary>
    string Name { get; }

    /// <summary>Absolute virtual address the hook is attached to.</summary>
    UInt64 TargetAddress { get; }

    /// <summary>Current lifecycle position. See <see cref="HookState"/>.</summary>
    HookState State { get; }

    /// <summary>Convenience for <c>State == HookState.Installed</c>.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Writes the patch into the target's instruction stream. Must be as short and allocation-free as practical: a transaction calls this for every hook in sequence, potentially while other threads are suspended.
    /// </summary>
    void Enable();

    /// <summary>Restores the original bytes. Safe to call when not installed.</summary>
    void Disable();
}
