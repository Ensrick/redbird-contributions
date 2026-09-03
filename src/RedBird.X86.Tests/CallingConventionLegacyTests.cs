using System;
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

/// <summary>
/// The x86 convention corpus, ported from the CallingConventionDispatcher test suite.
/// </summary>
public sealed class CallingConventionLegacyTests
{
    private const string X86Only = "x86 calling-convention execution requires a 32-bit x86 test host.";

    private const string MicrosoftX86Only =
        "Microsoft x86 calling-convention execution requires a 32-bit Windows test host.";

    /// <summary>Matches <c>NativeFunction4IntArgs1StructArgMUTATED_t</c> in the dispatcher corpus.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Triple
    {
        public UInt32 A;
        public UInt32 B;
        public UInt16 C;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FourInts(int first, int second, int third, int fourth);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FourIntsAndTriple(int first, int second, int third, int fourth, Triple fifth);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PointerPair(IntPtr text, IntPtr value);

    //
    // cdecl: NativeFunction4IntArgsCDECL
    //

    [Fact]
    public void Cdecl_NeedsNoTranslationStubs()
    {
        Assert.SkipUnless(RuntimeInformation.ProcessArchitecture == Architecture.X86, X86Only);

        // return (a1 * a2) + a3 + a4, arguments right-to-left on a caller-cleaned stack.
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, __dword_ptr[esp + 4]);
            assembler.imul(eax, __dword_ptr[esp + 8]);
            assembler.add(eax, __dword_ptr[esp + 12]);
            assembler.add(eax, __dword_ptr[esp + 16]);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret();
        });

        // A cdecl target already speaks the managed-facing ABI, so no call site is needed.
        FourInts invoke = target.GetDelegate<FourInts>();
        Assert.Equal(1337, invoke(1, 1, 1, 1335));

        X86Detour<FourInts>? detour = null;
        detour = (X86Detour<FourInts>)X86DetourBackend.Instance.CreateDetour(new DetourRequest<FourInts>
        {
            Name = "cdecl-adapter",
            TargetAddress = target.Address,
            Callback = (first, second, third, fourth) =>
                detour!.Original(first, second, third, fourth) + 100,
            IntermediaryFactory = new X86CallingConventionIntermediaryFactory<FourInts>(
                X86CallingConvention.Cdecl),
        });

        using (detour)
        {
            // The factory recognises the identity case and hands both addresses straight back.
            Assert.Equal(detour.TrampolineAddress, detour.OriginalEntryPointAddress);

            detour.Enable();
            Assert.Equal(1437, invoke(1, 1, 1, 1335));
            detour.Disable();
            Assert.Equal(1337, invoke(1, 1, 1, 1335));
        }
    }

    //
    // stdcall: NativeFunction4IntArgsSTDCALL, NativeFunction4Int1StructArgsSTDCALL
    //

    [Fact]
    public void Stdcall_CalleeCleansFourIntegerArguments()
    {
        Assert.SkipUnless(IsMicrosoftHost(), MicrosoftX86Only);

        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, __dword_ptr[esp + 4]);
            assembler.imul(eax, __dword_ptr[esp + 8]);
            assembler.add(eax, __dword_ptr[esp + 12]);
            assembler.add(eax, __dword_ptr[esp + 16]);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret(4 * 4);
        });

        // stdcall differs from cdecl only in who pops, so the call site just forwards and lets the callee clean.
        // If the inbound stub emitted a bare "ret", esp would drift by 16 per call and the second invocation would return garbage.
        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(__dword_ptr[ebp + 20]);
            assembler.push(__dword_ptr[ebp + 16]);
            assembler.push(__dword_ptr[ebp + 12]);
            assembler.push(__dword_ptr[ebp + 8]);
            assembler.call((UInt32)target.Address);
            assembler.pop(ebp);
            assembler.ret();
        });

        FourInts invoke = callSite.GetDelegate<FourInts>();
        Assert.Equal(1337, invoke(1, 1, 1, 1335));

        X86Detour<FourInts>? detour = null;
        detour = (X86Detour<FourInts>)X86DetourBackend.Instance.CreateDetour(new DetourRequest<FourInts>
        {
            Name = "stdcall-adapter",
            TargetAddress = target.Address,
            Callback = (first, second, third, fourth) =>
                detour!.Original(first, second, third, fourth) + 100,
            IntermediaryFactory = new X86CallingConventionIntermediaryFactory<FourInts>(
                X86CallingConvention.Stdcall),
        });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(1437, invoke(1, 1, 1, 1335));

            // Repeat: a stack-cleanup mismatch is cumulative and only shows on a later call.
            Assert.Equal(1437, invoke(1, 1, 1, 1335));

            detour.Disable();
            Assert.Equal(1337, invoke(1, 1, 1, 1335));
        }
    }

    [Fact]
    public void Stdcall_CarriesAByValueStructAcrossBothStubs()
    {
        Assert.SkipUnless(IsMicrosoftHost(), MicrosoftX86Only);

        // The struct is 12 bytes, so it occupies three stack slots that both stubs have to copy high-to-low to keep its in-memory field order intact.
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, __dword_ptr[esp + 4]);
            assembler.imul(eax, __dword_ptr[esp + 8]);
            assembler.add(eax, __dword_ptr[esp + 12]);
            assembler.add(eax, __dword_ptr[esp + 16]);
            assembler.add(eax, __dword_ptr[esp + 20]);
            assembler.add(eax, __dword_ptr[esp + 24]);

            // Read C as a word: the trailing two bytes are struct padding, and only a field-wise read stays deterministic regardless of what the marshaller left there.
            assembler.movzx(ecx, __word_ptr[esp + 28]);
            assembler.add(eax, ecx);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret(7 * 4);
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(__dword_ptr[ebp + 32]);
            assembler.push(__dword_ptr[ebp + 28]);
            assembler.push(__dword_ptr[ebp + 24]);
            assembler.push(__dword_ptr[ebp + 20]);
            assembler.push(__dword_ptr[ebp + 16]);
            assembler.push(__dword_ptr[ebp + 12]);
            assembler.push(__dword_ptr[ebp + 8]);
            assembler.call((UInt32)target.Address);
            assembler.pop(ebp);
            assembler.ret();
        });

        FourIntsAndTriple invoke = callSite.GetDelegate<FourIntsAndTriple>();
        Triple triple = new() { A = 1, B = 1, C = 1 };
        Assert.Equal(1337, invoke(1, 1, 1, 1332, triple));

        X86Detour<FourIntsAndTriple>? detour = null;
        detour = (X86Detour<FourIntsAndTriple>)X86DetourBackend.Instance.CreateDetour(
            new DetourRequest<FourIntsAndTriple>
            {
                Name = "stdcall-struct-adapter",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth, fifth) =>
                {
                    // The callback sees the aggregate itself, not a pointer to it.
                    Assert.Equal(1u, fifth.A);
                    Assert.Equal((UInt16)1, fifth.C);
                    return detour!.Original(first, second, third, fourth, fifth) + 100;
                },
                IntermediaryFactory = new X86CallingConventionIntermediaryFactory<FourIntsAndTriple>(
                    X86CallingConvention.Stdcall),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(1437, invoke(1, 1, 1, 1332, triple));
        }
    }

    //
    // thiscall: NativeFunction4IntArgsVCTHISCALL
    //

    [Fact]
    public void Thiscall_TakesTheThisPointerFromEcx()
    {
        Assert.SkipUnless(IsMicrosoftHost(), MicrosoftX86Only);

        // ECX is the implicit "this"; the remaining three arguments are callee-cleaned stack.
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.imul(ecx, __dword_ptr[esp + 4]);
            assembler.add(ecx, __dword_ptr[esp + 8]);
            assembler.add(ecx, __dword_ptr[esp + 12]);
            assembler.mov(eax, ecx);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret(3 * 4);
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(__dword_ptr[ebp + 20]);
            assembler.push(__dword_ptr[ebp + 16]);
            assembler.push(__dword_ptr[ebp + 12]);
            assembler.mov(ecx, __dword_ptr[ebp + 8]);
            assembler.call((UInt32)target.Address);
            assembler.pop(ebp);
            assembler.ret();
        });

        FourInts invoke = callSite.GetDelegate<FourInts>();
        Assert.Equal(1337, invoke(1, 1, 1, 1335));

        X86Detour<FourInts>? detour = null;
        detour = (X86Detour<FourInts>)X86DetourBackend.Instance.CreateDetour(new DetourRequest<FourInts>
        {
            Name = "thiscall-adapter",
            TargetAddress = target.Address,
            Callback = (instance, second, third, fourth) =>
            {
                // The managed delegate declares 'this' as an ordinary leading argument.
                Assert.Equal(1, instance);
                return detour!.Original(instance, second, third, fourth) + 100;
            },
            IntermediaryFactory = new X86CallingConventionIntermediaryFactory<FourInts>(
                X86CallingConvention.Thiscall),
        });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(1437, invoke(1, 1, 1, 1335));
            detour.Disable();
            Assert.Equal(1337, invoke(1, 1, 1, 1335));
        }
    }

    //
    // fastcall: NativeFunction2SpecialArgsFASTCALL
    //

    [Fact]
    public void Fastcall_PassesPointerArgumentsInEcxAndEdx()
    {
        Assert.SkipUnless(IsMicrosoftHost(), MicrosoftX86Only);

        // Two pointer-sized arguments fill both fastcall registers, so the frame has no stack portion at all: the inbound stub must still 'ret' without an immediate.
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, edx);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret();
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.mov(ecx, __dword_ptr[ebp + 8]);
            assembler.mov(edx, __dword_ptr[ebp + 12]);
            assembler.call((UInt32)target.Address);
            assembler.pop(ebp);
            assembler.ret();
        });

        PointerPair invoke = callSite.GetDelegate<PointerPair>();
        IntPtr text = Marshal.StringToHGlobalAnsi("Hello World");

        try
        {
            Assert.Equal(1337, invoke(text, (IntPtr)1337));

            X86Detour<PointerPair>? detour = null;
            detour = (X86Detour<PointerPair>)X86DetourBackend.Instance.CreateDetour(
                new DetourRequest<PointerPair>
                {
                    Name = "fastcall-pointer-adapter",
                    TargetAddress = target.Address,
                    Callback = (pointer, value) =>
                    {
                        // A pointer argument has to arrive intact, not merely non-zero: this is the string the native side would have dereferenced.
                        Assert.Equal("Hello World", Marshal.PtrToStringAnsi(pointer));
                        return detour!.Original(pointer, value) + 100;
                    },
                    IntermediaryFactory = new X86CallingConventionIntermediaryFactory<PointerPair>(
                        X86CallingConvention.Fastcall),
                });

            using (detour)
            {
                detour.Enable();
                Assert.Equal(1437, invoke(text, (IntPtr)1337));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    //
    // mutated: NativeFunction4IntArgsMUTATED
    //

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [X86CallingConvention(
        X86CallingConvention.Custom,
        StackCleanup = X86StackCleanup.Caller,
        ArgumentOrder = X86ArgumentOrder.RightToLeft)]
    [return: X86RegisterArgument(Register.ECX)]
    private delegate int MutatedFourRegisters(
        [X86RegisterArgument(Register.EAX)] int first,
        [X86RegisterArgument(Register.EBP)] int second,
        [X86RegisterArgument(Register.EDX)] int third,
        [X86RegisterArgument(Register.EDI)] int fourth);

    [Fact]
    public void Custom_TakesEveryArgumentFromARegisterAndReturnsInEcx()
    {
        Assert.SkipUnless(RuntimeInformation.ProcessArchitecture == Architecture.X86, X86Only);

        // Args in EAX/EBP/EDX/EDI, result in ECX, nothing on the stack. EBP is both an argument and the stub's own frame pointer, and EDI is nonvolatile, so this is the case where a
        // naive stub corrupts its caller instead of returning a wrong number.
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.add(eax, ebp);
            assembler.add(eax, edx);
            assembler.add(eax, edi);
            assembler.mov(ecx, eax);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret();
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(edi);
            assembler.mov(eax, __dword_ptr[ebp + 8]);
            assembler.mov(edx, __dword_ptr[ebp + 16]);
            assembler.mov(edi, __dword_ptr[ebp + 20]);

            // EBP is loaded last: it is the frame base for every other load above.
            assembler.mov(ecx, __dword_ptr[ebp + 12]);
            assembler.mov(ebp, ecx);
            assembler.call((UInt32)target.Address);
            assembler.mov(eax, ecx);

            // The call is stack-balanced, so ESP still addresses the saved registers.
            assembler.pop(edi);
            assembler.pop(ebp);
            assembler.ret();
        });

        MutatedFourRegisters invoke = callSite.GetDelegate<MutatedFourRegisters>();
        Assert.Equal(1337, invoke(1, 1, 1, 1334));

        X86Detour<MutatedFourRegisters>? detour = null;
        detour = (X86Detour<MutatedFourRegisters>)X86DetourBackend.Instance.CreateDetour(
            new DetourRequest<MutatedFourRegisters>
            {
                Name = "mutated-four-registers",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth) =>
                    detour!.Original(first, second, third, fourth) + 100,
                IntermediaryFactory = new X86CallingConventionIntermediaryFactory<MutatedFourRegisters>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(1437, invoke(1, 1, 1, 1334));
            detour.Disable();
            Assert.Equal(1337, invoke(1, 1, 1, 1334));
        }
    }

    //
    // mutated: NativeFunction6IntArgsMUTATED
    //

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [X86CallingConvention(
        X86CallingConvention.Custom,
        StackCleanup = X86StackCleanup.Caller,
        ArgumentOrder = X86ArgumentOrder.RightToLeft)]
    [return: X86RegisterArgument(Register.EDX)]
    private delegate int MutatedSixCallerClean(
        [X86RegisterArgument(Register.EAX)] int first,
        [X86RegisterArgument(Register.EDX)] int second,
        [X86RegisterArgument(Register.ECX)] int third,
        int fourth,
        int fifth,
        int sixth);

    [Fact]
    public void Custom_MixesThreeRegisterArgumentsWithThreeStackArguments()
    {
        Assert.SkipUnless(RuntimeInformation.ProcessArchitecture == Architecture.X86, X86Only);

        // EDX is both an argument and the result register, which is only safe because the stub moves the result into EAX after the call rather than before.
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.imul(edx, eax);
            assembler.add(edx, ecx);
            assembler.add(edx, __dword_ptr[esp + 4]);
            assembler.add(edx, __dword_ptr[esp + 8]);
            assembler.add(edx, __dword_ptr[esp + 12]);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret();
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(__dword_ptr[ebp + 28]);
            assembler.push(__dword_ptr[ebp + 24]);
            assembler.push(__dword_ptr[ebp + 20]);
            assembler.mov(eax, __dword_ptr[ebp + 8]);
            assembler.mov(edx, __dword_ptr[ebp + 12]);
            assembler.mov(ecx, __dword_ptr[ebp + 16]);
            assembler.call((UInt32)target.Address);
            assembler.add(esp, 3 * 4);
            assembler.mov(eax, edx);
            assembler.pop(ebp);
            assembler.ret();
        });

        MutatedSixCallerClean invoke = callSite.GetDelegate<MutatedSixCallerClean>();
        Assert.Equal(1337, invoke(1, 2, 1, 1, 1, 1332));

        X86Detour<MutatedSixCallerClean>? detour = null;
        detour = (X86Detour<MutatedSixCallerClean>)X86DetourBackend.Instance.CreateDetour(
            new DetourRequest<MutatedSixCallerClean>
            {
                Name = "mutated-six-caller-clean",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth, fifth, sixth) =>
                    detour!.Original(first, second, third, fourth, fifth, sixth) + 100,
                IntermediaryFactory = new X86CallingConventionIntermediaryFactory<MutatedSixCallerClean>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(1437, invoke(1, 2, 1, 1, 1, 1332));
        }
    }

    //
    // mutated: NativeFunction6IntArgsMUTATED_STDCALLSTYLE
    //

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [X86CallingConvention(
        X86CallingConvention.Custom,
        StackCleanup = X86StackCleanup.Callee,
        ArgumentOrder = X86ArgumentOrder.RightToLeft)]
    [return: X86RegisterArgument(Register.EDX)]
    private delegate int MutatedSixCalleeClean(
        [X86RegisterArgument(Register.EAX)] int first,
        [X86RegisterArgument(Register.EDX)] int second,
        [X86RegisterArgument(Register.ECX)] int third,
        int fourth,
        int fifth,
        int sixth);

    [Fact]
    public void Custom_CleansOnlyTheStackPortionWhenTheCalleeReturnsWithAnImmediate()
    {
        Assert.SkipUnless(RuntimeInformation.ProcessArchitecture == Architecture.X86, X86Only);

        // Same shape as above but callee-cleaned. The "ret 12" has to count the three stack arguments only: counting all six would unwind the caller's frame as well.
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.add(edx, eax);
            assembler.add(edx, ecx);
            assembler.add(edx, __dword_ptr[esp + 4]);
            assembler.add(edx, __dword_ptr[esp + 8]);
            assembler.add(edx, __dword_ptr[esp + 12]);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret(3 * 4);
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(__dword_ptr[ebp + 28]);
            assembler.push(__dword_ptr[ebp + 24]);
            assembler.push(__dword_ptr[ebp + 20]);
            assembler.mov(eax, __dword_ptr[ebp + 8]);
            assembler.mov(edx, __dword_ptr[ebp + 12]);
            assembler.mov(ecx, __dword_ptr[ebp + 16]);
            assembler.call((UInt32)target.Address);
            assembler.mov(eax, edx);
            assembler.pop(ebp);
            assembler.ret();
        });

        MutatedSixCalleeClean invoke = callSite.GetDelegate<MutatedSixCalleeClean>();
        Assert.Equal(1337, invoke(1, 1, 1, 1, 1, 1332));

        X86Detour<MutatedSixCalleeClean>? detour = null;
        detour = (X86Detour<MutatedSixCalleeClean>)X86DetourBackend.Instance.CreateDetour(
            new DetourRequest<MutatedSixCalleeClean>
            {
                Name = "mutated-six-callee-clean",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth, fifth, sixth) =>
                    detour!.Original(first, second, third, fourth, fifth, sixth) + 100,
                IntermediaryFactory = new X86CallingConventionIntermediaryFactory<MutatedSixCalleeClean>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(1437, invoke(1, 1, 1, 1, 1, 1332));
            Assert.Equal(1437, invoke(1, 1, 1, 1, 1, 1332));
        }
    }

    //
    // mutated: NativeFunction4IntArgs1StructArgMUTATED
    //

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [X86CallingConvention(
        X86CallingConvention.Custom,
        StackCleanup = X86StackCleanup.Caller,
        ArgumentOrder = X86ArgumentOrder.RightToLeft)]
    [return: X86RegisterArgument(Register.ECX)]
    private delegate int MutatedRegistersAndStruct(
        [X86RegisterArgument(Register.EAX)] int first,
        [X86RegisterArgument(Register.EBP)] int second,
        [X86RegisterArgument(Register.EDX)] int third,
        [X86RegisterArgument(Register.EDI)] int fourth,
        Triple fifth);

    [Fact]
    public void Custom_CombinesRegisterArgumentsWithAByValueStruct()
    {
        Assert.SkipUnless(RuntimeInformation.ProcessArchitecture == Architecture.X86, X86Only);

        // Four register arguments plus a 12-byte aggregate: the stack layout has to account for the struct's three slots while skipping the four arguments that never reach the stack.
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.add(eax, ebp);
            assembler.add(eax, edx);
            assembler.add(eax, edi);
            assembler.add(eax, __dword_ptr[esp + 4]);
            assembler.add(eax, __dword_ptr[esp + 8]);
            assembler.movzx(ecx, __word_ptr[esp + 12]);
            assembler.add(eax, ecx);
            assembler.mov(ecx, eax);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret();
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(edi);
            assembler.push(__dword_ptr[ebp + 32]);
            assembler.push(__dword_ptr[ebp + 28]);
            assembler.push(__dword_ptr[ebp + 24]);
            assembler.mov(eax, __dword_ptr[ebp + 8]);
            assembler.mov(edx, __dword_ptr[ebp + 16]);
            assembler.mov(edi, __dword_ptr[ebp + 20]);
            assembler.mov(ecx, __dword_ptr[ebp + 12]);
            assembler.mov(ebp, ecx);
            assembler.call((UInt32)target.Address);
            assembler.add(esp, 3 * 4);
            assembler.mov(eax, ecx);
            assembler.pop(edi);
            assembler.pop(ebp);
            assembler.ret();
        });

        MutatedRegistersAndStruct invoke = callSite.GetDelegate<MutatedRegistersAndStruct>();
        Triple triple = new() { A = 1, B = 2, C = 3 };
        Assert.Equal(1337, invoke(1, 10, 5, 1315, triple));

        X86Detour<MutatedRegistersAndStruct>? detour = null;
        detour = (X86Detour<MutatedRegistersAndStruct>)X86DetourBackend.Instance.CreateDetour(
            new DetourRequest<MutatedRegistersAndStruct>
            {
                Name = "mutated-registers-and-struct",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth, fifth) =>
                {
                    Assert.Equal(2u, fifth.B);
                    return detour!.Original(first, second, third, fourth, fifth) + 100;
                },
                IntermediaryFactory = new X86CallingConventionIntermediaryFactory<MutatedRegistersAndStruct>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(1437, invoke(1, 10, 5, 1315, triple));
        }
    }

    //
    // mutated: NativeFunctionOrderedMUTATED
    //

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [X86CallingConvention(
        X86CallingConvention.Custom,
        StackCleanup = X86StackCleanup.Caller,
        ArgumentOrder = X86ArgumentOrder.RightToLeft)]
    private delegate int MutatedInterleaved(
        [X86RegisterArgument(Register.EAX)] int first,
        int second,
        [X86RegisterArgument(Register.EDX)] int third,
        int fourth);

    [Fact]
    public void Custom_InterleavesRegisterAndStackArgumentsWithoutReorderingTheStack()
    {
        Assert.SkipUnless(RuntimeInformation.ProcessArchitecture == Architecture.X86, X86Only);

        // (eax * a1) + edx + a2, with the register arguments declared first and third.
        // The two stack arguments must land adjacent and in declaration order: an implementation that reserved slots for the register arguments would put the second one at [esp + 16].
        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.imul(eax, __dword_ptr[esp + 4]);
            assembler.add(eax, __dword_ptr[esp + 8]);
            assembler.add(eax, edx);
            X86TestFunctions.Pad(assembler, 24);
            assembler.ret();
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.push(ebp);
            assembler.mov(ebp, esp);
            assembler.push(__dword_ptr[ebp + 20]);
            assembler.push(__dword_ptr[ebp + 12]);
            assembler.mov(eax, __dword_ptr[ebp + 8]);
            assembler.mov(edx, __dword_ptr[ebp + 16]);
            assembler.call((UInt32)target.Address);
            assembler.add(esp, 2 * 4);
            assembler.pop(ebp);
            assembler.ret();
        });

        MutatedInterleaved invoke = callSite.GetDelegate<MutatedInterleaved>();
        Assert.Equal(1337, invoke(2, 668, 1, 0));

        X86Detour<MutatedInterleaved>? detour = null;
        detour = (X86Detour<MutatedInterleaved>)X86DetourBackend.Instance.CreateDetour(
            new DetourRequest<MutatedInterleaved>
            {
                Name = "mutated-interleaved",
                TargetAddress = target.Address,
                Callback = (first, second, third, fourth) =>
                {
                    // Wrong stack offsets would swap these two, which the sum alone can hide.
                    Assert.Equal(668, second);
                    Assert.Equal(0, fourth);
                    return detour!.Original(first, second, third, fourth) + 100;
                },
                IntermediaryFactory = new X86CallingConventionIntermediaryFactory<MutatedInterleaved>(),
            });

        using (detour)
        {
            detour.Enable();
            Assert.Equal(1437, invoke(2, 668, 1, 0));
        }
    }

    //
    // Signature validation. These run on any host: the factory validates in its constructor, before it needs to emit anything.
    //

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [X86CallingConvention(
        X86CallingConvention.Custom,
        StackCleanup = X86StackCleanup.Caller,
        ArgumentOrder = X86ArgumentOrder.RightToLeft)]
    private delegate int StructInARegister([X86RegisterArgument(Register.EAX)] Triple value);

    [Fact]
    public void Custom_RejectsAByValueStructInARegister()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new X86CallingConventionIntermediaryFactory<StructInARegister>());

        Assert.Contains("must be a scalar", error.Message);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [X86CallingConvention(
        X86CallingConvention.Custom,
        StackCleanup = X86StackCleanup.Caller,
        ArgumentOrder = X86ArgumentOrder.RightToLeft)]
    private delegate int DuplicateRegisters(
        [X86RegisterArgument(Register.EAX)] int first,
        [X86RegisterArgument(Register.EAX)] int second);

    [Fact]
    public void Custom_RejectsTheSameRegisterTwice()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new X86CallingConventionIntermediaryFactory<DuplicateRegisters>());

        Assert.Contains("more than one argument", error.Message);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StructFirstThiscall(Triple instance, int value);

    [Fact]
    public void Thiscall_RequiresAScalarLeadingArgument()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new X86CallingConventionIntermediaryFactory<StructFirstThiscall>(
                X86CallingConvention.Thiscall));

        Assert.Contains("'this' argument", error.Message);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AnnotatedFastcall(
        [X86RegisterArgument(Register.ESI)] int first,
        int second);

    [Fact]
    public void RegisterAnnotations_AreRejectedOnAStandardConvention()
    {
        // Fastcall's register assignment is fixed by the ABI; honouring an annotation here would silently produce a frame the native target does not use.
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new X86CallingConventionIntermediaryFactory<AnnotatedFastcall>(
                X86CallingConvention.Fastcall));

        Assert.Contains("X86CallingConvention.Custom", error.Message);
    }

    private static bool IsMicrosoftHost()
        => RuntimeInformation.ProcessArchitecture == Architecture.X86 &&
           RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
