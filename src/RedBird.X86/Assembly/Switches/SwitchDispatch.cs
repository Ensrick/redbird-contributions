using System;
using System.Collections.Generic;

namespace RedBird.X86.Assembly.Switches;

/// <summary>A decoded x86 switch dispatcher and the case blocks reached through it.</summary>
public sealed class SwitchDispatch
{
    private readonly IReadOnlyDictionary<long, SwitchCaseBlock> _casesByValue;

    internal SwitchDispatch(
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
        IReadOnlyList<SwitchCaseBlock> blocks,
        IReadOnlyDictionary<long, SwitchCaseBlock> casesByValue,
        SwitchCaseBlock defaultBlock)
    {
        FunctionAddress = functionAddress;
        DispatchAddress = dispatchAddress;
        FunctionEndAddressExclusive = functionEndAddressExclusive;
        MinimumCaseValue = minimumCaseValue;
        MaximumCaseValue = maximumCaseValue;
        DefaultTargetAddress = defaultTargetAddress;
        TableAddress = tableAddress;
        EntryBaseAddress = entryBaseAddress;
        MappingTableAddress = mappingTableAddress;
        Layout = layout;
        EntryEncoding = entryEncoding;
        Blocks = blocks;
        _casesByValue = casesByValue;
        DefaultBlock = defaultBlock;
    }

    /// <summary>Address from which the dispatcher search began.</summary>
    public UInt64 FunctionAddress { get; }

    /// <summary>Address of the indirect <c>jmp</c> that performs the dispatch.</summary>
    public UInt64 DispatchAddress { get; }

    /// <summary>Detected or caller-supplied exclusive end of the containing function.</summary>
    public UInt64 FunctionEndAddressExclusive { get; }

    /// <summary>Lowest source-level selector value represented by the table.</summary>
    public long MinimumCaseValue { get; }

    /// <summary>Highest source-level selector value represented by the table.</summary>
    public long MaximumCaseValue { get; }

    /// <summary>Target of the dispatcher's out-of-range conditional branch.</summary>
    public UInt64 DefaultTargetAddress { get; }

    /// <summary>Address of the 32-bit jump table.</summary>
    public UInt64 TableAddress { get; }

    /// <summary>Base added to relative entries, or zero for absolute-pointer tables.</summary>
    public UInt64 EntryBaseAddress { get; }

    /// <summary>Address of the byte mapping table, or null for a direct table.</summary>
    public UInt64? MappingTableAddress { get; }

    /// <summary>Whether selectors directly index the table or first pass through a byte map.</summary>
    public SwitchTableLayout Layout { get; }

    /// <summary>How each 32-bit entry becomes a virtual address.</summary>
    public SwitchTableEntryEncoding EntryEncoding { get; }

    /// <summary>Distinct handler blocks, ordered by virtual address.</summary>
    public IReadOnlyList<SwitchCaseBlock> Blocks { get; }

    /// <summary>Selector values and the handler block reached by each value.</summary>
    public IReadOnlyDictionary<long, SwitchCaseBlock> Cases => _casesByValue;

    /// <summary>The block reached by an out-of-range selector.</summary>
    public SwitchCaseBlock DefaultBlock { get; }

    /// <summary>Number of in-range selector values represented by the dispatch table.</summary>
    public int CaseCount => _casesByValue.Count;

    /// <summary>Looks up the handler and boundary associated with a selector value.</summary>
    public bool TryGetCase(long caseValue, out SwitchCaseBlock? block)
        => _casesByValue.TryGetValue(caseValue, out block);

    /// <summary>Returns a case handler or throws when the value is outside the table.</summary>
    public SwitchCaseBlock GetCase(long caseValue)
    {
        if (!_casesByValue.TryGetValue(caseValue, out SwitchCaseBlock? block))
            throw new KeyNotFoundException($"The switch has no table entry for case value {caseValue}.");

        return block;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"switch@0x{DispatchAddress:X8}, cases {MinimumCaseValue}..{MaximumCaseValue}, {Blocks.Count} block(s)";
}
