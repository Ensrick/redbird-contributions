using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RedBird.X86.Assembly.InstructionWalker;

namespace RedBird.X86.Assembly.Switches;

/// <summary>
/// One distinct switch-handler entry point and its conservative linear layout boundary.
/// Several selector values can share the same block.
/// </summary>
public sealed class SwitchCaseBlock
{
    internal SwitchCaseBlock(UInt64 startAddress, UInt64 endAddressExclusive, IReadOnlyList<long> caseValues, bool isDefaultTarget)
    {
        if (startAddress == 0 || startAddress > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(startAddress));
        if (endAddressExclusive <= startAddress || endAddressExclusive > (UInt64)UInt32.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(endAddressExclusive));

        StartAddress = startAddress;
        EndAddressExclusive = endAddressExclusive;
        CaseValues = caseValues ?? throw new ArgumentNullException(nameof(caseValues));
        IsDefaultTarget = isDefaultTarget;
    }

    /// <summary>First instruction in the handler.</summary>
    public UInt64 StartAddress { get; }

    /// <summary>
    /// Exclusive linear-layout boundary: the next distinct handler entry or the detected function end.
    /// </summary>
    public UInt64 EndAddressExclusive { get; }

    /// <summary>Number of bytes in this half-open interval.</summary>
    public UInt64 ByteLength => EndAddressExclusive - StartAddress;

    /// <summary>All normalized-back selector values that dispatch to this entry point.</summary>
    public IReadOnlyList<long> CaseValues { get; }

    /// <summary>True when this entry point is also the bounds-check/default target.</summary>
    public bool IsDefaultTarget { get; }

    /// <summary>True when more than one selector value shares this handler.</summary>
    public bool IsShared => CaseValues.Count > 1;

    /// <summary>Creates an instruction walker capped at this block's linear boundary.</summary>
    public InstructionWalker.InstructionWalker CreateWalker(InstructionWalkerConfig config, ILogger? logger = null)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        UInt64 requestedEnd = config.EndAddressExclusive;
        UInt64 boundedEnd = requestedEnd == 0 || requestedEnd > EndAddressExclusive
            ? EndAddressExclusive
            : requestedEnd;

        if (boundedEnd <= StartAddress)
            throw new ArgumentOutOfRangeException(nameof(config), "The configured end address does not extend beyond this case entry.");

        InstructionWalkerConfig bounded = new()
        {
            Bitness = config.Bitness,
            WalkRules = config.WalkRules,
            MaxReadLength = config.MaxReadLength,
            MaxInstructions = config.MaxInstructions,
            EndAddressExclusive = boundedEnd,
            EndDetection = config.EndDetection,
        };

        return new InstructionWalker.InstructionWalker(StartAddress, bounded, logger);
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"0x{StartAddress:X8}..0x{EndAddressExclusive:X8} ({CaseValues.Count} value(s))";
}
