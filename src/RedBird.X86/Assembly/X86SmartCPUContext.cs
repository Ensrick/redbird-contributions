using System;
using System.Runtime.InteropServices;

namespace RedBird.X86.Assembly;

/// <summary>Mutable snapshot exposed to a 32-bit context-hook callback.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct X86SmartCPUContext
{
    public UInt32 EAX;
    public UInt32 EBX;
    public UInt32 ECX;
    public UInt32 EDX;
    public UInt32 ESI;
    public UInt32 EDI;
    public UInt32 EBP;
    public UInt32 ESP;
    public UInt32 EFlags;

    /// <summary>Returns a typed pointer relative to the stack pointer captured at the hook site.</summary>
    public readonly T* GetStackPtr<T>(int offsetBytes) where T : unmanaged
        => (T*)(ESP + unchecked((UInt32)offsetBytes));

    /// <summary>Returns a cdecl/stdcall stack argument; index zero is at <c>ESP + 4</c>.</summary>
    public readonly T* GetStackArgument<T>(int index) where T : unmanaged
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        return (T*)(ESP + 4u + (UInt32)(index * 4));
    }

    /// <summary>Returns the return address present at the hook-site stack pointer.</summary>
    public readonly UInt32 ReturnAddress => *(UInt32*)ESP;
}
