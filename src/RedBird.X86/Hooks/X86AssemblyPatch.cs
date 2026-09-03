using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Memory;

namespace RedBird.X86.Hooks;

/// <summary>
/// A 32-bit in-place assembly patch: assembles a user-supplied body and writes it directly over the instructions at the target address, overwriting them.
/// <para>
/// This is the destructive counterpart to <see cref="X86InlineHook"/>. 
/// An inline hook is non-destructive of behaviour: it displaces a few bytes, jumps into a stub, runs your code, then jumps back and continues the original function. 
/// A patch has no stub and no return path, what you emit simply becomes the code at the target. Use it to NOP out a check, replace a constant, redirect a branch, or splice a short sequence in place without the overhead of a trampoline.
/// </para>
/// <para>
/// When the assembled body is shorter than the instructions it lands on, the patch rounds the overwritten range up to the next whole-instruction boundary and
/// pads the remainder with <c>NOP</c>. It never leaves a truncated instruction behind, which would corrupt the following code.
/// </para>
/// <para>Only usable in a 32-bit process; the constructor rejects any other architecture.</para>
/// </summary>
public sealed class X86AssemblyPatch : IHook
{
    private const int Bitness = 32;
    private const int MaxInstructionLength = 15;
    private const byte Nop = 0x90;

    private readonly ILogger _logger;
    private readonly UInt64 _targetAddress;
    private readonly int _maxByteCount;
    private readonly Assembler? _asm;

    /// <summary>Delegates referenced by the emitted code; must outlive the patch or the GC collects them.</summary>
    private readonly List<Delegate> _pinnedDelegates = [];

    // The exact bytes written to the target: assembled body followed by NOP padding to the instruction boundary. Empty until Generate runs.
    private byte[] _patchBytes = [];

    // The bytes that were at the target before installation, captured verbatim so Disable restores exactly what was there rather than a re-encoding of what Iced thought was there.
    private byte[] _originalBytesBackup = [];

    private int _bodyByteCount;
    private int _overwrittenByteCount;

    /// <summary>Creates a patch for <paramref name="targetAddress"/>.</summary>
    /// <param name="targetAddress">Absolute 32-bit VA to overwrite.</param>
    /// <param name="maxByteCount">
    /// Maximum number of bytes the patch may overwrite, or <c>0</c> for no limit. 
    /// The count is compared against the whole overwritten range, including any NOP padding added to reach an instruction boundary.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="name">Diagnostic name surfaced through <see cref="IHook.Name"/>.</param>
    /// <exception cref="PlatformNotSupportedException">The process is not x86.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxByteCount"/> is negative, or the address does not fit in 32 bits.</exception>
    public X86AssemblyPatch(UInt64 targetAddress, int maxByteCount = 0, ILogger? logger = null, string? name = null)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
            throw new PlatformNotSupportedException($"{nameof(X86AssemblyPatch)} requires an x86 process.");

        if (maxByteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(maxByteCount), "The max size constraint cannot be negative; use 0 for no limit.");

        _logger = logger ?? NullLogger.Instance;
        _maxByteCount = maxByteCount;
        Name = name ?? $"x86-patch@0x{targetAddress:X8}";

        if (targetAddress == 0)
        {
            State = HookState.Failed;
            return;
        }
        if (targetAddress > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(targetAddress), "An x86 patch address must fit in 32 bits.");

        _targetAddress = targetAddress;
        _asm = new Assembler(Bitness);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public UInt64 TargetAddress => _targetAddress;

    /// <inheritdoc/>
    public HookState State { get; private set; } = HookState.Created;

    /// <inheritdoc/>
    public bool IsInstalled => State == HookState.Installed;

    /// <summary>Maximum bytes this patch may overwrite, or <c>0</c> for no limit.</summary>
    public int MaxByteCount => _maxByteCount;

    /// <summary>Bytes the assembled body occupies, before instruction-boundary padding. Valid from <see cref="HookState.Prepared"/> on.</summary>
    public int BodyByteCount => _bodyByteCount;

    /// <summary>Total bytes overwritten at the target, including NOP padding. Valid from <see cref="HookState.Prepared"/> on.</summary>
    public int OverwrittenByteCount => _overwrittenByteCount;

    // -------------------------------------------------------------------------
    // Generation (prepare phase: nothing is written to the target here)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Assembles the patch body, decides how many original bytes it must overwrite and snapshots them, leaving the patch <see cref="HookState.Prepared"/>. 
    /// No byte of the target is modified until <see cref="Enable"/> runs.
    /// </summary>
    /// <param name="generator">Emits the replacement code.</param>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The body is empty, the target could not be decoded, or the overwritten range exceeds
    /// <see cref="MaxByteCount"/>.
    /// </exception>
    public void Generate(AssemblyPatchGenerator generator)
    {
        if (generator is null)
            throw new ArgumentNullException(nameof(generator));

        if (_targetAddress == 0 || _asm is null)
            return;

        // Assemble the body at the target's own address so a near CALL/JMP and other EIP-relative
        // operands encode for where the code will actually execute.
        generator(_asm, _targetAddress);
        byte[] body = Assemble();

        if (body.Length == 0)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"The assembly patch generator for 0x{_targetAddress:X8} emitted no instructions; there is nothing to write.");
        }

        _bodyByteCount = body.Length;
        _overwrittenByteCount = MeasureOverwrittenRange(body.Length);

        if (_maxByteCount > 0 && _overwrittenByteCount > _maxByteCount)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"The patch for 0x{_targetAddress:X8} needs to overwrite {_overwrittenByteCount} bytes (a {body.Length}-byte body rounded up to the next instruction boundary), but the max size constraint is {_maxByteCount}. Raise maxByteCount or shorten the patch.");
        }

        _originalBytesBackup = CodePatch.Read(_targetAddress, _overwrittenByteCount);
        _patchBytes = BuildPatchBytes(body, _overwrittenByteCount);

        State = HookState.Prepared;
        _logger.LogTrace("[{Name}] prepared an in-place patch for 0x{Address:X8}: {Body}-byte body, {Overwritten} bytes overwritten.", Name, _targetAddress, _bodyByteCount, _overwrittenByteCount);
    }

    private byte[] Assemble()
    {
        using MemoryStream stream = new();
        StreamCodeWriter writer = new(stream);

        if (!_asm!.TryAssemble(writer, _targetAddress, out string? error, out _))
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Iced failed to assemble the patch for 0x{_targetAddress:X8}: {error}");
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Returns the smallest number of whole original instructions whose combined length covers
    /// <paramref name="bodyLength"/>, so the overwrite never stops in the middle of an instruction.
    /// </summary>
    private unsafe int MeasureOverwrittenRange(int bodyLength)
    {
        // Read a little past the body so an instruction straddling the boundary still decodes.
        MemoryCodeReader reader = new((byte*)_targetAddress, bodyLength + MaxInstructionLength);
        Decoder decoder = Decoder.Create(Bitness, reader, _targetAddress);

        int byteCount = 0;
        while (byteCount < bodyLength)
        {
            Instruction instruction = decoder.Decode();
            if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
                break;

            byteCount += instruction.Length;
        }

        if (byteCount < bodyLength)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Could not decode {bodyLength} bytes of instructions at 0x{_targetAddress:X8} to establish a safe overwrite boundary. Writing the body alone here could leave a truncated instruction and corrupt the following code.");
        }

        return byteCount;
    }

    private static byte[] BuildPatchBytes(byte[] body, int totalLength)
    {
        if (body.Length == totalLength)
            return body;

        byte[] patch = new byte[totalLength];
        Array.Copy(body, patch, body.Length);
        for (int i = body.Length; i < totalLength; i++)
            patch[i] = Nop;

        return patch;
    }

    // -------------------------------------------------------------------------
    // Install / uninstall
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public void Enable()
    {
        if (State is HookState.Installed or HookState.Disposed or HookState.Failed)
            return;

        if (_targetAddress == 0 || _patchBytes.Length == 0)
            throw new InvalidOperationException($"Patch '{Name}' has no assembled body; call Generate before Enable.");

        CodePatch.Write(_targetAddress, _patchBytes);

        State = HookState.Installed;
        _logger.LogDebug("[{Name}] wrote {Count} bytes over 0x{Address:X8}.", Name, _patchBytes.Length, _targetAddress);
    }

    /// <inheritdoc/>
    public void Disable()
    {
        if (State != HookState.Installed || _targetAddress == 0)
            return;

        RestoreOriginalBytes();

        State = HookState.Disabled;
        _logger.LogDebug("[{Name}] restored the original bytes at 0x{Address:X8}.", Name, _targetAddress);
    }

    private void RestoreOriginalBytes()
    {
        if (_originalBytesBackup.Length == 0)
        {
            _logger.LogError("No byte snapshot for 0x{Address:X8}; refusing to restore and leaving the patch installed.", _targetAddress);
            return;
        }

        try
        {
            CodePatch.Write(_targetAddress, _originalBytesBackup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore the original bytes at 0x{Address:X8}.", _targetAddress);
        }
    }

    // -------------------------------------------------------------------------
    // Delegate lifetime
    // -------------------------------------------------------------------------

    /// <summary>
    /// Roots <paramref name="callback"/> for the lifetime of this patch and returns a stable native function pointer to it, for a body that calls back into managed code.
    /// </summary>
    public IntPtr PinDelegate(Delegate callback)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));

        _pinnedDelegates.Add(callback);
        return Marshal.GetFunctionPointerForDelegate(callback);
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    /// <summary>Restores the target if still installed and drops references.</summary>
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
            _patchBytes = [];
            State = HookState.Disposed;
        }
    }
}
