using Iced.Intel;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using RedBird.Core.Memory;

namespace RedBird.X64.Assembly;

/// <summary>
/// Finds the extent of a function by decoding it.
/// </summary>
public static unsafe class FunctionBoundary
{

    /// <summary>Decodes the function starting at <paramref name="address"/>.</summary>
    public static FunctionDisassembly Disassemble(UInt64 address, FunctionScanOptions? options = null, ILogger? logger = null)
    {
        if (address == 0)
            throw new ArgumentOutOfRangeException(nameof(address));

        options ??= FunctionScanOptions.Default;

        if (options.Bitness != 64)
            throw new ArgumentOutOfRangeException(nameof(options), "RedBird.X64 only decodes 64-bit functions.");
        if (options.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Function scan byte limit must be positive.");
        if (options.MaxInstructions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Function scan instruction limit must be positive.");

        MemoryCodeReader reader = new((byte*)address, options.MaxBytes);
        Decoder decoder = Decoder.Create(options.Bitness, reader, address);

        List<Instruction> instructions = new(256);
        UInt64 limit = address + (UInt64)options.MaxBytes;
        UInt64 furthestTarget = address;
        int byteCount = 0;
        bool truncated = true;

        while (byteCount < options.MaxBytes && instructions.Count < options.MaxInstructions)
        {
            Instruction instruction = decoder.Decode();
            if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
                break;

            instructions.Add(instruction);
            byteCount += instruction.Length;

            if (options.EndDetection == FunctionEndDetection.FirstInt3)
            {
                if (instruction.Code == Code.Int3)
                {
                    truncated = false;
                    break;
                }

                continue;
            }

            // Track the furthest forward branch target that still lies inside the scan window, so a terminator in the middle of the function does not end the walk while code beyond  it is still reachable.
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

        if (truncated)
        {
            logger?.LogWarning("Disassembly of 0x{Address:X16} stopped at a limit ({ByteCount} bytes, {InstructionCount} instructions) without reaching a function terminator. Results are incomplete.", address, byteCount, instructions.Count);
        }

        if (options.TrimTrailingPadding)
            byteCount -= TrimPadding(instructions);

        return new FunctionDisassembly(instructions, address, byteCount, truncated);
    }

    private static bool IsNearBranch(in Instruction instruction) => instruction.Op0Kind switch
    {
        OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 => true,
        _ => false,
    };

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
