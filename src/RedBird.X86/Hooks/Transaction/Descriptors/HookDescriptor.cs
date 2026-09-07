using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Memory.Scanners;

namespace RedBird.X86.Hooks.Transaction.Descriptors;

/// <summary>Services a descriptor needs while building its hook.</summary>
internal readonly struct HookBuildContext(IHookBackend backend, ILoggerFactory? loggerFactory)
{
    /// <summary>Detour implementation selected for this transaction.</summary>
    public IHookBackend Backend { get; } = backend;

    /// <summary>Optional logger factory supplied by the consumer.</summary>
    public ILoggerFactory? LoggerFactory { get; } = loggerFactory;

    /// <summary>Creates a category logger, falling back to a null logger.</summary>
    public ILogger CreateLogger(string category)
        => LoggerFactory?.CreateLogger(category) ?? NullLogger.Instance;
}

/// <summary>
/// One registered-but-not-yet-installed hook.
/// </summary>
internal abstract class HookDescriptor(string name, HookTarget target)
{
    /// <summary>Registration name, used in logs and <see cref="Abstractions.Hooks.Transaction.HookFailure"/>.</summary>
    public string Name { get; } = name;

    /// <summary>Where this hook attaches.</summary>
    public HookTarget Target { get; } = target;

    /// <summary>
    /// Pattern parsed at registration time, for pattern targets.
    /// </summary>
    public CompiledPattern? Pattern { get; set; }

    /// <summary>Address produced by the resolve phase.</summary>
    public UInt64 ResolvedAddress { get; set; }

    /// <summary>Specific reason resolution failed after a pattern matched, when available.</summary>
    public string? ResolutionFailure { get; set; }

    /// <summary>The built hook, available from the prepare phase onwards.</summary>
    public IHook? Hook { get; protected set; }

    /// <summary>
    /// Builds everything the hook needs: stub allocation, assembly, trampoline, without modifying a single byte of the target's code. Must leave <see cref="Hook"/> in <see cref="HookState.Prepared"/>.
    /// </summary>
    public abstract void Prepare(UInt64 address, in HookBuildContext context);

    /// <summary>Publishes the installed hook to the consumer's handle.</summary>
    public abstract void Publish();

    /// <summary>Records a failure on the consumer's handle.</summary>
    public abstract void Reject(Exception failure);

    /// <summary>Releases a prepared-but-never-installed hook. Cheap: nothing was written.</summary>
    public void Discard()
    {
        try
        {
            Hook?.Dispose();
        }
        finally
        {
            Hook = null;
        }
    }
}
