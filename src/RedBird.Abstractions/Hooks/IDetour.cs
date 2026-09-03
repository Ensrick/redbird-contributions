using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// A hook that redirects a whole function and preserves a callable path to the original.
/// </summary>
public interface IDetour : IHook
{
    /// <summary>Address of the trampoline that reaches the un-hooked original function.</summary>
    IntPtr TrampolineAddress { get; }
}

/// <summary>
/// Strongly-typed detour. <typeparamref name="TFunction"/> is the managed signature of the hooked function.
/// </summary>
/// <typeparam name="TFunction">Delegate matching the target's calling convention and signature.</typeparam>
public interface IDetour<out TFunction> : IDetour where TFunction : Delegate
{
    /// <summary>
    /// Invokes the original, un-hooked function. 
    /// Cached at install time, so calling this in a hot callback costs one field read plus the delegate invoke: no marshalling per call.
    /// </summary>
    /// <exception cref="InvalidOperationException">The detour is not installed.</exception>
    TFunction Original { get; }
}
