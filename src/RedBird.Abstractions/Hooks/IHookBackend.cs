using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// Everything a transaction needs to know about a function-detour implementation.
/// </summary>
public interface IHookBackend
{
    /// <summary>Backend identifier for diagnostics, e.g. <c>"PolyHook2"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Builds a detour <b>without installing it</b>. The returned hook must be in <see cref="HookState.Prepared"/>.
    /// The transaction decides when (and whether) to call <see cref="IHook.Enable"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The backend cannot hook the requested address (e.g. the prologue is too short to relocate).
    /// </exception>
    IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate;
}
