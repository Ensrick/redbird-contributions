using Iced.Intel;
using System;

namespace RedBird.X64.Assembly.InstructionWalker;

/// <summary>
/// Delegate that determines if an instruction matches search criteria.
/// </summary>
/// <param name="instruction">The instruction to evaluate.</param>
/// <returns>True if the instruction matches, false otherwise.</returns>
public delegate bool InstructionPredicate(Instruction instruction);

/// <summary>
/// Delegate that extracts a value from an instruction.
/// </summary>
/// <param name="instruction">The instruction to extract from.</param>
/// <returns>A tuple containing success status and the extracted value.</returns>
public delegate ExtractionResult InstructionExtractor(Instruction instruction);

/// <summary>
/// Result of an extraction operation.
/// </summary>
public struct ExtractionResult(bool success, UInt64 value)
{
    public bool Success = success;
    public UInt64 Value = value;

    public static ExtractionResult Succeeded(UInt64 value) => new(true, value);
    public static ExtractionResult Failed() => new(false, 0);
}
