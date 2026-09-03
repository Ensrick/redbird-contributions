using Microsoft.Extensions.Logging;
using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>Inputs for <see cref="IHookBackend.CreateDetour{TFunction}"/>.</summary>
/// <typeparam name="TFunction">Delegate matching the target function signature.</typeparam>
public readonly struct DetourRequest<TFunction> where TFunction : Delegate
{
    /// <summary>Detour name, sent to <see cref="IHook.Name"/>.</summary>
    public string Name { get; init; }

    /// <summary>Absolute virtual address of the function to detour.</summary>
    public UInt64 TargetAddress { get; init; }

    /// <summary>Managed replacement invoked instead of the original.</summary>
    public TFunction Callback { get; init; }

    /// <summary>
    /// Optional factory that inserts architecture-specific entry and original-call stubs around the detour. 
    /// This is the extension point for calling-convention translators.
    /// </summary>
    /// <remarks>
    /// Native backends call the factory after relocating the target prologue, when both the raw callback pointer and the trampoline address are known. 
    /// Backends that cannot support intermediary stubs must reject the request instead of silently ignoring it.
    /// </remarks>
    public IDetourIntermediaryFactory? IntermediaryFactory { get; init; }

    /// <summary>Optional logger scoped to this hook.</summary>
    public ILogger? Logger { get; init; }
}
