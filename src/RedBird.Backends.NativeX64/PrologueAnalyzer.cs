using Iced.Intel;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using RedBird.Core.Memory;
using RedBird.Core.Platform;

namespace RedBird.Backends.NativeX64;

/// <summary>
/// Works out where a function really starts, how many bytes of it can be replaced, and whether anything jumps back into the region being replaced.
/// </summary>
internal static unsafe class PrologueAnalyzer
{
    private const int Bitness = 64;

    /// <summary>
    /// Resolves thunks and returns the function's instructions plus a map of which addresses inside it are branch targets.
    /// </summary>
    public static bool TryAnalyze(
        UInt64 address,
        NativeDetourOptions options,
        ILogger logger,
        out UInt64 resolvedAddress,
        out List<Instruction> instructions,
        out Dictionary<UInt64, List<Instruction>> branchSources)
    {
        resolvedAddress = address;
        instructions = [];
        branchSources = [];

        if (options.FollowJumps && !TryFollowThunks(ref resolvedAddress, options, logger))
            return false;

        instructions = Disassemble(resolvedAddress, options.FunctionScanLimit);

        if (instructions.Count == 0)
        {
            logger.LogError("Nothing decodable at 0x{Address:X16}.", resolvedAddress);
            return false;
        }

        branchSources = BuildBranchMap(instructions);
        return true;
    }

    /// <summary>
    /// Walks a chain of unconditional jumps at the entry point down to the real body.
    /// </summary>
    private static bool TryFollowThunks(ref UInt64 address, NativeDetourOptions options, ILogger logger)
    {
        for (int depth = 0; depth < options.MaxFollowDepth; depth++)
        {
            Instruction first = DecodeOne(address);

            if (first.IsInvalid)
            {
                logger.LogError("Nothing decodable at 0x{Address:X16}.", address);
                return false;
            }

            bool isJump = first.FlowControl is FlowControl.UnconditionalBranch or FlowControl.IndirectBranch;
            bool isCall = first.FlowControl is FlowControl.Call or FlowControl.IndirectCall;

            if (!isJump && !(isCall && options.FollowCalls))
                return true;

            // jmp rax / jmp [rax] and friends resolve only at run time.
            if (first.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
            {
                if (first.IsIPRelativeMemoryOperand)
                {
                    // jmp qword ptr [rip+x]. This is both what an import thunk looks like and what this engine's own Indirect patch looks like, so the slot contents decide.
                    UInt64 slot = first.IPRelativeMemoryAddress;
                    UInt64 target = *(UInt64*)slot;

                    if (target == 0)
                    {
                        logger.LogWarning("Import thunk at 0x{Address:X16} is unbound; hooking the thunk itself.", address);
                        return true;
                    }

                    // Indirect: an import thunk always points into a module, so there is no local escape hatch here, our own Indirect patch has exactly this shape.
                    if (!ShouldFollow(address, target, options, logger, allowLocalThunk: false))
                        return true;

                    logger.LogDebug("Followed import thunk 0x{From:X16} -> 0x{To:X16}.", address, target);
                    address = target;
                    continue;
                }

                logger.LogDebug("Entry at 0x{Address:X16} branches through a register; hooking it in place.", address);
                return true;
            }

            UInt64 destination = first.NearBranchTarget;

            if (!ShouldFollow(address, destination, options, logger, allowLocalThunk: true))
                return true;

            logger.LogDebug("Followed thunk 0x{From:X16} -> 0x{To:X16}.", address, destination);
            address = destination;
        }

        logger.LogError("Jump chain at 0x{Address:X16} exceeded {MaxDepth} levels.", address, options.MaxFollowDepth);
        return false;
    }

    /// <summary>
    /// Chooses the instructions to displace: at least <paramref name="minimumBytes"/>, rounded up to an instruction boundary, then widened until nothing branches into the middle of it.
    /// </summary>
    public static bool TrySelectPrologue(
        UInt64 functionAddress,
        List<Instruction> instructions,
        Dictionary<UInt64, List<Instruction>> branchSources,
        int minimumBytes,
        ILogger logger,
        out PrologueSelection? selection)
    {
        selection = null;

        int required = minimumBytes;

        // Widening can pull in another branch target, which can require widening again.
        for (int pass = 0; pass < 16; pass++)
        {
            if (!TryTakeAtLeast(instructions, required, out List<Instruction> taken, out int length))
            {
                logger.LogDebug("0x{Address:X16} has only {Length} usable bytes before its end; {Required} are needed.", functionAddress, length, required);
                return false;
            }

            int widened = WidenForSelfBranches(functionAddress, taken, branchSources, length);

            if (widened <= required)
            {
                selection = new PrologueSelection
                {
                    FunctionAddress = functionAddress,
                    Instructions = taken,
                    Length = length,
                };

                return true;
            }

            logger.LogDebug("Prologue at 0x{Address:X16} widened from {Old} to {New} bytes to cover a branch into it.", functionAddress, required, widened);

            required = widened;
        }

        logger.LogWarning("Prologue selection at 0x{Address:X16} did not converge.", functionAddress);
        return false;
    }

    /// <summary>
    /// Takes whole instructions until at least <paramref name="minimumBytes"/> are covered.
    /// Padding after a <c>ret</c> counts, because it belongs to nobody; anything after that does not.
    /// </summary>
    private static bool TryTakeAtLeast(List<Instruction> instructions, int minimumBytes, out List<Instruction> taken, out int length)
    {
        taken = [];
        length = 0;
        bool sawEnd = false;

        foreach (Instruction instruction in instructions)
        {
            // Once the function has ended, only alignment padding may be consumed, the next real instruction belongs to a different function.
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

    /// <summary>
    /// Returns the number of bytes the displaced region must cover so that every branch targeting an address inside it is itself displaced.
    /// </summary>
    private static int WidenForSelfBranches(UInt64 functionAddress, List<Instruction> prologue, Dictionary<UInt64, List<Instruction>> branchSources, int currentLength)
    {
        UInt64 end = functionAddress + (UInt64)currentLength;
        UInt64 furthest = end;

        foreach (Instruction instruction in prologue)
        {
            if (!branchSources.TryGetValue(instruction.IP, out List<Instruction>? sources))
                continue;

            foreach (Instruction source in sources)
            {
                UInt64 sourceEnd = source.IP + (UInt64)source.Length;

                // A branch from after the region jumps backwards into it; the region has to grow far enough forward to include the branch instruction itself.
                if (sourceEnd > furthest)
                    furthest = sourceEnd;
            }
        }

        return (int)(furthest - functionAddress);
    }

    /// <summary>Maps each branch target inside the function to the instructions that reach it.</summary>
    private static Dictionary<UInt64, List<Instruction>> BuildBranchMap(List<Instruction> instructions)
    {
        Dictionary<UInt64, List<Instruction>> map = [];

        foreach (Instruction instruction in instructions)
        {
            if (instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
                continue;

            UInt64 target = instruction.NearBranchTarget;

            if (!map.TryGetValue(target, out List<Instruction>? sources))
                map[target] = sources = [];

            sources.Add(instruction);
        }

        return map;
    }

    /// <summary>
    /// Decodes the function, stopping at a terminator that no forward branch reaches past.
    /// </summary>
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

            if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            {
                UInt64 target = instruction.NearBranchTarget;
                if (target > furthestTarget && target > address && target < ceiling)
                    furthestTarget = target;
            }

            if (IsFunctionEnd(instruction) && instruction.NextIP > furthestTarget)
                break;
        }

        return instructions;
    }

    /// <summary>
    /// Decides whether a jump at the function's entry point is a thunk worth following, or an existing detour that must be left in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a detail that makes hook layering work. 
    /// A compiler-emitted thunk lands inside a mapped image; a detour's jump lands in a trampoline, a code cave, or a JIT-compiled marshalling stub, none of which belong to a module. 
    /// Following the first is helpful.
    /// Following the second means the new hook is installed on the previous hook's <i>callback</i> rather than on the function, which is exactly what PolyHook2 does, since its
    /// <c>Instruction::getDestination()</c> dereferences indirect jumps and <c>followJmp</c> follows whatever comes back.
    /// </para>
    /// <para>
    /// Leaving it in place instead means the previous hook's jump is displaced into the new trampoline, and the chain composes: the new callback's <c>Original</c> reaches the old callback, whose <c>Original</c> reaches the real function.
    /// </para>
    /// </remarks>
    private static bool ShouldFollow(UInt64 from, UInt64 to, NativeDetourOptions options, ILogger logger, bool allowLocalThunk)
    {
        if (ModuleLocator.TryGetContainingModule(to, out _))
            return true;

        // A direct jump to code a few bytes away is a thunk sitting in front of its own body, generated code does this, and neither end belongs to a module.
        // The exemption is limited to direct jumps on purpose: an indirect jump in anonymous memory is a detour, never a thunk.
        UInt64 distance = to > from ? to - from : from - to;

        if (allowLocalThunk && distance <= (UInt64)options.LocalThunkRange)
            return true;

        logger.LogInformation("0x{From:X16} jumps to 0x{To:X16}, which is not inside any loaded module - treating it as an existing hook and layering on top rather than following it.", from, to);

        return false;
    }

    private static Instruction DecodeOne(UInt64 address)
    {
        MemoryCodeReader reader = new((byte*)address, 16);
        Decoder decoder = Decoder.Create(Bitness, reader, address);
        return decoder.Decode();
    }

    /// <summary>A <c>ret</c>, an unconditional jump out, or an <c>int3</c>.</summary>
    private static bool IsFunctionEnd(in Instruction instruction)
        => instruction.Code == Code.Int3
        || instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch;

    /// <summary>Alignment filler between functions.</summary>
    private static bool IsPadding(in Instruction instruction)
        => instruction.Code == Code.Int3 || instruction.Mnemonic == Mnemonic.Nop;
}
