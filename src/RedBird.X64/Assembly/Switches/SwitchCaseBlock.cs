using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RedBird.X64.Assembly.InstructionWalker;

namespace RedBird.X64.Assembly.Switches;

/// <summary>
/// One distinct switch-handler entry point and its conservative linear layout boundary.
/// Several selector values can share the same block.
/// </summary>
public sealed class SwitchCaseBlock
{
    internal SwitchCaseBlock(UInt64 startAddress, UInt64 endAddressExclusive, IReadOnlyList<long> caseValues, bool isDefaultTarget)
    {
        if (startAddress == 0)
            throw new ArgumentOutOfRangeException(nameof(startAddress));
        if (endAddressExclusive <= startAddress)
            throw new ArgumentOutOfRangeException(nameof(endAddressExclusive));

        StartAddress = startAddress;
        EndAddressExclusive = endAddressExclusive;
        CaseValues = caseValues ?? throw new ArgumentNullException(nameof(caseValues));
        IsDefaultTarget = isDefaultTarget;
    }

    /// <summary>First instruction in the handler.</summary>
    public UInt64 StartAddress { get; }

    /// <summary>
    /// Exclusive linear-layout boundary. This is the next distinct handler entry, or the detected
    /// end of the containing function for the last block.
    /// </summary>
    public UInt64 EndAddressExclusive { get; }

    /// <summary>Number of bytes in the half-open interval represented by this block.</summary>
    public UInt64 ByteLength => EndAddressExclusive - StartAddress;

    /// <summary>All normalized-back selector values that dispatch to this entry point.</summary>
    public IReadOnlyList<long> CaseValues { get; }

    /// <summary>True when this entry point is also the bounds-check/default target.</summary>
    public bool IsDefaultTarget { get; }

    /// <summary>True when more than one selector value shares this handler.</summary>
    public bool IsShared => CaseValues.Count > 1;

    /// <summary>
    /// Creates a walker starting at this handler and capped at <see cref="EndAddressExclusive"/>.
    /// The boundary is a linear layout boundary, not a claim that every branch stays inside it.
    /// </summary>
    public InstructionWalker.InstructionWalker CreateWalker(InstructionWalkerConfig config, ILogger? logger = null)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        UInt64 requestedEnd = config.EndAddressExclusive;
        UInt64 boundedEnd = requestedEnd == 0 || requestedEnd > EndAddressExclusive ? EndAddressExclusive : requestedEnd;

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
        => $"0x{StartAddress:X16}..0x{EndAddressExclusive:X16} ({CaseValues.Count} value(s))";
}
