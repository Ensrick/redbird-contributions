using Iced.Intel;
using System;
using System.Collections.Generic;

namespace RedBird.X64.Assembly;

/// <summary>Result of disassembling one function.</summary>
public sealed class FunctionDisassembly(IReadOnlyList<Instruction> instructions, UInt64 address, int byteLength, bool truncated)
{
    /// <summary>Decoded instructions in address order.</summary>
    public IReadOnlyList<Instruction> Instructions { get; } = instructions;

    /// <summary>Address the disassembly started at.</summary>
    public UInt64 Address { get; } = address;

    /// <summary>Total byte length covered by <see cref="Instructions"/>.</summary>
    public int ByteLength { get; } = byteLength;

    /// <summary>
    /// True when a limit was hit rather than a terminator: the function is longer than what was decoded, and anything built from this (a clone, a walk) is incomplete.
    /// </summary>
    public bool Truncated { get; } = truncated;
}
