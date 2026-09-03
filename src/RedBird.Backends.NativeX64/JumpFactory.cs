using System;
using System.IO;
using Iced.Intel;
using RedBird.Core.Memory;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.Backends.NativeX64;

/// <summary>
/// Builds the entry patch for each detour scheme.
/// </summary>
internal static unsafe class JumpFactory
{
    private const int Bitness = 64;

    /// <summary>Bytes each scheme needs, before rounding up to an instruction boundary.</summary>
    public static int GetPatchSize(DetourScheme scheme) => scheme switch
    {
        DetourScheme.Indirect => 6,             // FF 25 <rel32>
        DetourScheme.Absolute => 14,            // FF 25 00000000 <abs64>
        DetourScheme.PushRet => 12,             // 48 B8 <imm64> | 50 | C3
        DetourScheme.PushRetRedZoneSafe => 23,  // lea/push/mov/xchg/ret imm16
        _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unknown detour scheme."),
    };

    /// <summary>
    /// Produces the patch for <paramref name="scheme"/>, allocating a pointer slot if the scheme needs one.
    /// </summary>
    /// <returns>Null when the scheme cannot be satisfied here, typically no memory within reach.</returns>
    public static DetourPatch? TryBuild(DetourScheme scheme, UInt64 functionAddress, UInt64 callbackAddress)
    {
        return scheme switch
        {
            DetourScheme.Indirect => BuildIndirect(functionAddress, callbackAddress),
            DetourScheme.Absolute => BuildAbsolute(callbackAddress),
            DetourScheme.PushRet => BuildAssembled(scheme, functionAddress, a =>
            {
                a.mov(rax, callbackAddress);
                a.push(rax);
                a.ret();
            }),
            DetourScheme.PushRetRedZoneSafe => BuildAssembled(scheme, functionAddress, a =>
            {
                // Step over the System V red zone before touching the stack, then swap the saved rax for the destination so ret jumps there with rax intact.
                // Straight out of PolyHook2's INPLACE scheme; the ret 0x80 undoes the lea.
                a.lea(rsp, __qword_ptr[rsp - 0x80]);
                a.push(rax);
                a.mov(rax, callbackAddress);
                a.xchg(__qword_ptr[rsp], rax);
                a.ret(0x80);
            }),
            _ => null,
        };
    }

    /// <summary>
    /// <c>jmp qword ptr [rip+disp32]</c> with the destination in a slot allocated near the target.
    /// </summary>
    private static DetourPatch? BuildIndirect(UInt64 functionAddress, UInt64 callbackAddress)
    {
        IntPtr slot = NativeMemoryManager.AllocateStub(functionAddress, sizeof(UInt64));
        if (slot == IntPtr.Zero)
            return null;

        // Check the range before writing anything, so a rejected scheme leaves no half-built slot.
        // The rel32 is measured from the end of the 6-byte instruction.
        Int64 delta = (Int64)(UInt64)slot - (Int64)(functionAddress + 6);
        if (delta < Int32.MinValue || delta > Int32.MaxValue)
            return null;

        // Not a direct store: the slab may be mapped execute-read, and on a second detour it is also a page some other stub already lives on.
        if (!NativeMemoryManager.WriteStub(slot, callbackAddress))
            return null;

        byte[] bytes = new byte[6];
        bytes[0] = 0xFF;
        bytes[1] = 0x25;

        fixed (byte* p = bytes)
        {
            *(Int32*)(p + 2) = (Int32)delta;
        }

        return new DetourPatch { Scheme = DetourScheme.Indirect, Bytes = bytes, PointerSlot = slot };
    }

    /// <summary><c>jmp qword ptr [rip+0]</c> with the destination inline immediately after it.</summary>
    private static DetourPatch BuildAbsolute(UInt64 callbackAddress)
        => new()
        {
            Scheme = DetourScheme.Absolute,
            Bytes = CodePatch.BuildAbsoluteJump(callbackAddress),
        };

    private static DetourPatch? BuildAssembled(DetourScheme scheme, UInt64 functionAddress, Action<Assembler> build)
    {
        Assembler assembler = new(Bitness);
        build(assembler);

        using MemoryStream stream = new();
        if (!assembler.TryAssemble(new StreamCodeWriter(stream), functionAddress, out _, out _))
            return null;

        return new DetourPatch { Scheme = scheme, Bytes = stream.ToArray() };
    }
}
