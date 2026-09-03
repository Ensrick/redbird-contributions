namespace RedBird.Abstractions.Hooks.Transaction;

/// <summary>Describes how a commit failure is handled.</summary>
public enum TransactionFailureMode
{
    /// <summary>
    /// Roll back every hook activated so far and rethrow.
    /// Leaves the process in the same state as before <c>HookTransaction.Commit</c> was called.
    /// </summary>
    RollbackAndThrow,

    /// <summary>
    /// Log and skip each hook that fails. Successfully installed hooks remain active.
    /// </summary>
    ContinueOnError,
}
