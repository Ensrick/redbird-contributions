using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// Process-wide registry for the detour engine transactions use when none is specified.
/// </summary>
public static class HookBackends
{
    private static IHookBackend? _default;

    /// <summary>
    /// The detour engine used when <c>HookTransactionOptions.Backend</c> is left unset.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before any backend was registered.</exception>
    public static IHookBackend Default
    {
        get => _default ?? throw new InvalidOperationException("No detour backend is registered. Reference a backend package (e.g. RedBird.Backends.PolyHook2) and set HookBackends.Default = PolyHook2Backend.Instance during startup, or set Backend explicitly on HookTransactionOptions.");
        set => _default = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>True when a default backend has been registered.</summary>
    public static bool HasDefault => _default is not null;

    /// <summary>
    /// Registers <paramref name="backend"/> as the default unless one is already set. 
    /// Intended for backend packages that want to self-register without overriding a host's explicit choice.
    /// </summary>
    public static bool TrySetDefault(IHookBackend backend)
    {
        if (backend is null)
            throw new ArgumentNullException(nameof(backend));

        if (_default is not null)
            return false;

        _default = backend;
        return true;
    }

    /// <summary>Resolves <paramref name="preferred"/>, falling back to <see cref="Default"/>.</summary>
    public static IHookBackend Resolve(IHookBackend? preferred) => preferred ?? Default;
}
