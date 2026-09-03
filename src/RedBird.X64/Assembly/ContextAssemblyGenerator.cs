using System;
using Iced.Intel;
using RedBird.Core.Platform;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Assembly;

/// <summary>
/// Emits the save / call / restore sequence around a managed context-hook callback.
/// </summary>
internal static class ContextAssemblyGenerator
{
    // Struct layout offsets, matching X64SmartCPUContext.
    private const int Offset_Rax = 0x00;
    private const int Offset_Rbx = 0x08;
    private const int Offset_Rcx = 0x10;
    private const int Offset_Rdx = 0x18;
    private const int Offset_Rsi = 0x20;
    private const int Offset_Rdi = 0x28;
    private const int Offset_Rbp = 0x30;
    private const int Offset_Rsp = 0x38;
    private const int Offset_R8 = 0x40;
    private const int Offset_R9 = 0x48;
    private const int Offset_R10 = 0x50;
    private const int Offset_R11 = 0x58;
    private const int Offset_R12 = 0x60;
    private const int Offset_R13 = 0x68;
    private const int Offset_R14 = 0x70;
    private const int Offset_R15 = 0x78;
    private const int Offset_Flags = 0x80;

    /// <summary>16 registers * 8 bytes + flags + padding to a 16-byte multiple.</summary>
    private const int ContextSize = 144;

    /// <summary>Slot below the context holding the pre-alignment stack pointer. Keeps 16-byte alignment.</summary>
    private const int SaveSlotSize = 16;

    public static void Generate(Assembler asm, IntPtr fnCallback, X64SmartCPUContextRegs regs)
    {
        int redZone = PlatformInfo.RedZoneSize;                    // 128 on System V, 0 on Windows
        int shadowSpace = PlatformInfo.IsWindows ? 32 : 0;         // Microsoft x64 caller-allocated
        AssemblerRegister64 argumentRegister = PlatformInfo.IsWindows ? rcx : rdi;

        // Step over the caller's red zone before touching the stack at all.
        if (redZone != 0)
            asm.sub(rsp, redZone);

        // Allocate the context; it lives at [rsp].
        asm.sub(rsp, ContextSize);

        // RAX is the generator's scratch register. Snapshot it before the first LEA even when it is not part of the public capture set, otherwise the flags path stores the calculated stack pointer in the RAX slot instead of the incoming value.
        asm.mov(__qword_ptr[rsp + Offset_Rax], rax);
        if (regs.HasFlag(X64SmartCPUContextRegs.RBX)) asm.mov(__qword_ptr[rsp + Offset_Rbx], rbx);
        if (regs.HasFlag(X64SmartCPUContextRegs.RCX)) asm.mov(__qword_ptr[rsp + Offset_Rcx], rcx);
        if (regs.HasFlag(X64SmartCPUContextRegs.RDX)) asm.mov(__qword_ptr[rsp + Offset_Rdx], rdx);
        if (regs.HasFlag(X64SmartCPUContextRegs.RSI)) asm.mov(__qword_ptr[rsp + Offset_Rsi], rsi);
        if (regs.HasFlag(X64SmartCPUContextRegs.RDI)) asm.mov(__qword_ptr[rsp + Offset_Rdi], rdi);
        if (regs.HasFlag(X64SmartCPUContextRegs.RBP)) asm.mov(__qword_ptr[rsp + Offset_Rbp], rbp);

        // Report the caller's rsp as it was at the hook site, before the red-zone step and the context allocation.
        asm.lea(rax, __qword_ptr[rsp + ContextSize + redZone]);
        asm.mov(__qword_ptr[rsp + Offset_Rsp], rax);

        // rax is the scratch register for the sequence above and below.
        bool raxCaptured = regs.HasFlag(X64SmartCPUContextRegs.RAX);

        if (regs.HasFlag(X64SmartCPUContextRegs.R8)) asm.mov(__qword_ptr[rsp + Offset_R8], r8);
        if (regs.HasFlag(X64SmartCPUContextRegs.R9)) asm.mov(__qword_ptr[rsp + Offset_R9], r9);
        if (regs.HasFlag(X64SmartCPUContextRegs.R10)) asm.mov(__qword_ptr[rsp + Offset_R10], r10);
        if (regs.HasFlag(X64SmartCPUContextRegs.R11)) asm.mov(__qword_ptr[rsp + Offset_R11], r11);
        if (regs.HasFlag(X64SmartCPUContextRegs.R12)) asm.mov(__qword_ptr[rsp + Offset_R12], r12);
        if (regs.HasFlag(X64SmartCPUContextRegs.R13)) asm.mov(__qword_ptr[rsp + Offset_R13], r13);
        if (regs.HasFlag(X64SmartCPUContextRegs.R14)) asm.mov(__qword_ptr[rsp + Offset_R14], r14);
        if (regs.HasFlag(X64SmartCPUContextRegs.R15)) asm.mov(__qword_ptr[rsp + Offset_R15], r15);

        if (regs.HasFlag(X64SmartCPUContextRegs.Flags))
        {
            asm.pushfq();
            asm.pop(rax);
            asm.mov(__qword_ptr[rsp + Offset_Flags], rax);
            asm.mov(rax, __qword_ptr[rsp + Offset_Rax]);
        }
        else if (!raxCaptured)
        {
            asm.mov(rax, __qword_ptr[rsp + Offset_Rax]);
        }

        // --- Call sequence -------------------------------------------------
        //
        //   rax  = context pointer (also the pre-alignment rsp)
        //   rsp  = aligned to 16, with one slot holding the value to restore
        //
        // rsp is arbitrary at a hook site, so it has to be aligned explicitly: the callback is
        // JIT-compiled managed code and may use aligned SSE moves against its own frame.

        asm.mov(rax, rsp);                       // context pointer
        asm.mov(argumentRegister, rax);          // first integer argument (rcx on MS x64, rdi on System V)

        asm.and(rsp, -16);                       // align
        asm.sub(rsp, SaveSlotSize);              // reserve, keeping the alignment
        asm.mov(__qword_ptr[rsp], rax);          // remember where to come back to

        if (shadowSpace != 0)
            asm.sub(rsp, shadowSpace);           // Microsoft x64 requires 32 bytes of shadow space

        asm.mov(rax, (UInt64)fnCallback);
        asm.call(rax);

        if (shadowSpace != 0)
            asm.add(rsp, shadowSpace);

        asm.mov(rsp, __qword_ptr[rsp]);          // back to the context pointer

        // --- Restore -------------------------------------------------------

        if (regs.HasFlag(X64SmartCPUContextRegs.Flags))
        {
            asm.mov(rax, __qword_ptr[rsp + Offset_Flags]);
            asm.push(rax);
            asm.popfq();
        }

        if (regs.HasFlag(X64SmartCPUContextRegs.R15)) asm.mov(r15, __qword_ptr[rsp + Offset_R15]);
        if (regs.HasFlag(X64SmartCPUContextRegs.R14)) asm.mov(r14, __qword_ptr[rsp + Offset_R14]);
        if (regs.HasFlag(X64SmartCPUContextRegs.R13)) asm.mov(r13, __qword_ptr[rsp + Offset_R13]);
        if (regs.HasFlag(X64SmartCPUContextRegs.R12)) asm.mov(r12, __qword_ptr[rsp + Offset_R12]);
        if (regs.HasFlag(X64SmartCPUContextRegs.R11)) asm.mov(r11, __qword_ptr[rsp + Offset_R11]);
        if (regs.HasFlag(X64SmartCPUContextRegs.R10)) asm.mov(r10, __qword_ptr[rsp + Offset_R10]);
        if (regs.HasFlag(X64SmartCPUContextRegs.R9)) asm.mov(r9, __qword_ptr[rsp + Offset_R9]);
        if (regs.HasFlag(X64SmartCPUContextRegs.R8)) asm.mov(r8, __qword_ptr[rsp + Offset_R8]);

        // Offset_Rsp is informational; restoring it would desynchronise the stub's own frame.

        if (regs.HasFlag(X64SmartCPUContextRegs.RBP)) asm.mov(rbp, __qword_ptr[rsp + Offset_Rbp]);
        if (regs.HasFlag(X64SmartCPUContextRegs.RDI)) asm.mov(rdi, __qword_ptr[rsp + Offset_Rdi]);
        if (regs.HasFlag(X64SmartCPUContextRegs.RSI)) asm.mov(rsi, __qword_ptr[rsp + Offset_Rsi]);
        if (regs.HasFlag(X64SmartCPUContextRegs.RDX)) asm.mov(rdx, __qword_ptr[rsp + Offset_Rdx]);
        if (regs.HasFlag(X64SmartCPUContextRegs.RCX)) asm.mov(rcx, __qword_ptr[rsp + Offset_Rcx]);
        if (regs.HasFlag(X64SmartCPUContextRegs.RBX)) asm.mov(rbx, __qword_ptr[rsp + Offset_Rbx]);
        if (regs.HasFlag(X64SmartCPUContextRegs.RAX)) asm.mov(rax, __qword_ptr[rsp + Offset_Rax]);

        asm.add(rsp, ContextSize + redZone);
    }
}
