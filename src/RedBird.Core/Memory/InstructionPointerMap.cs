using System;

namespace RedBird.Core.Memory;

/// <summary>
/// Maps instruction boundaries in a prologue to their relocated counterparts in a trampoline.
/// </summary>
internal sealed class InstructionPointerMap
{
    public InstructionPointerMap(UInt64 sourceAddress, int sourceLength, UInt64[] sourceInstructionPointers, UInt64[] relocatedInstructionPointers)
    {
        if (sourceAddress == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceAddress));
        if (sourceLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceLength));
        if (sourceInstructionPointers is null)
            throw new ArgumentNullException(nameof(sourceInstructionPointers));
        if (relocatedInstructionPointers is null)
            throw new ArgumentNullException(nameof(relocatedInstructionPointers));
        if (sourceInstructionPointers.Length != relocatedInstructionPointers.Length)
            throw new ArgumentException("Source and relocated instruction maps must have the same length.");

        SourceAddress = sourceAddress;
        SourceLength = sourceLength;
        SourceInstructionPointers = sourceInstructionPointers;
        RelocatedInstructionPointers = relocatedInstructionPointers;
    }

    public UInt64 SourceAddress { get; }
    public int SourceLength { get; }
    public UInt64[] SourceInstructionPointers { get; }
    public UInt64[] RelocatedInstructionPointers { get; }

    public bool Contains(UInt64 instructionPointer)
        => instructionPointer >= SourceAddress &&
           instructionPointer - SourceAddress < (UInt64)SourceLength;

    public bool TryRelocate(UInt64 instructionPointer, out UInt64 relocatedInstructionPointer)
    {
        for (int index = 0; index < SourceInstructionPointers.Length; index++)
        {
            if (SourceInstructionPointers[index] != instructionPointer)
                continue;

            relocatedInstructionPointer = RelocatedInstructionPointers[index];
            return relocatedInstructionPointer != 0;
        }

        relocatedInstructionPointer = 0;
        return false;
    }

    public bool IsInstructionBoundary(UInt64 instructionPointer)
    {
        foreach (UInt64 boundary in SourceInstructionPointers)
        {
            if (boundary == instructionPointer)
                return true;
        }

        return false;
    }
}
