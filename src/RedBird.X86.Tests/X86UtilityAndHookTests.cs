using System;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX86;
using RedBird.Core.Memory;
using RedBird.X86.Assembly;
using RedBird.X86.Assembly.InstructionWalker;
using RedBird.X86.Assembly.Stateful;
using RedBird.X86.Extensions;
using RedBird.X86.Hooks;
using RedBird.X86.Hooks.Context;
using RedBird.X86.Hooks.Transaction;
using RedBird.X86.Memory;
using RedBird.X86.Memory.Scanners;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Tests;

/// <summary>Exercises the x86-specific utility and hook surface in a real 32-bit process.</summary>
public sealed unsafe class X86UtilityAndHookTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BinaryInt(int left, int right);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NullaryInt();

    [Fact]
    public void WalkerScannerManagedValueAndRvaValue_Use32BitOperands()
    {
        using ExecutableRegion region = ExecutableRegion.ScannerFixture();

        InstructionWalkerConfig config = new()
        {
            Bitness = 32,
            WalkRules =
            [
                InstructionWalkRule.FindAndExtract(
                    InstructionWalkerHelpers.IsMov,
                    InstructionWalkerHelpers.ExtractImmediate),
            ],
        };

        InstructionWalker walker = new(region.Address, config);
        Assert.True(walker.TryWalk(out UInt32 rva));
        Assert.Equal(0x80u, rva);
        Assert.Equal(32, config.ToScanOptions().Bitness);

        using ScanRegion scanRegion = ScanRegion.FromPointer(region.Pointer, region.Size);
        DataScanner scanner = DataScanner.Create(scanRegion)
            .Scan("B8 80 00 00 00 A1 ?? ?? ?? ?? C3");

        Assert.True(scanner.Found);
        Assert.Equal(0, scanner.CurrentOffset);
        Assert.True(scanner.TryGetManagedImmediate(out ManagedAssemblyImmediate<UInt32>? site, operand: 1));
        Assert.NotNull(site);

        scanner.Advance(5);
        Assert.Equal(5, scanner.CurrentOffset);
        Assert.True(scanner.TryReadAbsoluteAddress(out UInt64 absoluteAddress, operand: 1));
        Assert.Equal(region.Address + 0x80, absoluteAddress);
        Assert.True(AssemblyUtil.TryGetPointerToDisplacement(scanner.CurrentAddress, out byte* displacement, out int size));
        Assert.Equal(4, size);
        Assert.Equal((UInt32)(region.Address + 0x80), *(UInt32*)displacement);

        RVAMemoryValue<UInt32> liveValue = RVAMemoryValue<UInt32>.From(site!, region.Address, live: true);
        Assert.Equal(111u, liveValue.Value);

        site!.SetValue(0x84u);
        Assert.Equal(region.Address + 0x84, liveValue.Address);
        Assert.Equal(222u, liveValue.Value);
        site.RestoreOriginal();
        Assert.Equal(region.Address + 0x80, liveValue.Address);
        site.Dispose();
    }

    [Fact]
    public void InlineHook_CanInsertAnIntermediaryBeforeDisplacedInstructions()
    {
        using ExecutableRegion region = ExecutableRegion.AddFunction();
        BinaryInt function = region.GetDelegate<BinaryInt>();
        Assert.Equal(7, function(3, 4));

        using X86InlineHook hook = new(region.Address);
        hook.Generate((assembler, instructions, _) =>
        {
            assembler.add(__dword_ptr[esp + 4], 10);
            assembler.AddInstructions(instructions);
        });

        hook.Enable();
        Assert.Equal(17, function(3, 4));
        hook.Disable();
        Assert.Equal(7, function(3, 4));
    }

    [Fact]
    public void ContextHook_CanReadAndModifyCdeclStackArguments()
    {
        using ExecutableRegion region = ExecutableRegion.AddFunction();
        BinaryInt function = region.GetDelegate<BinaryInt>();
        using X86InlineHook hook = new(region.Address);

        ContextHookDelegate callback = context =>
        {
            ref X86SmartCPUContext state = ref context.AsRef();
            *state.GetStackArgument<Int32>(0) += 20;
        };

        ContextHookOptions options = new()
        {
            Registers = X86SmartCPUContextRegs.All,
            Placement = OverwrittenInstructionPlacement.AfterCallback,
        };

        ContextHookDelegate guarded = ContextStub.Guard(callback, options, region.Address, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        hook.Generate(ContextStub.CreateGenerator(hook, guarded, options));
        hook.Enable();

        Assert.Equal(27, function(3, 4));
        hook.Disable();
        Assert.Equal(7, function(3, 4));
    }

    [Fact]
    public void JumpTargetPatch_RetargetsAndRestoresRel32()
    {
        using ExecutableRegion region = ExecutableRegion.JumpFixture();
        NullaryInt function = region.GetDelegate<NullaryInt>();
        Assert.Equal(1, function());

        using X86JmpTargetPatch patch = new(region.Address);
        Assert.Equal(region.Address + 0x20, patch.OriginalTarget);
        patch.Apply(region.Address + 0x40);
        Assert.Equal(2, function());
        patch.Undo();
        Assert.Equal(1, function());
    }

    [Fact]
    public void JumpTargetPatch_RetargetsAbsoluteIndirectPointerSlot()
    {
        using ExecutableRegion region = ExecutableRegion.IndirectJumpFixture();
        NullaryInt function = region.GetDelegate<NullaryInt>();
        Assert.Equal(1, function());

        using X86JmpTargetPatch patch = new(region.Address);
        Assert.Equal(region.Address + 0x10, patch.PointerSlotAddress);
        patch.Apply(region.Address + 0x40);
        Assert.Equal(2, function());
        patch.Undo();
        Assert.Equal(1, function());
    }

    [Fact]
    public void FunctionCloneHook_InjectsAtMatchedInstructionAndRestoresEntry()
    {
        using ExecutableRegion region = ExecutableRegion.AddFunction();
        BinaryInt function = region.GetDelegate<BinaryInt>();

        FunctionClonePatch patch = new()
        {
            Name = "increment-first-argument",
            Predicate = instruction => instruction.IP == region.Address,
            PreserveFlags = false,
            Generator = (Assembler assembler, Instruction _, ref bool suppressOriginal) =>
            {
                assembler.inc(__dword_ptr[esp + 8]);
                suppressOriginal = false;
            },
        };

        using X86FunctionCloneHook hook = new(region.Address, [patch]);
        hook.Compile();
        hook.Enable();
        Assert.Equal(8, function(3, 4));
        hook.Disable();
        Assert.Equal(7, function(3, 4));
    }

    [Fact]
    public void Transaction_UsesNativeX86BackendByDefault()
    {
        using ExecutableRegion region = ExecutableRegion.AddFunction();
        BinaryInt function = region.GetDelegate<BinaryInt>();
        using ScanRegion scanRegion = ScanRegion.FromPointer(region.Pointer, region.Size);
        using HookTransaction transaction = new(
            scanRegion,
            options: new HookTransactionOptions { OwnsHooks = true });

        RedBird.Abstractions.Hooks.Transaction.DetourHandle<BinaryInt>? handle = null;
        BinaryInt callback = (left, right) => handle!.Original(left, right) * 3;
        handle = transaction.AddDetour(HookTarget.FromAddress(region.Address), callback);

        transaction.Commit();
        Assert.Equal(21, function(3, 4));
        transaction.DisableAll();
        Assert.Equal(7, function(3, 4));
    }

    private sealed class ExecutableRegion : IDisposable
    {
        public const int DefaultSize = 4096;
        private readonly IntPtr _pointer;

        private ExecutableRegion(IntPtr pointer) => _pointer = pointer;

        public IntPtr Pointer => _pointer;
        public UInt64 Address => unchecked((UInt32)_pointer.ToInt32());
        public int Size => DefaultSize;

        public static ExecutableRegion AddFunction()
        {
            return FromAssembler(assembler =>
            {
                assembler.mov(eax, __dword_ptr[esp + 4]);
                assembler.add(eax, __dword_ptr[esp + 8]);
                for (int index = 0; index < 24; index++)
                    assembler.nop();
                assembler.ret();
            });
        }

        public static ExecutableRegion JumpFixture()
        {
            ExecutableRegion region = AllocateWritable();
            try
            {
                Span<byte> bytes = new((void*)region.Pointer, region.Size);
                X86JumpFactory.BuildDirect(region.Address, region.Address + 0x20).CopyTo(bytes);
                bytes[0x20] = 0xB8;
                *(UInt32*)((byte*)region.Pointer + 0x21) = 1;
                bytes[0x25] = 0xC3;
                bytes[0x40] = 0xB8;
                *(UInt32*)((byte*)region.Pointer + 0x41) = 2;
                bytes[0x45] = 0xC3;
                region.MakeExecutable();
                return region;
            }
            catch
            {
                region.Dispose();
                throw;
            }
        }

        public static ExecutableRegion IndirectJumpFixture()
        {
            ExecutableRegion region = AllocateWritable();
            try
            {
                byte* bytes = (byte*)region.Pointer;
                bytes[0] = 0xFF;
                bytes[1] = 0x25;
                *(UInt32*)(bytes + 2) = unchecked((UInt32)(region.Address + 0x10));
                *(UInt32*)(bytes + 0x10) = unchecked((UInt32)(region.Address + 0x20));
                bytes[0x20] = 0xB8;
                *(UInt32*)(bytes + 0x21) = 1;
                bytes[0x25] = 0xC3;
                bytes[0x40] = 0xB8;
                *(UInt32*)(bytes + 0x41) = 2;
                bytes[0x45] = 0xC3;
                region.MakeExecutable();
                return region;
            }
            catch
            {
                region.Dispose();
                throw;
            }
        }

        public static ExecutableRegion ScannerFixture()
        {
            ExecutableRegion region = AllocateWritable();
            try
            {
                Span<byte> bytes = new((void*)region.Pointer, region.Size);
                bytes[0] = 0xB8;
                *(UInt32*)((byte*)region.Pointer + 1) = 0x80;
                bytes[5] = 0xA1;
                *(UInt32*)((byte*)region.Pointer + 6) = unchecked((UInt32)(region.Address + 0x80));
                bytes[10] = 0xC3;
                *(UInt32*)((byte*)region.Pointer + 0x80) = 111;
                *(UInt32*)((byte*)region.Pointer + 0x84) = 222;
                region.MakeExecutable();
                return region;
            }
            catch
            {
                region.Dispose();
                throw;
            }
        }

        public T GetDelegate<T>() where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(_pointer);

        public void Dispose() => NativeMemoryManager.Free(_pointer, Size);

        private static ExecutableRegion FromAssembler(Action<Assembler> generate)
        {
            ExecutableRegion region = AllocateWritable();
            try
            {
                Assembler assembler = new(32);
                generate(assembler);
                using MemoryStream stream = new();
                if (!assembler.TryAssemble(new StreamCodeWriter(stream), region.Address, out string? error, out _))
                    throw new InvalidOperationException(error);

                byte[] code = stream.ToArray();
                Marshal.Copy(code, 0, region.Pointer, code.Length);
                region.MakeExecutable();
                return region;
            }
            catch
            {
                region.Dispose();
                throw;
            }
        }

        private static ExecutableRegion AllocateWritable()
        {
            IntPtr pointer = NativeMemoryManager.Alloc(DefaultSize, MemoryProtection.ReadWrite);
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate x86 test memory.");

            new Span<byte>((void*)pointer, DefaultSize).Fill(0xCC);
            return new ExecutableRegion(pointer);
        }

        private void MakeExecutable()
        {
            if (!NativeMemoryManager.FinalizeStub(_pointer, Size))
                throw new InvalidOperationException("Could not finalize x86 test memory.");
            InstructionCache.Flush(Address, Size);
        }
    }
}
