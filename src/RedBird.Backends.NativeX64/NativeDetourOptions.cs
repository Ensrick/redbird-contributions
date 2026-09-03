using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RedBird.Core.Platform;

namespace RedBird.Backends.NativeX64;

/// <summary>Tuning for <see cref="NativeDetourBackend"/>.</summary>
public sealed record NativeDetourOptions
{
    /// <summary>Shared all-defaults instance.</summary>
    public static NativeDetourOptions Default { get; } = new();

    /// <summary>Which schemes may be used, tried in the order they are declared on <see cref="DetourScheme"/>.</summary>
    public DetourScheme AllowedSchemes { get; init; } = DetourScheme.Recommended;

    /// <summary>
    /// Follow a leading <c>jmp</c> to the real function body.
    /// <para>
    /// Import thunks, incremental-link thunks and hot-patch stubs all present as a one-instruction function that jumps elsewhere. 
    /// Hooking the thunk works only for callers that route through it: hooking the body catches everyone.
    /// </para>
    /// </summary>
    public bool FollowJumps { get; init; } = true;

    /// <summary>
    /// Follow a leading <c>call</c> as well. Off by default: a function starting with <c>call</c> is far more likely to be doing real work than to be a thunk, and PolyHook has accumulated
    /// several special cases (routines that read the return address off the stack) that break when this is on.
    /// </summary>
    public bool FollowCalls { get; init; }

    /// <summary>How many jumps to follow before giving up.</summary>
    public int MaxFollowDepth { get; init; } = 5;

    /// <summary>Upper bound on bytes decoded while looking for branches back into the prologue.</summary>
    public int FunctionScanLimit { get; init; } = 16 * 1024;

    /// <summary>
    /// What to do when this engine has already detoured the target.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="HookChainingPolicy.Allow"/>. Layering is normal, a shipped game's <c>Present</c> carries hooks from the overlay, the anti-cheat, the streaming client and a
    /// profiler simultaneously, and refusing it would make this engine unusable in exactly the situations detours exist for.
    /// </remarks>
    public HookChainingPolicy Chaining { get; init; } = HookChainingPolicy.Allow;

    /// <summary>
    /// Refuse to restore the original bytes when the target no longer holds this detour's patch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guards one unique hazard in a hook chain: unhooking out of order. 
    /// If another layer went on top after this one, the bytes at the entry point are now <i>its</i> jump, and writing this detour's saved bytes over them silently unhooks the layer above, which then keeps running, believing it is installed, while calls route straight past it.
    /// </para>
    /// <para>
    /// With this on, the mismatch is reported and the target left alone; the detour stays installed until the layers above it come off. 
    /// Set it false for the old behaviour of restoring regardless.
    /// </para>
    /// </remarks>
    public bool SafeUnhook { get; init; } = true;

    /// <summary>
    /// Suspend peer threads while installing or removing the entry patch and redirect any thread stopped in the displaced prologue to the equivalent trampoline instruction.
    /// </summary>
    /// <remarks>
    /// Enabled by default on Windows and on Linux x86-64 when <c>/proc/self/maps</c> is available. It affects installation/removal latency only, never calls through an installed hook.
    /// Linux uses a packaged native signal/futex companion so an asynchronous signal never enters managed code.
    /// </remarks>
    public bool SuspendThreadsDuringPatch { get; init; } = PlatformInfo.IsWindows || (PlatformInfo.IsLinux && ProcMaps.IsAvailable && RuntimeInformation.ProcessArchitecture == Architecture.X64);

    /// <summary>
    /// How close a jump target has to be to count as a thunk in front of its own body, when neither end belongs to a loaded module.
    /// </summary>
    public int LocalThunkRange { get; init; } = 4096;

    /// <summary>Optional logger factory for per-detour logs.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>The schemes actually worth trying on this platform, in preference order.</summary>
    internal DetourScheme[] ResolveOrder()
    {
        List<DetourScheme> order = [];

        if ((AllowedSchemes & DetourScheme.Indirect) != 0)
            order.Add(DetourScheme.Indirect);

        if ((AllowedSchemes & DetourScheme.Absolute) != 0)
            order.Add(DetourScheme.Absolute);

        // On System V the plain push/ret form scribbles on the caller's red zone, and its red-zone-safe replacement is longer than Absolute
        // so it has nothing left to offer and is skipped unless explicitly requested.
        if ((AllowedSchemes & DetourScheme.PushRet) != 0 && !PlatformInfo.HasRedZone)
            order.Add(DetourScheme.PushRet);

        if ((AllowedSchemes & DetourScheme.PushRetRedZoneSafe) != 0)
            order.Add(DetourScheme.PushRetRedZoneSafe);

        return [.. order];
    }
}
