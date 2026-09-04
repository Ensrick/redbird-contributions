using System;
using System.Collections.Generic;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using RedBird.Core.Memory;

namespace RedBird.Backends.NativeX64;

/// <summary>
/// Relocates a displaced prologue into executable memory and appends a jump back to the rest of the function.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where the port diverges most from PolyHook2, and where most of its complexity disappears.</b> PolyHook allocates its trampoline with <c>new uint8_t[]</c>, ordinary heap 
/// so the distance from the original code is arbitrary. That forces it to carry:
/// </para>
/// <list type="bullet">
///   <item>a hand-built jump table of 8-byte destination holders, sized by guessing five entries and retrying;</item>
///   <item><c>buildRelocationList</c>, classifying each displaced instruction as re-encodable, needing a table entry, or needing translation;</item>
///   <item><c>generateTranslationRoutine</c> and <c>translate_instruction</c>, which rewrite an out-of-range RIP-relative operand into a scratch-register sequence using <c>r15</c>/<c>rax</c>, with a lookup table of which mnemonics must store the scratch register back afterwards.</item>
/// </list>
/// <para>
/// Allocating the trampoline <i>near</i> the function instead removes the premise. 
/// Every relocated displacement stays inside rel32 range, so Iced's <c>BlockEncoder</c> re-encodes the block directly: it widens short branches, and emits its own inline pointer data for anything that needs an indirect jump. 
/// The scratch-register translation machinery has nothing left to do.
/// </para>
/// </remarks>
internal static class TrampolineBuilder
{
    private const int Bitness = 64;

    /// <summary>Slack added on top of the measured size before allocating.</summary>
    private const int SizeSlack = 32;

    /// <summary>Builds the trampoline for <paramref name="prologue"/>.</summary>
    /// <returns>The trampoline address, or <see cref="IntPtr.Zero"/> on failure.</returns>
    public static IntPtr Build(
        PrologueSelection prologue,
        ILogger logger,
        out int trampolineSize,
        out InstructionPointerMap? instructionPointers,
        out string? failureReason)
    {
        trampolineSize = 0;
        instructionPointers = null;
        failureReason = null;

        UInt64 resumeAddress = prologue.FunctionAddress + (UInt64)prologue.Length;

        List<Instruction> body = new(prologue.Instructions.Count + 1);
        body.AddRange(prologue.Instructions);

        // Jump back to whatever follows the displaced bytes. IP stays 0: this instruction never existed in the target, and Iced keys its branch-target map on original IPs, throwing on
        // any duplicate non-zero one.
        body.Add(Instruction.CreateBranch(Code.Jmp_rel32_64, resumeAddress));

        // Measure at a provisional address, then allocate exactly what is needed and encode again at the real one.
        if (!TryEncode(body, prologue.FunctionAddress, logger, out byte[] probe, out _))
        {
            failureReason = "the relocation sizing pass failed";
            return IntPtr.Zero;
        }

        int reservation = probe.Length + SizeSlack;
        IntPtr trampoline = NativeMemoryManager.AllocateStub(prologue.FunctionAddress, reservation);

        if (trampoline == IntPtr.Zero)
        {
            failureReason = NativeMemoryManager.LastAllocationFailure ?? $"no executable memory was available within relative-branch range of 0x{prologue.FunctionAddress:X16}";
            logger.LogError("Could not allocate a trampoline near 0x{Address:X16}: {Reason}", prologue.FunctionAddress, failureReason);

            return IntPtr.Zero;
        }

        if (!TryEncode(body, (UInt64)trampoline, logger, out byte[] code, out uint[] newInstructionOffsets))
        {
            failureReason = $"the displaced instructions could not be relocated to 0x{(UInt64)trampoline:X16}";
            return IntPtr.Zero;
        }

        if (code.Length > reservation)
        {
            failureReason = $"the final trampoline grew from {probe.Length} to {code.Length} bytes";
            logger.LogError("Trampoline for 0x{Address:X16} grew from {Probe} to {Final} bytes between passes.", prologue.FunctionAddress, probe.Length, code.Length);

            return IntPtr.Zero;
        }

        if (!NativeMemoryManager.WriteStub(trampoline, code))
        {
            failureReason = $"the trampoline allocation at 0x{(UInt64)trampoline:X16} was not writable";
            logger.LogError("Could not write the trampoline for 0x{Address:X16}.", prologue.FunctionAddress);
            return IntPtr.Zero;
        }

        trampolineSize = code.Length;
        instructionPointers = CreateInstructionPointerMap(prologue, (UInt64)trampoline, newInstructionOffsets);

        logger.LogDebug("Trampoline for 0x{Address:X16} at 0x{Trampoline:X16} ({Size} bytes, {Count} displaced instructions, resumes at 0x{Resume:X16}).", prologue.FunctionAddress, (UInt64)trampoline, code.Length, prologue.Instructions.Count, resumeAddress);

        return trampoline;
    }

    private static bool TryEncode(List<Instruction> body, UInt64 rip, ILogger logger, out byte[] code, out uint[] newInstructionOffsets)
    {
        GrowableCodeWriter writer = new(body.Count * 8);
        InstructionBlock block = new(writer, body, rip);

        if (!BlockEncoder.TryEncode(Bitness, block, out string? error, out BlockEncoderResult result, BlockEncoderOptions.ReturnNewInstructionOffsets))
        {
            logger.LogError("Relocating the prologue to 0x{Rip:X16} failed: {Error}", rip, error);
            code = [];
            newInstructionOffsets = [];
            return false;
        }

        code = writer.ToArray();
        newInstructionOffsets = result.NewInstructionOffsets;
        return true;
    }

    private static InstructionPointerMap CreateInstructionPointerMap(PrologueSelection prologue, UInt64 trampoline, uint[] newInstructionOffsets)
    {
        int count = prologue.Instructions.Count;
        UInt64[] source = new UInt64[count];
        UInt64[] relocated = new UInt64[count];

        for (int index = 0; index < count; index++)
        {
            source[index] = prologue.Instructions[index].IP;
            uint offset = newInstructionOffsets[index];
            relocated[index] = offset == UInt32.MaxValue ? 0 : trampoline + offset;
        }

        return new InstructionPointerMap(prologue.FunctionAddress, prologue.Length, source, relocated);
    }

    private sealed class GrowableCodeWriter(int capacity) : CodeWriter
    {
        private readonly List<byte> _bytes = new(capacity);

        public override void WriteByte(byte value) => _bytes.Add(value);

        public byte[] ToArray() => [.. _bytes];
    }
}
