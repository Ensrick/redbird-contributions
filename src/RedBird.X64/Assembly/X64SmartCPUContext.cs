using System;
using System.Runtime.InteropServices;

namespace RedBird.X64.Assembly;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct X64SmartCPUContext
{
    // General Purpose Registers
    public UInt64 RAX;
    public UInt64 RBX;
    public UInt64 RCX;
    public UInt64 RDX;
    public UInt64 RSI;
    public UInt64 RDI;
    public UInt64 RBP;
    public UInt64 RSP;
    public UInt64 R8;
    public UInt64 R9;
    public UInt64 R10;
    public UInt64 R11;
    public UInt64 R12;
    public UInt64 R13;
    public UInt64 R14;
    public UInt64 R15;

    public UInt64 Rflags;

    /// <summary>
    /// Gets a pointer to a stack argument at a specific index.
    /// Index 0 = First argument pushed to stack (often "Stack Arg 5" in x64 fastcall).
    /// </summary>
    public readonly T* GetStackPtr<T>(int offsetBytes) where T : unmanaged
    {
        // Rsp points to the return address or the slot above it depending on where you hooked.
        // Assuming standard hook at function start: Rsp points to Return Address.
        // Arguments start at Rsp + 0xsomething.
        // This helper allows raw access based on the captured RSP.
        return (T*)(RSP + (ulong)offsetBytes);
    }

    /// <summary>
    /// Helper for standard x64 FastCall shadow space logic.
    /// </summary>
    public readonly T* GetFastCallStackArg<T>(int index) where T : unmanaged
    {
        int offset = 0x28 + ((index - 5) * 8);
        return (T*)(RSP + (ulong)offset);
    }
}
