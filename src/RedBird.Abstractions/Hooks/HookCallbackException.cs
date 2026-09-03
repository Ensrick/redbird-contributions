using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// Wraps an exception thrown by a hook callback to provide additional context in logs.
/// </summary>
public sealed class HookCallbackException(UInt64 hookAddress, string message, Exception inner) : Exception(message, inner)
{
    /// <summary>The absolute address of the hook that threw.</summary>
    public UInt64 HookAddress { get; } = hookAddress;
}
