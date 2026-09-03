using System;
using Iced.Intel;
using RedBird.Core.Memory;

namespace RedBird.X64.Assembly;

/// <summary>Single-instruction decoding helpers for x86-64 code.</summary>
public static unsafe class AssemblyUtil
{
    private const int Bitness = 64;
    private const int MaximumInstructionLength = 15;

    /// <summary>Decodes one x64 instruction from live memory.</summary>
    public static bool TryDecode(UInt64 instructionAddress, out Instruction instruction)
    {
        instruction = default;
        if (instructionAddress == 0)
            return false;

        MemoryCodeReader reader = new((byte*)instructionAddress, MaximumInstructionLength);
        Decoder decoder = Decoder.Create(Bitness, reader, instructionAddress);
        instruction = decoder.Decode();
        return decoder.LastError == DecoderError.None && !instruction.IsInvalid;
    }

    /// <summary>Gets the absolute address calculated by any RIP/EIP-relative memory operand.</summary>
    public static bool TryGetAddressFromRipRelativeOperand(UInt64 instructionAddress, out UInt64 targetAddress)
    {
        targetAddress = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            !instruction.IsIPRelativeMemoryOperand)
        {
            return false;
        }

        targetAddress = instruction.IPRelativeMemoryAddress;
        return true;
    }

    /// <summary>Gets a RIP-relative target only when the decoded instruction is a MOV.</summary>
    public static bool TryGetAddressFromRipRelativeMov(UInt64 instructionAddress, out UInt64 retrievedAddress)
    {
        retrievedAddress = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            instruction.Mnemonic != Mnemonic.Mov ||
            !instruction.IsIPRelativeMemoryOperand)
        {
            return false;
        }

        retrievedAddress = instruction.IPRelativeMemoryAddress;
        return true;
    }

    /// <summary>Gets an absolute no-base/no-index memory address when present.</summary>
    public static bool TryGetAbsoluteMemoryAddress(UInt64 instructionAddress, out UInt64 targetAddress, int operand = -1)
    {
        targetAddress = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction))
            return false;

        if (operand >= 0 && (operand >= instruction.OpCount || instruction.GetOpKind(operand) != OpKind.Memory))
            return false;

        if (instruction.MemoryBase != Register.None || instruction.MemoryIndex != Register.None)
            return false;

        targetAddress = instruction.MemoryDisplacement64;
        return true;
    }

    /// <summary>Gets a memory operand's decoded displacement/absolute target value.</summary>
    public static bool TryGetDisplacementFromInstructionMemory(UInt64 instructionAddress, out UInt64 displacement, int operand = 1)
    {
        displacement = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            operand < 0 || operand >= instruction.OpCount ||
            instruction.GetOpKind(operand) != OpKind.Memory)
        {
            return false;
        }

        displacement = instruction.MemoryDisplacement64;
        return true;
    }

    /// <summary>
    /// Returns a pointer to the encoded displacement bytes inside the instruction. The previous implementation accidentally added the displacement's value instead of its byte offset.
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
            instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
        {
            return false;
        }

        targetAddress = instruction.NearBranchTarget;
        return true;
    }

    /// <summary>Gets a direct target matching a flow-control category.</summary>
    public static bool TryGetFlowControlTarget(UInt64 instructionAddress, FlowControl flowControl, out UInt64 targetAddress)
    {
        targetAddress = 0;
        if (!TryDecode(instructionAddress, out Instruction instruction) ||
            instruction.FlowControl != flowControl ||
            instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
        {
            return false;
        }

        targetAddress = instruction.NearBranchTarget;
        return true;
    }

    private static bool TryDecodeWithOffsets(UInt64 instructionAddress, out Instruction instruction, out ConstantOffsets offsets)
    {
        instruction = default;
        offsets = default;
        if (instructionAddress == 0)
            return false;

        MemoryCodeReader reader = new((byte*)instructionAddress, MaximumInstructionLength);
        Decoder decoder = Decoder.Create(Bitness, reader, instructionAddress);
        instruction = decoder.Decode();
        if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
            return false;

        offsets = decoder.GetConstantOffsets(instruction);
        return true;
    }
}
