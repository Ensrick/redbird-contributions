using System;
using System.Runtime.InteropServices;
using RedBird.Abstractions.Hooks;

namespace RedBird.Backends.NativeX64;

/// <summary>
/// A fully managed x64 detour engine, implementing <see cref="IHookBackend"/> based on PolyHook2.
/// </summary>
/// <remarks>
/// <para>
/// Register it once at startup, in place of the PolyHook2 backend:
/// </para>
/// <code>
/// HookBackends.Default = NativeDetourBackend.Instance;
///
/// // or with non-default schemes
/// HookBackends.Default = new NativeDetourBackend(new NativeDetourOptions
/// {
///     AllowedSchemes = DetourScheme.Indirect | DetourScheme.Absolute,
///     FollowJumps = true,
/// });
/// </code>
/// </remarks>
public sealed class NativeDetourBackend : IHookBackend
{
    /// <summary>Shared instance using <see cref="NativeDetourOptions.Default"/>.</summary>
    public static NativeDetourBackend Instance { get; } = new();

    private readonly NativeDetourOptions _options;

    /// <summary>Creates a backend with default options.</summary>
    public NativeDetourBackend() : this(NativeDetourOptions.Default) { }

    /// <summary>Creates a backend with the given options.</summary>
    public NativeDetourBackend(NativeDetourOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException($"{nameof(NativeDetourBackend)} emits x86-64 machine code; this process is {RuntimeInformation.ProcessArchitecture}.");
        }
    }

    /// <inheritdoc/>
    public string Name => "NativeX64";

    /// <summary>The options in effect.</summary>
    public NativeDetourOptions Options => _options;

    /// <inheritdoc/>
    public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate
    {
        if (request.TargetAddress == 0)
            throw new ArgumentException("Detour target address is zero.", nameof(request));
        if (request.Callback is null)
            throw new ArgumentException("Detour callback is null.", nameof(request));

        return new NativeDetour<TFunction>(request, _options);
    }
}
