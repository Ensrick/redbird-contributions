using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.Extensions.CallingConventions.NativeX64;
using RedBird.X64.Tests.Infrastructure;
using static Iced.Intel.AssemblerRegisters;

#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.X86;

namespace RedBird.X64.Tests;

public sealed class VectorcallIntermediaryTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Float4
    {
        public float X;
        public float Y;
        public float Z;
        public float W;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Float2
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Float3
    {
        public float X;
        public float Y;
        public float Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Double2
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VectorPair
    {
        public Float4 First;
        public Float4 Second;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VectorSingle
    {
        public Float4 Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Float8
    {
        public float X0;
        public float X1;
        public float X2;
        public float X3;
        public float X4;
        public float X5;
        public float X6;
        public float X7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vector256Pair
    {
        public Float8 First;
        public Float8 Second;
    }

    private delegate double SixDoubles(
        double first,
        double second,
        double third,
        double fourth,
        double fifth,
        double sixth);

    private delegate float FifthVector(
        long first,
        long second,
        long third,
        long fourth,
        [VectorcallArgument(16)] Float4 vector);

    private delegate float DiscontiguousHva(
        float first,
        [VectorcallHva(4, 2)] Float2 pair,
        float last);

    private delegate double DoubleHva([VectorcallHva(8, 2)] Double2 aggregate);

    private delegate float ExhaustedHva(
        float first,
        float second,
        float third,
        float fourth,
        [VectorcallHva(4, 3)] Float3 aggregate);

    private delegate float VectorMemberHva([VectorcallHva(16, 2)] VectorPair aggregate);
    private delegate float SingleVectorMemberHva([VectorcallHva(16, 1)] VectorSingle aggregate);
    private delegate float Vector256MemberHva([VectorcallHva(32, 2)] Vector256Pair aggregate);

    private delegate float StackHva(
        float first,
        float second,
        float third,
        float fourth,
        float fifth,
        float sixth,
        float seventh,
        float eighth,
        [VectorcallHva(4, 2)] Float2 aggregate);

    private delegate float PartialRegisterHva(
        float first,
        float second,
        float third,
        float fourth,
        float fifth,
        [VectorcallHva(4, 3)] Float3 aggregate);

    [return: VectorcallHva(4, 4)]
    private delegate Float4 ReturnHva();

    [return: VectorcallHva(16, 2)]
    private delegate VectorPair ReturnVectorHva();

    [return: VectorcallHva(16, 2)]
    private delegate VectorPair ReturnVectorHvaWithSeed(float seed);

    private delegate IntPtr AddressGetter();
    private delegate void ReturnVectorHvaOut(IntPtr output);

    [Fact]
    public void Vectorcall_TranslatesFifthAndSixthFloatingArgumentsInBothDirections()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.addsd(xmm0, xmm1);
            assembler.addsd(xmm0, xmm2);
            assembler.addsd(xmm0, xmm3);
            assembler.addsd(xmm0, xmm4);
            assembler.addsd(xmm0, xmm5);
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        // The CLR invokes this call-site with Microsoft x64 fastcall. It loads the two stack doubles into vectorcall's XMM4/XMM5 and supplies six eight-byte home slots.
        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 48);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.movsd(xmm4, __qword_ptr[rbp + 48]);
                assembler.movsd(xmm5, __qword_ptr[rbp + 56]);
            }
            assembler.mov(rax, target.Address);
            assembler.call(rax);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        SixDoubles invoke = callSite.GetDelegate<SixDoubles>();
        Assert.Equal(21d, invoke(1, 2, 3, 4, 5, 6));

        NativeDetour<SixDoubles>? detour = null;
        detour = (NativeDetour<SixDoubles>)NativeDetourBackend.Instance.CreateDetour(new DetourRequest<SixDoubles>
        {
            Name = "vectorcall-adapter",
            TargetAddress = target.Address,
            Callback = (first, second, third, fourth, fifth, sixth) =>
                detour!.Original(first, second, third, fourth, fifth, sixth) + 100,
            IntermediaryFactory = new X64VectorcallIntermediaryFactory<SixDoubles>(),
        });

        using (detour)
        {
            Assert.NotEqual(IntPtr.Zero, detour.HookEntryPointAddress);
            Assert.NotEqual((IntPtr)target.Address, detour.HookEntryPointAddress);
            Assert.NotEqual(detour.TrampolineAddress, detour.OriginalEntryPointAddress);

            detour.Enable();
            Assert.Equal(121d, invoke(1, 2, 3, 4, 5, 6));
            detour.Disable();
            Assert.Equal(21d, invoke(1, 2, 3, 4, 5, 6));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesAByValueSimdArgumentInBothDirections()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.movaps(xmm0, xmm4);
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 48);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.mov(rax, __qword_ptr[rbp + 48]);
                assembler.movups(xmm4, __xmmword_ptr[rax]);
            }
            else
            {
                // A four-float aggregate uses two packed SSE eightbytes in System V. The native vectorcall side expects the complete vector in positional XMM4.
                assembler.movsd(__qword_ptr[rsp + 32], xmm0);
                assembler.movsd(__qword_ptr[rsp + 40], xmm1);
                assembler.movups(xmm4, __xmmword_ptr[rsp + 32]);
            }
            assembler.mov(rax, target.Address);
            assembler.call(rax);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        FifthVector invoke = callSite.GetDelegate<FifthVector>();
        Float4 value = new() { X = 7, Y = 8, Z = 9, W = 10 };
        Assert.Equal(7f, invoke(1, 2, 3, 4, value));

        NativeDetour<FifthVector>? detour = null;
        detour = (NativeDetour<FifthVector>)NativeDetourBackend.Instance.CreateDetour(new DetourRequest<FifthVector>
        {
            Name = "vectorcall-simd-adapter",
            TargetAddress = target.Address,
            Callback = (first, second, third, fourth, vector) =>
                detour!.Original(first, second, third, fourth, vector) + 100,
            IntermediaryFactory = new X64VectorcallIntermediaryFactory<FifthVector>(),
        });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(107f, invoke(1, 2, 3, 4, value));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesPlatformHvaRegisterLayoutsInBothDirections()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Microsoft vectorcall assigns the two HVA members to the remaining discontiguous XMM1 and XMM3 registers.
                assembler.addss(xmm0, xmm1);
                assembler.addss(xmm0, xmm3);
                assembler.addss(xmm0, xmm2);
            }
            else
            {
                // Clang's Linux vectorcall lowers this System V aggregate to one packed
                // SSE eightbyte in positional XMM1.
                assembler.movaps(xmm3, xmm1);
                assembler.addss(xmm0, xmm1);
                assembler.shufps(xmm3, xmm3, 0x55);
                assembler.addss(xmm0, xmm3);
                assembler.addss(xmm0, xmm2);
            }
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 32);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Microsoft x64 passes the eight-byte managed aggregate in RDX.
                assembler.movd(xmm1, edx);
                assembler.shr(rdx, 32);
                assembler.movd(xmm3, edx);
            }
            assembler.mov(rax, target.Address);
            assembler.call(rax);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        DiscontiguousHva invoke = callSite.GetDelegate<DiscontiguousHva>();
        Float2 pair = new() { X = 2, Y = 3 };
        Assert.Equal(10f, invoke(1, pair, 4));

        NativeDetour<DiscontiguousHva>? detour = null;
        detour = (NativeDetour<DiscontiguousHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<DiscontiguousHva>
            {
                Name = "vectorcall-discontiguous-hva",
                TargetAddress = target.Address,
                Callback = (first, aggregate, last) => detour!.Original(first, aggregate, last) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<DiscontiguousHva>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(110f, invoke(1, pair, 4));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesDoubleHvaMembersInBothDirections()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.addsd(xmm0, xmm1);
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 32);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.movsd(xmm0, __qword_ptr[rcx]);
                assembler.movsd(xmm1, __qword_ptr[rcx + 8]);
            }
            assembler.mov(rax, target.Address);
            assembler.call(rax);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        DoubleHva invoke = callSite.GetDelegate<DoubleHva>();
        Double2 value = new() { X = 7, Y = 11 };
        Assert.Equal(18d, invoke(value));

        NativeDetour<DoubleHva>? detour = null;
        detour = (NativeDetour<DoubleHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<DoubleHva>
            {
                Name = "vectorcall-double-hva",
                TargetAddress = target.Address,
                Callback = aggregate => detour!.Original(aggregate) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<DoubleHva>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(118d, invoke(value));
        }
    }

    [Fact]
    public void Vectorcall_HandlesAnHvaAtTheEndOfTheRegisterArea()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.addss(xmm0, xmm1);
            assembler.addss(xmm0, xmm2);
            assembler.addss(xmm0, xmm3);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Only XMM4/XMM5 remain, so a three-member Microsoft HVA is indirect.
                assembler.mov(rax, __qword_ptr[rsp + 40]);
                assembler.addss(xmm0, __dword_ptr[rax]);
                assembler.addss(xmm0, __dword_ptr[rax + 4]);
                assembler.addss(xmm0, __dword_ptr[rax + 8]);
            }
            else
            {
                // Linux lowers three floats to two SSE chunks in XMM4/XMM5.
                assembler.movaps(xmm6, xmm4);
                assembler.addss(xmm0, xmm4);
                assembler.shufps(xmm6, xmm6, 0x55);
                assembler.addss(xmm0, xmm6);
                assembler.addss(xmm0, xmm5);
            }
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 64);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.mov(rax, __qword_ptr[rbp + 48]);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                assembler.mov(__qword_ptr[rsp + 32], rax);
            assembler.mov(r11, target.Address);
            assembler.call(r11);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        ExhaustedHva invoke = callSite.GetDelegate<ExhaustedHva>();
        Float3 aggregate = new() { X = 5, Y = 6, Z = 7 };
        Assert.Equal(28f, invoke(1, 2, 3, 4, aggregate));

        NativeDetour<ExhaustedHva>? detour = null;
        detour = (NativeDetour<ExhaustedHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<ExhaustedHva>
            {
                Name = "vectorcall-indirect-hva",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth, value) =>
                    detour!.Original(first, second, third, fourth, value) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<ExhaustedHva>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(128f, invoke(1, 2, 3, 4, aggregate));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesAStackPassedSystemVHvaToNativeIndirectStorage()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.addss(xmm0, xmm1);
            assembler.addss(xmm0, xmm2);
            assembler.addss(xmm0, xmm3);
            assembler.addss(xmm0, xmm4);
            assembler.addss(xmm0, xmm5);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.mov(rax, __qword_ptr[rsp + 72]);
                assembler.addss(xmm0, __dword_ptr[rsp + 56]);
                assembler.addss(xmm0, __dword_ptr[rsp + 64]);
                assembler.addss(xmm0, __dword_ptr[rax]);
                assembler.addss(xmm0, __dword_ptr[rax + 4]);
            }
            else
            {
                // Linux omits the Windows home area. Logical vectorcall positions four and five still reserve stack slots, so positions six through eight appear at entry offsets 24, 32, and 40.
                assembler.addss(xmm0, __dword_ptr[rsp + 24]);
                assembler.addss(xmm0, __dword_ptr[rsp + 32]);
                assembler.addss(xmm0, __dword_ptr[rsp + 40]);
                assembler.addss(xmm0, __dword_ptr[rsp + 44]);
            }
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 80);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.movss(xmm4, __dword_ptr[rbp + 48]);
                assembler.movss(xmm5, __dword_ptr[rbp + 56]);
                assembler.mov(eax, __dword_ptr[rbp + 64]);
                assembler.mov(__dword_ptr[rsp + 48], eax);
                assembler.mov(eax, __dword_ptr[rbp + 72]);
                assembler.mov(__dword_ptr[rsp + 56], eax);
                assembler.lea(rax, __qword_ptr[rbp + 80]);
            }
            else
            {
                assembler.movss(__dword_ptr[rsp + 16], xmm6);
                assembler.movss(__dword_ptr[rsp + 24], xmm7);
                assembler.mov(rax, __qword_ptr[rbp + 16]);
                assembler.mov(__qword_ptr[rsp + 32], rax);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                assembler.mov(__qword_ptr[rsp + 64], rax);
            assembler.mov(r11, target.Address);
            assembler.call(r11);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        StackHva invoke = callSite.GetDelegate<StackHva>();
        Float2 aggregate = new() { X = 9, Y = 10 };
        Assert.Equal(55f, invoke(1, 2, 3, 4, 5, 6, 7, 8, aggregate));

        NativeDetour<StackHva>? detour = null;
        detour = (NativeDetour<StackHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<StackHva>
            {
                Name = "vectorcall-stack-hva",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth, fifth, sixth, seventh, eighth, value) =>
                    detour!.Original(first, second, third, fourth, fifth, sixth, seventh, eighth, value) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<StackHva>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(155f, invoke(1, 2, 3, 4, 5, 6, 7, 8, aggregate));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesAnHvaSplitBetweenALinuxRegisterAndTheStack()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.addss(xmm0, xmm1);
            assembler.addss(xmm0, xmm2);
            assembler.addss(xmm0, xmm3);
            assembler.addss(xmm0, xmm4);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.mov(rax, __qword_ptr[rsp + 48]);
                assembler.addss(xmm0, __dword_ptr[rax]);
                assembler.addss(xmm0, __dword_ptr[rax + 4]);
                assembler.addss(xmm0, __dword_ptr[rax + 8]);
            }
            else
            {
                assembler.movaps(xmm6, xmm5);
                assembler.addss(xmm0, xmm5);
                assembler.shufps(xmm6, xmm6, 0x55);
                assembler.addss(xmm0, xmm6);
                assembler.addss(xmm0, __dword_ptr[rsp + 24]);
            }
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 48);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.movss(xmm4, __dword_ptr[rbp + 48]);
                assembler.mov(rax, __qword_ptr[rbp + 56]);
                assembler.mov(__qword_ptr[rsp + 40], rax);
            }
            else
            {
                assembler.movss(__dword_ptr[rsp + 16], xmm6);
            }
            assembler.mov(r11, target.Address);
            assembler.call(r11);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        PartialRegisterHva invoke = callSite.GetDelegate<PartialRegisterHva>();
        Float3 aggregate = new() { X = 6, Y = 7, Z = 8 };
        Assert.Equal(36f, invoke(1, 2, 3, 4, 5, aggregate));

        NativeDetour<PartialRegisterHva>? detour = null;
        detour = (NativeDetour<PartialRegisterHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<PartialRegisterHva>
            {
                Name = "vectorcall-partial-register-hva",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth, fifth, value) =>
                    detour!.Original(first, second, third, fourth, fifth, value) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<PartialRegisterHva>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(136f, invoke(1, 2, 3, 4, 5, aggregate));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesHvaVectorMembersInBothDirections()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.addss(xmm0, xmm1);
            }
            else
            {
                assembler.movss(xmm0, __dword_ptr[rcx]);
                assembler.addss(xmm0, __dword_ptr[rcx + 16]);
            }
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 32);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.movups(xmm0, __xmmword_ptr[rcx]);
                assembler.movups(xmm1, __xmmword_ptr[rcx + 16]);
            }
            else
            {
                assembler.lea(rcx, __qword_ptr[rbp + 16]);
            }
            assembler.mov(rax, target.Address);
            assembler.call(rax);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        VectorMemberHva invoke = callSite.GetDelegate<VectorMemberHva>();
        VectorPair pair = new()
        {
            First = new Float4 { X = 7, Y = 1, Z = 2, W = 3 },
            Second = new Float4 { X = 11, Y = 4, Z = 5, W = 6 },
        };
        Assert.Equal(18f, invoke(pair));

        NativeDetour<VectorMemberHva>? detour = null;
        detour = (NativeDetour<VectorMemberHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<VectorMemberHva>
            {
                Name = "vectorcall-vector-member-hva",
                TargetAddress = target.Address,
                Callback = value => detour!.Original(value) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<VectorMemberHva>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(118f, invoke(pair));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesASingleVectorHvaMemberFromSystemVEightbytes()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.movaps(xmm1, xmm0);
            assembler.shufps(xmm1, xmm1, 0xAA);
            assembler.addss(xmm0, xmm1);
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 48);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.movups(xmm0, __xmmword_ptr[rcx]);
            }
            else
            {
                assembler.movsd(__qword_ptr[rsp + 32], xmm0);
                assembler.movsd(__qword_ptr[rsp + 40], xmm1);
                assembler.movups(xmm0, __xmmword_ptr[rsp + 32]);
            }
            assembler.mov(rax, target.Address);
            assembler.call(rax);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        SingleVectorMemberHva invoke = callSite.GetDelegate<SingleVectorMemberHva>();
        VectorSingle value = new() { Value = new Float4 { X = 7, Z = 11 } };
        Assert.Equal(18f, invoke(value));

        NativeDetour<SingleVectorMemberHva>? detour = null;
        detour = (NativeDetour<SingleVectorMemberHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<SingleVectorMemberHva>
            {
                Name = "vectorcall-single-vector-member-hva",
                TargetAddress = target.Address,
                Callback = aggregate => detour!.Original(aggregate) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<SingleVectorMemberHva>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(118f, invoke(value));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesHvaVector256MembersInBothDirections()
    {
        RequireSupportedVectorcallHost();
        Assert.SkipUnless(Avx.IsSupported, "The 256-bit HVA execution test requires AVX.");

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                assembler.vaddss(xmm0, xmm0, xmm1);
            else
            {
                assembler.movss(xmm0, __dword_ptr[rcx]);
                assembler.addss(xmm0, __dword_ptr[rcx + 32]);
            }
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 32);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.vmovups(ymm0, __ymmword_ptr[rcx]);
                assembler.vmovups(ymm1, __ymmword_ptr[rcx + 32]);
            }
            else
            {
                assembler.lea(rcx, __qword_ptr[rbp + 16]);
            }
            assembler.mov(rax, target.Address);
            assembler.call(rax);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        Vector256MemberHva invoke = callSite.GetDelegate<Vector256MemberHva>();
        Vector256Pair pair = new()
        {
            First = new Float8 { X0 = 17 },
            Second = new Float8 { X0 = 19 },
        };
        Assert.Equal(36f, invoke(pair));

        NativeDetour<Vector256MemberHva>? detour = null;
        detour = (NativeDetour<Vector256MemberHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<Vector256MemberHva>
            {
                Name = "vectorcall-vector256-member-hva",
                TargetAddress = target.Address,
                Callback = value => detour!.Original(value) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<Vector256MemberHva>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(136f, invoke(pair));
        }
    }

    [Fact]
    public void Vectorcall_TranslatesHvaReturnsInBothDirections()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MoveFloatConstant(assembler, xmm0, 1f);
                MoveFloatConstant(assembler, xmm1, 2f);
                MoveFloatConstant(assembler, xmm2, 3f);
                MoveFloatConstant(assembler, xmm3, 4f);
            }
            else
            {
                MoveFloatConstant(assembler, xmm0, 1f);
                MoveFloatConstant(assembler, xmm4, 2f);
                assembler.unpcklps(xmm0, xmm4);
                MoveFloatConstant(assembler, xmm1, 3f);
                MoveFloatConstant(assembler, xmm4, 4f);
                assembler.unpcklps(xmm1, xmm4);
            }
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 48);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                assembler.mov(__qword_ptr[rsp + 32], rcx);

            assembler.mov(rax, target.Address);
            assembler.call(rax);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.mov(r10, __qword_ptr[rsp + 32]);
                assembler.movss(__dword_ptr[r10], xmm0);
                assembler.movss(__dword_ptr[r10 + 4], xmm1);
                assembler.movss(__dword_ptr[r10 + 8], xmm2);
                assembler.movss(__dword_ptr[r10 + 12], xmm3);
                assembler.mov(rax, r10);
            }

            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        ReturnHva invoke = callSite.GetDelegate<ReturnHva>();
        AssertFloat4(invoke(), 1, 2, 3, 4);

        NativeDetour<ReturnHva>? detour = null;
        detour = (NativeDetour<ReturnHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<ReturnHva>
            {
                Name = "vectorcall-hva-return",
                TargetAddress = target.Address,
                Callback = () =>
                {
                    Float4 result = detour!.Original();
                    result.X += 10;
                    result.Y += 10;
                    result.Z += 10;
                    result.W += 10;
                    return result;
                },
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<ReturnHva>(),
            });

        using (detour)
        {
            detour.Enable();
            AssertFloat4(invoke(), 11, 12, 13, 14);
        }
    }

    [Fact]
    public void Vectorcall_TranslatesLargeHvaHiddenReturnsInBothDirections()
    {
        RequireSupportedVectorcallHost();

        using ExecutableFunction target = ExecutableFunction.Create((assembler, _) =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.movss(xmm2, xmm0);
                BuildFloat4Register(assembler, xmm0, xmm4, xmm5, 1, 2, 3, 4);
                BuildFloat4Register(assembler, xmm1, xmm4, xmm5, 5, 6, 7, 8);
                assembler.movss(xmm0, xmm2);
            }
            else
            {
                assembler.movss(__dword_ptr[rcx], xmm1);
                StoreFloatConstant(assembler, rcx, 4, 2);
                StoreFloatConstant(assembler, rcx, 8, 3);
                StoreFloatConstant(assembler, rcx, 12, 4);
                StoreFloatConstant(assembler, rcx, 16, 5);
                StoreFloatConstant(assembler, rcx, 20, 6);
                StoreFloatConstant(assembler, rcx, 24, 7);
                StoreFloatConstant(assembler, rcx, 28, 8);
                assembler.mov(rax, rcx);
            }
            for (int index = 0; index < 24; index++)
                assembler.nop();
            assembler.ret();
        });

        using ExecutableFunction callSite = ExecutableFunction.Create((assembler, _) =>
        {
            assembler.push(rbp);
            assembler.mov(rbp, rsp);
            assembler.sub(rsp, 48);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.mov(__qword_ptr[rsp + 32], rcx);
                assembler.movss(xmm0, xmm1);
            }
            else
            {
                assembler.mov(__qword_ptr[rsp + 32], rdi);
                assembler.mov(rcx, rdi);
                assembler.movss(xmm1, xmm0);
            }

            assembler.mov(r11, target.Address);
            assembler.call(r11);

            assembler.mov(r10, __qword_ptr[rsp + 32]);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                assembler.movups(__xmmword_ptr[r10], xmm0);
                assembler.movups(__xmmword_ptr[r10 + 16], xmm1);
            }
            assembler.mov(rax, r10);
            assembler.mov(rsp, rbp);
            assembler.pop(rbp);
            assembler.ret();
        });

        ReturnVectorHvaWithSeed invoke = callSite.GetDelegate<ReturnVectorHvaWithSeed>();
        AssertVectorPair(invoke(1), 1, 2, 3, 4, 5, 6, 7, 8);

        NativeDetour<ReturnVectorHvaWithSeed>? detour = null;
        detour = (NativeDetour<ReturnVectorHvaWithSeed>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<ReturnVectorHvaWithSeed>
            {
                Name = "vectorcall-large-hva-return",
                TargetAddress = target.Address,
                Callback = seed =>
                {
                    VectorPair result = detour!.Original(seed);
                    result.First.X += 10;
                    result.First.Y += 10;
                    result.First.Z += 10;
                    result.First.W += 10;
                    result.Second.X += 10;
                    result.Second.Y += 10;
                    result.Second.Z += 10;
                    result.Second.W += 10;
                    return result;
                },
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<ReturnVectorHvaWithSeed>(),
            });

        using (detour)
        {
            detour.Enable();
            AssertVectorPair(invoke(1), 11, 12, 13, 14, 15, 16, 17, 18);
        }
    }

    [Fact]
    public void Vectorcall_MatchesCompilerGeneratedClangLinuxHvaAbi()
    {
        string? fixturePath = Environment.GetEnvironmentVariable("RedBird_VECTORCALL_FIXTURE");
        Assert.SkipUnless(
            RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !string.IsNullOrEmpty(fixturePath),
            "Set RedBird_VECTORCALL_FIXTURE to the compiled Clang Linux ABI probe to run this integration test.");

        IntPtr library = NativeLibrary.Load(fixturePath!);
        try
        {
            string? fixtureCase = Environment.GetEnvironmentVariable("RedBird_VECTORCALL_FIXTURE_CASE");
            if (fixtureCase is null or "registers")
                VerifyCompilerDiscontiguousHva(library);
            if (fixtureCase is null or "tail")
                VerifyCompilerExhaustedHva(library);
            if (fixtureCase is null or "split")
                VerifyCompilerPartialRegisterHva(library);
            if (fixtureCase is null or "vectors")
                VerifyCompilerVectorMemberHva(library);
            if (fixtureCase is null or "return")
                VerifyCompilerHvaReturn(library);
            if (fixtureCase is null or "hidden-return")
                VerifyCompilerVectorHvaReturn(library);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static void VerifyCompilerDiscontiguousHva(IntPtr library)
    {
        DiscontiguousHva invoke = GetFixtureDelegate<DiscontiguousHva>(library, "call_hva_discontiguous");
        UInt64 target = GetFixtureAddress(library, "address_hva_discontiguous");
        Float2 pair = new() { X = 2, Y = 3 };
        Assert.Equal(10f, invoke(1, pair, 4));

        NativeDetour<DiscontiguousHva>? detour = null;
        detour = (NativeDetour<DiscontiguousHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<DiscontiguousHva>
            {
                Name = "clang-linux-hva-registers",
                TargetAddress = target,
                Callback = (first, value, last) => detour!.Original(first, value, last) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<DiscontiguousHva>(),
            });
        using (detour)
        {
            detour.Enable();
            Assert.Equal(110f, invoke(1, pair, 4));
        }
    }

    private static void VerifyCompilerExhaustedHva(IntPtr library)
    {
        ExhaustedHva invoke = GetFixtureDelegate<ExhaustedHva>(library, "call_hva_exhausted");
        UInt64 target = GetFixtureAddress(library, "address_hva_exhausted");
        Float3 value = new() { X = 5, Y = 6, Z = 7 };
        Assert.Equal(28f, invoke(1, 2, 3, 4, value));

        NativeDetour<ExhaustedHva>? detour = null;
        detour = (NativeDetour<ExhaustedHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<ExhaustedHva>
            {
                Name = "clang-linux-hva-register-tail",
                TargetAddress = target,
                Callback = (first, second, third, fourth, aggregate) =>
                    detour!.Original(first, second, third, fourth, aggregate) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<ExhaustedHva>(),
            });
        using (detour)
        {
            detour.Enable();
            Assert.Equal(128f, invoke(1, 2, 3, 4, value));
        }
    }

    private static void VerifyCompilerVectorMemberHva(IntPtr library)
    {
        VectorMemberHva invoke = GetFixtureDelegate<VectorMemberHva>(library, "call_hva_vector_members");
        UInt64 target = GetFixtureAddress(library, "address_hva_vector_members");
        VectorPair value = new()
        {
            First = new Float4 { X = 7 },
            Second = new Float4 { X = 11 },
        };
        Assert.Equal(18f, invoke(value));

        NativeDetour<VectorMemberHva>? detour = null;
        detour = (NativeDetour<VectorMemberHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<VectorMemberHva>
            {
                Name = "clang-linux-vector-hva",
                TargetAddress = target,
                Callback = aggregate => detour!.Original(aggregate) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<VectorMemberHva>(),
            });
        using (detour)
        {
            detour.Enable();
            Assert.Equal(118f, invoke(value));
        }
    }

    private static void VerifyCompilerPartialRegisterHva(IntPtr library)
    {
        PartialRegisterHva invoke = GetFixtureDelegate<PartialRegisterHva>(
            library,
            "call_hva_partial_register_space");
        UInt64 target = GetFixtureAddress(library, "address_hva_partial_register_space");
        Float3 value = new() { X = 6, Y = 7, Z = 8 };
        Assert.Equal(36f, invoke(1, 2, 3, 4, 5, value));

        NativeDetour<PartialRegisterHva>? detour = null;
        detour = (NativeDetour<PartialRegisterHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<PartialRegisterHva>
            {
                Name = "clang-linux-partial-register-hva",
                TargetAddress = target,
                Callback = (first, second, third, fourth, fifth, aggregate) =>
                    detour!.Original(first, second, third, fourth, fifth, aggregate) + 100,
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<PartialRegisterHva>(),
            });
        using (detour)
        {
            detour.Enable();
            Assert.Equal(136f, invoke(1, 2, 3, 4, 5, value));
        }
    }

    private static void VerifyCompilerHvaReturn(IntPtr library)
    {
        ReturnHva invoke = GetFixtureDelegate<ReturnHva>(library, "call_hva_return");
        UInt64 target = GetFixtureAddress(library, "address_hva_return");
        AssertFloat4(invoke(), 1, 2, 3, 4);

        NativeDetour<ReturnHva>? detour = null;
        detour = (NativeDetour<ReturnHva>)NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<ReturnHva>
            {
                Name = "clang-linux-hva-return",
                TargetAddress = target,
                Callback = () =>
                {
                    Float4 result = detour!.Original();
                    result.X += 10;
                    result.Y += 10;
                    result.Z += 10;
                    result.W += 10;
                    return result;
                },
                IntermediaryFactory = new X64VectorcallIntermediaryFactory<ReturnHva>(),
            });
        using (detour)
        {
            detour.Enable();
            AssertFloat4(invoke(), 11, 12, 13, 14);
        }
    }

    private static void VerifyCompilerVectorHvaReturn(IntPtr library)
    {
        ReturnVectorHvaOut invoke = GetFixtureDelegate<ReturnVectorHvaOut>(library, "call_hva_vector_return_out");
        UInt64 target = GetFixtureAddress(library, "address_hva_vector_return");
        float[] values = new float[8];
        GCHandle valuesHandle = GCHandle.Alloc(values, GCHandleType.Pinned);

        try
        {
            invoke(valuesHandle.AddrOfPinnedObject());
            Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }, values);

            NativeDetour<ReturnVectorHva>? detour = null;
            detour = (NativeDetour<ReturnVectorHva>)NativeDetourBackend.Instance.CreateDetour(
                new DetourRequest<ReturnVectorHva>
                {
                    Name = "clang-linux-vector-hva-return",
                    TargetAddress = target,
                    Callback = () =>
                    {
                        VectorPair result = detour!.Original();
                        result.First.X += 10;
                        result.Second.X += 10;
                        return result;
                    },
                    IntermediaryFactory = new X64VectorcallIntermediaryFactory<ReturnVectorHva>(),
                });
            using (detour)
            {
                detour.Enable();
                invoke(valuesHandle.AddrOfPinnedObject());
                Assert.Equal(11, values[0]);
                Assert.Equal(15, values[4]);
            }
        }
        finally
        {
            valuesHandle.Free();
        }
    }

    private static TDelegate GetFixtureDelegate<TDelegate>(IntPtr library, string name)
        where TDelegate : Delegate
        => Marshal.GetDelegateForFunctionPointer<TDelegate>(NativeLibrary.GetExport(library, name));

    private static UInt64 GetFixtureAddress(IntPtr library, string getterName)
    {
        AddressGetter getter = GetFixtureDelegate<AddressGetter>(library, getterName);
        return (UInt64)getter();
    }

    private static void RequireSupportedVectorcallHost()
    {
        Assert.SkipUnless(
            RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
            (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
             RuntimeInformation.IsOSPlatform(OSPlatform.Linux)),
            "x64 vectorcall execution requires a 64-bit Windows or Linux test host.");
    }

    private static void MoveFloatConstant(Assembler assembler, AssemblerRegisterXMM register, float value)
    {
        assembler.mov(eax, BitConverter.SingleToInt32Bits(value));
        assembler.movd(register, eax);
    }

    private static void StoreFloatConstant(
        Assembler assembler,
        AssemblerRegister64 destination,
        int offset,
        float value)
    {
        assembler.mov(eax, BitConverter.SingleToInt32Bits(value));
        assembler.mov(__dword_ptr[destination + offset], eax);
    }

    private static void BuildFloat4Register(
        Assembler assembler,
        AssemblerRegisterXMM destination,
        AssemblerRegisterXMM firstScratch,
        AssemblerRegisterXMM secondScratch,
        float x,
        float y,
        float z,
        float w)
    {
        MoveFloatConstant(assembler, destination, x);
        MoveFloatConstant(assembler, firstScratch, y);
        assembler.unpcklps(destination, firstScratch);
        MoveFloatConstant(assembler, firstScratch, z);
        MoveFloatConstant(assembler, secondScratch, w);
        assembler.unpcklps(firstScratch, secondScratch);
        assembler.movlhps(destination, firstScratch);
    }

    private static void AssertFloat4(Float4 actual, float x, float y, float z, float w)
    {
        Assert.Equal(x, actual.X);
        Assert.Equal(y, actual.Y);
        Assert.Equal(z, actual.Z);
        Assert.Equal(w, actual.W);
    }

    private static void AssertVectorPair(
        VectorPair actual,
        float firstX,
        float firstY,
        float firstZ,
        float firstW,
        float secondX,
        float secondY,
        float secondZ,
        float secondW)
    {
        AssertFloat4(actual.First, firstX, firstY, firstZ, firstW);
        AssertFloat4(actual.Second, secondX, secondY, secondZ, secondW);
    }
}
#endif
