using System;
using System.Runtime.InteropServices;
using RedBird.Abstractions.Hooks;

namespace RedBird.Backends.NativeX86;

/// <summary>Fully managed 32-bit x86 detour backend.</summary>
/// <remarks>
/// Uses Iced's 32-bit decoder and block encoder, a five-byte rel32 entry patch, and the shared Windows/POSIX executable-memory layer. 
/// It can only be instantiated in an x86 process.
/// </remarks>
public sealed class X86DetourBackend : IHookBackend
{
    private static readonly Lazy<X86DetourBackend> LazyInstance = new(() => new X86DetourBackend());

    /// <summary>Shared instance using <see cref="X86DetourOptions.Default"/>.</summary>
    public static X86DetourBackend Instance => LazyInstance.Value;

    private readonly X86DetourOptions _options;

    /// <summary>Creates a backend with default options.</summary>
    public X86DetourBackend() : this(X86DetourOptions.Default) { }

    /// <summary>Creates a backend with explicit options.</summary>
    public X86DetourBackend(X86DetourOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
        {
            throw new PlatformNotSupportedException($"{nameof(X86DetourBackend)} emits 32-bit x86 machine code; this process is {RuntimeInformation.ProcessArchitecture}.");
        }
    }

    /// <inheritdoc/>
    public string Name => "NativeX86";

    /// <summary>Options in effect for this backend.</summary>
    public X86DetourOptions Options => _options;

    /// <inheritdoc/>
    public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate
    {
        if (request.TargetAddress == 0 || request.TargetAddress > UInt32.MaxValue)
            throw new ArgumentException("Detour target is not a valid non-zero x86 address.", nameof(request));
        if (request.Callback is null)
            throw new ArgumentException("Detour callback is null.", nameof(request));

        return new X86Detour<TFunction>(request, _options);
    }
}
