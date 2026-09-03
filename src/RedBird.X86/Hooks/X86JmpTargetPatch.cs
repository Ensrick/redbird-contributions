using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Backends.NativeX86;
using RedBird.Core.Memory;
using RedBird.X86.Assembly;

namespace RedBird.X86.Hooks;

/// <summary>Retargets an existing 32-bit unconditional jump and can restore its original target.</summary>
public sealed unsafe class X86JmpTargetPatch : IDisposable
{
    private const int NearJumpSize = 5;
    private const int ShortJumpSize = 2;
    private readonly ILogger _logger;
    private readonly UInt64 _jumpAddress;
    private readonly JmpEncoding _encoding;
    private readonly UInt64 _pointerSlot;
    private byte[] _originalBytes = [];
    private UInt64 _snapshotAddress;
    private bool _applied;
    private bool _disposed;

    public X86JmpTargetPatch(UInt64 jumpAddress, ILogger? logger = null)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
            throw new PlatformNotSupportedException($"{nameof(X86JmpTargetPatch)} requires an x86 process.");
        if (jumpAddress == 0 || jumpAddress > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(jumpAddress));

        _logger = logger ?? NullLogger.Instance;
        _jumpAddress = jumpAddress;
        _encoding = DetectEncoding(jumpAddress);
        if (_encoding == JmpEncoding.IndirectAbsolute &&
            !AssemblyUtil.TryGetIndirectPointerSlot(jumpAddress, out _pointerSlot))
        {
            throw new ArgumentException($"Could not resolve the pointer slot used by the jump at 0x{jumpAddress:X8}.", nameof(jumpAddress));
        }

        OriginalTarget = ReadCurrentTarget();
    }

    public enum JmpEncoding
    {
        NearRelative,
        ShortRelative,
        IndirectAbsolute,
    }

    public UInt64 JmpAddress => _jumpAddress;
    public UInt64 OriginalTarget { get; }
    public UInt64 TrampolineAddress => OriginalTarget;
    public bool IsApplied => _applied;
    public JmpEncoding Encoding => _encoding;
    public UInt64 PointerSlotAddress => _pointerSlot;

    public void Apply(UInt64 newTarget)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(X86JmpTargetPatch));
        if (newTarget > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(newTarget), "An x86 jump target must fit in 32 bits.");

        if (_originalBytes.Length == 0)
            SnapshotOriginal();

        switch (_encoding)
        {
            case JmpEncoding.NearRelative:
                CodePatch.Write(_jumpAddress, X86JumpFactory.BuildDirect(_jumpAddress, newTarget));
                break;
            case JmpEncoding.ShortRelative:
                WriteShortOrPromote(newTarget);
                break;
            case JmpEncoding.IndirectAbsolute:
                CodePatch.Write(_pointerSlot, BitConverter.GetBytes(unchecked((UInt32)newTarget)));
                break;
            default:
                throw new InvalidOperationException("Unknown x86 jump encoding.");
        }

        _applied = true;
        _logger.LogDebug("Retargeted x86 jump at 0x{Address:X8} to 0x{Target:X8}.", _jumpAddress, newTarget);
    }

    public void Undo()
    {
        if (!_applied || _originalBytes.Length == 0)
            return;

        CodePatch.Write(_snapshotAddress, _originalBytes);
        _applied = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Undo();
        _disposed = true;
    }

    private JmpEncoding DetectEncoding(UInt64 address)
    {
        byte opcode = *(byte*)address;
        if (opcode == 0xE9)
            return JmpEncoding.NearRelative;
        if (opcode == 0xEB)
            return JmpEncoding.ShortRelative;
        if (opcode == 0xFF && *((byte*)address + 1) == 0x25)
            return JmpEncoding.IndirectAbsolute;

        if (!AssemblyUtil.TryDecode(address, out Instruction instruction) ||
            instruction.FlowControl is not (FlowControl.UnconditionalBranch or FlowControl.IndirectBranch))
        {
            throw new ArgumentException($"The instruction at 0x{address:X8} is not a supported unconditional x86 jump.", nameof(address));
        }

        throw new ArgumentException($"The jump at 0x{address:X8} uses an unsupported encoding ({instruction.Code}).", nameof(address));
    }

    private UInt64 ReadCurrentTarget()
    {
        byte* pointer = (byte*)_jumpAddress;
        return _encoding switch
        {
            JmpEncoding.NearRelative => unchecked((UInt32)((UInt32)_jumpAddress + NearJumpSize + *(Int32*)(pointer + 1))),
            JmpEncoding.ShortRelative => unchecked((UInt32)((UInt32)_jumpAddress + ShortJumpSize + *(SByte*)(pointer + 1))),
            JmpEncoding.IndirectAbsolute => *(UInt32*)_pointerSlot,
            _ => throw new InvalidOperationException(),
        };
    }

    private void SnapshotOriginal()
    {
        _snapshotAddress = _encoding == JmpEncoding.IndirectAbsolute ? _pointerSlot : _jumpAddress;
        int size = _encoding switch
        {
            JmpEncoding.IndirectAbsolute => sizeof(UInt32),
            JmpEncoding.ShortRelative => NearJumpSize,
            _ => NearJumpSize,
        };

        _originalBytes = new byte[size];
        Marshal.Copy((IntPtr)_snapshotAddress, _originalBytes, 0, size);
    }

    private void WriteShortOrPromote(UInt64 target)
    {
        Int32 shortDisplacement = unchecked((Int32)((UInt32)target - ((UInt32)_jumpAddress + ShortJumpSize)));
        if (shortDisplacement is >= SByte.MinValue and <= SByte.MaxValue)
        {
            CodePatch.Write(_jumpAddress, [0xEB, unchecked((Byte)(SByte)shortDisplacement)]);
            return;
        }

        byte* padding = (byte*)(_jumpAddress + ShortJumpSize);
        for (int i = 0; i < NearJumpSize - ShortJumpSize; i++)
        {
            if (padding[i] != 0xCC && padding[i] != 0x90)
            {
                throw new InvalidOperationException($"Cannot promote the short jump at 0x{_jumpAddress:X8}: the following bytes are not INT3/NOP padding.");
            }
        }

        CodePatch.Write(_jumpAddress, X86JumpFactory.BuildDirect(_jumpAddress, target));
    }
}
