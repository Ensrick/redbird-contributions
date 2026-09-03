using System;

namespace RedBird.Abstractions.Hooks.Transaction;

/// <summary>
/// Thrown by an architecture-specific <c>HookTransaction.Commit</c> when a hook fails and the mode is <see cref="TransactionFailureMode.RollbackAndThrow"/>.
/// </summary>
public sealed class HookTransactionException(string hookKey, string message, Exception? inner = null) : Exception(message, inner)
{
    public string HookKey { get; } = hookKey;
}
