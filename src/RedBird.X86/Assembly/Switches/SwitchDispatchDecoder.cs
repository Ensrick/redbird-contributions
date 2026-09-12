using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using RedBird.Core.Memory;

namespace RedBird.X86.Assembly.Switches;

/// <summary>
/// Recognises common x86 compiler jump-table dispatchers and resolves their selector values,
/// targets, shared handlers, default target, and conservative linear case boundaries.
/// </summary>
/// <remarks>
/// Supported forms include absolute 32-bit pointer tables and 32-bit relative tables whose base
/// can be recovered from a nearby immediate load or absolute <c>lea</c>. Selectors may index the
/// table directly or pass through a byte remapping table first.
/// </remarks>
public static class SwitchDispatchDecoder
{
    private const int Bitness = 32;

    /// <summary>Decodes the first supported switch dispatcher found after <paramref name="functionAddress"/>.</summary>
    public static SwitchDispatch Decode(
        ScanRegion region,
        UInt64 functionAddress,
        SwitchDispatchOptions? options = null,
        ILogger? logger = null)
    {
        if (!TryDecode(region, functionAddress, out SwitchDispatch? dispatch, out string? error, options, logger))
            throw new NotSupportedException(error ?? "No supported x86 switch dispatcher was found.");

        return dispatch;
    }

    /// <summary>Tries to decode the first supported x86 switch dispatcher after the supplied address.</summary>
    public static bool TryDecode(
        ScanRegion region,
        UInt64 functionAddress,
        out SwitchDispatch? dispatch,
        out string? error,
        SwitchDispatchOptions? options = null,
        ILogger? logger = null)
    {
        if (region is null)
            throw new ArgumentNullException(nameof(region));
        if (functionAddress == 0 || functionAddress > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(functionAddress), "An x86 function address must fit in 32 bits.");

        options ??= new SwitchDispatchOptions();
        ValidateOptions(options);

        dispatch = null;
        error = null;

        if (!TryDecodeInstructions(region, functionAddress, options, out List<Instruction> instructions, out error))
            return false;

        string? lastCandidateError = null;
        for (int index = 0; index < instructions.Count; index++)
        {
            Instruction instruction = instructions[index];
            if (instruction.Mnemonic != Mnemonic.Jmp ||
                instruction.Op0Kind is not (OpKind.Register or OpKind.Memory))
            {
                continue;
            }

            bool decoded = instruction.Op0Kind == OpKind.Memory
                ? TryDecodeMemoryJump(region, functionAddress, instructions, index, options, out dispatch, out lastCandidateError)
                : TryDecodeRegisterJump(region, functionAddress, instructions, index, options, out dispatch, out lastCandidateError);

            if (!decoded)
                continue;

            logger?.LogDebug(
                "Decoded {Layout} x86 switch at 0x{DispatchAddress:X8}: {CaseCount} selector values, {BlockCount} blocks.",
                dispatch!.Layout,
                dispatch.DispatchAddress,
                dispatch.CaseCount,
                dispatch.Blocks.Count);
            return true;
        }

        error = lastCandidateError ?? $"No supported x86 switch dispatcher was found within {options.MaxSearchBytes} bytes of 0x{functionAddress:X8}.";
        logger?.LogWarning("{SwitchDecodeError}", error);
        return false;
    }

    private static bool TryDecodeMemoryJump(
        ScanRegion region,
        UInt64 functionAddress,
        IReadOnlyList<Instruction> instructions,
        int jumpIndex,
        SwitchDispatchOptions options,
        out SwitchDispatch? dispatch,
        out string? error)
    {
        Instruction jump = instructions[jumpIndex];
        dispatch = null;
        error = null;

        if (jump.Op0Kind != OpKind.Memory ||
            jump.MemoryIndex == Register.None ||
            jump.MemoryIndexScale != 4)
        {
            return Fail("The indirect memory jump does not use a selector scaled by four.", out error);
        }

        int selectorRegister = GetGprFamily(jump.MemoryIndex);
        int setupIndex = PreviousMeaningful(instructions, jumpIndex - 1);
        if (selectorRegister < 0 ||
            !TryResolveMemoryAnchor(instructions, setupIndex, jump, options.MaximumLookbackInstructions, out UInt64 tableAddress))
        {
            return Fail("The absolute x86 jump-table address could not be resolved statically.", out error);
        }

        UInt64? mappingTableAddress = null;
        if (TryDecodeByteMap(
                instructions,
                setupIndex,
                selectorRegister,
                options.MaximumLookbackInstructions,
                out int sourceSelectorRegister,
                out UInt64 mappingAddress))
        {
            selectorRegister = sourceSelectorRegister;
            mappingTableAddress = mappingAddress;
            setupIndex = PreviousMeaningful(instructions, setupIndex - 1);
        }

        return TryFinishCandidate(
            region,
            functionAddress,
            jump.IP,
            instructions,
            setupIndex,
            selectorRegister,
            tableAddress,
            entryBaseAddress: 0,
            mappingTableAddress,
            SwitchTableEntryEncoding.AbsolutePointer32,
            options,
            out dispatch,
            out error);
    }

    private static bool TryDecodeRegisterJump(
        ScanRegion region,
        UInt64 functionAddress,
        IReadOnlyList<Instruction> instructions,
        int jumpIndex,
        SwitchDispatchOptions options,
        out SwitchDispatch? dispatch,
        out string? error)
    {
        Instruction jump = instructions[jumpIndex];
        dispatch = null;
        error = null;

        int jumpRegister = GetGprFamily(jump.Op0Register);
        if (jumpRegister < 0)
            return Fail("The indirect jump does not use a general-purpose x86 register.", out error);

        int previousIndex = PreviousMeaningful(instructions, jumpIndex - 1);
        if (previousIndex < 0)
            return Fail("The indirect register jump has no preceding table load.", out error);

        bool relativeEntries = false;
        UInt64 entryBaseAddress = 0;
        int tableLoadIndex = previousIndex;
        Instruction previous = instructions[previousIndex];
        if (previous.Mnemonic == Mnemonic.Add &&
            previous.Op0Kind == OpKind.Register &&
            previous.Op1Kind == OpKind.Register &&
            GetGprFamily(previous.Op0Register) == jumpRegister)
        {
            relativeEntries = true;
            int entryBaseRegister = GetGprFamily(previous.Op1Register);
            tableLoadIndex = PreviousMeaningful(instructions, previousIndex - 1);
            if (entryBaseRegister < 0 || tableLoadIndex < 0 ||
                !TryResolveStaticRegister(
                    instructions,
                    tableLoadIndex - 1,
                    entryBaseRegister,
                    options.MaximumLookbackInstructions,
                    out entryBaseAddress))
            {
                return Fail("The relative entry base is not recoverable from a nearby immediate load or absolute LEA.", out error);
            }
        }

        Instruction tableLoad = instructions[tableLoadIndex];
        if (tableLoad.Code != Code.Mov_r32_rm32 ||
            tableLoad.Op0Kind != OpKind.Register ||
            tableLoad.Op1Kind != OpKind.Memory ||
            GetGprFamily(tableLoad.Op0Register) != jumpRegister ||
            tableLoad.MemoryIndex == Register.None ||
            tableLoad.MemoryIndexScale != 4)
        {
            return Fail("The register dispatcher does not use a supported 32-bit table load.", out error);
        }

        int selectorRegister = GetGprFamily(tableLoad.MemoryIndex);
        int setupIndex = PreviousMeaningful(instructions, tableLoadIndex - 1);
        if (selectorRegister < 0 ||
            !TryResolveMemoryAnchor(instructions, setupIndex, tableLoad, options.MaximumLookbackInstructions, out UInt64 tableAddress))
        {
            return Fail("The x86 jump-table address could not be resolved statically.", out error);
        }

        UInt64? mappingTableAddress = null;
        if (TryDecodeByteMap(
                instructions,
                setupIndex,
                selectorRegister,
                options.MaximumLookbackInstructions,
                out int sourceSelectorRegister,
                out UInt64 mappingAddress))
        {
            selectorRegister = sourceSelectorRegister;
            mappingTableAddress = mappingAddress;
            setupIndex = PreviousMeaningful(instructions, setupIndex - 1);
        }

        return TryFinishCandidate(
            region,
            functionAddress,
            jump.IP,
            instructions,
            setupIndex,
            selectorRegister,
            tableAddress,
            entryBaseAddress,
            mappingTableAddress,
            relativeEntries ? SwitchTableEntryEncoding.SignedRelative32 : SwitchTableEntryEncoding.AbsolutePointer32,
            options,
            out dispatch,
            out error);
    }

    private static bool TryFinishCandidate(
        ScanRegion region,
        UInt64 functionAddress,
        UInt64 dispatchAddress,
        IReadOnlyList<Instruction> instructions,
        int setupIndex,
        int selectorRegister,
        UInt64 tableAddress,
        UInt64 entryBaseAddress,
        UInt64? mappingTableAddress,
        SwitchTableEntryEncoding entryEncoding,
        SwitchDispatchOptions options,
        out SwitchDispatch? dispatch,
        out string? error)
    {
        dispatch = null;
        error = null;

        if (!TryFindBounds(
                instructions,
                setupIndex,
                selectorRegister,
                options.MaximumLookbackInstructions,
                out long minimumCaseValue,
                out int caseCount,
                out UInt64 defaultTargetAddress,
                out error))
        {
            return false;
        }

        if (caseCount > options.MaxCaseCount)
            return Fail($"The dispatcher describes {caseCount} selector values, exceeding the configured limit of {options.MaxCaseCount}.", out error);

        List<KeyValuePair<long, UInt64>> resolvedCases = new(caseCount);
        for (int selectorIndex = 0; selectorIndex < caseCount; selectorIndex++)
        {
            int tableIndex = selectorIndex;
            if (mappingTableAddress.HasValue)
            {
                UInt64 mappingEntryAddress = mappingTableAddress.Value + (UInt64)selectorIndex;
                if (!TryReadByte(region, mappingEntryAddress, out byte mappedIndex))
                    return Fail($"The byte mapping entry for selector index {selectorIndex} lies outside the scan region.", out error);

                tableIndex = mappedIndex;
            }

            UInt64 entryAddress = tableAddress + (UInt64)tableIndex * sizeof(UInt32);
            if (entryAddress > UInt32.MaxValue || !TryReadUInt32(region, entryAddress, out UInt32 rawEntry))
                return Fail($"Jump-table entry {tableIndex} lies outside the scan region.", out error);

            UInt64 targetAddress = entryEncoding == SwitchTableEntryEncoding.AbsolutePointer32
                ? rawEntry
                : unchecked((UInt32)((UInt32)entryBaseAddress + rawEntry));

            if (!Contains(region, targetAddress, 1))
                return Fail($"Jump-table entry {tableIndex} resolves outside the scan region to 0x{targetAddress:X8}.", out error);

            if (minimumCaseValue > Int64.MaxValue - selectorIndex)
                return Fail("The normalized selector values overflow Int64.", out error);

            resolvedCases.Add(new KeyValuePair<long, UInt64>(minimumCaseValue + selectorIndex, targetAddress));
        }

        if (defaultTargetAddress > UInt32.MaxValue || !Contains(region, defaultTargetAddress, 1))
            return Fail($"The default target 0x{defaultTargetAddress:X8} lies outside the scan region.", out error);

        UInt64 greatestTarget = defaultTargetAddress;
        foreach (KeyValuePair<long, UInt64> resolvedCase in resolvedCases)
            greatestTarget = Math.Max(greatestTarget, resolvedCase.Value);

        UInt64 functionEndAddressExclusive = options.FunctionEndAddressExclusive;
        if (functionEndAddressExclusive == 0 &&
            !TryFindLinearBlockEnd(region, greatestTarget, options, out functionEndAddressExclusive))
        {
            return Fail($"Could not determine the end of the final switch block at 0x{greatestTarget:X8}; provide FunctionEndAddressExclusive explicitly.", out error);
        }

        if (functionEndAddressExclusive <= greatestTarget ||
            functionEndAddressExclusive > (UInt64)UInt32.MaxValue + 1 ||
            !ContainsEnd(region, functionEndAddressExclusive))
        {
            return Fail("The supplied or detected function end does not contain every x86 switch target.", out error);
        }

        dispatch = BuildDispatch(
            functionAddress,
            dispatchAddress,
            functionEndAddressExclusive,
            minimumCaseValue,
            checked(minimumCaseValue + caseCount - 1),
            defaultTargetAddress,
            tableAddress,
            entryBaseAddress,
            mappingTableAddress,
            mappingTableAddress.HasValue ? SwitchTableLayout.ByteMapped : SwitchTableLayout.Direct,
            entryEncoding,
            resolvedCases);
        return true;
    }

    private static SwitchDispatch BuildDispatch(
        UInt64 functionAddress,
        UInt64 dispatchAddress,
        UInt64 functionEndAddressExclusive,
        long minimumCaseValue,
        long maximumCaseValue,
        UInt64 defaultTargetAddress,
        UInt64 tableAddress,
        UInt64 entryBaseAddress,
        UInt64? mappingTableAddress,
        SwitchTableLayout layout,
        SwitchTableEntryEncoding entryEncoding,
        IReadOnlyList<KeyValuePair<long, UInt64>> resolvedCases)
    {
        Dictionary<UInt64, List<long>> valuesByTarget = [];
        foreach (KeyValuePair<long, UInt64> resolvedCase in resolvedCases)
        {
            if (!valuesByTarget.TryGetValue(resolvedCase.Value, out List<long>? values))
            {
                values = [];
                valuesByTarget.Add(resolvedCase.Value, values);
            }

            values.Add(resolvedCase.Key);
        }

        if (!valuesByTarget.ContainsKey(defaultTargetAddress))
            valuesByTarget.Add(defaultTargetAddress, []);

        List<UInt64> starts = [.. valuesByTarget.Keys];
        starts.Sort();

        List<SwitchCaseBlock> blocks = new(starts.Count);
        Dictionary<UInt64, SwitchCaseBlock> blocksByTarget = new(starts.Count);
        for (int index = 0; index < starts.Count; index++)
        {
            UInt64 start = starts[index];
            UInt64 end = index + 1 < starts.Count ? starts[index + 1] : functionEndAddressExclusive;
            ReadOnlyCollection<long> values = valuesByTarget[start].AsReadOnly();
            SwitchCaseBlock block = new(start, end, values, start == defaultTargetAddress);
            blocks.Add(block);
            blocksByTarget.Add(start, block);
        }

        Dictionary<long, SwitchCaseBlock> casesByValue = new(resolvedCases.Count);
        foreach (KeyValuePair<long, UInt64> resolvedCase in resolvedCases)
            casesByValue.Add(resolvedCase.Key, blocksByTarget[resolvedCase.Value]);

        return new SwitchDispatch(
            functionAddress,
            dispatchAddress,
            functionEndAddressExclusive,
            minimumCaseValue,
            maximumCaseValue,
            defaultTargetAddress,
            tableAddress,
            entryBaseAddress,
            mappingTableAddress,
            layout,
            entryEncoding,
            blocks.AsReadOnly(),
            new ReadOnlyDictionary<long, SwitchCaseBlock>(casesByValue),
            blocksByTarget[defaultTargetAddress]);
    }

    private static bool TryDecodeByteMap(
        IReadOnlyList<Instruction> instructions,
        int mappingIndex,
        int tableIndexRegister,
        int maximumLookback,
        out int selectorRegister,
        out UInt64 mappingTableAddress)
    {
        selectorRegister = -1;
        mappingTableAddress = 0;
        if (mappingIndex < 0)
            return false;

        Instruction mappingLoad = instructions[mappingIndex];
        if (mappingLoad.Code != Code.Movzx_r32_rm8 ||
            mappingLoad.Op0Kind != OpKind.Register ||
            mappingLoad.Op1Kind != OpKind.Memory ||
            GetGprFamily(mappingLoad.Op0Register) != tableIndexRegister)
        {
            return false;
        }

        if (mappingLoad.MemoryIndex != Register.None && mappingLoad.MemoryIndexScale == 1)
        {
            int candidate = GetGprFamily(mappingLoad.MemoryIndex);
            if (candidate >= 0 && TryResolveMemoryAnchor(
                    instructions,
                    mappingIndex - 1,
                    mappingLoad,
                    maximumLookback,
                    out mappingTableAddress))
            {
                selectorRegister = candidate;
                return true;
            }

            // In 32-bit addressing, [base + index] is symmetric when the scale is one. A
            // compiler may therefore put the selector in the base field and the static table
            // anchor in the index field. Try that assignment too instead of relying on the
            // encoder's arbitrary base/index choice.
            int baseCandidate = GetGprFamily(mappingLoad.MemoryBase);
            int staticIndexRegister = GetGprFamily(mappingLoad.MemoryIndex);
            if (baseCandidate >= 0 && staticIndexRegister >= 0 &&
                TryResolveStaticRegister(
                    instructions,
                    mappingIndex - 1,
                    staticIndexRegister,
                    maximumLookback,
                    out UInt64 staticIndexAddress))
            {
                selectorRegister = baseCandidate;
                mappingTableAddress = unchecked((UInt32)(
                    (UInt32)staticIndexAddress + (UInt32)mappingLoad.MemoryDisplacement64));
                return true;
            }
        }

        if (mappingLoad.MemoryIndex == Register.None && mappingLoad.MemoryBase != Register.None)
        {
            selectorRegister = GetGprFamily(mappingLoad.MemoryBase);
            mappingTableAddress = (UInt32)mappingLoad.MemoryDisplacement64;
            return selectorRegister >= 0;
        }

        return false;
    }

    private static bool TryResolveMemoryAnchor(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        Instruction memoryInstruction,
        int maximumLookback,
        out UInt64 address)
    {
        UInt32 displacement = (UInt32)memoryInstruction.MemoryDisplacement64;
        if (memoryInstruction.MemoryBase == Register.None)
        {
            address = displacement;
            return true;
        }

        int baseRegister = GetGprFamily(memoryInstruction.MemoryBase);
        if (baseRegister < 0 ||
            !TryResolveStaticRegister(instructions, startIndex, baseRegister, maximumLookback, out UInt64 baseAddress))
        {
            address = 0;
            return false;
        }

        address = unchecked((UInt32)((UInt32)baseAddress + displacement));
        return true;
    }

    private static bool TryResolveStaticRegister(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        int register,
        int maximumLookback,
        out UInt64 value)
    {
        value = 0;
        int inspected = 0;
        for (int index = startIndex; index >= 0 && inspected < maximumLookback; index--)
        {
            Instruction instruction = instructions[index];
            if (instruction.Mnemonic == Mnemonic.Nop)
                continue;

            inspected++;
            if (instruction.Op0Kind != OpKind.Register || GetGprFamily(instruction.Op0Register) != register)
                continue;

            if (instruction.Mnemonic == Mnemonic.Mov &&
                TryGetUnsignedImmediate(instruction, 1, out UInt64 immediate) &&
                immediate <= UInt32.MaxValue)
            {
                value = immediate;
                return true;
            }

            if (instruction.Mnemonic == Mnemonic.Lea &&
                instruction.Op1Kind == OpKind.Memory &&
                instruction.MemoryBase == Register.None &&
                instruction.MemoryIndex == Register.None)
            {
                value = (UInt32)instruction.MemoryDisplacement64;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryFindBounds(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        int selectorRegister,
        int maximumLookback,
        out long minimumCaseValue,
        out int caseCount,
        out UInt64 defaultTargetAddress,
        out string? error)
    {
        minimumCaseValue = 0;
        caseCount = 0;
        defaultTargetAddress = 0;
        error = null;

        int currentSelectorRegister = selectorRegister;
        int inspected = 0;
        for (int branchIndex = startIndex; branchIndex >= 0 && inspected < maximumLookback; branchIndex--)
        {
            Instruction branch = instructions[branchIndex];
            if (branch.Mnemonic == Mnemonic.Nop)
                continue;

            inspected++;
            if (TryTraceRegisterCopy(branch, currentSelectorRegister, out int sourceRegister))
            {
                currentSelectorRegister = sourceRegister;
                continue;
            }

            if (branch.Mnemonic is not (Mnemonic.Ja or Mnemonic.Jae) || !IsNearBranch(branch))
                continue;

            int compareIndex = PreviousMeaningful(instructions, branchIndex - 1);
            if (compareIndex < 0)
                continue;

            Instruction compare = instructions[compareIndex];
            if (compare.Mnemonic != Mnemonic.Cmp ||
                compare.Op0Kind != OpKind.Register ||
                GetGprFamily(compare.Op0Register) != currentSelectorRegister ||
                !TryGetUnsignedImmediate(compare, 1, out UInt64 bound))
            {
                continue;
            }

            if (branch.Mnemonic == Mnemonic.Ja && bound == UInt64.MaxValue)
                return Fail("The switch upper bound overflows its selector count.", out error);

            UInt64 count = branch.Mnemonic == Mnemonic.Jae ? bound : bound + 1;
            if (count == 0 || count > Int32.MaxValue)
                return Fail("The switch bounds produce an unsupported selector count.", out error);

            int normalizationIndex = PreviousMeaningful(instructions, compareIndex - 1);
            if (normalizationIndex >= 0)
            {
                Instruction normalization = instructions[normalizationIndex];
                if (normalization.Mnemonic == Mnemonic.Sub &&
                    normalization.Op0Kind == OpKind.Register &&
                    GetGprFamily(normalization.Op0Register) == currentSelectorRegister &&
                    TryGetUnsignedImmediate(normalization, 1, out UInt64 minimum) &&
                    minimum <= Int64.MaxValue)
                {
                    minimumCaseValue = (long)minimum;
                }
            }

            caseCount = (int)count;
            defaultTargetAddress = branch.NearBranchTarget;
            return true;
        }

        return Fail("No supported unsigned bounds check was found for the x86 jump-table selector.", out error);
    }

    private static bool TryTraceRegisterCopy(Instruction instruction, int destinationRegister, out int sourceRegister)
    {
        sourceRegister = -1;
        if (instruction.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movsx or Mnemonic.Movzx) ||
            instruction.Op0Kind != OpKind.Register ||
            instruction.Op1Kind != OpKind.Register ||
            GetGprFamily(instruction.Op0Register) != destinationRegister)
        {
            return false;
        }

        sourceRegister = GetGprFamily(instruction.Op1Register);
        return sourceRegister >= 0;
    }

    private static bool TryDecodeInstructions(
        ScanRegion region,
        UInt64 address,
        SwitchDispatchOptions options,
        out List<Instruction> instructions,
        out string? error)
    {
        instructions = [];
        error = null;

        if (!TryGetOffset(region, address, 1, out int offset))
            return Fail($"The x86 switch search address 0x{address:X8} lies outside the scan region.", out error);

        int byteCount = Math.Min(options.MaxSearchBytes, region.Length - offset);
        if (options.FunctionEndAddressExclusive != 0)
        {
            if (options.FunctionEndAddressExclusive <= address ||
                options.FunctionEndAddressExclusive > (UInt64)UInt32.MaxValue + 1)
            {
                return Fail("FunctionEndAddressExclusive must be above the search address and within the x86 address space.", out error);
            }

            UInt64 distance = options.FunctionEndAddressExclusive - address;
            if (distance < (UInt64)byteCount)
                byteCount = checked((int)distance);
        }

        byte[] bytes = region.Span.Slice(offset, byteCount).ToArray();
        Decoder decoder = Decoder.Create(Bitness, new ByteArrayCodeReader(bytes), address);
        int decodedBytes = 0;
        while (decodedBytes < byteCount && instructions.Count < options.MaxInstructions)
        {
            Instruction instruction = decoder.Decode();
            if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
                break;

            instructions.Add(instruction);
            decodedBytes += instruction.Length;
            if (instruction.Code == Code.Int3)
                break;
        }

        if (instructions.Count == 0)
            return Fail($"No x86 instructions could be decoded at 0x{address:X8}.", out error);

        return true;
    }

    private static bool TryFindLinearBlockEnd(
        ScanRegion region,
        UInt64 startAddress,
        SwitchDispatchOptions options,
        out UInt64 endAddressExclusive)
    {
        endAddressExclusive = 0;
        if (!TryGetOffset(region, startAddress, 1, out int offset))
            return false;

        int byteCount = Math.Min(options.MaxBoundaryBytes, region.Length - offset);
        byte[] bytes = region.Span.Slice(offset, byteCount).ToArray();
        Decoder decoder = Decoder.Create(Bitness, new ByteArrayCodeReader(bytes), startAddress);
        UInt64 furthestForwardTarget = startAddress;
        int decodedBytes = 0;
        int instructionCount = 0;

        while (decodedBytes < byteCount && instructionCount < options.MaxBoundaryInstructions)
        {
            Instruction instruction = decoder.Decode();
            if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
                return false;

            decodedBytes += instruction.Length;
            instructionCount++;

            if ((instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch) &&
                IsNearBranch(instruction))
            {
                UInt64 target = instruction.NearBranchTarget;
                UInt64 limit = startAddress + (UInt64)byteCount;
                if (target > furthestForwardTarget && target < limit)
                    furthestForwardTarget = target;
            }

            if (instruction.Code == Code.Int3 && instruction.IP >= furthestForwardTarget)
            {
                endAddressExclusive = instruction.IP;
                return endAddressExclusive > startAddress;
            }

            if ((instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch) &&
                instruction.NextIP > furthestForwardTarget)
            {
                endAddressExclusive = instruction.NextIP;
                return true;
            }
        }

        return false;
    }

    private static int PreviousMeaningful(IReadOnlyList<Instruction> instructions, int index)
    {
        while (index >= 0 && instructions[index].Mnemonic == Mnemonic.Nop)
            index--;

        return index;
    }

    private static bool TryGetUnsignedImmediate(Instruction instruction, int operand, out UInt64 value)
    {
        value = 0;
        if (operand < 0 || operand >= instruction.OpCount)
            return false;

        OpKind kind = instruction.GetOpKind(operand);
        if (kind is not (OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64 or
            OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate32to64))
        {
            return false;
        }

        value = instruction.GetImmediate(operand);
        return true;
    }

    private static bool TryReadByte(ScanRegion region, UInt64 address, out byte value)
    {
        value = 0;
        if (!TryGetOffset(region, address, 1, out int offset))
            return false;

        value = region.Span[offset];
        return true;
    }

    private static bool TryReadUInt32(ScanRegion region, UInt64 address, out UInt32 value)
    {
        value = 0;
        if (!TryGetOffset(region, address, sizeof(UInt32), out int offset))
            return false;

        ReadOnlySpan<byte> bytes = region.Span.Slice(offset, sizeof(UInt32));
        value = (UInt32)(bytes[0] |
            (bytes[1] << 8) |
            (bytes[2] << 16) |
            (bytes[3] << 24));
        return true;
    }

    private static bool TryGetOffset(ScanRegion region, UInt64 address, int length, out int offset)
    {
        offset = 0;
        if (length < 0 || address < region.BaseAddress)
            return false;

        UInt64 relative = address - region.BaseAddress;
        if (relative > Int32.MaxValue || relative + (UInt64)length > (UInt64)region.Length)
            return false;

        offset = (int)relative;
        return true;
    }

    private static bool Contains(ScanRegion region, UInt64 address, int length)
        => TryGetOffset(region, address, length, out _);

    private static bool ContainsEnd(ScanRegion region, UInt64 endAddressExclusive)
        => endAddressExclusive >= region.BaseAddress &&
           endAddressExclusive - region.BaseAddress <= (UInt64)region.Length;

    private static bool IsNearBranch(Instruction instruction)
        => instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;

    private static int GetGprFamily(Register register)
    {
        return register switch
        {
            Register.AL or Register.AH or Register.AX or Register.EAX => 0,
            Register.CL or Register.CH or Register.CX or Register.ECX => 1,
            Register.DL or Register.DH or Register.DX or Register.EDX => 2,
            Register.BL or Register.BH or Register.BX or Register.EBX => 3,
            Register.SP or Register.ESP => 4,
            Register.BP or Register.EBP => 5,
            Register.SI or Register.ESI => 6,
            Register.DI or Register.EDI => 7,
            _ => -1,
        };
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static void ValidateOptions(SwitchDispatchOptions options)
    {
        if (options.MaxSearchBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSearchBytes must be positive.");
        if (options.MaxInstructions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxInstructions must be positive.");
        if (options.MaxCaseCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxCaseCount must be positive.");
        if (options.MaxBoundaryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBoundaryBytes must be positive.");
        if (options.MaxBoundaryInstructions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBoundaryInstructions must be positive.");
        if (options.MaximumLookbackInstructions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumLookbackInstructions must be positive.");
    }
}
