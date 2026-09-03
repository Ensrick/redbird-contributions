using Iced.Intel;
using System;
using System.ComponentModel;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Extensions;

/// <summary>
/// Provides extension methods for the <see cref="Iced.Intel.Assembler"/> class to simplify common x64 assembly programming tasks, such as creating unrestricted jumps, preserving registers, and adhering to the Windows x64 calling convention.
/// </summary>
public static class AssemblerExtensions
{
    /// <summary>
    /// Emits an x64 assembly instruction sequence to perform an absolute jump to a 64-bit address.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <param name="address">The absolute 64-bit destination address for the jump.</param>
    /// <remarks>
    /// <para>
    /// This method circumvents the standard 32-bit relative jump (<c>jmp rel32</c>) limitation, which is restricted to a +/- 2GB range from the current instruction pointer (RIP).
    /// </para>
    /// <para>
    /// The method generates the instruction <c>jmp qword ptr [rip+0]</c>, immediately followed by the 8-byte target address. This is a common and efficient technique for creating an absolute jump in position-independent 64-bit code.
    /// </para>
    /// </remarks>
    public static void AddUnrestrictedJmp(this Assembler assembler, UInt64 address)
    {
        byte[] jmpRipInstruction = [0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 ];
        assembler.db(jmpRipInstruction);
        assembler.dq(address);
    }

    /// <summary>
    /// Emits a raw, unrestricted x64 <c>call</c> instruction sequence to a 64-bit address.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <param name="address">The absolute 64-bit destination address for the call.</param>
    /// <remarks>
    /// <warning type="caution">
    /// This is a low-level "raw" implementation. It generates a <c>call qword ptr [rip+0]</c> instruction followed by the target address data. 
    /// This method is inherently unsafe for standard function calls because the return address pushed to the stack will point to the data immediately following the <c>call</c> instruction, not to executable code. 
    /// Attempting to <c>ret</c> from the called function will almost certainly cause a crash due to an illegal instruction exception.
    /// </warning>
    /// </remarks>
    /// <seealso cref="AddUnrestrictedCall"/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void AddUnrestrictedCallRaw(this Assembler assembler, UInt64 address)
    {
        byte[] callRipInstruction = [0xFF, 0x15, 0x00, 0x00, 0x00, 0x00];
        assembler.db(callRipInstruction);
        assembler.dq(address);
    }

    /// <summary>
    /// Emits a safe and robust x64 assembly instruction sequence for an absolute call to a 64-bit address, ensuring that program flow can correctly return.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <param name="address">The absolute 64-bit destination address for the call.</param>
    /// <remarks>
    /// <para>
    /// This method generates a small trampoline that jumps over an 8-byte data block containing the target address, and then calls the address from a RIP-relative operand. 
    /// This ensures that when the called function executes a <c>ret</c> instruction, control flow returns to the instruction immediately following this entire trampoline sequence, preventing a crash.
    /// </para>
    /// <para>
    /// This is the recommended method for making function calls to arbitrary 64-bit addresses.
    /// </para>
    /// </remarks>
    /// <seealso cref="AddUnrestrictedCallRaw"/>
    public static void AddUnrestrictedCall(this Assembler assembler, UInt64 address)
    {
        // We will create two labels to let the assembler calculate the relative offsets for us
        Label jmpLabel = assembler.CreateLabel();
        Label dataLabel = assembler.CreateLabel();

        // Jump to the code, hopping over the data block
        assembler.jmp(jmpLabel);

        // Define the data block. Mark its location with a label
        assembler.Label(ref dataLabel);
        assembler.dq(address);

        // This is where the jmp instruction will land
        assembler.Label(ref jmpLabel);

        // The assembler automatically calculates the negative RIP-relative displacement from the current position back to the dataLabel
        assembler.call(__qword_ptr[dataLabel]);
    }

    /// <summary>
    /// Adds a collection of <see cref="Instruction"/> objects to the assembler.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <param name="instructions">An array or enumerable of <see cref="Instruction"/> objects to add.</param>
    public static void AddInstructions(this Assembler assembler, Instruction[] instructions)
    {
        foreach (Instruction instruction in instructions)
        {
            assembler.AddInstruction(instruction);
        }
    }

    /// <summary>
    /// Adds a collection of <see cref="Instruction"/> objects to the assembler.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <param name="instructions">An array or enumerable of <see cref="Instruction"/> objects to add.</param>
    public static void AddInstructions(this Assembler assembler, ReadOnlySpan<Instruction> instructions)
    {
        foreach (Instruction instruction in instructions)
        {
            assembler.AddInstruction(instruction);
        }
    }

    /// <summary>
    /// Pushes all 15 general-purpose registers (GPRs) onto the stack in x64 mode (RAX, RBX... R15).
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <remarks>
    /// This is useful for creating function prologues where the entire machine state needs to be preserved.
    /// The RSP register is not pushed. Use <see cref="PopAllGPR"/> to restore the registers.
    /// </remarks>
    /// <seealso cref="PopAllGPR"/>
    public static void PushAllGPR(this Assembler assembler)
    {
        assembler.push(rax);
        assembler.push(rbx);
        assembler.push(rcx);
        assembler.push(rdx);
        assembler.push(rsi);
        assembler.push(rdi);
        assembler.push(rbp);

        assembler.push(r8);
        assembler.push(r9);
        assembler.push(r10);
        assembler.push(r11);
        assembler.push(r12);
        assembler.push(r13);
        assembler.push(r14);
        assembler.push(r15);
    }

    /// <summary>
    /// Pops all 15 general-purpose registers (GPRs) from the stack in the reverse order they were pushed.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <remarks>
    /// This method is the counterpart to <see cref="PushAllGPR"/> and is typically used in a function epilogue to restore the machine state before returning.
    /// </remarks>
    /// <seealso cref="PushAllGPR"/>
    public static void PopAllGPR(this Assembler assembler)
    {
        assembler.pop(r15);
        assembler.pop(r14);
        assembler.pop(r13);
        assembler.pop(r12);
        assembler.pop(r11);
        assembler.pop(r10);
        assembler.pop(r9);
        assembler.pop(r8);

        assembler.pop(rbp);
        assembler.pop(rdi);
        assembler.pop(rsi);
        assembler.pop(rdx);
        assembler.pop(rcx);
        assembler.pop(rbx);
        assembler.pop(rax);
    }

    /// <summary>
    /// Pushes all volatile (caller-saved) general-purpose registers as defined by the Windows x64 calling convention.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <param name="preserveRAX">Whether to preseve RAX</param>
    /// <remarks>
    /// According to the ABI, the callee is free to modify these registers without saving them. 
    /// Therefore, the caller must save them if their values are needed after the function call. The volatile registers are:  RAX, RCX, RDX, R8, R9, R10, and R11.
    /// </remarks>
    /// <seealso cref="PopAllVolatile"/>
    public static void PushAllVolatile(this Assembler assembler, bool preserveRAX = true)
    {
        if (preserveRAX)
            assembler.push(rax);
        assembler.push(rcx);
        assembler.push(rdx);
        assembler.push(r8);
        assembler.push(r9);
        assembler.push(r10);
        assembler.push(r11);
    }

    /// <summary>
    /// Pops all volatile (caller-saved) registers in the reverse order they were pushed by <see cref="PushAllVolatile"/>.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <param name="preserveRAX">Whether to preseve RAX</param>
    /// <seealso cref="PushAllVolatile"/>
    public static void PopAllVolatile(this Assembler assembler, bool preserveRAX = true)
    {
        assembler.pop(r11);
        assembler.pop(r10);
        assembler.pop(r9);
        assembler.pop(r8);
        assembler.pop(rdx);
        assembler.pop(rcx);
        if (preserveRAX)
            assembler.pop(rax);
    }

    /// <summary>
    /// Pushes the first four integer/pointer argument registers used by the x64 fastcall calling convention.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <remarks>
    /// The registers are RCX, RDX, R8, and R9. This is useful for saving arguments before using these
    /// registers for another purpose, such as an inner function call.
    /// </remarks>
    /// <seealso cref="PopX64FastcallRegisters"/>
    public static void PushX64FastcallRegisters(this Assembler assembler)
    {
        assembler.push(rcx);
        assembler.push(rdx);
        assembler.push(r8);
        assembler.push(r9);
    }

    /// <summary>
    /// Pops the first four x64 fastcall argument registers in reverse order.
    /// </summary>
    /// <param name="assembler">The <see cref="Assembler"/> instance this method extends.</param>
    /// <seealso cref="PushX64FastcallRegisters"/>
    public static void PopX64FastcallRegisters(this Assembler assembler)
    {
        assembler.pop(r9);
        assembler.pop(r8);
        assembler.pop(rdx);
        assembler.pop(rcx);
    }

    /// <summary>
    /// Saves the 6 volatile XMM registers (XMM0-XMM5) to the stack.
    /// </summary>
    /// <param name="assembler">The assembler instance this method extends.</param>
    /// <remarks>
    /// This method allocates 96 bytes (6 * 16 bytes) on the stack. 
    /// Unaligned moves are used deliberately because hook sites do not necessarily inherit an ABI-aligned stack.
    /// The counterpart to this method is <see cref="PopVolatileXMM"/>.
    /// </remarks>
    public static void PushVolatileXMM(this Assembler assembler)
    {
        AssemblerRegisterXMM[] volatileXmmRegs = [xmm0, xmm1, xmm2, xmm3, xmm4, xmm5];
        int spaceNeeded = volatileXmmRegs.Length * 16; // 96 bytes

        assembler.sub(rsp, spaceNeeded);
        for (int i = 0; i < volatileXmmRegs.Length; i++)
        {
            assembler.movdqu(__xmmword_ptr[rsp + i * 16], volatileXmmRegs[i]);
        }
    }

    /// <summary>
    /// Restores the 6 volatile XMM registers (XMM0-XMM5) from the stack.
    /// </summary>
    /// <param name="assembler">The assembler instance this method extends.</param>
    /// <remarks>
    /// This method is the counterpart to <see cref="PushVolatileXMM"/>. 
    /// It restores the registers and then deallocates the 96 bytes of stack space used for storage.
    /// </remarks>
    public static void PopVolatileXMM(this Assembler assembler)
    {
        AssemblerRegisterXMM[] volatileXmmRegs = [xmm0, xmm1, xmm2, xmm3, xmm4, xmm5];
        int spaceNeeded = volatileXmmRegs.Length * 16; // 96 bytes

        for (int i = volatileXmmRegs.Length - 1; i >= 0; i--)
        {
            assembler.movdqu(volatileXmmRegs[i], __xmmword_ptr[rsp + i * 16]);
        }
        assembler.add(rsp, spaceNeeded);
    }

    /// <summary>
    /// Defines the source of an argument for a hooked function call.
    /// </summary>
    public enum X64ArgumentSourceType
    {
        /// <summary>
        /// The argument's value should be taken from the original function's stack.
        /// </summary>
        FromOriginalStack,

        /// <summary>
        /// The argument's value is a hardcoded immediate value.
        /// </summary>
        FromImmediate
    }

    /// <summary>
    /// Represents an argument to be passed on the stack to the C# hook function.
    /// </summary>
    /// <param name="SourceType">The source of the argument's value.</param>
    /// <param name="Value">
    /// If SourceType is FromOriginalStack, this is the 0-based index of the argument on the original stack.
    /// (e.g., 0 for [rsp+28h], 1 for [rsp+30h]).
    /// If SourceType is FromImmediate, this is the immediate value to pass.
    /// </param>
    public record X64StackArgument(X64ArgumentSourceType SourceType, ulong Value);

    /// <summary>
    /// Generates a complete Windows x64 ABI call stub with state preservation, stack alignment, and optional stack-argument forwarding.
    /// </summary>
    /// <param name="assembler">The assembler instance to write to.</param>
    /// <param name="targetAddress">The memory address of the function to call</param>
    /// <param name="totalArgumentCount">The total number of arguments that the C# function expects.</param>
    /// <param name="prepareArgumentsAction">Do something when an argument is being interacted with.</param>
    /// <param name="preserveRAX">Whether to preserve the RAX register.</param>
    /// <param name="preserveXMM">Whether to preserve the XMM registers.</param>
    /// <param name="stackArguments">A list describing the source for each stack-based argument (the 5th argument onwards).</param>
    /// <remarks>
    /// <para>
    /// This is the standard high-level call helper. The first four integer/pointer arguments are prepared in RCX, RDX, R8, and R9. 
    /// Additional arguments are described by <paramref name="stackArguments"/> and are written after the 32-byte home space.
    /// </para>
    /// <para>
    /// <see cref="X64ArgumentSourceType.FromOriginalStack"/> addresses arguments relative to the Windows x64 function-entry stack layout. 
    /// It is therefore valid only when the hook runs before the original function has adjusted RSP.
    /// </para>
    /// <para>
    /// The argument-preparation callback runs after registers have been saved but before the temporary outgoing frame is allocated. It should prepare register arguments only.
    /// </para>
    /// </remarks>
    public static void X64FastcallSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        int totalArgumentCount,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveRAX = true,
        bool preserveXMM = false,
        params X64StackArgument[] stackArguments)
    {
        if (totalArgumentCount < 0)
            throw new ArgumentOutOfRangeException(nameof(totalArgumentCount));
        if (stackArguments is null)
            throw new ArgumentNullException(nameof(stackArguments));

        int stackArgumentCount = Math.Max(0, totalArgumentCount - 4);
        if (stackArgumentCount != stackArguments.Length)
        {
            throw new ArgumentException("The number of provided stack arguments does not match the expected count.", nameof(stackArguments));
        }

        int gprSaveSize = (preserveRAX ? 7 : 6) * 8;
        int xmmSaveSize = preserveXMM ? 6 * 16 : 0;
        const int shadowSpace = 32;
        int outgoingSpace = checked(shadowSpace + (stackArgumentCount * 8));
        outgoingSpace = checked((outgoingSpace + 15) & ~15);
        int frameSize = checked(outgoingSpace + 16); // saved RSP plus alignment padding

        foreach (X64StackArgument argument in stackArguments)
        {
            if (argument is null)
                throw new ArgumentException("A stack-argument descriptor cannot be null.", nameof(stackArguments));

            if (argument.SourceType is not X64ArgumentSourceType.FromOriginalStack and
                not X64ArgumentSourceType.FromImmediate)
            {
                throw new ArgumentOutOfRangeException(nameof(stackArguments), $"Unknown x64 argument source {argument.SourceType}.");
            }

            if (argument.SourceType == X64ArgumentSourceType.FromOriginalStack)
            {
                if (argument.Value > Int32.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(stackArguments), "An original stack index is too large.");

                _ = checked(xmmSaveSize + gprSaveSize + 0x28 + ((int)argument.Value * 8));
            }
        }

        // Save state on the original stack. PushVolatileXMM uses unaligned moves, so this remains safe even at a non-ABI hook site.
        assembler.PushAllVolatile(preserveRAX);
        if (preserveXMM)
        {
            assembler.PushVolatileXMM();
        }

        // Let the callback observe the original RBP and prepare only ABI register arguments.
        prepareArgumentsAction?.Invoke(assembler);

        // R11 is caller-saved and not an argument register.
        // Keep the exact saved-state stack pointer in the outgoing frame so RBP remains available to the hook and target.
        assembler.mov(r11, rsp);
        assembler.and(rsp, -16);

        assembler.sub(rsp, frameSize);
        assembler.mov(__qword_ptr[rsp + outgoingSpace], r11);

        for (int i = 0; i < stackArguments.Length; i++)
        {
            X64StackArgument arg = stackArguments[i];
            int destinationOffset = checked(shadowSpace + (i * 8));

            switch (arg.SourceType)
            {
                case X64ArgumentSourceType.FromOriginalStack:
                    int originalStackIndex = (int)arg.Value;
                    int sourceOffset = checked(xmmSaveSize + gprSaveSize + 0x28 + (originalStackIndex * 8));

                    assembler.mov(rax, __qword_ptr[r11 + sourceOffset]);
                    assembler.mov(__qword_ptr[rsp + destinationOffset], rax);
                    break;

                case X64ArgumentSourceType.FromImmediate:
                    assembler.mov(rax, arg.Value);
                    assembler.mov(__qword_ptr[rsp + destinationOffset], rax);
                    break;
            }
        }

        assembler.AddUnrestrictedCall(targetAddress);

        // The callee may clobber R11, so reload the exact pre-alignment stack pointer from the private frame slot and restore state from there.
        assembler.mov(rsp, __qword_ptr[rsp + outgoingSpace]);

        if (preserveXMM)
        {
            assembler.PopVolatileXMM();
        }
        assembler.PopAllVolatile(preserveRAX);
    }

    /// <summary>
    /// Executes a call to a target address from within a new, temporarily aligned stack frame.
    /// This is a lightweight, surgical tool to ensure a call is ABI-compliant even if the caller's stack is misaligned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is for delicate hooking scenarios where you need to manually control
    /// general-purpose register preservation (e.g., with Push/PopAllVolatile) but require a guarantee that the
    /// stack will be 16-byte aligned before the 'call' instruction. It does not push or pop any GPRs itself.
    /// </para>
    /// <para>
    /// **Mechanism:**
    /// 1. Saves the RBP register.
    /// 2. Saves the current, potentially misaligned, stack pointer into RBP.
    /// 3. Forcibly aligns the active stack pointer (RSP) to a 16-byte boundary.
    /// 4. Allocates the mandatory 32-byte "shadow space" on the new, aligned stack.
    /// 5. Executes the call.
    /// 6. Restores the original stack pointer from RBP, instantly discarding the temporary frame.
    /// 7. Restores the original RBP register.
    /// </para>
    /// </remarks>
    /// <param name="assembler">The assembler instance to write to.</param>
    /// <param name="targetAddress">The absolute 64-bit address of the function to call.</param>
    public static void X64AlignedCall(this Assembler assembler, ulong targetAddress)
    {
        const int shadowSpace = 32;

        // --- Prologue: Create a new, aligned stack frame ---
        assembler.push(rbp);
        assembler.mov(rbp, rsp);
        assembler.and(rsp, -16);

        // Allocate shadow space on the newly aligned stack.
        assembler.sub(rsp, shadowSpace);

        // --- Perform The Call ---
        assembler.AddUnrestrictedCall(targetAddress);

        // --- Epilogue: Tear down the stack frame ---
        // Instantly restore the original stack pointer from before alignment.
        // This cleans up the shadow space and the alignment adjustment in one instruction.
        assembler.mov(rsp, rbp);
        assembler.pop(rbp);
    }

    /// <summary>
    /// Generates assembly instructions to move values from a set of source registers
    /// into the correct argument registers (RCX, RDX, R8, R9) as defined by the x64 fastcall convention.
    /// </summary>
    /// <param name="assembler">The assembler instance.</param>
    /// <param name="sourceRegisters">The source registers to read from, in the order of the arguments.</param>
    public static void RelocateRegistersToX64FastcallRegisterArgs(this Assembler assembler, params AssemblerRegister64[] sourceRegisters)
    {
        if (sourceRegisters == null || sourceRegisters.Length == 0)
        {
            return;
        }

        if (sourceRegisters.Length > 4)
        {
            throw new ArgumentException("Cannot pass more than 4 GPR arguments with this helper.", nameof(sourceRegisters));
        }

        // The order of the first four integer/pointer argument registers in the x64 calling convention
        Register[] fastcallArgRegs = [Register.RCX, Register.RDX, Register.R8, Register.R9];

        for (int i = 0; i < sourceRegisters.Length; i++)
        {
            AssemblerRegister64 sourceRegister = sourceRegisters[i];
            AssemblerRegister64 destRegister = new(fastcallArgRegs[i]);

            // Optimization: Don't generate a redundant "mov" if the value is already in the correct register.
            if (sourceRegister != destRegister)
            {
                assembler.mov(destRegister, sourceRegister);
            }
        }
    }
}
