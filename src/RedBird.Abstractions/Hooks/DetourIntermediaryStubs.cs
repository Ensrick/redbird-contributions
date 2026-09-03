using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// The two effective entry points returned by an <see cref="IDetourIntermediaryFactory"/>.
/// </summary>
/// <remarks>
/// This object is retained for the detour's lifetime. Pass an optional lifetime owner when the generated code, unwind metadata, pinned state, or other resources must be released with it.
/// Shared slab allocations that cannot be individually reclaimed need no owner.
/// </remarks>
public sealed class DetourIntermediaryStubs : IDisposable
{
    private IDisposable? _lifetime;

    /// <summary>Creates a pair of effective entry points.</summary>
    /// <param name="hookEntryPoint">Address to which the target will jump.</param>
    /// <param name="originalEntryPoint">Address exposed through <see cref="IDetour"/>.Original</param>
    /// <param name="lifetime">Optional owner disposed with the detour.</param>
    public DetourIntermediaryStubs(IntPtr hookEntryPoint, IntPtr originalEntryPoint, IDisposable? lifetime = null)
    {
        if (hookEntryPoint == IntPtr.Zero)
            throw new ArgumentOutOfRangeException(nameof(hookEntryPoint), "The hook entry point cannot be zero.");
        if (originalEntryPoint == IntPtr.Zero)
            throw new ArgumentOutOfRangeException(nameof(originalEntryPoint), "The original-call entry point cannot be zero.");

        HookEntryPoint = hookEntryPoint;
        OriginalEntryPoint = originalEntryPoint;
        _lifetime = lifetime;
    }

    /// <summary>Address to which the patched target jumps.</summary>
    public IntPtr HookEntryPoint { get; }

    /// <summary>Address marshalled as the strongly typed <c>Original</c> delegate.</summary>
    public IntPtr OriginalEntryPoint { get; }

    /// <summary>Releases the optional lifetime owner once.</summary>
    public void Dispose()
    {
        IDisposable? lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Dispose();
    }
}
