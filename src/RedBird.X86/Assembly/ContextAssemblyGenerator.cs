using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Assembly;

/// <summary>Emits the cdecl-compatible save/call/restore sequence used by x86 context hooks.</summary>
internal static class ContextAssemblyGenerator
{
    private const int OffsetEax = 0x00;
    private const int OffsetEbx = 0x04;
    private const int OffsetEcx = 0x08;
    private const int OffsetEdx = 0x0C;
    private const int OffsetEsi = 0x10;
    private const int OffsetEdi = 0x14;
    private const int OffsetEbp = 0x18;
    private const int OffsetEsp = 0x1C;
    private const int OffsetFlags = 0x20;
    private const int OffsetSavedEbp = 0x24;
    private const int FrameSize = 0x28;

    public static void Generate(Assembler assembler, IntPtr callback, X86SmartCPUContextRegs registers)
    {
        assembler.sub(esp, FrameSize);

        // EAX and EBP are scratch registers used by the stub itself. Preserve their incoming values even when the callback is not allowed to edit them.
        assembler.mov(__dword_ptr[esp + OffsetEax], eax);
        assembler.mov(__dword_ptr[esp + OffsetSavedEbp], ebp);
        if (registers.HasFlag(X86SmartCPUContextRegs.EBP)) assembler.mov(__dword_ptr[esp + OffsetEbp], ebp);

        if (registers.HasFlag(X86SmartCPUContextRegs.EBX)) assembler.mov(__dword_ptr[esp + OffsetEbx], ebx);
        if (registers.HasFlag(X86SmartCPUContextRegs.ECX)) assembler.mov(__dword_ptr[esp + OffsetEcx], ecx);
        if (registers.HasFlag(X86SmartCPUContextRegs.EDX)) assembler.mov(__dword_ptr[esp + OffsetEdx], edx);
        if (registers.HasFlag(X86SmartCPUContextRegs.ESI)) assembler.mov(__dword_ptr[esp + OffsetEsi], esi);
        if (registers.HasFlag(X86SmartCPUContextRegs.EDI)) assembler.mov(__dword_ptr[esp + OffsetEdi], edi);

        assembler.lea(eax, __dword_ptr[esp + FrameSize]);
        assembler.mov(__dword_ptr[esp + OffsetEsp], eax);

        if (registers.HasFlag(X86SmartCPUContextRegs.Flags))
        {
            assembler.pushfd();
            assembler.pop(eax);
            assembler.mov(__dword_ptr[esp + OffsetFlags], eax);
        }

        // Keep the context in callee-saved EBP, dynamically align the callback stack and pass one cdecl argument. Both Windows x86 and System V i386 accept this layout.
        assembler.mov(ebp, esp);
        assembler.and(esp, -16);
        assembler.sub(esp, 12);
        assembler.push(ebp);
        assembler.mov(eax, unchecked((UInt32)callback.ToInt32()));
        assembler.call(eax);
        assembler.mov(esp, ebp);

        if (registers.HasFlag(X86SmartCPUContextRegs.Flags))
        {
            assembler.mov(eax, __dword_ptr[esp + OffsetFlags]);
            assembler.push(eax);
            assembler.popfd();
        }

        if (registers.HasFlag(X86SmartCPUContextRegs.EDI)) assembler.mov(edi, __dword_ptr[esp + OffsetEdi]);
        if (registers.HasFlag(X86SmartCPUContextRegs.ESI)) assembler.mov(esi, __dword_ptr[esp + OffsetEsi]);
        if (registers.HasFlag(X86SmartCPUContextRegs.EDX)) assembler.mov(edx, __dword_ptr[esp + OffsetEdx]);
        if (registers.HasFlag(X86SmartCPUContextRegs.ECX)) assembler.mov(ecx, __dword_ptr[esp + OffsetEcx]);
        if (registers.HasFlag(X86SmartCPUContextRegs.EBX)) assembler.mov(ebx, __dword_ptr[esp + OffsetEbx]);

        // EBP must always be restored because the stub used it internally. When selected, the callback may have replaced its slot and the edited value is intentionally used.
        if (registers.HasFlag(X86SmartCPUContextRegs.EBP))
            assembler.mov(ebp, __dword_ptr[esp + OffsetEbp]);
        else
            assembler.mov(ebp, __dword_ptr[esp + OffsetSavedEbp]);
        if (registers.HasFlag(X86SmartCPUContextRegs.EAX)) assembler.mov(eax, __dword_ptr[esp + OffsetEax]);

        assembler.add(esp, FrameSize);
    }
}
