using Iced.Intel;

namespace RedBird.X64.Assembly.InstructionWalker;

/// <summary>
/// Provides common predicates and extractors for use with InstructionWalker.
/// This makes it easier to construct walk rules without writing boilerplate code.
/// </summary>
public static class InstructionWalkerHelpers
{
    // ==================== PREDICATES ====================

    public static ExtractionResult ExtractSourceRegister(Instruction instr)
    {
        if (instr.OpCount >= 2 && instr.GetOpKind(1) == OpKind.Register)
            return ExtractionResult.Succeeded((ulong)instr.GetOpRegister(1));
        return ExtractionResult.Failed();
    }

    /// <summary>
    /// Matches any instruction with a memory operand (any operand position).
    /// </summary>
    public static bool HasMemoryOperand(Instruction instr)
    {
        return instr.Op0Kind == OpKind.Memory ||
               instr.Op1Kind == OpKind.Memory ||
               instr.Op2Kind == OpKind.Memory ||
               instr.Op3Kind == OpKind.Memory ||
               instr.Op4Kind == OpKind.Memory;
    }

    /// <summary>
    /// Creates a predicate that matches instructions with a specific mnemonic.
    /// </summary>
    public static InstructionPredicate IsMnemonic(Mnemonic mnemonic)
    {
        return instr => instr.Mnemonic == mnemonic;
    }

    /// <summary>
    /// Matches MOV instructions.
    /// </summary>
    public static bool IsMov(Instruction instr) => instr.Mnemonic == Mnemonic.Mov;

    /// <summary>
    /// Matches MOVSXD instructions (sign-extend move).
    /// </summary>
    public static bool IsMovsxd(Instruction instr) => instr.Mnemonic == Mnemonic.Movsxd;

    /// <summary>
    /// Matches LEA instructions.
    /// </summary>
    public static bool IsLea(Instruction instr) => instr.Mnemonic == Mnemonic.Lea;

    /// <summary>
    /// Matches IMUL instructions.
    /// </summary>
    public static bool IsImul(Instruction instr) => instr.Mnemonic == Mnemonic.Imul;

    /// <summary>
    /// Matches CALL instructions.
    /// </summary>
    public static bool IsCall(Instruction instr) => instr.Mnemonic == Mnemonic.Call;

    /// <summary>
    /// Creates a predicate that matches instructions using a specific register in any operand.
    /// </summary>
    public static InstructionPredicate UsesRegister(Register register)
    {
        return instr =>
        {
            for (int i = 0; i < instr.OpCount; i++)
            {
                if (instr.GetOpKind(i) == OpKind.Register && instr.GetOpRegister(i) == register)
                    return true;

                // Check memory operands
                if (instr.GetOpKind(i) == OpKind.Memory)
                {
                    if (instr.MemoryBase == register || instr.MemoryIndex == register)
                        return true;
                }
            }
            return false;
        };
    }

    /// <summary>
    /// Matches instructions that have a memory operand with displacement greater than zero.
    /// </summary>
    public static bool HasDisplacement(Instruction instr)
    {
        return HasMemoryOperand(instr) && instr.MemoryDisplacement64 != 0;
    }

    /// <summary>
    /// Creates a predicate that matches instructions with memory operand at a specific operand index.
    /// </summary>
    public static InstructionPredicate HasMemoryOperandAt(int operandIndex)
    {
        return instr => operandIndex < instr.OpCount && instr.GetOpKind(operandIndex) == OpKind.Memory;
    }

    /// <summary>
    /// Combines multiple predicates with AND logic.
    /// </summary>
    public static InstructionPredicate And(params InstructionPredicate[] predicates)
    {
        return instr =>
        {
            foreach (InstructionPredicate predicate in predicates)
            {
                if (!predicate(instr))
                    return false;
            }
            return true;
        };
    }

    /// <summary>
    /// Combines multiple predicates with OR logic.
    /// </summary>
    public static InstructionPredicate Or(params InstructionPredicate[] predicates)
    {
        return instr =>
        {
            foreach (InstructionPredicate predicate in predicates)
            {
                if (predicate(instr))
                    return true;
            }
            return false;
        };
    }

    // ==================== EXTRACTORS ====================

    /// <summary>
    /// Extracts the memory displacement from the first memory operand found.
    /// </summary>
    public static ExtractionResult ExtractDisplacement(Instruction instr)
    {
        if (HasMemoryOperand(instr))
        {
            return ExtractionResult.Succeeded(instr.MemoryDisplacement64);
        }
        return ExtractionResult.Failed();
    }

    /// <summary>
    /// Creates an extractor that extracts the memory displacement from a specific operand index.
    /// </summary>
    public static InstructionExtractor ExtractDisplacementAt(int operandIndex)
    {
        return instr =>
        {
            if (operandIndex < instr.OpCount && instr.GetOpKind(operandIndex) == OpKind.Memory)
            {
                return ExtractionResult.Succeeded(instr.MemoryDisplacement64);
            }
            return ExtractionResult.Failed();
        };
    }

    /// <summary>
    /// Extracts the immediate value from the first immediate operand found.
    /// </summary>
    public static ExtractionResult ExtractImmediate(Instruction instr)
    {
        for (int i = 0; i < instr.OpCount; i++)
        {
            OpKind opKind = instr.GetOpKind(i);
            if (opKind == OpKind.Immediate8 ||
                opKind == OpKind.Immediate16 ||
                opKind == OpKind.Immediate32 ||
                opKind == OpKind.Immediate64 ||
                opKind == OpKind.Immediate8to16 ||
                opKind == OpKind.Immediate8to32 ||
                opKind == OpKind.Immediate8to64 ||
                opKind == OpKind.Immediate32to64)
            {
                return ExtractionResult.Succeeded(instr.GetImmediate(i));
            }
        }
        return ExtractionResult.Failed();
    }

    /// <summary>
    /// Creates an extractor that extracts the immediate value from a specific operand index.
    /// </summary>
    public static InstructionExtractor ExtractImmediateAt(int operandIndex)
    {
        return instr =>
        {
            if (operandIndex >= instr.OpCount)
                return ExtractionResult.Failed();

            OpKind opKind = instr.GetOpKind(operandIndex);
            if (opKind == OpKind.Immediate8 ||
                opKind == OpKind.Immediate16 ||
                opKind == OpKind.Immediate32 ||
                opKind == OpKind.Immediate64 ||
                opKind == OpKind.Immediate8to16 ||
                opKind == OpKind.Immediate8to32 ||
                opKind == OpKind.Immediate8to64 ||
                opKind == OpKind.Immediate32to64)
            {
                return ExtractionResult.Succeeded(instr.GetImmediate(operandIndex));
            }
            return ExtractionResult.Failed();
        };
    }

    /// <summary>
    /// Extracts the target address of a CALL or JMP instruction.
    /// </summary>
    public static ExtractionResult ExtractBranchTarget(Instruction instr)
    {
        if (instr.FlowControl == FlowControl.Call ||
            instr.FlowControl == FlowControl.UnconditionalBranch ||
            instr.FlowControl == FlowControl.ConditionalBranch)
        {
            return ExtractionResult.Succeeded(instr.NearBranchTarget);
        }
        return ExtractionResult.Failed();
    }

    /// <summary>
    /// Extracts the RIP-relative address from an instruction.
    /// </summary>
    public static ExtractionResult ExtractRipRelativeAddress(Instruction instr)
    {
        if (instr.IsIPRelativeMemoryOperand)
        {
            return ExtractionResult.Succeeded(instr.IPRelativeMemoryAddress);
        }
        return ExtractionResult.Failed();
    }

    /// <summary>
    /// Extracts absolute memory address operands (common in 32-bit x86 code, e.g., MOV EAX, [0x401000]).
    /// </summary>
    public static ExtractionResult ExtractAbsoluteAddress(Instruction instr)
    {
        // Check for direct memory operand with no base/index register (32-bit direct pointer access)
        for (int i = 0; i < instr.OpCount; i++)
        {
            if (instr.GetOpKind(i) == OpKind.Memory &&
                instr.MemoryBase == Register.None &&
                instr.MemoryIndex == Register.None)
            {
                return ExtractionResult.Succeeded(instr.MemoryDisplacement64);
            }
        }

        return ExtractionResult.Failed();
    }

    /// <summary>
    /// Extracts the instruction's virtual address (IP).
    /// </summary>
    public static ExtractionResult ExtractInstructionAddress(Instruction instr)
    {
        return ExtractionResult.Succeeded(instr.IP);
    }

    /// <summary>
    /// Extracts the next instruction's address (NextIP).
    /// </summary>
    public static ExtractionResult ExtractNextInstructionAddress(Instruction instr)
    {
        return ExtractionResult.Succeeded(instr.NextIP);
    }
}
