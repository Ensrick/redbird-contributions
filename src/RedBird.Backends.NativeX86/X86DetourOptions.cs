using Microsoft.Extensions.Logging;
using RedBird.Core.Platform;

namespace RedBird.Backends.NativeX86;

/// <summary>Tuning for <see cref="X86DetourBackend"/>.</summary>
public sealed record X86DetourOptions
{
    /// <summary>Shared all-defaults instance.</summary>
    public static X86DetourOptions Default { get; } = new();

    /// <summary>Follow a leading direct or import-thunk jump to the underlying function body.</summary>
    public bool FollowJumps { get; init; } = true;

    /// <summary>Follow a leading call as well. Disabled because most leading calls are real work.</summary>
    public bool FollowCalls { get; init; }

    /// <summary>Maximum number of entry thunks to follow.</summary>
    public int MaxFollowDepth { get; init; } = 5;

    /// <summary>Maximum bytes decoded when finding branches into a displaced prologue.</summary>
    public int FunctionScanLimit { get; init; } = 16 * 1024;

    /// <summary>Policy for layering on top of an existing detour.</summary>
    public X86HookChainingPolicy Chaining { get; init; } = X86HookChainingPolicy.Allow;

    /// <summary>Refuse an out-of-order uninstall when another layer replaced this patch.</summary>
    public bool SafeUnhook { get; init; } = true;

    /// <summary>
    /// Suspend peer threads while installing/removing the patch and redirect a thread stopped in the displaced prologue to its equivalent trampoline instruction.
    /// </summary>
    /// <remarks>
    /// Enabled by default on Windows and paid only during enable/disable. Explicitly enabling it on a platform without Win32 thread contexts throws.
    /// </remarks>
    public bool SuspendThreadsDuringPatch { get; init; } = PlatformInfo.IsWindows;

    /// <summary>Maximum distance for an anonymous direct jump to count as a local thunk.</summary>
    public int LocalThunkRange { get; init; } = 4096;

    /// <summary>Optional logger factory for per-detour diagnostics.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
