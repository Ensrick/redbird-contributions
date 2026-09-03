using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX86;

namespace RedBird.X86.Hooks.Transaction;

/// <summary>Transaction-wide policy.</summary>
public sealed record HookTransactionOptions
{
    /// <summary>Shared all-defaults instance.</summary>
    public static HookTransactionOptions Default { get; } = new();

    /// <summary>What happens when a hook fails to resolve, prepare or install.</summary>
    public TransactionFailureMode FailureMode { get; init; } = TransactionFailureMode.ContinueOnError;

    /// <summary>
    /// Detour engine for this transaction.
    /// <para>
    /// Left <see langword="null"/> it resolves to the built-in native x86 backend at commit time.
    /// </para>
    /// </summary>
    public IHookBackend? Backend { get; init; }

    /// <summary>Partition the AOB sweep across cores when the region is large enough to justify it.</summary>
    public bool ParallelScan { get; init; } = true;

    /// <summary>
    /// When false, <c>Commit</c> resolves and prepares everything but leaves the target's code untouched, so a later <c>EnableAll</c> flips the whole batch on with no allocation,
    /// disassembly or stub work in between. Useful when hooks must go live at a precise moment (a level load, a specific game tick) rather than whenever startup happens to finish.
    /// </summary>
    public bool InstallOnCommit { get; init; } = true;

    /// <summary>Dispose hooks when the transaction is disposed. Off by default: hooks normally outlive the transaction.</summary>
    public bool OwnsHooks { get; init; }

    /// <summary>
    /// The engine this transaction will actually use.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// The selected backend could not be constructed in the current process architecture.
    /// </exception>
    public IHookBackend ResolveBackend() => Backend ?? X86DetourBackend.Instance;
}
