using RedBird.Abstractions.Hooks;
using System;

namespace RedBird.Backends.PolyHook2;

/// <summary>
/// <see cref="IHookBackend"/> implemented on top of PolyHook2.NET.
/// <para>
/// This assembly is the only place in the solution that references the PolyHook2 package.
/// Everything else, transactions, descriptors, handles, consumer callbacks, talks to
/// <see cref="IDetour{TFunction}"/>. Supporting MinHook, Microsoft Detours, or a hand-rolled
/// trampoline engine means shipping a sibling package, not editing the core.
/// </para>
/// <para>
/// Register it once during startup:
/// <code>
/// HookBackends.Default = PolyHook2Backend.Instance;
/// </code>
/// or per transaction via <c>HookTransactionOptions.Backend</c>.
/// </para>
/// </summary>
public sealed class PolyHook2Backend : IHookBackend
{
    /// <summary>Shared instance; the backend is stateless.</summary>
    public static PolyHook2Backend Instance { get; } = new();

    /// <inheritdoc/>
    public string Name => "PolyHook2";

    /// <inheritdoc/>
    public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate
    {
        if (request.TargetAddress == 0)
            throw new ArgumentException("Detour target address is zero.", nameof(request));
        if (request.Callback is null)
            throw new ArgumentException("Detour callback is null.", nameof(request));
        if (request.IntermediaryFactory is not null)
        {
            throw new NotSupportedException("PolyHook2 does not expose a preparation point at which both trampoline directions can be wrapped. Use a RedBird native backend for intermediary stubs.");
        }

        return new PolyHook2Detour<TFunction>(request);
    }
}
