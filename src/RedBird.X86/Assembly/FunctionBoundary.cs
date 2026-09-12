using Iced.Intel;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using RedBird.Core.Memory;

namespace RedBird.X86.Assembly;

/// <summary>
/// Finds the extent of a function by decoding it.
/// </summary>
public static unsafe class FunctionBoundary
{

    /// <summary>Decodes the function starting at <paramref name="address"/>.</summary>
    public static FunctionDisassembly Disassemble(UInt64 address, FunctionScanOptions? options = null, ILogger? logger = null)
    {
        if (address == 0 || address > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(address), "An x86 function address must fit in 32 bits.");

        options ??= FunctionScanOptions.Default;

        if (options.Bitness != 32)
            throw new ArgumentOutOfRangeException(nameof(options), "RedBird.X86 only decodes 32-bit functions.");
        if (options.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Function scan byte limit must be positive.");
        if (options.MaxInstructions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Function scan instruction limit must be positive.");

        int maximumBytes = options.MaxBytes;
        bool explicitBoundaryLimitsScan = false;
        if (options.EndAddressExclusive != 0)
        {
            if (options.EndAddressExclusive <= address || options.EndAddressExclusive > (UInt64)UInt32.MaxValue + 1)
                throw new ArgumentOutOfRangeException(nameof(options), "The exclusive end address must be above the start and within the x86 address space.");

            UInt64 distance = options.EndAddressExclusive - address;
            if (distance <= (UInt64)maximumBytes)
            {
                maximumBytes = checked((int)distance);
                explicitBoundaryLimitsScan = true;
            }
        }

        MemoryCodeReader reader = new((byte*)address, maximumBytes);
        Decoder decoder = Decoder.Create(options.Bitness, reader, address);

        List<Instruction> instructions = new(256);
        UInt64 limit = address + (UInt64)maximumBytes;
        UInt64 furthestTarget = address;
        int byteCount = 0;
        bool truncated = true;

        while (byteCount < maximumBytes && instructions.Count < options.MaxInstructions)
        {
            Instruction instruction = decoder.Decode();
            if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
                break;

            instructions.Add(instruction);
            byteCount += instruction.Length;

            if (explicitBoundaryLimitsScan && instruction.NextIP >= options.EndAddressExclusive)
            {
                truncated = false;
                break;
            }

            if (options.EndDetection == FunctionEndDetection.FirstInt3)
            {
                if (instruction.Code == Code.Int3)
                {
                    truncated = false;
                    break;
                }

                continue;
            }

            // Track the furthest forward branch target that still lies inside the scan window, so a terminator in the middle of the function does not end the walk while code beyond it is still reachable.
            if (IsNearBranch(instruction))
            {
                UInt64 target = instruction.NearBranchTarget;
                if (target > furthestTarget && target > address && target < limit)
                    furthestTarget = target;
            }

            if (IsTerminator(instruction) && instruction.NextIP > furthestTarget)
            {
                truncated = false;
                break;
            }
        }

        if (explicitBoundaryLimitsScan && byteCount >= maximumBytes)
            truncated = false;

        if (truncated)
        {
            logger?.LogWarning("Disassembly of 0x{Address:X8} stopped at a limit ({ByteCount} bytes, {InstructionCount} instructions) without reaching a function terminator. Results are incomplete.", address, byteCount, instructions.Count);
        }

        if (options.TrimTrailingPadding)
            byteCount -= TrimPadding(instructions);

        return new FunctionDisassembly(instructions, address, byteCount, truncated);
    }

    private static bool IsNearBranch(in Instruction instruction)
        => (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch) &&
           instruction.Op0Kind is (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64);

    private static bool IsTerminator(in Instruction instruction)
    {
        if (instruction.Code == Code.Int3)
            return true;

        return instruction.FlowControl switch
        {
            FlowControl.Return => true,
            FlowControl.UnconditionalBranch => true,
            _ => false,
        };
    }

    /// <summary>Removes trailing alignment padding and returns how many bytes were dropped.</summary>
    private static int TrimPadding(List<Instruction> instructions)
    {
        int removedBytes = 0;

        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            Instruction instruction = instructions[i];
            if (instruction.Code != Code.Int3 && instruction.Mnemonic != Mnemonic.Nop)
                break;

            removedBytes += instruction.Length;
            instructions.RemoveAt(i);
        }

        return removedBytes;
    }
}
