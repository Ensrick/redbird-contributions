using System;
using System.Collections.Generic;
using Iced.Intel;

namespace RedBird.Backends.NativeX86;

/// <summary>Whole x86 instructions selected for displacement into a trampoline.</summary>
internal sealed class X86PrologueSelection
{
    public required UInt64 FunctionAddress { get; init; }
    public required List<Instruction> Instructions { get; init; }
    public required int Length { get; init; }
}
