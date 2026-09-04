using System;
using Iced.Intel;
using Microsoft.Extensions.Logging;

namespace RedBird.X64.Assembly.Stateful;

/// <summary>
/// A memory operand's displacement, the <c>+8</c> in <c>lea edx, [rdi+8]</c>, exposed as an overridable value. Typically a struct field offset or an array stride.
/// </summary>
/// <typeparam name="T">Integral type of the displacement.</typeparam>
public sealed class ManagedAssemblyDisplacement<T> : AssemblyValueSite<T> where T : unmanaged
{
    private readonly int _memoryOperand;
    private readonly int _encodedBits;

    /// <summary>Wraps the displacement of a memory operand.</summary>
    /// <param name="instructionAddress">Address of the instruction holding the operand.</param>
    /// <param name="memoryOperand">Index of the memory operand. Usually 1.</param>
    /// <param name="withConstantValueOffset">Added to the logical value to produce the encoded one.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="description">Optional label for diagnostics.</param>
    public ManagedAssemblyDisplacement(
        IntPtr instructionAddress,
        int memoryOperand = 0,
        Int64 withConstantValueOffset = 0,
        ILogger? logger = null,
        string? description = null)
        : base(instructionAddress, withConstantValueOffset, logger, description)
    {
        _memoryOperand = memoryOperand;

        if (memoryOperand < 0 || memoryOperand >= Instruction.OpCount)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryOperand), memoryOperand, $"[{Instruction}] at 0x{(UInt64)instructionAddress:X16} has {Instruction.OpCount} operands.");
        }

        if (Instruction.GetOpKind(memoryOperand) != OpKind.Memory)
        {
            throw new ArgumentException($"Operand {memoryOperand} of [{Instruction}] at 0x{(UInt64)instructionAddress:X16} is {Instruction.GetOpKind(memoryOperand)}, not a memory operand.", nameof(memoryOperand));
        }

        IsRipRelative = Instruction.IsIPRelativeMemoryOperand;

        if (IsRipRelative && System.Runtime.CompilerServices.Unsafe.SizeOf<T>() < 8)
        {
            // For a RIP-relative operand Iced exposes MemoryDisplacement64 as the absolute target address, not the encoded rel32. Narrower types would silently truncate it.
            throw new ArgumentException($"[{Instruction}] at 0x{(UInt64)instructionAddress:X16} is RIP-relative, so the displacement is an absolute 64-bit target address. Use ManagedAssemblyDisplacement<ulong> for this site.", nameof(memoryOperand));
        }

        _encodedBits = Instruction.MemoryDisplSize switch
        {
            1 => 8,
            2 => 16,
            4 => 32,
            8 => 64,
            _ => 0,
        };

        if (_encodedBits == 0)
        {
            throw new ArgumentException($"[{Instruction}] at 0x{(UInt64)instructionAddress:X16} encodes no displacement, so there is nothing to change. A site like [rdi] has no displacement byte to widen into.", nameof(memoryOperand));
        }

        Initialise();
    }

    /// <summary>Index of the wrapped memory operand.</summary>
    public int MemoryOperand => _memoryOperand;

    /// <summary>Width in bits of the displacement as encoded.</summary>
    public int EncodedBits => _encodedBits;

    /// <summary>
    /// True when the operand is RIP-relative, in which case the value is the absolute target address and Iced recomputes the rel32 on encode.
    /// </summary>
    public bool IsRipRelative { get; }

    /// <summary>
    /// Largest displacement that still fits the original encoding. Anything beyond this would lengthen the instruction and is rejected.
    /// </summary>
    public Int64 MaximumDisplacement => _encodedBits >= 64 ? Int64.MaxValue : (1L << (_encodedBits - 1)) - 1;

    /// <summary>Smallest displacement that still fits the original encoding.</summary>
    public Int64 MinimumDisplacement => _encodedBits >= 64 ? Int64.MinValue : -(1L << (_encodedBits - 1));

    /// <inheritdoc/>
    protected override T ReadFromInstruction(in Instruction instruction)
    {
        Int64 encoded = unchecked((Int64)instruction.MemoryDisplacement64);
        Int64 logical = unchecked(encoded - ConstantValueOffset);

        return FromRaw(unchecked((UInt64)logical));
    }

    /// <inheritdoc/>
    protected override void WriteToInstruction(ref Instruction instruction, T value)
    {
        Int64 logical = ToLogical(value);
        Int64 encoded = unchecked(logical + ConstantValueOffset);

        if (!IsRipRelative)
            EnsureFits(encoded, _encodedBits, "displacement");

        instruction.MemoryDisplacement64 = unchecked((UInt64)encoded);
    }
}
