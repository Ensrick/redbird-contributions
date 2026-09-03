using System;
using Iced.Intel;

namespace RedBird.X86.Hooks;

/// <summary>Generates the user-controlled body of a 32-bit inline-hook stub.</summary>
/// <param name="assembler">Assembler configured for 32-bit x86.</param>
/// <param name="instructions">Whole instructions displaced by the entry jump.</param>
/// <param name="returnAddress">First original instruction after the displaced range.</param>
public delegate void InlineHookGenerator(
    Assembler assembler,
    ReadOnlySpan<Instruction> instructions,
    UInt64 returnAddress);
