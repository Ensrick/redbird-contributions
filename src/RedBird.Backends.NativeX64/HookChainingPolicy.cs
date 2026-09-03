namespace RedBird.Backends.NativeX64;

/// <summary>
/// What happens when a function is detoured that this engine has already detoured.
/// </summary>
public enum HookChainingPolicy
{
    /// <summary>Install on top of the existing hook. The default.</summary>
    Allow,

    /// <summary>Install, and log a warning naming the existing layer.</summary>
    WarnAndAllow,

    /// <summary>
    /// Throw rather than layer. 
    /// For a hook that owns its function outright and would be broken by anything sitting between it and the caller.
    /// </summary>
    Refuse,
}
