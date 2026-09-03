using Iced.Intel;
using System;

namespace RedBird.X64.Assembly.InstructionWalker;

/// <summary>
/// Represents the context of an instruction walk, containing the address and the instruction itself.
/// </summary>
public struct InstructionWalkContext
{
    public UInt64 Address;
    public Instruction Instruction;
}
