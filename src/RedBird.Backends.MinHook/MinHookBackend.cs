using System;
using System.Runtime.InteropServices;
using MinHook;
using RedBird.Abstractions.Hooks;

namespace RedBird.Backends.MinHook;

/// <summary>
/// <see cref="IHookBackend"/> implemented on top of MinHook.NET.
/// </summary>
/// <remarks>
/// <para>
/// Register it once during startup, in place of any other engine:
/// </para>
/// <code>
/// HookBackends.Default = MinHookBackend.Instance;
/// </code>
/// <para>
/// <b>What it brings that the others do not.</b>
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Thread suspension around the patch.</b> <c>HookEngine.EnableHook</c> suspends every other thread in the process while the jump goes in. 
///     That is one hazard PolyHook2 does not address, the window where a thread is executing the exact bytes being overwritten.
///     See the caveats below before treating it as settled.
///   </item>
///   <item>
///     <b>Architecture-agnostic.</b> MinHook.NET emits both x86 and x64 patches, so this one package serves <c>RedBird.X86</c> as well.
///   </item>
///   <item>
///     <b>Five-byte entry patch.</b> On x64 it writes <c>E9 rel32</c> to a relay stub held in the trampoline buffer, so it displaces five bytes rather than the six the indirect scheme needs or the fourteen the absolute one does. 
///     It also supports patching into the alignment padding <i>above</i> a function, which reaches prologues nothing else here can.
///   </item>
/// </list>
/// <para>
/// <b>Caveats, in the order they are likely to matter.</b>
/// </para>
/// <list type="bullet">
///   <item>
///     Windows only. MinHook.NET is fully managed, no native asset, unlike PolyHook2.NET, but it calls <c>VirtualProtect</c>, <c>OpenThread</c> and <c>SuspendThread</c> directly.
///   </item>
///   <item>
///     Thread suspension is skipped entirely when a debugger is attached, because suspending everything under a debugger deadlocks. So the protection you get while debugging is not the protection you get in production.
///   </item>
///   <item>
///     Suspension does not relocate the instruction pointer of a thread already stopped inside the prologue being replaced; MinHook.NET's own source flags this. Rare, and fatal when it happens.
///   </item>
///   <item>
///     Hooks cannot be individually removed, only enabled and disabled. Trampoline buffers are released when the whole engine is disposed. <see cref="Dispose"/> on this backend tears down every hook it created.
///   </item>
///   <item>
///     Each hook needs its own callback delegate instance: MinHook indexes callbacks by delegate identity to root them, so reusing one instance across two hooks collides. <see cref="MinHookDetour{TFunction}"/> turns that into a message that says so.
///   </item>
/// </list>
/// </remarks>
public sealed class MinHookBackend : IHookBackend, IDisposable
{
    /// <summary>Shared instance. Owns a single <see cref="HookEngine"/> for the process.</summary>
    public static MinHookBackend Instance { get; } = new();

    private readonly HookEngine _engine;
    private bool _disposed;

    /// <summary>Creates a backend with its own hook engine.</summary>
    /// <param name="refuseJumpPrologues">
    /// Refuse a target whose first instruction is already a near or short jump. See <see cref="RefuseJumpPrologues"/>
    /// </param>
    public MinHookBackend(bool refuseJumpPrologues = true)
    {
        RefuseJumpPrologues = refuseJumpPrologues;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException($"{nameof(MinHookBackend)} needs VirtualProtect and the Win32 thread APIs; this platform is not Windows. Use NativeDetourBackend, which runs anywhere the memory layer does.");
        }

        // One engine per backend, not one per detour: its MemoryAllocator pools trampoline buffers within reach of the targets they serve, and a fresh allocator per hook would give up that locality and leak a heap of near-allocations.
        _engine = new HookEngine();
    }

    /// <inheritdoc/>
    public string Name => "MinHook";

    /// <summary>
    /// Whether to refuse a target that already begins with a jump.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guards a possible oversight in MinHook.NET's trampoline builder. In <c>Trampoline.cs</c> the short and near JMP branches are swapped:
    /// </para>
    /// <code>
    /// if ((inst.Bytes[0] == 0xE9)) { //short JMP
    ///     dest = IntPtr.Add(dest, inst.Operands[0].LvalByte);      // a rel32 read as a rel8
    /// } else {
    ///     dest = IntPtr.Add(dest, inst.Operands[0].LvalSDWord);
    /// }
    /// </code>
    /// <para>
    /// <c>0xEB</c> is the short jump and <c>0xE9</c> the near one, so this reads the low byte of a 32-bit displacement whenever it meets an <c>E9</c>: which is exactly what MinHook writes at a function's entry point. 
    /// The trampoline then jumps to <c>target + 5 + (rel32 &amp; 0xFF)</c>, and layering a second hook returns nonsense or crashes.
    /// </para>
    /// <para>
    /// The alternative to refusing is producing a detour whose <c>Original</c> jumps somewhere arbitrary, which is kinda bad and might be even worse. Set this false once the this is fixed upstream.
    /// </para>
    /// </remarks>
    public bool RefuseJumpPrologues { get; }

    /// <inheritdoc/>
    public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MinHookBackend));

        if (request.TargetAddress == 0)
            throw new ArgumentException("Detour target address is zero.", nameof(request));
        if (request.Callback is null)
            throw new ArgumentException("Detour callback is null.", nameof(request));

        return new MinHookDetour<TFunction>(_engine, request, RefuseJumpPrologues);
    }

    /// <summary>
    /// Disables every hook this backend created and releases the trampoline buffers.
    /// </summary>
    /// <remarks>
    /// This is process-wide for the engine, so disposing the shared <see cref="Instance"/> tears down every MinHook detour regardless of who created it. 
    /// Construct your own <see cref="MinHookBackend"/> if you want a lifetime you control.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _engine.Dispose();
        GC.SuppressFinalize(this);
    }
}
