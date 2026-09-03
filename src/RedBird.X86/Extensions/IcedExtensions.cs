using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Extensions;

/// <summary>Architecture-correct Iced conveniences for 32-bit x86 stubs.</summary>
public static class IcedExtensions
{
    /// <summary>Pushes a 16- or 32-bit general-purpose register.</summary>
    public static void push(this Assembler assembler, Register register)
    {
        if (register.IsGPR16())
            assembler.push(new AssemblerRegister16(register));
        else if (register.IsGPR32())
            assembler.push(new AssemblerRegister32(register));
        else
            throw new ArgumentException($"{register} is not an x86 general-purpose register.", nameof(register));
    }

    /// <summary>Pops a 16- or 32-bit general-purpose register.</summary>
    public static void pop(this Assembler assembler, Register register)
    {
        if (register.IsGPR16())
            assembler.pop(new AssemblerRegister16(register));
        else if (register.IsGPR32())
            assembler.pop(new AssemblerRegister32(register));
        else
            throw new ArgumentException($"{register} is not an x86 general-purpose register.", nameof(register));
    }

    /// <summary>Pushes registers now and emits matching pops in reverse order on disposal.</summary>
    public static IDisposable Preserve(this Assembler assembler, params Register[] registers)
    {
        foreach (Register register in registers)
            assembler.push(register);

        return new DisposableAction(() =>
        {
            for (int index = registers.Length - 1; index >= 0; index--)
                assembler.pop(registers[index]);
        });
    }

    /// <summary>Preserves EFLAGS and the cdecl/stdcall/thiscall volatile GPRs EAX, ECX and EDX.</summary>
    public static IDisposable PreserveVolatile(this Assembler assembler)
    {
        assembler.pushfd();
        IDisposable registers = assembler.Preserve(eax, ecx, edx);
        return new DisposableAction(() =>
        {
            registers.Dispose();
            assembler.popfd();
        });
    }

    /// <summary>Clones instructions and clears their original IP identity.</summary>
    public static Instruction[] CloneInstructionsWithoutIP(this ReadOnlySpan<Instruction> instructions)
    {
        Instruction[] copy = new Instruction[instructions.Length];
        for (int index = 0; index < instructions.Length; index++)
        {
            copy[index] = instructions[index];
            copy[index].IP = 0;
        }

        return copy;
    }

    public static void ZeroInstructionsIP(this Instruction[] instructions)
    {
        for (int index = 0; index < instructions.Length; index++)
            instructions[index].IP = 0;
    }

    public static void ZeroInstructionsNextIP(this Instruction[] instructions)
    {
        for (int index = 0; index < instructions.Length; index++)
            instructions[index].NextIP = 0;
    }

    public static bool IsUnconditionalBranch(this Instruction instruction)
        => instruction.FlowControl == FlowControl.UnconditionalBranch;

    public static bool IsCall(this Instruction instruction)
        => instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall;

    public static bool IsRet(this Instruction instruction)
        => instruction.FlowControl == FlowControl.Return;

    /// <summary>Checks register operands plus memory base/index registers.</summary>
    public static bool UsesRegister(this Instruction instruction, Register register)
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

    private sealed class DisposableAction(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose()
        {
            Action? action = _action;
            _action = null;
            action?.Invoke();
        }
    }
}
