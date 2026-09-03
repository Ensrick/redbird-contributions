using System;

namespace RedBird.Backends.NativeX86;

/// <summary>Builds the five-byte position-independent x86 near jump used at hook entries.</summary>
internal static unsafe class X86JumpFactory
{
    public const int PatchSize = 5;

    /// <summary>
    /// Emits <c>E9 rel32</c>. In 32-bit mode the addition wraps modulo 2^32, so this instruction reaches every address in the process without a nearby slot or register-clobbering sequence.
    /// </summary>
    public static byte[] BuildDirect(UInt64 functionAddress, UInt64 destinationAddress)
        => BuildDirect(functionAddress, destinationAddress, PatchSize);

    /// <summary>Emits a near jump and pads the rest of a displaced instruction range with NOPs.</summary>
    public static byte[] BuildDirect(UInt64 functionAddress, UInt64 destinationAddress, int patchLength)
    {
        if (patchLength < PatchSize)
            throw new ArgumentOutOfRangeException(nameof(patchLength));
        if (functionAddress > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(functionAddress));
        if (destinationAddress > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(destinationAddress));

        UInt32 next = unchecked((UInt32)functionAddress + PatchSize);
        Int32 displacement = unchecked((Int32)((UInt32)destinationAddress - next));

        byte[] bytes = new byte[patchLength];
        for (int index = 0; index < bytes.Length; index++)
            bytes[index] = 0x90;
        bytes[0] = 0xE9;
        fixed (byte* pointer = bytes)
            *(Int32*)(pointer + 1) = displacement;

        return bytes;
    }
}
