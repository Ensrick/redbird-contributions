namespace RedBird.Abstractions.Hooks;

/// <summary>
/// Lifecycle of a single hook. 
/// The distinction between <see cref="Prepared"/> and <see cref="Installed"/> is what makes a transactional commit possible: everything expensive and failure-prone (stub allocation, assembly, trampoline creation) happens
/// while reaching <see cref="Prepared"/>, and no byte of the target process has been modified yet. 
/// Only <see cref="IHook.Enable"/> touches live code.
/// </summary>
public enum HookState
{
    /// <summary>Constructed; no resources acquired, no code written.</summary>
    Created,

    /// <summary>
    /// Fully built (stub allocated, code assembled, trampoline resolved) but the target instructions are still untouched. 
    /// Discarding from here is free and side-effect-less.
    /// </summary>
    Prepared,

    /// <summary>The patch is live in the target's instruction body.</summary>
    Installed,

    /// <summary>Was installed at least once; original bytes are currently restored.</summary>
    Disabled,

    /// <summary>Resources released. Terminal.</summary>
    Disposed,

    /// <summary>Preparation or installation failed. Terminal.</summary>
    Failed,
}
