using System;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX86;
using RedBird.Extensions.CallingConventions.NativeX86;
using RedBird.X86.Tests.Infrastructure;
using static Iced.Intel.AssemblerRegisters;
using RedBird.Abstractions.CallingConventions;

namespace RedBird.X86.Tests;

public sealed class CallingConventionIntermediaryTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FourInts(int first, int second, int third, int fourth);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Pair
    {
        public int Left;
        public int Right;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IntPairInt(int first, Pair pair, int third);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [X86CallingConvention(
        X86CallingConvention.Custom,
        StackCleanup = X86StackCleanup.Caller,
        ArgumentOrder = X86ArgumentOrder.RightToLeft)]
    [return: X86RegisterArgument(Register.EDX)]
    private delegate int CustomRegisters(
        [X86RegisterArgument(Register.EDI)] int first,
        [X86RegisterArgument(Register.EBP)] int second,
        int third);

    [Fact]
    public void Fastcall_TranslatesHookAndOriginalPaths()
    {
        Assert.SkipUnless(
            RuntimeInformation.ProcessArchitecture == Architecture.X86 &&
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "Microsoft x86 calling-convention execution requires a 32-bit Windows test host.");

        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, ecx);
            assembler.add(eax, edx);
            assembler.add(eax, __dword_ptr[esp + 4]);
            assembler.add(eax, __dword_ptr[esp + 8]);
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret(8);
        });

        // Managed callers use cdecl. This small test call-site supplies the target's native fastcall frame, including callee-clean stack arguments three and four.
        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(__dword_ptr[ebp + 20]);
            assembler.push(__dword_ptr[ebp + 16]);
            assembler.mov(ecx, __dword_ptr[ebp + 8]);
            assembler.mov(edx, __dword_ptr[ebp + 12]);
            assembler.call((UInt32)target.Address);
            assembler.pop(ebp);
            assembler.ret();
        });

        FourInts invoke = callSite.GetDelegate<FourInts>();
        Assert.Equal(10, invoke(1, 2, 3, 4));

        X86Detour<FourInts>? detour = null;
        detour = (X86Detour<FourInts>)X86DetourBackend.Instance.CreateDetour(new DetourRequest<FourInts>
        {
            Name = "fastcall-adapter",
            TargetAddress = target.Address,
            Callback = (first, second, third, fourth) =>
                detour!.Original(first, second, third, fourth) + 100,
            IntermediaryFactory = new X86CallingConventionIntermediaryFactory<FourInts>(
                X86CallingConvention.Fastcall),
        });

        using (detour)
        {
            Assert.NotEqual(IntPtr.Zero, detour.HookEntryPointAddress);
            Assert.NotEqual((IntPtr)target.Address, detour.HookEntryPointAddress);
            Assert.NotEqual(detour.TrampolineAddress, detour.OriginalEntryPointAddress);

            detour.Enable();
            Assert.Equal(110, invoke(1, 2, 3, 4));
            detour.Disable();
            Assert.Equal(10, invoke(1, 2, 3, 4));
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WrongManagedConvention(int value);

    [Fact]
    public void Factory_RejectsANonCdeclManagedDelegate()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new X86CallingConventionIntermediaryFactory<WrongManagedConvention>(X86CallingConvention.Fastcall));

        Assert.Contains("managed-facing x86 ABI is cdecl", error.Message);
    }

    [Fact]
    public void Fastcall_SkipsStructsWhenAssigningEcxAndEdx()
    {
        Assert.SkipUnless(
            RuntimeInformation.ProcessArchitecture == Architecture.X86 &&
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "Microsoft x86 calling-convention execution requires a 32-bit Windows test host.");

        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, ecx);
            assembler.add(eax, edx);
            assembler.add(eax, __dword_ptr[esp + 4]);
            assembler.add(eax, __dword_ptr[esp + 8]);
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret(8);
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(__dword_ptr[ebp + 16]);
            assembler.push(__dword_ptr[ebp + 12]);
            assembler.mov(ecx, __dword_ptr[ebp + 8]);
            assembler.mov(edx, __dword_ptr[ebp + 20]);
            assembler.call((UInt32)target.Address);
            assembler.pop(ebp);
            assembler.ret();
        });

        IntPairInt invoke = callSite.GetDelegate<IntPairInt>();
        Pair pair = new() { Left = 2, Right = 3 };
        Assert.Equal(10, invoke(1, pair, 4));

        X86Detour<IntPairInt>? detour = null;
        detour = (X86Detour<IntPairInt>)X86DetourBackend.Instance.CreateDetour(new DetourRequest<IntPairInt>
        {
            Name = "fastcall-struct-adapter",
            TargetAddress = target.Address,
            Callback = (first, value, third) => detour!.Original(first, value, third) + 100,
            IntermediaryFactory = new X86CallingConventionIntermediaryFactory<IntPairInt>(
                X86CallingConvention.Fastcall),
        });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(110, invoke(1, pair, 4));
        }
    }

    [Fact]
    public void CustomConvention_PreservesNonVolatilesAndMapsTheReturnRegister()
    {
        Assert.SkipUnless(
            RuntimeInformation.ProcessArchitecture == Architecture.X86,
            "Custom x86 calling-convention execution requires a 32-bit x86 test host.");

        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(edx, edi);
            assembler.add(edx, ebp);
            assembler.add(edx, __dword_ptr[esp + 4]);
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(edi);
            assembler.push(__dword_ptr[ebp + 16]);
            assembler.mov(edi, __dword_ptr[ebp + 8]);
            assembler.mov(eax, __dword_ptr[ebp + 12]);
            assembler.mov(ebp, eax);
            assembler.call((UInt32)target.Address);
            assembler.add(esp, 4);
            assembler.mov(eax, edx);
            assembler.pop(edi);
            assembler.pop(ebp);
            assembler.ret();
        });

        CustomRegisters invoke = callSite.GetDelegate<CustomRegisters>();
        Assert.Equal(6, invoke(1, 2, 3));

        X86Detour<CustomRegisters>? detour = null;
        detour = (X86Detour<CustomRegisters>)X86DetourBackend.Instance.CreateDetour(
            new DetourRequest<CustomRegisters>
            {
                Name = "custom-register-adapter",
                TargetAddress = target.Address,
                Callback = (first, second, third) => detour!.Original(first, second, third) + 100,
                IntermediaryFactory = new X86CallingConventionIntermediaryFactory<CustomRegisters>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(106, invoke(1, 2, 3));
        }
    }
}
