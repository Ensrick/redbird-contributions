using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Tests.Infrastructure;

/// <summary>Delegate shapes the assembled targets are called through.</summary>
public static class TestDelegates
{
    /// <summary><c>int f(int, int)</c>.</summary>
    public delegate int BinaryInt(int a, int b);

    /// <summary><c>int f(int, int, int, int)</c>, to exercise all four register arguments.</summary>
    public delegate int QuadInt(int a, int b, int c, int d);

    /// <summary><c>int f()</c>.</summary>
    public delegate int NullaryInt();
}

/// <summary>
/// The target shapes the suite hooks. Each one isolates a property of the prologue that the engine has to handle.
/// </summary>
public static class TestFunctions
{
    /// <summary>
    /// <c>a + b</c>, with the code before the <c>ret</c> padded to exactly
    /// <paramref name="prologueBytes"/>.
    /// </summary>
    /// <remarks>
    /// The padding is what makes prologue-length a test parameter rather than an accident of what the assembler happened to emit. 
    /// 5 bytes is below every scheme's minimum, 6 is exactly the indirect jump, 14 the absolute one.
    /// </remarks>
    public static ExecutableFunction Add(int prologueBytes = 24)
        => ExecutableFunction.Create((asm, _) =>
        {
            // mov eax, arg0 (2 bytes); add eax, arg1 (2 bytes)
            asm.mov(eax, AbiRegisters.Arg0D);
            asm.add(eax, AbiRegisters.Arg1D);

            PadTo(asm, 4, prologueBytes);

            asm.ret();
        });

    /// <summary><c>a + b + c + d</c>, so a wrong ABI or a clobbered register shows up as a wrong sum.</summary>
    public static ExecutableFunction AddFour()
        => ExecutableFunction.Create((asm, _) =>
        {
            asm.mov(eax, AbiRegisters.Arg0D);
            asm.add(eax, AbiRegisters.Arg1D);
            asm.add(eax, AbiRegisters.Arg2D);
            asm.add(eax, AbiRegisters.Arg3D);

            PadTo(asm, 8, 24);

            asm.ret();
        });

    /// <summary>Returns a constant.</summary>
    public static ExecutableFunction Constant(int value, int prologueBytes = 24)
        => ExecutableFunction.Create((asm, _) =>
        {
            asm.mov(eax, value);          // 5 bytes
            PadTo(asm, 5, prologueBytes);
            asm.ret();
        });

    /// <summary>
    /// Reads its return value through a RIP-relative operand.
    /// </summary>
    /// <remarks>
    /// The single most common thing in a real prologue that relocation can get wrong: the displacement is relative to the instruction's own address, so moving the instruction into a
    /// trampoline without re-encoding it makes it read from the wrong place. A wrong answer here means relocation is broken; a crash means it is badly broken.
    /// </remarks>
    public static ExecutableFunction RipRelativeRead(int value)
        => ExecutableFunction.Create(
            (asm, layout) =>
            {
                // mov eax, dword ptr [rip + (data - next_ip)]
                asm.AddInstruction(Instruction.Create(
                    Code.Mov_r32_rm32,
                    Register.EAX,
                    new MemoryOperand(Register.RIP, (long)layout.DataAddress)));

                PadTo(asm, 6, 24);

                asm.ret();
            },
            layout => layout.SetData(value));

    /// <summary>
    /// Loops back into its own first few bytes, so part of the region the engine wants to overwrite is a live branch target.
    /// </summary>
    /// <remarks>
    /// This is the case <c>expandProlSelfJmps</c> exists for. If the displaced region is not widened to include the branch, the branch survives in the trampoline pointing at an address that now
    /// holds the middle of a jump patch. It usually does not crash immediately.
    /// <para>Returns 3.</para>
    /// </remarks>
    public static ExecutableFunction SelfBranchingLoop()
        => ExecutableFunction.Create((asm, _) =>
        {
            Label loop = asm.CreateLabel();

            asm.xor(eax, eax);        // 2 bytes, offset 0
            asm.Label(ref loop);      //           offset 2  <-- branch target inside the prologue
            asm.inc(eax);             // 2 bytes, offset 2
            asm.cmp(eax, 3);          // 3 bytes, offset 4
            asm.jl(loop);             // 2 bytes, offset 7
            asm.ret();                // 1 byte,  offset 9
        });

    /// <summary>Bytes in a near jump with a 32-bit displacement.</summary>
    private const int ThunkJumpSize = 5;

    /// <summary>
    /// Filler between the thunk and the body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It forces the jump to use a 32-bit displacement. 
    /// With the body sitting immediately after it, Iced encodes <c>jmp rel8</c>: two bytes.
    /// Anything past 127 bytes cannot be reached by a short branch, so the encoder has no choice and the layout becomes deterministic.
    /// </para>
    /// </remarks>
    private const int ThunkGap = 256;

    /// <summary>
    /// A <c>jmp rel32</c> thunk in front of the real body, the shape import and incremental-link thunks take.
    /// </summary>
    /// <remarks>
    /// <param name="value">What the body returns.</param>
    /// <param name="bodyOffset">Distance from the entry point to the real body.</param>
    public static ExecutableFunction ThunkTo(int value, out UInt64 bodyOffset)
    {
        bodyOffset = ThunkJumpSize + ThunkGap;

        return ExecutableFunction.Create((asm, _) =>
        {
            Label body = asm.CreateLabel();

            asm.jmp(body);

            for (int i = 0; i < ThunkGap; i++)
                asm.nop();

            asm.Label(ref body);
            asm.mov(eax, value);
            PadTo(asm, 5, 24);
            asm.ret();
        });
    }

    /// <summary>
    /// Three bytes and a <c>ret</c>: too small for any scheme to patch without running off the end of the function.
    /// </summary>
    /// <remarks>
    /// The expected outcome is a clean refusal. The failure mode being guarded against is an engine that writes 14 bytes over a 4-byte function and corrupts whatever follows.
    /// </remarks>
    public static ExecutableFunction TooSmall()
        => ExecutableFunction.Create((asm, _) =>
        {
            asm.xor(eax, eax);   // 2 bytes
            asm.ret();           // 1 byte
        });

    /// <summary>
    /// Emits single-byte <c>nop</c>s until the code reaches <paramref name="targetBytes"/>.
    /// </summary>
    private static void PadTo(Assembler asm, int emittedSoFar, int targetBytes)
    {
        for (int i = emittedSoFar; i < targetBytes; i++)
            asm.nop();
    }
}
