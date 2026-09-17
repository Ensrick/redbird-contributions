using System;
using Iced.Intel;
using RedBird.Abstractions.CallingConventions;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Extensions;

/// <summary>Assembler helpers for common 32-bit x86 hook and calling-convention tasks.</summary>
public static class AssemblerExtensions
{
    /// <summary>
    /// Emits a five-byte x86 near jump. Unlike x64, rel32 spans the complete 32-bit address  space modulo 2^32, so no absolute-jump sequence is required.
    /// </summary>
    public static void AddUnrestrictedJmp(this Assembler assembler, UInt64 address)
    {
        EnsureX86Address(address, nameof(address));
        assembler.AddInstruction(Instruction.CreateBranch(Code.Jmp_rel32_32, address));
    }

    /// <summary>Emits a returning five-byte x86 near call to any 32-bit address.</summary>
    public static void AddUnrestrictedCall(this Assembler assembler, UInt64 address)
    {
        EnsureX86Address(address, nameof(address));
        assembler.AddInstruction(Instruction.CreateBranch(Code.Call_rel32_32, address));
    }

    public static void AddInstructions(this Assembler assembler, ReadOnlySpan<Instruction> instructions)
    {
        foreach (Instruction instruction in instructions)
            assembler.AddInstruction(instruction);
    }

    public static void AddInstructions(this Assembler assembler, Instruction[] instructions)
        => AddInstructions(assembler, instructions.AsSpan());

    /// <summary>Pushes EAX, EBX, ECX, EDX, ESI, EDI and EBP. ESP is intentionally excluded.</summary>
    public static void PushAllGPR(this Assembler assembler)
    {
        assembler.push(eax);
        assembler.push(ebx);
        assembler.push(ecx);
        assembler.push(edx);
        assembler.push(esi);
        assembler.push(edi);
        assembler.push(ebp);
    }

    /// <summary>Restores registers saved by <see cref="PushAllGPR"/>.</summary>
    public static void PopAllGPR(this Assembler assembler)
    {
        assembler.pop(ebp);
        assembler.pop(edi);
        assembler.pop(esi);
        assembler.pop(edx);
        assembler.pop(ecx);
        assembler.pop(ebx);
        assembler.pop(eax);
    }

    /// <summary>Pushes EAX, ECX and EDX, volatile under the common x86 conventions.</summary>
    public static void PushAllVolatile(this Assembler assembler, bool preserveEax = true)
    {
        if (preserveEax)
            assembler.push(eax);
        assembler.push(ecx);
        assembler.push(edx);
    }

    /// <summary>Restores registers saved by <see cref="PushAllVolatile"/>.</summary>
    public static void PopAllVolatile(this Assembler assembler, bool preserveEax = true)
    {
        assembler.pop(edx);
        assembler.pop(ecx);
        if (preserveEax)
            assembler.pop(eax);
    }

    /// <summary>
    /// Saves XMM0-XMM7, which are caller-saved under the common 32-bit x86 ABIs.
    /// Unaligned moves are used because an arbitrary hook site may not have an aligned stack.
    /// </summary>
    public static void PushVolatileXMM(this Assembler assembler)
    {
        AssemblerRegisterXMM[] registers = [xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7];
        int spaceNeeded = registers.Length * 16;

        assembler.sub(esp, spaceNeeded);
        for (int index = 0; index < registers.Length; index++)
            assembler.movdqu(__xmmword_ptr[esp + index * 16], registers[index]);
    }

    /// <summary>Restores XMM0-XMM7 saved by <see cref="PushVolatileXMM"/>.</summary>
    public static void PopVolatileXMM(this Assembler assembler)
    {
        AssemblerRegisterXMM[] registers = [xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7];
        int spaceNeeded = registers.Length * 16;

        for (int index = registers.Length - 1; index >= 0; index--)
            assembler.movdqu(registers[index], __xmmword_ptr[esp + index * 16]);
        assembler.add(esp, spaceNeeded);
    }

    /// <summary>Pushes immediate arguments right-to-left as required by cdecl and stdcall.</summary>
    public static void PushArgumentsRightToLeft(this Assembler assembler, params UInt32[] arguments)
    {
        for (int index = arguments.Length - 1; index >= 0; index--)
            assembler.push(unchecked((Int32)arguments[index]));
    }


    /// <summary>
    /// Calls a target and applies the caller cleanup appropriate for the selected convention.
    /// Arguments must already be placed in registers/on the stack.
    /// </summary>
    public static void X86Call(
        this Assembler assembler,
        UInt64 targetAddress,
        X86CallingConvention convention,
        int stackArgumentBytes = 0)
    {
        if (stackArgumentBytes < 0 || (stackArgumentBytes & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(stackArgumentBytes), "x86 stack arguments must be a non-negative multiple of four bytes.");

        IsKnownConvention(convention);
        assembler.AddUnrestrictedCall(targetAddress);

        if (IsCallerClean(convention) && stackArgumentBytes != 0)
            assembler.add(esp, stackArgumentBytes);
    }

    public static void X86Cdecl(this Assembler assembler, UInt64 targetAddress, int stackArgumentBytes = 0)
        => assembler.X86Call(targetAddress, X86CallingConvention.Cdecl, stackArgumentBytes);

    public static void X86Stdcall(this Assembler assembler, UInt64 targetAddress, int stackArgumentBytes = 0)
        => assembler.X86Call(targetAddress, X86CallingConvention.Stdcall, stackArgumentBytes);

    public static void X86Thiscall(this Assembler assembler, UInt64 targetAddress, int stackArgumentBytes = 0)
        => assembler.X86Call(targetAddress, X86CallingConvention.Thiscall, stackArgumentBytes);

    public static void X86Fastcall(this Assembler assembler, UInt64 targetAddress, int stackArgumentBytes = 0)
        => assembler.X86Call(targetAddress, X86CallingConvention.Fastcall, stackArgumentBytes);

    public static void X86GccCdecl(this Assembler assembler, UInt64 targetAddress, int stackArgumentBytes = 0)
        => assembler.X86Call(targetAddress, X86CallingConvention.GccCdecl, stackArgumentBytes);

    public static void X86GccThiscall(this Assembler assembler, UInt64 targetAddress, int stackArgumentBytes = 0)
        => assembler.X86Call(targetAddress, X86CallingConvention.GccThiscall, stackArgumentBytes);

    public static void X86Pascal(this Assembler assembler, UInt64 targetAddress, int stackArgumentBytes = 0)
        => assembler.X86Call(targetAddress, X86CallingConvention.Pascal, stackArgumentBytes);

    /// <summary>
    /// Creates a temporary 16-byte-aligned stack frame, lets the caller push exactly
    /// <paramref name="stackArgumentBytes"/> bytes, and performs a cdecl call. 
    /// This is suitable for managed reverse-P/Invoke thunks on both Windows and System V i386.
    /// </summary>
    public static void X86AlignedCdecl(
        this Assembler assembler,
        UInt64 targetAddress,
        int stackArgumentBytes,
        Action<Assembler> prepareArguments)
    {
        if (prepareArguments is null)
            throw new ArgumentNullException(nameof(prepareArguments));
        if (stackArgumentBytes < 0 || (stackArgumentBytes & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(stackArgumentBytes));

        assembler.push(ebp);
        assembler.mov(ebp, esp);
        assembler.and(esp, -16);

        int padding = (16 - (stackArgumentBytes & 15)) & 15;
        if (padding != 0)
            assembler.sub(esp, padding);

        prepareArguments(assembler);
        assembler.AddUnrestrictedCall(targetAddress);
        assembler.mov(esp, ebp);
        assembler.pop(ebp);
    }


    /// <summary>Defines where a 4-byte x86 stack slot obtains its value.</summary>
    public enum X86ArgumentSourceType
    {
        /// <summary>Copy a slot from the original function-entry stack.</summary>
        FromOriginalStack,

        /// <summary>Use a hardcoded 32-bit bit pattern.</summary>
        FromImmediate,
    }

    /// <summary>
    /// Describes one 4-byte outgoing x86 stack slot.
    /// </summary>
    /// <param name="SourceType">Where the slot obtains its value.</param>
    /// <param name="Value">
    /// For <see cref="X86ArgumentSourceType.FromOriginalStack"/>, the zero-based slot index
    /// at the original function entry. For <see cref="X86ArgumentSourceType.FromImmediate"/>,
    /// the 32-bit value to pass.
    /// </param>
    public record X86StackArgument(X86ArgumentSourceType SourceType, UInt32 Value);

    /// <summary>
    /// Generates an aligned, state-preserving x86 call for a common native calling convention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stack arguments are supplied in declaration order. Cdecl, stdcall, thiscall, fastcall,
    /// and their GCC variants place the first supplied stack argument nearest the return
    /// address; Pascal reverses the memory order to model its left-to-right pushes.
    /// </para>
    /// <para>
    /// The argument-preparation callback runs after volatile state has been saved but before
    /// the temporary outgoing frame is allocated. Use it to prepare ECX for Microsoft thiscall,
    /// ECX/EDX for Microsoft fastcall, or any custom register inputs required by the target.
    /// </para>
    /// <para>
    /// <see cref="X86ArgumentSourceType.FromOriginalStack"/> uses the function-entry layout
    /// ([ESP] return address, first stack slot at [ESP+4]). It is valid only before the original
    /// function adjusts ESP.
    /// </para>
    /// </remarks>
    /// <param name="assembler">The assembler instance to write to.</param>
    /// <param name="targetAddress">The 32-bit target address.</param>
    /// <param name="convention">The target's native calling convention.</param>
    /// <param name="prepareArgumentsAction">Optional register-argument preparation.</param>
    /// <param name="preserveEAX">Whether to restore the original EAX instead of retaining the target's return value.</param>
    /// <param name="preserveXMM">Whether to preserve XMM0-XMM7.</param>
    /// <param name="stackArguments">The target's stack slots in declaration order.</param>
    public static void X86CallSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        X86CallingConvention convention,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveEAX = true,
        bool preserveXMM = false,
        params X86StackArgument[] stackArguments)
    {
        EnsureX86Address(targetAddress, nameof(targetAddress));
        IsKnownConvention(convention);
        if (stackArguments is null)
            throw new ArgumentNullException(nameof(stackArguments));

        int gprSaveSize = (preserveEAX ? 3 : 2) * 4;
        int xmmSaveSize = preserveXMM ? 8 * 16 : 0;
        int stackArgumentBytes = checked(stackArguments.Length * 4);
        int outgoingSpace = checked((stackArgumentBytes + 15) & ~15);
        int frameSize = checked(outgoingSpace + 16);
        bool callerClean = IsCallerClean(convention);
        bool leftToRight = convention == X86CallingConvention.Pascal;
        int restoreOffset = outgoingSpace - (callerClean ? 0 : stackArgumentBytes);

        for (int index = 0; index < stackArguments.Length; index++)
        {
            X86StackArgument argument = stackArguments[index];
            if (argument is null)
                throw new ArgumentException("A stack-argument descriptor cannot be null.", nameof(stackArguments));

            if (argument.SourceType is not X86ArgumentSourceType.FromOriginalStack and
                not X86ArgumentSourceType.FromImmediate)
            {
                throw new ArgumentOutOfRangeException(nameof(stackArguments), $"Unknown x86 argument source {argument.SourceType}.");
            }

            if (argument.SourceType == X86ArgumentSourceType.FromOriginalStack)
            {
                if (argument.Value > Int32.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(stackArguments), "An original stack index is too large.");

                _ = checked(xmmSaveSize + gprSaveSize + 4 + ((int)argument.Value * 4));
            }
        }

        assembler.PushAllVolatile(preserveEAX);
        if (preserveXMM)
            assembler.PushVolatileXMM();

        prepareArgumentsAction?.Invoke(assembler);

        // EAX is not an argument register in these conventions. Save the exact pre-alignment
        // ESP in a private slot so EBP remains untouched and can still carry hook state.
        assembler.mov(eax, esp);
        assembler.and(esp, -16);
        assembler.sub(esp, frameSize);
        assembler.mov(__dword_ptr[esp + outgoingSpace], eax);

        for (int index = 0; index < stackArguments.Length; index++)
        {
            X86StackArgument argument = stackArguments[index];
            int destinationIndex = leftToRight ? stackArguments.Length - 1 - index : index;
            int destinationOffset = destinationIndex * 4;

            if (argument.SourceType == X86ArgumentSourceType.FromOriginalStack)
            {
                int sourceOffset = checked(xmmSaveSize + gprSaveSize + 4 + ((int)argument.Value * 4));
                assembler.mov(eax, __dword_ptr[esp + outgoingSpace]);
                assembler.mov(eax, __dword_ptr[eax + sourceOffset]);
            }
            else
            {
                assembler.mov(eax, unchecked((Int32)argument.Value));
            }

            assembler.mov(__dword_ptr[esp + destinationOffset], eax);
        }

        assembler.AddUnrestrictedCall(targetAddress);

        // A callee-clean return advances ESP by the stack-argument byte count.
        // Account for that displacement when locating the private saved-ESP slot.
        assembler.mov(esp, __dword_ptr[esp + restoreOffset]);

        if (preserveXMM)
            assembler.PopVolatileXMM();
        assembler.PopAllVolatile(preserveEAX);
    }

    public static void X86CdeclSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveEAX = true,
        bool preserveXMM = false,
        params X86StackArgument[] stackArguments)
        => assembler.X86CallSafe(targetAddress, X86CallingConvention.Cdecl, prepareArgumentsAction, preserveEAX, preserveXMM, stackArguments);

    public static void X86StdcallSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveEAX = true,
        bool preserveXMM = false,
        params X86StackArgument[] stackArguments)
        => assembler.X86CallSafe(targetAddress, X86CallingConvention.Stdcall, prepareArgumentsAction, preserveEAX, preserveXMM, stackArguments);

    public static void X86ThiscallSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveEAX = true,
        bool preserveXMM = false,
        params X86StackArgument[] stackArguments)
        => assembler.X86CallSafe(targetAddress, X86CallingConvention.Thiscall, prepareArgumentsAction, preserveEAX, preserveXMM, stackArguments);

    public static void X86FastcallSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveEAX = true,
        bool preserveXMM = false,
        params X86StackArgument[] stackArguments)
        => assembler.X86CallSafe(targetAddress, X86CallingConvention.Fastcall, prepareArgumentsAction, preserveEAX, preserveXMM, stackArguments);

    public static void X86GccCdeclSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveEAX = true,
        bool preserveXMM = false,
        params X86StackArgument[] stackArguments)
        => assembler.X86CallSafe(targetAddress, X86CallingConvention.GccCdecl, prepareArgumentsAction, preserveEAX, preserveXMM, stackArguments);

    public static void X86GccThiscallSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveEAX = true,
        bool preserveXMM = false,
        params X86StackArgument[] stackArguments)
        => assembler.X86CallSafe(targetAddress, X86CallingConvention.GccThiscall, prepareArgumentsAction, preserveEAX, preserveXMM, stackArguments);

    public static void X86PascalSafe(
        this Assembler assembler,
        UInt64 targetAddress,
        Action<Assembler>? prepareArgumentsAction = null,
        bool preserveEAX = true,
        bool preserveXMM = false,
        params X86StackArgument[] stackArguments)
        => assembler.X86CallSafe(targetAddress, X86CallingConvention.Pascal, prepareArgumentsAction, preserveEAX, preserveXMM, stackArguments);

    /// <summary>Moves up to two values into Microsoft x86 fastcall's ECX/EDX argument registers.</summary>
    public static void RelocateRegistersToX86FastcallArgs(
        this Assembler assembler,
        params AssemblerRegister32[] sourceRegisters)
    {
        if (sourceRegisters.Length > 2)
            throw new ArgumentException("Microsoft x86 fastcall has only two GPR argument registers.", nameof(sourceRegisters));

        AssemblerRegister32[] destinations = [ecx, edx];
        for (int index = 0; index < sourceRegisters.Length; index++)
        {
            if (sourceRegisters[index] != destinations[index])
                assembler.mov(destinations[index], sourceRegisters[index]);
        }
    }

    private static void EnsureX86Address(UInt64 address, string parameterName)
    {
        if (address > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "The address is outside the x86 virtual address space.");
    }

    private static bool IsCallerClean(X86CallingConvention convention)
        => convention is X86CallingConvention.Cdecl or
            X86CallingConvention.GccCdecl or
            X86CallingConvention.GccThiscall;

    private static void IsKnownConvention(X86CallingConvention convention)
    {
        if (convention is not X86CallingConvention.Cdecl and
            not X86CallingConvention.GccCdecl and
            not X86CallingConvention.Stdcall and
            not X86CallingConvention.Fastcall and
            not X86CallingConvention.Thiscall and
            not X86CallingConvention.GccThiscall and
            not X86CallingConvention.Pascal)
        {
            throw new ArgumentOutOfRangeException(nameof(convention));
        }
    }
}
