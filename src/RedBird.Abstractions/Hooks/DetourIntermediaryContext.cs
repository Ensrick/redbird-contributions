using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// Addresses available when building calling-convention or instrumentation stubs for a detour.
/// </summary>
/// <remarks>
/// The hook path is target -> HookEntryPoint -> CallbackAddress. 
/// The original-call path is managed callback -> OriginalEntryPoint -> TrampolineAddress. 
/// A factory may wrap either or both paths and return the unwrapped address for the other.
/// </remarks>
/// <remarks>Creates a context supplied to <see cref="IDetourIntermediaryFactory"/>.</remarks>
public readonly struct DetourIntermediaryContext(DetourArchitecture architecture, UInt64 targetAddress, IntPtr callbackAddress, IntPtr trampolineAddress)
{

    /// <summary>Instruction set in which the stubs must be emitted.</summary>
    public DetourArchitecture Architecture { get; } = architecture;

    /// <summary>Resolved target entry point, after thunk following.</summary>
    public UInt64 TargetAddress { get; } = targetAddress;

    /// <summary>Raw native entry point for the managed callback.</summary>
    public IntPtr CallbackAddress { get; } = callbackAddress;

    /// <summary>Raw relocated-original trampoline produced by the backend.</summary>
    public IntPtr TrampolineAddress { get; } = trampolineAddress;
}
