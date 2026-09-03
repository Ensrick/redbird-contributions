using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Core.Memory;
using RedBird.X64.Assembly;

namespace RedBird.X64.Hooks;

/// <summary>
/// Retargets an existing x64 near, short or RIP-relative indirect jump and restores its original bytes on undo. 
/// Memory protection and instruction-cache handling are delegated to the shared Windows/POSIX code-patching layer.
/// </summary>
public sealed unsafe class X64JmpTargetPatch : IDisposable
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

    public X64JmpTargetPatch(UInt64 jumpAddress, ILogger? logger = null)
    {
        if (jumpAddress == 0)
            throw new ArgumentOutOfRangeException(nameof(jumpAddress));

        _logger = logger ?? NullLogger.Instance;
        _jumpAddress = jumpAddress;
        _encoding = DetectEncoding(jumpAddress);

        if (_encoding == JmpEncoding.IndirectAbsolute &&
            !AssemblyUtil.TryGetAddressFromRipRelativeOperand(jumpAddress, out _pointerSlot))
        {
            throw new ArgumentException($"Could not resolve the pointer slot used by the jump at 0x{jumpAddress:X16}.", nameof(jumpAddress));
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
            throw new ObjectDisposedException(nameof(X64JmpTargetPatch));

        if (_originalBytes.Length == 0)
            SnapshotOriginal();

        switch (_encoding)
        {
            case JmpEncoding.NearRelative:
                CodePatch.Write(_jumpAddress, BuildNearJump(newTarget));
                break;
            case JmpEncoding.ShortRelative:
                WriteShortOrPromote(newTarget);
                break;
            case JmpEncoding.IndirectAbsolute:
                CodePatch.Write(_pointerSlot, BitConverter.GetBytes(newTarget));
                break;
            default:
                throw new InvalidOperationException("Unknown x64 jump encoding.");
        }

        _applied = true;
        _logger.LogDebug("Retargeted x64 jump at 0x{Address:X16} to 0x{Target:X16}.", _jumpAddress, newTarget);
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

    private static JmpEncoding DetectEncoding(UInt64 address)
    {
        byte* pointer = (byte*)address;
        if (pointer[0] == 0xE9)
            return JmpEncoding.NearRelative;
        if (pointer[0] == 0xEB)
            return JmpEncoding.ShortRelative;
        if (pointer[0] == 0xFF && pointer[1] == 0x25)
            return JmpEncoding.IndirectAbsolute;

        if (!AssemblyUtil.TryDecode(address, out Instruction instruction) ||
            instruction.FlowControl is not (FlowControl.UnconditionalBranch or FlowControl.IndirectBranch))
        {
            throw new ArgumentException($"The instruction at 0x{address:X16} is not a supported unconditional x64 jump.", nameof(address));
        }

        throw new ArgumentException($"The jump at 0x{address:X16} uses an unsupported encoding ({instruction.Code}).", nameof(address));
    }

    private UInt64 ReadCurrentTarget()
    {
        byte* pointer = (byte*)_jumpAddress;
        return _encoding switch
        {
            JmpEncoding.NearRelative => unchecked((UInt64)((Int64)_jumpAddress + NearJumpSize + *(Int32*)(pointer + 1))),
            JmpEncoding.ShortRelative => unchecked((UInt64)((Int64)_jumpAddress + ShortJumpSize + *(SByte*)(pointer + 1))),
            JmpEncoding.IndirectAbsolute => *(UInt64*)_pointerSlot,
            _ => throw new InvalidOperationException(),
        };
    }

    private void SnapshotOriginal()
    {
        _snapshotAddress = _encoding == JmpEncoding.IndirectAbsolute ? _pointerSlot : _jumpAddress;
        int size = _encoding switch
        {
            JmpEncoding.IndirectAbsolute => sizeof(UInt64),
            JmpEncoding.ShortRelative => NearJumpSize,
            _ => NearJumpSize,
        };

        _originalBytes = new byte[size];
        Marshal.Copy((IntPtr)_snapshotAddress, _originalBytes, 0, size);
    }

    private byte[] BuildNearJump(UInt64 target)
    {
        Int64 displacement = unchecked((Int64)target - (Int64)(_jumpAddress + NearJumpSize));
        if (displacement < Int32.MinValue || displacement > Int32.MaxValue)
        {
            throw new InvalidOperationException($"Target 0x{target:X16} is outside rel32 range of the jump at 0x{_jumpAddress:X16}.");
        }

        byte[] patch = new byte[NearJumpSize];
        patch[0] = 0xE9;
        fixed (byte* pointer = patch)
            *(Int32*)(pointer + 1) = (Int32)displacement;
        return patch;
    }

    private void WriteShortOrPromote(UInt64 target)
    {
        Int64 shortDisplacement = unchecked((Int64)target - (Int64)(_jumpAddress + ShortJumpSize));
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
                throw new InvalidOperationException($"Cannot promote the short jump at 0x{_jumpAddress:X16}: the following bytes are not INT3/NOP padding.");
            }
        }

        CodePatch.Write(_jumpAddress, BuildNearJump(target));
    }
}
