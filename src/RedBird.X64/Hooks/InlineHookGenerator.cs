using Iced.Intel;
using System;

namespace RedBird.X64.Hooks;

/// <summary>
/// Delegate for generating inline hook assembly code with access to overwritten instructions.
/// Uses <see cref="ReadOnlySpan{T}"/> for zero-copy access to the instruction array.
/// </summary>
/// <param name="asm">The Iced assembler to generate code with.</param>
/// <param name="instructions">Read-only span of the original instructions that will be overwritten by the hook.</param>
/// <param name="returnAddress">The address where execution should return after the hooked instructions (original code continuation point).</param>
public delegate void InlineHookGenerator(Assembler asm, ReadOnlySpan<Instruction> instructions, UInt64 returnAddress);
