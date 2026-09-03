using System;

namespace RedBird.Abstractions.Hooks.Transaction;

/// <summary>
/// A typed handle to a hook that a transaction will install later.
/// <para>
/// Declare one as a static readonly field, register it, and read it from your callback after <c>Commit</c>.
/// </para>
/// </summary>
/// <typeparam name="THook">Hook type, e.g. <c>X64InlineHook</c>.</typeparam>
public class HookHandle<THook> where THook : class, IHook
{
    /// <summary>The installed hook, or <see langword="null"/> if registration/installation failed.</summary>
    public THook? Hook { get; private set; }

    /// <summary>Why this hook is missing, when it is. <see langword="null"/> on success.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Resolved absolute address, or 0 if resolution never succeeded.</summary>
    public UInt64 ResolvedAddress { get; private set; }

    /// <summary>True once the hook exists and is live in the target's instruction stream.</summary>
    public bool IsInstalled => Hook is { IsInstalled: true };

    /// <summary>True once the hook object exists, regardless of enable state.</summary>
    public bool Success => Hook is not null;

    /// <summary>The hook, or a exception.</summary>
    /// <exception cref="InvalidOperationException">The hook was never installed.</exception>
    public THook Require() => Hook ?? throw new InvalidOperationException($"Hook '{Name}' is not installed. {(Failure is null ? "It was never registered or the transaction has not been committed." : "Registration failed: " + Failure.Message)}", Failure);

    /// <summary>Diagnostic name, assigned at registration from the caller's expression.</summary>
    public string Name { get; private set; } = "<unregistered>";

    /// <summary>Called by a transaction during registration.</summary>
    internal void Register(string name) => Name = name;

    /// <summary>Called by a transaction once the hook is live.</summary>
    internal virtual void Attach(THook hook, UInt64 resolvedAddress)
    {
        Hook = hook;
        ResolvedAddress = resolvedAddress;
        Failure = null;
    }

    /// <summary>Called by a transaction when resolution, preparation or installation failed.</summary>
    internal void Reject(Exception failure)
    {
        Hook = null;
        Failure = failure;
    }

    /// <summary>Called by a transaction when rolling back.</summary>
    internal void Detach()
    {
        Hook = null;
        ResolvedAddress = 0;
    }
}
