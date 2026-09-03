namespace RedBird.Abstractions.Hooks.Transaction;

/// <summary>Which phase of a commit a failure came from.</summary>
public enum HookFailurePhase
{
    /// <summary>Pattern not found, export missing, or address invalid.</summary>
    Resolve,

    /// <summary>Stub allocation, disassembly, assembly or trampoline construction failed.</summary>
    Prepare,

    /// <summary>Writing the patch into live code failed.</summary>
    Install,
}
