using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using RedBird.Core.Memory;

namespace RedBird.X64.Assembly.Switches;

/// <summary>
/// Recognises common x64 compiler jump-table dispatchers and resolves their selector values, targets, shared handlers, default target, and conservative linear case boundaries.
/// </summary>
/// <remarks>
/// Supported forms use a RIP-derived base, 32-bit relative table entries, an unsigned upper-bound check, and either a direct selector index or a byte remapping table. 
/// This covers common MSVC image-relative tables and common GCC/Clang signed table-relative dispatchers without relying on compiler-identifying metadata.
/// </remarks>
public static class SwitchDispatchDecoder
{
    private const int Bitness = 64;

    /// <summary>Decodes the first supported switch dispatcher found after <paramref name="functionAddress"/>.</summary>
    public static SwitchDispatch Decode(
        ScanRegion region,
        UInt64 functionAddress,
        SwitchDispatchOptions? options = null,
        ILogger? logger = null)
    {
        if (!TryDecode(region, functionAddress, out SwitchDispatch? dispatch, out string? error, options, logger))
            throw new NotSupportedException(error ?? "No supported x64 switch dispatcher was found.");

        return dispatch;
    }

    /// <summary>Tries to decode the first supported switch dispatcher found after the supplied address.</summary>
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
            if (instruction.Mnemonic != Mnemonic.Jmp || instruction.Op0Kind != OpKind.Register)
                continue;

            if (TryDecodeCandidate(region, functionAddress, instructions, index, options, out dispatch, out string? candidateError))
            {
                logger?.LogDebug("Decoded {Layout} x64 switch at 0x{DispatchAddress:X16}: {CaseCount} selector values, {BlockCount} blocks.",
                    dispatch.Layout,
                    dispatch.DispatchAddress,
                    dispatch.CaseCount,
                    dispatch.Blocks.Count);
                return true;
            }

            lastCandidateError = candidateError;
        }

        error = lastCandidateError ?? $"No supported x64 switch dispatcher was found within {options.MaxSearchBytes} bytes of 0x{functionAddress:X16}.";
        logger?.LogWarning("{SwitchDecodeError}", error);
        return false;
    }

    private static bool TryDecodeCandidate(
        ScanRegion region,
        UInt64 functionAddress,
        IReadOnlyList<Instruction> instructions,
        int jumpIndex,
        SwitchDispatchOptions options,
        out SwitchDispatch? dispatch,
        out string? error)
    {
        dispatch = null;
        error = null;

        Instruction jump = instructions[jumpIndex];
        int jumpRegister = GetGprFamily(jump.Op0Register);
        if (jumpRegister < 0)
            return Fail("The indirect jump does not use a general-purpose register.", out error);

        int addIndex = PreviousMeaningful(instructions, jumpIndex - 1);
        if (addIndex < 0)
            return Fail("The indirect jump has no preceding target-base addition.", out error);

        Instruction add = instructions[addIndex];
        if (add.Mnemonic != Mnemonic.Add ||
            add.Op0Kind != OpKind.Register ||
            add.Op1Kind != OpKind.Register ||
            GetGprFamily(add.Op0Register) != jumpRegister)
        {
            return Fail("The indirect jump is not preceded by a supported relative-target addition.", out error);
        }

        int baseRegister = GetGprFamily(add.Op1Register);
        if (baseRegister < 0)
            return Fail("The jump-table base is not held in a general-purpose register.", out error);

        int tableLoadIndex = PreviousMeaningful(instructions, addIndex - 1);
        if (tableLoadIndex < 0)
            return Fail("The target addition has no preceding table load.", out error);

        Instruction tableLoad = instructions[tableLoadIndex];
        bool signedEntries = tableLoad.Code == Code.Movsxd_r64_rm32;
        bool unsignedEntries = tableLoad.Code == Code.Mov_r32_rm32;

        if ((!signedEntries && !unsignedEntries) ||
            tableLoad.Op0Kind != OpKind.Register ||
            tableLoad.Op1Kind != OpKind.Memory ||
            GetGprFamily(tableLoad.Op0Register) != jumpRegister ||
            GetGprFamily(tableLoad.MemoryBase) != baseRegister ||
            tableLoad.MemoryIndex == Register.None ||
            tableLoad.MemoryIndexScale != 4)
        {
            return Fail("The dispatcher does not use a supported 32-bit relative jump-table load.", out error);
        }

        int tableIndexRegister = GetGprFamily(tableLoad.MemoryIndex);
        int setupIndex = PreviousMeaningful(instructions, tableLoadIndex - 1);
        bool hasByteMap = false;
        Instruction mappingLoad = default;
        int selectorRegister = tableIndexRegister;

        if (setupIndex >= 0)
        {
            Instruction possibleMap = instructions[setupIndex];
            if (IsByteMappingLoad(possibleMap) &&
                possibleMap.Op0Kind == OpKind.Register &&
                possibleMap.Op1Kind == OpKind.Memory &&
                GetGprFamily(possibleMap.Op0Register) == tableIndexRegister &&
                GetGprFamily(possibleMap.MemoryBase) == baseRegister &&
                possibleMap.MemoryIndex != Register.None &&
                possibleMap.MemoryIndexScale == 1)
            {
                hasByteMap = true;
                mappingLoad = possibleMap;
                selectorRegister = GetGprFamily(possibleMap.MemoryIndex);
                setupIndex = PreviousMeaningful(instructions, setupIndex - 1);
            }
        }

        if (selectorRegister < 0)
            return Fail("The table selector is not a general-purpose register.", out error);

        if (!TryFindRipRelativeBase(
                instructions,
                setupIndex,
                baseRegister,
                options.MaximumLookbackInstructions,
                out UInt64 entryBaseAddress))
        {
            return Fail("The relative jump-table base was not established by a nearby RIP-relative LEA.", out error);
        }

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

        UInt64 tableAddress = unchecked(entryBaseAddress + tableLoad.MemoryDisplacement64);
        UInt64? mappingTableAddress = hasByteMap
            ? unchecked(entryBaseAddress + mappingLoad.MemoryDisplacement64)
            : null;

        List<KeyValuePair<long, UInt64>> resolvedCases = new(caseCount);
        for (int selectorIndex = 0; selectorIndex < caseCount; selectorIndex++)
        {
            int tableIndex = selectorIndex;
            if (mappingTableAddress.HasValue)
            {
                if (!TryAddUnsigned(mappingTableAddress.Value, (UInt64)selectorIndex, out UInt64 mappingEntryAddress) ||
                    !TryReadByte(region, mappingEntryAddress, out byte mappedIndex))
                {
                    return Fail($"The byte mapping entry for selector index {selectorIndex} lies outside the scan region.", out error);
                }

                tableIndex = mappedIndex;
            }

            UInt64 tableOffset = (UInt64)tableIndex * sizeof(UInt32);
            if (!TryAddUnsigned(tableAddress, tableOffset, out UInt64 entryAddress) ||
                !TryReadUInt32(region, entryAddress, out UInt32 rawEntry))
            {
                return Fail($"Jump-table entry {tableIndex} lies outside the scan region.", out error);
            }

            if (!TryResolveTarget(entryBaseAddress, rawEntry, signedEntries, out UInt64 targetAddress))
                return Fail($"Jump-table entry {tableIndex} overflows the virtual-address range.", out error);

            if (!Contains(region, targetAddress, 1))
                return Fail($"Jump-table entry {tableIndex} resolves outside the scan region to 0x{targetAddress:X16}.", out error);

            if (minimumCaseValue > Int64.MaxValue - selectorIndex)
                return Fail("The normalized selector values overflow Int64.", out error);

            long caseValue = minimumCaseValue + selectorIndex;
            resolvedCases.Add(new KeyValuePair<long, UInt64>(caseValue, targetAddress));
        }

        if (!Contains(region, defaultTargetAddress, 1))
            return Fail($"The default target 0x{defaultTargetAddress:X16} lies outside the scan region.", out error);

        UInt64 greatestTarget = defaultTargetAddress;
        foreach (KeyValuePair<long, UInt64> resolvedCase in resolvedCases)
            greatestTarget = Math.Max(greatestTarget, resolvedCase.Value);

        UInt64 functionEndAddressExclusive = options.FunctionEndAddressExclusive;
        if (functionEndAddressExclusive == 0)
        {
            if (!TryFindLinearBlockEnd(region, greatestTarget, options, out functionEndAddressExclusive))
                return Fail($"Could not determine the end of the final switch block at 0x{greatestTarget:X16}; provide FunctionEndAddressExclusive explicitly.", out error);
        }

        if (functionEndAddressExclusive <= greatestTarget || !ContainsEnd(region, functionEndAddressExclusive))
            return Fail("The supplied or detected function end does not contain every switch target.", out error);

        dispatch = BuildDispatch(
            functionAddress,
            jump.IP,
            functionEndAddressExclusive,
            minimumCaseValue,
            checked(minimumCaseValue + caseCount - 1),
            defaultTargetAddress,
            tableAddress,
            entryBaseAddress,
            mappingTableAddress,
            hasByteMap ? SwitchTableLayout.ByteMapped : SwitchTableLayout.Direct,
            signedEntries ? SwitchTableEntryEncoding.SignedRelative32 : SwitchTableEntryEncoding.UnsignedRelative32,
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
            return Fail($"The switch search address 0x{address:X16} lies outside the scan region.", out error);

        int byteCount = Math.Min(options.MaxSearchBytes, region.Length - offset);
        if (options.FunctionEndAddressExclusive != 0)
        {
            if (options.FunctionEndAddressExclusive <= address)
                return Fail("FunctionEndAddressExclusive must be greater than the switch search address.", out error);

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
            return Fail($"No x64 instructions could be decoded at 0x{address:X16}.", out error);

        return true;
    }

    private static bool TryFindRipRelativeBase(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        int baseRegister,
        int maximumLookback,
        out UInt64 baseAddress)
    {
        baseAddress = 0;

        int inspected = 0;
        for (int index = startIndex; index >= 0 && inspected < maximumLookback; index--)
        {
            Instruction instruction = instructions[index];
            if (instruction.Mnemonic == Mnemonic.Nop)
                continue;

            inspected++;
            if (instruction.Mnemonic == Mnemonic.Lea &&
                instruction.Op0Kind == OpKind.Register &&
                GetGprFamily(instruction.Op0Register) == baseRegister &&
                instruction.IsIPRelativeMemoryOperand)
            {
                baseAddress = instruction.IPRelativeMemoryAddress;
                return true;
            }
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

        return Fail("No supported unsigned bounds check was found for the jump-table selector.", out error);
    }

    private static bool TryTraceRegisterCopy(
        Instruction instruction,
        int destinationRegister,
        out int sourceRegister)
    {
        sourceRegister = -1;
        if (instruction.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movsx or Mnemonic.Movsxd or Mnemonic.Movzx) ||
            instruction.Op0Kind != OpKind.Register ||
            instruction.Op1Kind != OpKind.Register ||
            GetGprFamily(instruction.Op0Register) != destinationRegister)
        {
            return false;
        }

        sourceRegister = GetGprFamily(instruction.Op1Register);
        return sourceRegister >= 0;
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

    private static bool TryResolveTarget(UInt64 baseAddress, UInt32 rawEntry, bool signed, out UInt64 targetAddress)
    {
        if (!signed)
            return TryAddUnsigned(baseAddress, rawEntry, out targetAddress);

        Int32 displacement = unchecked((Int32)rawEntry);
        if (displacement >= 0)
            return TryAddUnsigned(baseAddress, (UInt32)displacement, out targetAddress);

        UInt64 magnitude = (UInt64)(-(Int64)displacement);
        if (baseAddress < magnitude)
        {
            targetAddress = 0;
            return false;
        }

        targetAddress = baseAddress - magnitude;
        return true;
    }

    private static bool TryAddUnsigned(UInt64 left, UInt64 right, out UInt64 result)
    {
        if (left > UInt64.MaxValue - right)
        {
            result = 0;
            return false;
        }

        result = left + right;
        return true;
    }

    private static bool IsNearBranch(Instruction instruction)
        => instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;

    private static bool IsByteMappingLoad(Instruction instruction)
        => instruction.Code is Code.Movzx_r32_rm8 or Code.Movzx_r64_rm8;

    private static int GetGprFamily(Register register)
    {
        return register switch
        {
            Register.AL or Register.AH or Register.AX or Register.EAX or Register.RAX => 0,
            Register.CL or Register.CH or Register.CX or Register.ECX or Register.RCX => 1,
            Register.DL or Register.DH or Register.DX or Register.EDX or Register.RDX => 2,
            Register.BL or Register.BH or Register.BX or Register.EBX or Register.RBX => 3,
            Register.SPL or Register.SP or Register.ESP or Register.RSP => 4,
            Register.BPL or Register.BP or Register.EBP or Register.RBP => 5,
            Register.SIL or Register.SI or Register.ESI or Register.RSI => 6,
            Register.DIL or Register.DI or Register.EDI or Register.RDI => 7,
            Register.R8L or Register.R8W or Register.R8D or Register.R8 => 8,
            Register.R9L or Register.R9W or Register.R9D or Register.R9 => 9,
            Register.R10L or Register.R10W or Register.R10D or Register.R10 => 10,
            Register.R11L or Register.R11W or Register.R11D or Register.R11 => 11,
            Register.R12L or Register.R12W or Register.R12D or Register.R12 => 12,
            Register.R13L or Register.R13W or Register.R13D or Register.R13 => 13,
            Register.R14L or Register.R14W or Register.R14D or Register.R14 => 14,
            Register.R15L or Register.R15W or Register.R15D or Register.R15 => 15,
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
