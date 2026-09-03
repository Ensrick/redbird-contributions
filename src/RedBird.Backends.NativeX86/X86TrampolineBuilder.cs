using System;
using System.Collections.Generic;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using RedBird.Core.Memory;

namespace RedBird.Backends.NativeX86;

/// <summary>Relocates an x86 prologue and appends a branch to the first undisplaced byte.</summary>
internal static class X86TrampolineBuilder
{
    private const int Bitness = 32;
    private const int SizeSlack = 32;

    public static IntPtr Build(X86PrologueSelection prologue, ILogger logger, out int trampolineSize, out InstructionPointerMap? instructionPointers)
    {
        trampolineSize = 0;
        instructionPointers = null;
        UInt64 resumeAddress = (prologue.FunctionAddress + (UInt64)prologue.Length) & UInt32.MaxValue;

        List<Instruction> body = new(prologue.Instructions.Count + 1);
        body.AddRange(prologue.Instructions);
        body.Add(Instruction.CreateBranch(Code.Jmp_rel32_32, resumeAddress));

        if (!TryEncode(body, prologue.FunctionAddress, logger, out byte[] probe, out _))
            return IntPtr.Zero;

        int reservation = probe.Length + SizeSlack;
        IntPtr trampoline = NativeMemoryManager.AllocateStub(reservation);
        if (trampoline == IntPtr.Zero)
        {
            logger.LogError("No executable memory is available for an x86 trampoline at 0x{Address:X8}.", prologue.FunctionAddress);
            return IntPtr.Zero;
        }

        UInt64 trampolineAddress = unchecked((UInt32)trampoline.ToInt32());
        if (!TryEncode(body, trampolineAddress, logger, out byte[] code, out uint[] newInstructionOffsets))
            return IntPtr.Zero;

        if (code.Length > reservation)
        {
            logger.LogError("x86 trampoline for 0x{Address:X8} grew from {Probe} to {Final} bytes between passes.", prologue.FunctionAddress, probe.Length, code.Length);
            return IntPtr.Zero;
        }

        if (!NativeMemoryManager.WriteStub(trampoline, code))
        {
            logger.LogError("Could not write the x86 trampoline for 0x{Address:X8}.", prologue.FunctionAddress);
            return IntPtr.Zero;
        }

        trampolineSize = code.Length;
        instructionPointers = CreateInstructionPointerMap(prologue, trampolineAddress, newInstructionOffsets);
        logger.LogDebug("x86 trampoline for 0x{Address:X8} at 0x{Trampoline:X8} ({Size} bytes, resumes at 0x{Resume:X8}).", prologue.FunctionAddress, trampolineAddress, code.Length, resumeAddress);
        return trampoline;
    }

    private static bool TryEncode(List<Instruction> body, UInt64 instructionPointer, ILogger logger, out byte[] code, out uint[] newInstructionOffsets)
    {
        GrowableCodeWriter writer = new(body.Count * 8);
        InstructionBlock block = new(writer, body, instructionPointer);
        if (!BlockEncoder.TryEncode(Bitness, block, out string? error, out BlockEncoderResult result, BlockEncoderOptions.ReturnNewInstructionOffsets))
        {
            logger.LogError("Relocating the x86 prologue to 0x{Address:X8} failed: {Error}", instructionPointer, error);
            code = [];
            newInstructionOffsets = [];
            return false;
        }

        code = writer.ToArray();
        newInstructionOffsets = result.NewInstructionOffsets;
        return true;
    }

    private static InstructionPointerMap CreateInstructionPointerMap(X86PrologueSelection prologue, UInt64 trampoline, uint[] newInstructionOffsets)
    {
        int count = prologue.Instructions.Count;
        UInt64[] source = new UInt64[count];
        UInt64[] relocated = new UInt64[count];

        for (int index = 0; index < count; index++)
        {
            source[index] = prologue.Instructions[index].IP & UInt32.MaxValue;
            uint offset = newInstructionOffsets[index];
            relocated[index] = offset == UInt32.MaxValue ? 0 : (trampoline + offset) & UInt32.MaxValue;
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
