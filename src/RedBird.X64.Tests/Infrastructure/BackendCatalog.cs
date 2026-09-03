using System;
using System.Runtime.InteropServices;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.MinHook;
using RedBird.Backends.NativeX64;
using RedBird.Backends.PolyHook2;

namespace RedBird.X64.Tests.Infrastructure;

/// <summary>
/// Resolves the detour engines under test.
/// </summary>
/// <remarks>
/// <para>
/// The theories are parameterised by engine <i>name</i> rather than by an <see cref="IHookBackend"/> instance, because xUnit has to serialise theory data to identify and re-run an individual case. 
/// A string keeps the test names readable in the runner (<c>Enable_RedirectsCalls(backendName: "PolyHook2")</c>) and lets the backend itself be constructed inside the test, where a failure to load can be reported properly.
/// </para>
/// </remarks>
public static class BackendCatalog
{
    /// <summary>Name of the managed engine.</summary>
    public const string NativeX64 = "NativeX64";

    /// <summary>Name of the PolyHook2-backed engine.</summary>
    public const string PolyHook2 = "PolyHook2";

    /// <summary>Name of the MinHook.NET-backed engine.</summary>
    public const string MinHook = "MinHook";

    /// <summary>Every engine the suite knows about, for cross-engine theories.</summary>
    public static TheoryData<string> All => new() { NativeX64, PolyHook2, MinHook };

    /// <summary>
    /// Skips the current test when <paramref name="name"/> cannot relocate a jump that sits at the entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers two shapes that share one cause: a thunk (<c>jmp</c> in the original code) and a layered hook (<c>jmp</c> written by whoever hooked first).
    /// </para>
    /// <para>
    /// MinHook.NET miscomputes the destination while building its trampoline, in <c>Trampoline.cs</c> the short and near JMP branches are swapped, so an <c>E9 cd</c> is decoded as though it carried an 8-bit displacement:
    /// </para>
    /// <code>
    /// if ((inst.Bytes[0] == 0xE9)) { //short JMP
    ///     dest = IntPtr.Add(dest, inst.Operands[0].LvalByte);      // rel32 read as rel8
    /// } else {
    ///     dest = IntPtr.Add(dest, inst.Operands[0].LvalSDWord);
    /// }
    /// </code>
    /// <para>
    /// This is an upstream defect, not a limitation of the backend. With the corrected <c>Trampoline.cs</c> in place and <c>MinHookBackend(refuseJumpPrologues: false)</c>, delete this gate and the affected theories should pass for all three engines.
    /// </para>
    /// </remarks>
    public static void RequireLeadingJumpRelocation(string name)
    {
        Assert.SkipWhen(name == MinHook,
            "MinHook.NET reads a near JMP's rel32 displacement as a rel8 (Trampoline.cs, the short/near branches are swapped), so it cannot relocate a jump found at the entry point - which covers both thunks and layered hooks. Remove this gate once the patched Trampoline.cs is in use.");
    }

    /// <summary>
    /// Skips the current test when <paramref name="name"/> cannot relocate a RIP-relative operand found in the prologue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MinHook.NET reads the displacement from the wrong operand while rewriting the instruction into its trampoline:
    /// </para>
    /// <code>
    /// uint updatedRel = (uint)((ulong)oldInst + (ulong)inst.Length
    ///                        + inst.Operands[0].LvalUDWord          // operand 0 is the register
    ///                        - ((ulong)newInst + (ulong)inst.Length));
    /// </code>
    /// <para>
    /// For <c>mov eax, [rip+disp32]</c> operand 0 is <c>EAX</c>; the memory reference is operand 1. 
    /// The displacement therefore reads as zero and the relocated instruction ends up reading from <c>oldInst + length</c>, the bytes immediately after the original instruction.
    /// </para>
    /// <para>
    /// <b>This one deserves more caution than a skipped test suggests.</b> The other two gaps announce themselves: a mangled jump crashes, and a branch back into the prologue crashes.
    /// This one does not. The hook installs cleanly, the callback runs, <c>Original</c> returns: and the displaced instruction quietly reads the wrong memory. 
    /// RIP-relative operands are ordinary in compiler output (<c>lea rcx, [rip+x]</c> for a string literal, a global, a vtable), so a real target is more likely to contain one than not.
    /// </para>
    /// </remarks>
    public static void RequireRipRelativeRelocation(string name)
    {
        Assert.SkipWhen(name == MinHook,
            "MinHook.NET reads a RIP-relative displacement from Operands[0], which is the register rather than the memory operand (Trampoline.cs), so the relocated instruction addresses the wrong memory. Unlike the other MinHook gaps this one fails silently rather than crashing: weigh it before using this engine against real prologues.");
    }

    /// <summary>
    /// Skips the current test when <paramref name="name"/> cannot notice a branch from beyond the prologue back into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="RequireLeadingJumpRelocation"/>, this one is a design difference.
    /// </para>
    /// <para>
    /// MinHook examines only the bytes it is about to displace. Given a loop whose backwards branch sits past those bytes and targets an address inside them, it never disassembles the branch,
    /// so it cannot know the region needs widening, and the relocated loop ends up jumping into the middle of the installed patch. PolyHook2 scans a 100-byte window and <c>NativeX64</c>
    /// scans the whole function, so both widen the displaced region to cover the branch.
    /// </remarks>
    public static void RequireBranchIntoPrologueSupport(string name)
    {
        Assert.SkipWhen(name == MinHook,
            "MinHook only inspects the bytes it displaces, so a branch from beyond the prologue back into it is invisible to it and the displaced region is never widened to cover the branch. This is a design difference, not a bug.");
    }


    /// <summary>
    /// Returns the named engine, skipping the current test when it cannot run here.
    /// </summary>
    public static IHookBackend Resolve(string name)
    {
        Assert.SkipUnless(RuntimeInformation.ProcessArchitecture == Architecture.X64,
            $"The hooking tests assemble x86-64 machine code; this process is {RuntimeInformation.ProcessArchitecture}.");

        switch (name)
        {
            case NativeX64:
                return NativeDetourBackend.Instance;

            case PolyHook2:
                Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                    "PolyHook2.NET ships native assets for win-x86, win-x64 and linux-x64 only.");

                return ResolvePolyHook();

            case MinHook:
                Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                    "MinHook.NET calls VirtualProtect and the Win32 thread APIs directly and cannot run on this platform.");

                return ResolveMinHook();

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown backend.");
        }
    }

    private static IHookBackend ResolveMinHook()
    {
        try
        {
            return MinHookBackend.Instance;
        }
        catch (Exception ex)
        {
            Assert.Skip($"MinHook could not be initialised: {ex.Message}");
            throw; // Unreachable; Assert.Skip does not return.
        }
    }

    private static IHookBackend ResolvePolyHook()
    {
        try
        {
            return PolyHook2Backend.Instance;
        }
        catch (Exception ex)
        {
            Assert.Skip($"PolyHook2 could not be loaded: {ex.Message}");
            throw; // Unreachable; Assert.Skip does not return.
        }
    }

    /// <summary>
    /// Builds a request for the engine under test.
    /// </summary>
    public static DetourRequest<TFunction> Request<TFunction>(UInt64 target, TFunction callback, string name = "test")
        where TFunction : Delegate
        => new()
        {
            Name = name,
            TargetAddress = target,
            Callback = callback,
            Logger = null,
        };
}
