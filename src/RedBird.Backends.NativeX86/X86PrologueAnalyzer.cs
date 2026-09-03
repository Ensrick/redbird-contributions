using System;
using System.Collections.Generic;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using RedBird.Core.Memory;
using RedBird.Core.Platform;

namespace RedBird.Backends.NativeX86;

/// <summary>Decodes x86 code, follows entry thunks, and selects safe whole-instruction prologues.</summary>
internal static unsafe class X86PrologueAnalyzer
{
    private const int Bitness = 32;

    public static bool TryAnalyze(
        UInt64 address,
        X86DetourOptions options,
        ILogger logger,
        out UInt64 resolvedAddress,
        out List<Instruction> instructions,
        out Dictionary<UInt64, List<Instruction>> branchSources)
    {
        resolvedAddress = address;
        instructions = [];
        branchSources = [];

        if (address > UInt32.MaxValue)
        {
            logger.LogError("0x{Address:X16} is not a valid x86-32 address.", address);
            return false;
        }

        if (options.FollowJumps && !TryFollowThunks(ref resolvedAddress, options, logger))
            return false;

        instructions = Disassemble(resolvedAddress, options.FunctionScanLimit);
        if (instructions.Count == 0)
        {
            logger.LogError("Nothing decodable at 0x{Address:X8}.", resolvedAddress);
            return false;
        }

        branchSources = BuildBranchMap(instructions);
        return true;
    }

    private static bool TryFollowThunks(ref UInt64 address, X86DetourOptions options, ILogger logger)
    {
        for (int depth = 0; depth < options.MaxFollowDepth; depth++)
        {
            Instruction first = DecodeOne(address);
            if (first.IsInvalid)
            {
                logger.LogError("Nothing decodable at 0x{Address:X8}.", address);
                return false;
            }

            bool isJump = first.FlowControl is FlowControl.UnconditionalBranch or FlowControl.IndirectBranch;
            bool isCall = first.FlowControl is FlowControl.Call or FlowControl.IndirectCall;
            if (!isJump && !(isCall && options.FollowCalls))
                return true;

            if (first.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32)
            {
                UInt64 destination = first.NearBranchTarget & UInt32.MaxValue;
                if (!ShouldFollow(address, destination, options, logger, allowLocalThunk: true))
                    return true;

                logger.LogDebug("Followed x86 thunk 0x{From:X8} -> 0x{To:X8}.", address, destination);
                address = destination;
                continue;
            }

            if (TryGetAbsolutePointerSlot(in first, out UInt64 slot))
            {
                UInt32 target = *(UInt32*)slot;
                if (target == 0)
                {
                    logger.LogWarning("Import thunk at 0x{Address:X8} is unbound; hooking the thunk itself.", address);
                    return true;
                }

                if (!ShouldFollow(address, target, options, logger, allowLocalThunk: false))
                    return true;

                logger.LogDebug("Followed x86 import thunk 0x{From:X8} -> 0x{To:X8}.", address, target);
                address = target;
                continue;
            }

            logger.LogDebug("Entry at 0x{Address:X8} branches through a register; hooking it in place.", address);
            return true;
        }

        logger.LogError("Jump chain at 0x{Address:X8} exceeded {MaxDepth} levels.", address, options.MaxFollowDepth);
        return false;
    }

    private static bool TryGetAbsolutePointerSlot(in Instruction instruction, out UInt64 slot)
    {
        slot = 0;
        if (instruction.Op0Kind != OpKind.Memory ||
            instruction.MemoryBase != Register.None ||
            instruction.MemoryIndex != Register.None)
        {
            return false;
        }

        slot = instruction.MemoryDisplacement64 & UInt32.MaxValue;
        return slot != 0;
    }

    public static bool TrySelectPrologue(
        UInt64 functionAddress,
        List<Instruction> instructions,
        Dictionary<UInt64, List<Instruction>> branchSources,
        int minimumBytes,
        ILogger logger,
        out X86PrologueSelection? selection)
    {
        selection = null;
        int required = minimumBytes;

        for (int pass = 0; pass < 16; pass++)
        {
            if (!TryTakeAtLeast(instructions, required, out List<Instruction> taken, out int length))
            {
                logger.LogDebug("0x{Address:X8} has only {Length} usable bytes before its end; {Required} are needed.", functionAddress, length, required);
                return false;
            }

            int widened = WidenForSelfBranches(functionAddress, taken, branchSources, length);
            if (widened <= required)
            {
                selection = new X86PrologueSelection
                {
                    FunctionAddress = functionAddress,
                    Instructions = taken,
                    Length = length,
                };
                return true;
            }

            required = widened;
        }

        logger.LogWarning("Prologue selection at 0x{Address:X8} did not converge.", functionAddress);
        return false;
    }

    private static bool TryTakeAtLeast(List<Instruction> instructions, int minimumBytes, out List<Instruction> taken, out int length)
    {
        taken = [];
        length = 0;
        bool sawEnd = false;

        foreach (Instruction instruction in instructions)
        {
            if (sawEnd && !IsPadding(instruction))
                break;

            taken.Add(instruction);
            length += instruction.Length;

            if (length >= minimumBytes)
                return true;

            if (IsFunctionEnd(instruction))
                sawEnd = true;
        }

        return false;
    }

    private static int WidenForSelfBranches(UInt64 functionAddress, List<Instruction> prologue, Dictionary<UInt64, List<Instruction>> branchSources, int currentLength)
    {
        UInt64 furthest = functionAddress + (UInt64)currentLength;

        foreach (Instruction instruction in prologue)
        {
            if (!branchSources.TryGetValue(instruction.IP, out List<Instruction>? sources))
                continue;

            foreach (Instruction source in sources)
            {
                UInt64 sourceEnd = source.IP + (UInt64)source.Length;
                if (sourceEnd > furthest)
                    furthest = sourceEnd;
            }
        }

        return (int)(furthest - functionAddress);
    }

    private static Dictionary<UInt64, List<Instruction>> BuildBranchMap(List<Instruction> instructions)
    {
        Dictionary<UInt64, List<Instruction>> map = [];
        foreach (Instruction instruction in instructions)
        {
            if (instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32))
                continue;

            UInt64 target = instruction.NearBranchTarget & UInt32.MaxValue;
            if (!map.TryGetValue(target, out List<Instruction>? sources))
                map[target] = sources = [];

            sources.Add(instruction);
        }

        return map;
    }

    private static List<Instruction> Disassemble(UInt64 address, int limit)
    {
        MemoryCodeReader reader = new((byte*)address, limit);
        Decoder decoder = Decoder.Create(Bitness, reader, address);
        List<Instruction> instructions = new(64);
        UInt64 ceiling = address + (UInt64)limit;
        UInt64 furthestTarget = address;
        int consumed = 0;

        while (consumed < limit)
        {
            Instruction instruction = decoder.Decode();
            if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
                break;

            instructions.Add(instruction);
            consumed += instruction.Length;

            if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32)
            {
                UInt64 target = instruction.NearBranchTarget & UInt32.MaxValue;
                if (target > furthestTarget && target > address && target < ceiling)
                    furthestTarget = target;
            }

            if (IsFunctionEnd(instruction) && instruction.NextIP > furthestTarget)
                break;
        }

        return instructions;
    }

    private static bool ShouldFollow(UInt64 from, UInt64 to, X86DetourOptions options, ILogger logger, bool allowLocalThunk)
    {
        if (ModuleLocator.TryGetContainingModule(to, out _))
            return true;

        UInt64 distance = to > from ? to - from : from - to;
        if (allowLocalThunk && distance <= (UInt64)options.LocalThunkRange)
            return true;

        logger.LogInformation("0x{From:X8} jumps to anonymous memory at 0x{To:X8}; treating it as an existing hook.", from, to);
        return false;
    }

    private static Instruction DecodeOne(UInt64 address)
    {
        MemoryCodeReader reader = new((byte*)address, 16);
        Decoder decoder = Decoder.Create(Bitness, reader, address);
        return decoder.Decode();
    }

    private static bool IsFunctionEnd(in Instruction instruction)
        => instruction.Code == Code.Int3 ||
           instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch;

    private static bool IsPadding(in Instruction instruction)
        => instruction.Code == Code.Int3 || instruction.Mnemonic == Mnemonic.Nop;
}
