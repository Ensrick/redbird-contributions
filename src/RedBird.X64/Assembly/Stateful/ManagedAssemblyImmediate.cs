using System;
using Iced.Intel;
using Microsoft.Extensions.Logging;

namespace RedBird.X64.Assembly.Stateful;

/// <summary>
/// A hardcoded immediate operand, the <c>0x64</c> in <c>mov ebx, 100</c>, exposed as a value several parties can override and that always knows how to get back to what the target shipped with.
/// </summary>
/// <typeparam name="T">Integral type of the constant.</typeparam>
public sealed class ManagedAssemblyImmediate<T> : AssemblyValueSite<T> where T : unmanaged
{
    private readonly int _operand;
    private readonly int _encodedBits;

    /// <summary>Wraps the immediate of the instruction at <paramref name="instructionAddress"/>.</summary>
    /// <param name="instructionAddress">Address of the instruction holding the constant.</param>
    /// <param name="operand">Index of the immediate operand.</param>
    /// <param name="withConstantValueOffset">Added to the logical value to produce the encoded one.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="description">Optional label for diagnostics, e.g. the game's name for the constant.</param>
    public ManagedAssemblyImmediate(
        IntPtr instructionAddress,
        int operand = 0,
        Int64 withConstantValueOffset = 0,
        ILogger? logger = null,
        string? description = null)
        : base(instructionAddress, withConstantValueOffset, logger, description)
    {
        _operand = operand;

        if (operand < 0 || operand >= Instruction.OpCount)
        {
            throw new ArgumentOutOfRangeException(nameof(operand), operand, $"[{Instruction}] at 0x{(UInt64)instructionAddress:X16} has {Instruction.OpCount} operands.");
        }

        OpKind kind = Instruction.GetOpKind(operand);
        _encodedBits = GetEncodedBits(kind);

        if (_encodedBits == 0)
        {
            throw new ArgumentException($"Operand {operand} of [{Instruction}] at 0x{(UInt64)instructionAddress:X16} is {kind}, not an immediate.", nameof(operand));
        }

        Initialise();
    }

    /// <summary>Index of the wrapped immediate operand.</summary>
    public int Operand => _operand;

    /// <summary>Width in bits of the immediate as it is encoded in the instruction.</summary>
    public int EncodedBits => _encodedBits;

    /// <inheritdoc/>
    protected override T ReadFromInstruction(in Instruction instruction)
    {
        UInt64 encoded = instruction.GetImmediate(_operand);
        UInt64 logical = unchecked(encoded - (UInt64)ConstantValueOffset);

        // Iced sign-extends narrow immediates into the full 64-bit value (an imm8 of -124 comes
        // back as 0xFFFFFFFFFFFFFF84), so mask to the width of T before reinterpreting.
        logical &= MaskFor(System.Runtime.CompilerServices.Unsafe.SizeOf<T>());

        return FromRaw(logical);
    }

    /// <inheritdoc/>
    protected override void WriteToInstruction(ref Instruction instruction, T value)
    {
        Int64 logical = ToLogical(value);
        Int64 encoded = unchecked(logical + ConstantValueOffset);
        EnsureFits(encoded, _encodedBits, "immediate");

        if (IsSigned)
            instruction.SetImmediate(_operand, encoded);
        else
            instruction.SetImmediate(_operand, unchecked((UInt64)encoded));
    }

    private static UInt64 MaskFor(int byteCount) => byteCount switch
    {
        1 => 0xFF,
        2 => 0xFFFF,
        4 => 0xFFFF_FFFF,
        _ => UInt64.MaxValue,
    };

    /// <summary>
    /// Width of the immediate <i>as encoded</i>, which is what determines whether changing it changes the instruction's length. <c>Immediate8to32</c> occupies one byte even though it means a 32-bit value.
    /// </summary>
    private static int GetEncodedBits(OpKind kind) => kind switch
    {
        OpKind.Immediate8 or OpKind.Immediate8_2nd => 8,
        OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 => 8,
        OpKind.Immediate16 => 16,
        OpKind.Immediate32 or OpKind.Immediate32to64 => 32,
        OpKind.Immediate64 => 64,
        _ => 0,
    };
}
