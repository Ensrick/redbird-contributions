namespace RedBird.X64.Hooks.Transaction;

/// <summary>Where a transaction is in its lifecycle.</summary>
public enum TransactionState
{
    /// <summary>Accepting registrations.</summary>
    Building,

    /// <summary>Commit in progress.</summary>
    Committing,

    /// <summary>Commit finished; registrations are closed.</summary>
    Committed,

    /// <summary>Commit failed and everything was rolled back.</summary>
    RolledBack,
}
