using System;
using System.Collections.Generic;

namespace RedBird.X64.Assembly.Stateful;

/// <summary>
/// A patched constant somewhere in the target's code, independent of its value type, so sites of different widths can live in one collection.
/// </summary>
public interface IAssemblyValueSite : IDisposable
{
    /// <summary>Address of the instruction this site rewrites.</summary>
    IntPtr InstructionAddress { get; }

    /// <summary>Human-readable description, used in diagnostics.</summary>
    string Description { get; }

    /// <summary>Number of overrides currently registered.</summary>
    int OverrideCount { get; }

    /// <summary>Removes every override belonging to <paramref name="owner"/> and re-evaluates.</summary>
    /// <returns>True when at least one override was removed.</returns>
    bool ClearOverrides(object owner);

    /// <summary>
    /// Removes every override and writes the value the target shipped with.
    /// </summary>
    void RestoreOriginal();

    /// <summary>One line per active override, highest priority first.</summary>
    IReadOnlyList<string> DescribeOverrides();

    /// <summary>
    /// Legacy stack pop. Prefer <see cref="ClearOverrides"/>; see the remarks on
    /// <see cref="IAssemblyValueSite{T}"/>.
    /// </summary>
    void Pop();
}

/// <summary>
/// A patched constant of a known type.
/// </summary>
/// <typeparam name="T">Integral value type of the constant.</typeparam>
public interface IAssemblyValueSite<T> : IAssemblyValueSite where T : unmanaged
{
    /// <summary>The value the target shipped with, captured before anything was written.</summary>
    T OriginalValue { get; }

    /// <summary>The value currently encoded in the instruction.</summary>
    T CurrentValue { get; }

    /// <summary>Reads the current value.</summary>
    T GetValue();

    /// <summary>
    /// Registers or replaces an override for <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">Identity of the requesting party, compared by reference.</param>
    /// <param name="value">The value this owner wants.</param>
    /// <param name="priority">Higher wins; ties go to whichever was registered later.</param>
    /// <param name="reason">Optional note for diagnostics.</param>
    /// <returns>A scope that withdraws the override when disposed.</returns>
    AssemblyValueOverrideScope SetOverride(object owner, T value, int priority = 0, string? reason = null);

    /// <summary>
    /// Writes <paramref name="value"/> under a shared ambient owner. Convenience for the common
    /// single-consumer case; prefer <see cref="SetOverride"/> when more than one party is involved.
    /// </summary>
    void SetValue(T value);

    /// <summary>Legacy stack push.</summary>
    void Push(T value);

    /// <summary>Legacy stack pop, returning the value now in effect.</summary>
    new T Pop();

    /// <summary>Fires whenever the effective value changes.</summary>
    event Action<T>? ValueChanged;
}
