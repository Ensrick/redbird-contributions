using System.Collections.Generic;

namespace RedBird.Abstractions.Hooks.Transaction;

/// <summary>
/// Outcome of <c>HookTransaction.Commit</c>.
/// Callers can branch on <see cref="IsCompleteSuccess"/> or inspect individual failures through this.
/// </summary>
public sealed class CommitResult(IReadOnlyList<IHook> installed, IReadOnlyList<HookFailure> failures, int registeredCount, long scanMilliseconds, long totalMilliseconds)
{
    /// <summary>Hooks that are live, in installation order.</summary>
    public IReadOnlyList<IHook> Installed { get; } = installed;

    /// <summary>Everything that did not install, with phase and reason.</summary>
    public IReadOnlyList<HookFailure> Failures { get; } = failures;

    /// <summary>How many hooks were registered on the transaction.</summary>
    public int RegisteredCount { get; } = registeredCount;

    /// <summary>Wall-clock time spent in the AOB scan phase.</summary>
    public long ScanMilliseconds { get; } = scanMilliseconds;

    /// <summary>Wall-clock time for the whole commit.</summary>
    public long TotalMilliseconds { get; } = totalMilliseconds;

    /// <summary>True when every registered hook installed.</summary>
    public bool IsCompleteSuccess => Failures.Count == 0 && Installed.Count == RegisteredCount;

    /// <inheritdoc/>
    public override string ToString() => $"{Installed.Count}/{RegisteredCount} installed, {Failures.Count} failed, {TotalMilliseconds}ms ({ScanMilliseconds}ms scanning)";
}
