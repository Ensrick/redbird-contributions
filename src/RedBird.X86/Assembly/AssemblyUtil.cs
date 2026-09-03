using System;
using Iced.Intel;
using RedBird.Core.Memory;

namespace RedBird.X86.Assembly;

/// <summary>Single-instruction decoding helpers for 32-bit x86 code.</summary>
public static unsafe class AssemblyUtil
{
    private const int Bitness = 32;
    private const int MaximumInstructionLength = 15;

    /// <summary>Decodes one x86 instruction from live memory.</summary>
    public static bool TryDecode(UInt64 instructionAddress, out Instruction instruction)
    {
        instruction = default;
        if (instructionAddress == 0 || instructionAddress > UInt32.MaxValue)
            return false;

        MemoryCodeReader reader = new((byte*)instructionAddress, MaximumInstructionLength);
        Decoder decoder = Decoder.Create(Bitness, reader, instructionAddress);
        instruction = decoder.Decode();
        return decoder.LastError == DecoderError.None && !instruction.IsInvalid;
    }

    /// <summary>
    /// Gets the absolute address represented by a direct x86 memory operand such as <c>mov eax,[0x401000]</c> or <c>jmp dword ptr [0x402000]</c>.
    /// </summary>
    public static bool TryGetAbsoluteMemoryAddress(UInt64 instructionAddress, out UInt64 targetAddress, int operand = -1)
    {
        targetAddress = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction))
            return false;

        if (operand >= 0 && (operand >= instruction.OpCount || instruction.GetOpKind(operand) != OpKind.Memory))
            return false;

        if (instruction.MemoryBase != Register.None || instruction.MemoryIndex != Register.None)
            return false;

        targetAddress = instruction.MemoryDisplacement64 & UInt32.MaxValue;
        return true;
    }

    /// <summary>Gets the encoded displacement from a selected memory operand.</summary>
    public static bool TryGetDisplacementFromInstructionMemory(UInt64 instructionAddress, out UInt64 displacement, int operand = 1)
    {
        displacement = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            operand < 0 || operand >= instruction.OpCount ||
            instruction.GetOpKind(operand) != OpKind.Memory)
        {
            return false;
        }

        displacement = instruction.MemoryDisplacement64 & UInt32.MaxValue;
        return true;
    }

    /// <summary>
    /// Returns a pointer to the displacement bytes inside the instruction, not the address represented by those bytes.
    /// </summary>
    public static bool TryGetPointerToDisplacement(UInt64 instructionAddress, out byte* pointerToDisplacement, out int displacementSize)
    {
        pointerToDisplacement = null;
        displacementSize = 0;

        if (!TryDecodeWithOffsets(instructionAddress, out _, out ConstantOffsets offsets) ||
            offsets.DisplacementSize == 0)
        {
            return false;
        }

        pointerToDisplacement = (byte*)(instructionAddress + offsets.DisplacementOffset);
        displacementSize = offsets.DisplacementSize;
        return true;
    }

    /// <summary>Gets a direct conditional/unconditional branch target.</summary>
    public static bool TryGetBranchTarget(UInt64 instructionAddress, out UInt64 targetAddress)
    {
        targetAddress = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            instruction.FlowControl is not (FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch) ||
            instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32))
        {
            return false;
        }

        targetAddress = instruction.NearBranchTarget & UInt32.MaxValue;
        return true;
    }

    /// <summary>Gets a direct target whose flow-control category matches <paramref name="flowControl"/>.</summary>
    public static bool TryGetFlowControlTarget(UInt64 instructionAddress, FlowControl flowControl, out UInt64 targetAddress)
    {
        targetAddress = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            instruction.FlowControl != flowControl ||
            instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32))
        {
            return false;
        }

        targetAddress = instruction.NearBranchTarget & UInt32.MaxValue;
        return true;
    }

    /// <summary>Gets an immediate operand without sign interpretation.</summary>
    public static bool TryGetImmediate(UInt64 instructionAddress, int operand, out UInt64 value)
    {
        value = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            operand < 0 || operand >= instruction.OpCount ||
            !IsImmediate(instruction.GetOpKind(operand)))
        {
            return false;
        }

        value = instruction.GetImmediate(operand);
        return true;
    }

    /// <summary>
    /// Gets the absolute pointer slot used by an indirect x86 call/jump such as <c>jmp dword ptr [0x402000]</c>.
    /// </summary>
    public static bool TryGetIndirectPointerSlot(UInt64 instructionAddress, out UInt64 slotAddress)
    {
        slotAddress = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            instruction.FlowControl is not (FlowControl.IndirectBranch or FlowControl.IndirectCall) ||
            instruction.Op0Kind != OpKind.Memory ||
            instruction.MemoryBase != Register.None ||
            instruction.MemoryIndex != Register.None)
        {
            return false;
        }

        slotAddress = instruction.MemoryDisplacement64 & UInt32.MaxValue;
        return slotAddress != 0;
    }

    private static bool TryDecodeWithOffsets(UInt64 instructionAddress, out Instruction instruction, out ConstantOffsets offsets)
    {
        instruction = default;
        offsets = default;
        if (instructionAddress == 0 || instructionAddress > UInt32.MaxValue)
            return false;

        MemoryCodeReader reader = new((byte*)instructionAddress, MaximumInstructionLength);
        Decoder decoder = Decoder.Create(Bitness, reader, instructionAddress);
        instruction = decoder.Decode();
        if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
            return false;

        offsets = decoder.GetConstantOffsets(instruction);
        return true;
    }

    private static bool IsImmediate(OpKind kind) => kind is
        OpKind.Immediate8 or OpKind.Immediate8_2nd or
        OpKind.Immediate16 or OpKind.Immediate32 or
        OpKind.Immediate8to16 or OpKind.Immediate8to32 or
        OpKind.Immediate32to64;
}
