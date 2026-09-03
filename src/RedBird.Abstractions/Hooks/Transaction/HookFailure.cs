using System;

namespace RedBird.Abstractions.Hooks.Transaction;

/// <summary>One hook that did not make it, with enough context to act on it, hopefully.</summary>
public sealed class HookFailure(string name, HookTarget target, HookFailurePhase phase, string message, Exception? exception = null)
{
    /// <summary>Registration name of the failed hook.</summary>
    public string Name { get; } = name;

    /// <summary>What the hook was pointed at.</summary>
    public HookTarget Target { get; } = target;

    /// <summary>Phase the failure occurred in.</summary>
    public HookFailurePhase Phase { get; } = phase;

    /// <summary>Human-readable reason.</summary>
    public string Message { get; } = message;

    /// <summary>Underlying exception, when there was one.</summary>
    public Exception? Exception { get; } = exception;

    /// <inheritdoc/>
    public override string ToString() => $"[{Phase}] {Name} ({Target}): {Message}";
}
