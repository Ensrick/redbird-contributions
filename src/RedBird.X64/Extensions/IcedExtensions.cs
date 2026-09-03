using Iced.Intel;
using System;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Extensions;

/// <summary>
/// Provides extension methods for the Iced.Intel Assembler and Instruction classes, enhancing their functionality for assembly code generation and manipulation.
/// </summary>
public static class IcedExtensions
{

    /// <summary>
    /// Auto push the correct GPR just from a Register enum.
    /// </summary>
    /// <param name="asm">The associated assembler</param>
    /// <param name="register">The register</param>
    public static void push(this Assembler asm, Register register)
    {
        if (register.IsGPR16())
        {
            asm.push(new AssemblerRegister16(register));
        }
        else if (register.IsGPR32())
        {
            asm.push(new AssemblerRegister32(register));
        }
        else if (register.IsGPR64())
        {
            asm.push(new AssemblerRegister64(register));
        }
    }

    /// <summary>
    /// Auto pop the correct GPR just from a Register enum.
    /// </summary>
    /// <param name="asm">The associated assembler</param>
    /// <param name="register">The register</param>
    public static void pop(this Assembler asm, Register register)
    {
        if (register.IsGPR16())
        {
            asm.pop(new AssemblerRegister16(register));
        }
        else if (register.IsGPR32())
        {
            asm.pop(new AssemblerRegister32(register));
        }
        else if (register.IsGPR64())
        {
            asm.pop(new AssemblerRegister64(register));
        }
    }

    /// <summary>
    /// Creates a scope that Pushes specific registers and Pops them on Dispose.
    /// </summary>
    public static IDisposable Preserve(this Assembler asm, params Register[] regs)
    {
        foreach (Register r in regs)
            asm.push(r);
        return new DisposableAction(() =>
        {
            // Pop in reverse order
            for (int i = regs.Length - 1; i >= 0; i--)
                asm.pop(regs[i]);
        });
    }

    /// <summary>
    /// Creates a scope that preserves all volatile registers (RAX, RCX, RDX, R8, R9, R10, R11).
    /// </summary>
    public static IDisposable PreserveVolatile(this Assembler asm)
    {
        // Pushing/Popping flags is usually desired when preserving volatile state for a hook
        asm.pushfq();
        IDisposable scope = asm.Preserve(rax, rcx, rdx, r8, r9, r10, r11);

        return new DisposableAction(() =>
        {
            scope.Dispose();
            asm.popfq();
        });
    }

    /// <summary>
    /// Safely calls a managed delegate/function pointer handling shadow space and alignment.
    /// </summary>
    public static void SafeCall(this Assembler asm, IntPtr functionPtr, int argCount = 0)
    {
        int shadowSpace = 0x20; // 32 bytes
        int stackArgs = Math.Max(0, argCount - 4) * 8;
        int totalAlloc = shadowSpace + stackArgs;

        // Ensure 16-byte alignment
        if (totalAlloc % 16 != 0) totalAlloc += 8;

        asm.sub(rsp, totalAlloc);
        asm.mov(rax, (ulong)functionPtr);
        asm.call(rax);
        asm.add(rsp, totalAlloc);
    }

    /// <summary>
    /// Creates a deep copy of an array of instructions, clearing the IP property on each copy.
    /// This is important for adding the same logical instructions multiple times to an assembler at different locations within a stub, as it prevents the "multiple instructions with the same IP" error.
    /// </summary>
    /// <param name="instructions">The original instructions to clone.</param>
    /// <returns>A new array of cloned instructions with their IP property set to 0.</returns>
    public static Instruction[] CloneInstructionsWithoutIP(this ReadOnlySpan<Instruction> instructions)
    {
        if (instructions.IsEmpty)
        {
            return [];
        }

        Instruction[] cloned = new Instruction[instructions.Length];
        for (int i = 0; i < instructions.Length; i++)
        {
            Instruction instructionCopy = instructions[i];
            instructionCopy.IP = 0;
            cloned[i] = instructionCopy;
        }
        return cloned;
    }

    public static void ZeroInstructionsNextIP(this Instruction[] instructions)
    {
        for (int i = 0; i < instructions.Length; i++)
        {
            instructions[i].NextIP = 0;
        }
    }
    public static void ZeroInstructionsIP(this Instruction[] instructions)
    {
        for (int i = 0; i < instructions.Length; i++)
        {
            instructions[i].IP = 0;
        }
    }

    public static bool IsUnconditionalBranch(this Instruction instruction)
    {
        return instruction.Mnemonic == Mnemonic.Jmp;
    }

    public static bool IsRet(this Instruction instruction)
    {
        switch (instruction.Mnemonic)
        {
            case Mnemonic.Ret:
            case Mnemonic.Retf:
            case Mnemonic.Iret:
            case Mnemonic.Iretd:
            case Mnemonic.Iretq:
                return true;
            default:
                return false;
        }
    }

    public static bool IsCall(this Instruction instruction)
    {
        switch (instruction.Mnemonic)
        {
            case Mnemonic.Call:
                return true;
            default:
                return false;
        }
    }

    public static bool HasMemoryRegisterInAnyOperand(this Instruction instruction, Register register)
    {
        for (int operand = 0; operand < instruction.OpCount; operand++)
        {
            OpKind kind = instruction.GetOpKind(operand);
            if (kind == OpKind.Register && instruction.GetOpRegister(operand) == register)
                return true;

            if (kind == OpKind.Memory &&
                (instruction.MemoryBase == register || instruction.MemoryIndex == register))
            {
                return true;
            }
        }

        return false;
    }

    // Helper for C# closures
    private class DisposableAction(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
