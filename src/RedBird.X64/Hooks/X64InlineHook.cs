using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Logging;
using RedBird.Core.Memory;
using RedBird.X64.Extensions;

namespace RedBird.X64.Hooks;

/// <summary>
/// Inline hook: overwrites instructions at the target with an absolute JMP into a generated stub.
/// </summary>
public sealed class X64InlineHook : IHook
{
    private const int Bitness = 64;
    private const int AbsoluteJumpSize = 14; // FF 25 00 00 00 00 | <8-byte absolute target>

    private readonly ILogger _logger;
    private readonly UInt64 _targetAddress;
    private readonly Assembler? _asm;

    /// <summary>Delegates referenced by the stub; must outlive the hook or the GC will collect them.</summary>
    private readonly List<Delegate> _pinnedDelegates = [];

    private Instruction[] _overwrittenInstructions = [];
    private byte[] _originalBytesBackup = [];
    private int _displacedByteCount;

    // Address inside the shared slab where the stub was written. The slab owns its lifetime.
    private IntPtr _stubAddress = IntPtr.Zero;

    /// <summary>Creates a hook for <paramref name="targetAddress"/>.</summary>
    /// <param name="targetAddress">Absolute VA to patch.</param>
    /// <param name="minHookSize">Minimum bytes to displace; raised to 14 if smaller.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="name">Diagnostic name surfaced through <see cref="IHook.Name"/>.</param>
    public X64InlineHook(UInt64 targetAddress, int minHookSize, ILogger? logger = null, string? name = null)
    {
        _logger = logger ?? NullLogger.Instance;
        Name = name ?? $"inline@0x{targetAddress:X16}";

        if (targetAddress == 0)
        {
            // Kept for the legacy HookBuilder "null object" path. Anything that reaches here is inert: State stays Failed and Enable/Generate are no-ops.
            State = HookState.Failed;
            return;
        }

        _targetAddress = targetAddress;
        _asm = new Assembler(Bitness);

        DecodeDisplacedInstructions(Math.Max(minHookSize, AbsoluteJumpSize));
        CaptureOriginalBytes();
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public UInt64 TargetAddress => _targetAddress;

    /// <inheritdoc/>
    public HookState State { get; private set; } = HookState.Created;

    /// <inheritdoc/>
    public bool IsInstalled => State == HookState.Installed;

    /// <summary>Address of the generated stub, or <see cref="IntPtr.Zero"/> before generation.</summary>
    public IntPtr StubAddress => _stubAddress;

    /// <summary>Number of bytes displaced at the target.</summary>
    public int DisplacedByteCount => _displacedByteCount;

    // -------------------------------------------------------------------------
    // Decoding
    // -------------------------------------------------------------------------

    private unsafe void DecodeDisplacedInstructions(int minBytes)
    {
        // Read a little past minBytes so a long instruction straddling the boundary still decodes.
        MemoryCodeReader reader = new((byte*)_targetAddress, minBytes + 16);
        Decoder decoder = Decoder.Create(Bitness, reader, _targetAddress);

        List<Instruction> decoded = [];
        int byteCount = 0;

        while (byteCount < minBytes)
        {
            Instruction instruction = decoder.Decode();
            if (instruction.IsInvalid)
                break;

            decoded.Add(instruction);
            byteCount += instruction.Length;
        }

        _overwrittenInstructions = [.. decoded];
        _displacedByteCount = byteCount;

        if (byteCount < AbsoluteJumpSize)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Only {byteCount} bytes could be decoded at 0x{_targetAddress:X16}, but an absolute JMP needs {AbsoluteJumpSize}. Installing here would overwrite bytes that cannot be restored.");
        }
    }

    /// <summary>
    /// Snapshots the displaced bytes verbatim, so <see cref="Disable"/> restores exactly what was there rather than a re-encoding of what Iced thinks was there.
    /// </summary>
    private void CaptureOriginalBytes()
    {
        if (_displacedByteCount == 0)
            return;

        _originalBytesBackup = new byte[_displacedByteCount];
        Marshal.Copy((IntPtr)_targetAddress, _originalBytesBackup, 0, _displacedByteCount);
    }

    // -------------------------------------------------------------------------
    // Generation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the stub: user body, then an automatic jump back to the instruction following the displaced range. Allocates and writes the stub but does not touch the target, the hook is <see cref="HookState.Prepared"/> afterwards.
    /// </summary>
    public void Generate(InlineHookGenerator generator)
    {
        if (_targetAddress == 0 || _asm is null)
            return;

        UInt64 returnAddress = _targetAddress + (UInt64)_displacedByteCount;

        generator(_asm, _overwrittenInstructions.AsSpan(), returnAddress);
        _asm.AddUnrestrictedJmp(returnAddress);

        Compile();
        State = HookState.Prepared;
    }

    // The stub is carved out of a shared 64 KB slab placed within +/-1.75 GB of the target, so
    // RIP-relative operands in the displaced instructions stay in range when re-emitted.
    private void Compile()
    {
        using MemoryStream stream = new();
        StreamCodeWriter writer = new(stream);

        // 15 bytes is the maximum x86-64 instruction length; the tail covers the trampoline jump.
        int estimatedSize = (_asm!.Instructions.Count * 15) + 64;
        _stubAddress = NativeMemoryManager.AllocateStub(_targetAddress, estimatedSize);

        if (_stubAddress == IntPtr.Zero)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Failed to allocate a stub near 0x{_targetAddress:X16}: no free region within +/-1.75 GB.");
        }

        // Assemble at the stub's final address so Iced encodes RIP-relative displacements correctly.
        if (!_asm.TryAssemble(writer, (UInt64)_stubAddress, out string? error, out _))
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Iced failed to assemble the stub for 0x{_targetAddress:X16}: {error}");
        }

        byte[] stub = stream.ToArray();

        if (stub.Length > estimatedSize)
        {
            // Past this point the stub has overrun into whatever the bump allocator hands out next, so this is an error rather than a warning.
            State = HookState.Failed;
            throw new InvalidOperationException($"Stub for 0x{_targetAddress:X16} encoded to {stub.Length} bytes but only {estimatedSize} were reserved.");
        }

        // The stub lives in the shared slab. On a W^X host that mapping is execute-read, so a direct copy faults; and raising it to write+execute here would leave every other stub on
        // the page writable-executable for good. WriteStub opens a window only if the host needs one, restores the slab's baseline, and flushes the instruction cache.
        if (!NativeMemoryManager.WriteStub(_stubAddress, stub))
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Could not write the stub for 0x{_targetAddress:X16}.");
        }

        _logger.InlineHookStubCompiled(_stubAddress, stub.Length, _targetAddress);
    }

    // -------------------------------------------------------------------------
    // Install / uninstall
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public void Enable()
    {
        if (State is HookState.Installed or HookState.Disposed or HookState.Failed)
            return;

        if (_targetAddress == 0 || _stubAddress == IntPtr.Zero)
            throw new InvalidOperationException($"Hook '{Name}' has no stub; call Generate before Enable.");

        // FF 25 00 00 00 00   JMP QWORD PTR [RIP+0]
        // <8 bytes>           absolute target
        byte[] patch = new byte[_displacedByteCount];
        patch[0] = 0xFF;
        patch[1] = 0x25;

        unsafe
        {
            fixed (byte* p = patch)
            {
                *(UInt64*)(p + 6) = (UInt64)_stubAddress;
            }
        }

        // NOP out whatever remains of the last displaced instruction, in one write rather than a
        // second Marshal.Copy of an Enumerable.Repeat array.
        for (int i = AbsoluteJumpSize; i < patch.Length; i++)
            patch[i] = 0x90;

        // CodePatch restores the page's original protection, falls back to a read-write transition where the host refuses write+execute, and flushes the instruction cache. Doing it by hand
        // here meant this path did none of that on a hardened kernel.
        CodePatch.Write(_targetAddress, patch);

        State = HookState.Installed;
        _logger.InlineHookEnabled(_targetAddress, _stubAddress);
    }

    /// <inheritdoc/>
    public void Disable()
    {
        if (State != HookState.Installed || _targetAddress == 0)
            return;

        RestoreOriginalBytes();

        State = HookState.Disabled;
        _logger.InlineHookDisabled(_targetAddress);
    }

    private void RestoreOriginalBytes()
    {
        if (_originalBytesBackup.Length == 0)
        {
            // Should be unreachable now that the constructor snapshots the bytes, but a partial restore is worse than none, so refuse rather than reassemble a best guess.
            _logger.LogError("No byte snapshot for 0x{TargetAddress:X16}; refusing to restore and leaving the hook installed.", _targetAddress);
            return;
        }

        try
        {
            CodePatch.Write(_targetAddress, _originalBytesBackup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore the original bytes at 0x{TargetAddress:X16}.", _targetAddress);
        }
    }

    // -------------------------------------------------------------------------
    // Delegate lifetime
    // -------------------------------------------------------------------------

    /// <summary>
    /// Roots <paramref name="callback"/> for the lifetime of this hook and returns a stable native function pointer to it.
    /// </summary>
    public IntPtr PinDelegate(Delegate callback)
    {
        _pinnedDelegates.Add(callback);
        return Marshal.GetFunctionPointerForDelegate(callback);
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Restores the target and drops references. The stub itself is intentionally not freed: it is a sub-allocation of a shared slab, and releasing it would unmap memory other live hooks are still executing. 
    /// Reclaiming stubs needs a real free-list in the slab allocator.
    /// </summary>
    public void Dispose()
    {
        if (State == HookState.Disposed)
            return;

        try
        {
            Disable();
        }
        finally
        {
            _pinnedDelegates.Clear();
            _stubAddress = IntPtr.Zero;
            State = HookState.Disposed;
        }
    }
}
