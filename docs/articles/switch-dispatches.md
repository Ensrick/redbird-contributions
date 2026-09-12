---
title: Switch dispatches
description: Resolve x86 and x64 jump tables into selector values, handler addresses, and bounded walkers.
---

# Switch dispatches

Optimized native switches often contain no case-value literal in their handler blocks. 
The value indexes a table instead, sometimes through a second byte table that merges selectors sharing the
same destination. The architecture package's `SwitchDispatchDecoder` reconstructs that relationship
from a supported x86 or x64 dispatcher.

```csharp
DataScanner scanner = DataScanner.Create(region, logger);
scanner.Scan("48 89 5C 24 ?? 48 89 7C 24 ?? 45 33 DB");

if (!scanner.Found ||
    !SwitchDispatchDecoder.TryDecode(
        region,
        scanner.CurrentAddress,
        out SwitchDispatch? dispatch,
        out string? error,
        logger: logger))
{
    throw new InvalidOperationException(error ?? "The function was not found.");
}

SwitchCaseBlock state65 = dispatch.GetCase(0x65);
logger.LogInformation("State 0x65 starts at 0x{Start:X16} and ends before 0x{End:X16}.", state65.StartAddress, state65.EndAddressExclusive);
```

Import `RedBird.X64.Assembly.Switches` or `RedBird.X86.Assembly.Switches` to select the decoder for
the target process. Both packages expose parallel API types and matching entry-encoding values.

`TryGetCase` is available when the value may be out of range. Multiple values can return the same
`SwitchCaseBlock`. Check `CaseValues`, `IsShared`, and `IsDefaultTarget` rather than assuming one
block per source-level case. 

The complete value-to-block mapping is available through `Cases`, and
`Blocks` enumerates distinct targets in virtual-address order.

The supplied `ScanRegion` must contain the dispatcher, mapping/jump tables, default target, and all
resolved handler entry points.

## Bounded walking

`StartAddress` and `EndAddressExclusive` form a half-open linear layout interval. 
Create a walker through the block to apply the detected upper bound automatically:

```csharp
InstructionWalker walker = state65.CreateWalker(new InstructionWalkerConfig
{
    WalkRules =
    [
        new InstructionWalkRule
        {
            Predicate = instruction => instruction.Mnemonic == Mnemonic.Movsx,
            Extractor = InstructionWalkerHelpers.ExtractInstructionAddress,
        },
    ],
}, logger);

while (walker.TryWalk(out ulong instructionAddress))
{
    // The linear walk cannot continue into the next distinct case entry.
}
```

The range is a layout boundary, not a control-flow graph. A case can branch to a shared tail outside its interval, fall through to the next block, or return early. 
The bounded walker deliberately does not follow those branches.

## Recognized x64 layouts

The decoder currently recognizes structural instruction shapes rather than compiler identities:

- a direct selector indexing signed 32-bit offsets relative to a RIP-derived table base, common in position-independent GCC and Clang output;
- a direct selector indexing unsigned 32-bit offsets relative to a RIP-derived image base, common in MSVC PE output;
- the same relative table preceded by a byte remapping table, used by MSVC to merge sparse/default selector values into a dense set of handlers;
- simple register copies between the bounds check and the table index, as commonly introduced by register allocation;
- an optional `sub selector, minimum` immediately before the bounds comparison.

The bounds check must currently be an unsigned `ja` or `jae`, and the table base must be established by a nearby RIP-relative `lea`. 

Decision trees, bit-test switches, absolute-pointer tables, word-sized remapping tables, split dispatchers, and profile-guided hot/cold partitions are rejected.

## Recognized x86 layouts

The x86 decoder recognizes:

- direct absolute pointer tables such as `jmp dword ptr [selector*4 + table]`;
- an absolute table loaded into a register before an indirect jump;
- signed 32-bit relative entries loaded through a statically recoverable base, followed by `add target, base` and `jmp target`;
- optional byte remapping tables;
- simple selector register copies and `sub selector, minimum` normalization.

Static x86 bases can currently come from a nearby immediate `mov` or an absolute `lea`. 
GCC/Clang PIC thunks based on a runtime `call`/`pop`, import-assisted bases, absolute tables whose entries lie
outside the supplied region, and pre-scaled selectors are rejected.

For unusual layouts, set `FunctionEndAddressExclusive` in `SwitchDispatchOptions` when automatic final-block boundary detection cannot prove the containing end.
