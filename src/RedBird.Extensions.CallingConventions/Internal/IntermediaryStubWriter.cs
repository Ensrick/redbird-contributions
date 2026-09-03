using System;
using System.IO;
using Iced.Intel;
using RedBird.Core.Memory;

namespace RedBird.Extensions.CallingConventions.Internal;

internal static class IntermediaryStubWriter
{
    public static IntPtr WriteX86(Assembler assembler, int capacity)
    {
        IntPtr address = NativeMemoryManager.AllocateStub(capacity);
        if (address == IntPtr.Zero)
            throw new InvalidOperationException("Could not allocate executable memory for an x86 calling-convention stub.");

        UInt64 origin = unchecked((UInt32)address.ToInt32());
        return AssembleAndWrite(assembler, address, origin, capacity, "x86");
    }

    public static IntPtr WriteX64(Assembler assembler, UInt64 anchor, int capacity)
    {
        IntPtr address = NativeMemoryManager.AllocateStub(anchor, capacity);
        if (address == IntPtr.Zero)
            throw new InvalidOperationException("Could not allocate nearby executable memory for an x64 vectorcall stub.");

        return AssembleAndWrite(assembler, address, (UInt64)address, capacity, "x64");
    }

    private static IntPtr AssembleAndWrite(Assembler assembler, IntPtr address, UInt64 origin, int capacity, string architecture)
    {
        using MemoryStream stream = new();
        if (!assembler.TryAssemble(new StreamCodeWriter(stream), origin, out string? error, out _))
            throw new InvalidOperationException($"Could not assemble the {architecture} calling-convention stub: {error}");

        byte[] code = stream.ToArray();
        if (code.Length > capacity)
        {
            throw new InvalidOperationException($"The generated {architecture} calling-convention stub needs {code.Length} bytes, which exceeds its {capacity}-byte allocation.");
        }

        if (!NativeMemoryManager.WriteStub(address, code))
            throw new InvalidOperationException($"Could not write the generated {architecture} calling-convention stub.");

        return address;
    }
}
