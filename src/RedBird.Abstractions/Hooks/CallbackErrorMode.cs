namespace RedBird.Abstractions.Hooks;

/// <summary>
/// Controls how exceptions thrown inside an architecture-specific context-hook callback (e.g. RedBird.X64's <c>ContextHookDelegate</c>) are handled.
/// </summary>
public enum CallbackErrorMode
{
    /// <summary>
    /// Exceptions propagate normally.
    /// An unhandled exception here will crash the process or thread because the call originates from native code: use this only when you are wrapping the callback yourself.
    /// </summary>
    Rethrow,

    /// <summary>
    /// Exceptions are caught, logged via the builder's <c>ILogger</c>, and silently swallowed so the hook returns cleanly without touching registers.
    /// Register state captured before the call is restored by the surrounding assembly stub exactly as it would be for a successful call.
    /// </summary>
    LogAndContinue,

    /// <summary>
    /// Like <see cref="LogAndContinue"/>, but also invokes a user-supplied recovery action  so you can zero out registers, apply a safe default, etc.
    /// The recovery action receives the same context pointer and the caught exception.
    /// </summary>
    LogAndContinueWithRecovery,
}
