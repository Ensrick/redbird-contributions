using System;
using Iced.Intel;
using RedBird.Core.Memory;
using RedBird.Core.Platform;
using RedBird.X64.Extensions;
using RedBird.X64.Tests.Infrastructure;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Tests;

public sealed class NativeStateTests
{
    [Fact]
    public void Flag_CanChangeAnAlreadyAssembledFunctionWithoutAManagedCallback()
    {
        using NativeStateBlock state = new();
        NativeFlag enabled = state.AllocateFlag();

        using ExecutableFunction function = ExecutableFunction.Create(assembler =>
        {
            Label disabled = assembler.CreateLabel();

            assembler.mov(r11d, 40);
            assembler.TestNativeFlag(enabled, r11);
            assembler.jz(disabled);
            assembler.mov(eax, r11d);
            assembler.ret();

            assembler.Label(ref disabled);
            assembler.mov(eax, r11d);
            assembler.inc(eax);
            assembler.ret();
        });

        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();

        Assert.Equal(41, call());
        enabled.Value = true;
        Assert.Equal(40, call());
        enabled.Value = false;
        Assert.Equal(41, call());
    }

    [Fact]
    public void FlagArray_IsBytePackedAndElementsAreIndependentlyAddressable()
    {
        using NativeStateBlock state = new();
        NativeFlagArray flags = state.AllocateFlags(4);

        flags[1] = true;
        flags[3] = true;

        Assert.False(flags[0]);
        Assert.True(flags[1]);
        Assert.False(flags[2]);
        Assert.True(flags[3]);
        Assert.Equal(flags.Address + 3, flags.GetAddress(3));

        flags.Fill(false);
        for (int index = 0; index < flags.Length; index++)
            Assert.False(flags[index]);
    }

    [Fact]
    public void FlagArray_CanUseRuntimeRegisterIndex()
    {
        using NativeStateBlock state = new();
        NativeFlagArray flags = state.AllocateFlags(4);
        flags[1] = true;
        flags[3] = true;

        using ExecutableFunction function = ExecutableFunction.Create(assembler =>
        {
            Label disabled = assembler.CreateLabel();

            assembler.mov(r11d, 40);
            assembler.TestNativeFlag(flags, AbiRegisters.Arg0D, r11);
            assembler.jz(disabled);
            assembler.mov(eax, r11d);
            assembler.ret();

            assembler.Label(ref disabled);
            assembler.mov(eax, r11d);
            assembler.inc(eax);
            assembler.ret();
        });

        TestDelegates.BinaryInt call = function.GetDelegate<TestDelegates.BinaryInt>();

        Assert.Equal(41, call(0, 0));
        Assert.Equal(40, call(1, 0));
        Assert.Equal(41, call(2, 0));
        Assert.Equal(40, call(3, 0));
    }

    [Fact]
    public void FlagArray_CanLoadRuntimeIndexFromStackMemory()
    {
        using NativeStateBlock state = new();
        NativeFlagArray flags = state.AllocateFlags(4);
        flags[2] = true;

        Int64 stackIndexOffset = PlatformInfo.IsWindows ? 0x38 : 0x08;
        using ExecutableFunction function = ExecutableFunction.Create(assembler =>
        {
            Label disabled = assembler.CreateLabel();

            assembler.mov(r11d, 40);
            assembler.TestNativeFlag(flags, __dword_ptr[rsp + stackIndexOffset], r10d, r11);
            assembler.jz(disabled);
            assembler.mov(eax, r11d);
            assembler.ret();

            assembler.Label(ref disabled);
            assembler.mov(eax, r11d);
            assembler.inc(eax);
            assembler.ret();
        });

        SevenInt call = function.GetDelegate<SevenInt>();

        Assert.Equal(41, call(0, 0, 0, 0, 0, 0, 1));
        Assert.Equal(40, call(0, 0, 0, 0, 0, 0, 2));
    }

    [Fact]
    public void ValuesAndArrays_AreAlignedAndShareOneStableBlock()
    {
        using NativeStateBlock state = new(128);
        NativeFlag flag = state.AllocateFlag(true);
        NativeStateValue<Int32> value = state.AllocateValue(123);
        NativeStateArray<UInt16> values = state.AllocateArray<UInt16>(3, 7);

        Assert.Equal(123, value.Value);
        Assert.Equal((UInt16)7, values[0]);
        Assert.Equal((UInt16)7, values[2]);
        Assert.Equal(0UL, value.Address & 3UL);
        Assert.Equal(0UL, values.Address & 1UL);
        Assert.InRange(flag.Address, state.Address, state.Address + (UInt64)state.Capacity - 1);

        value.Value = 456;
        values[1] = 9;
        Assert.Equal(456, value.Value);
        Assert.Equal((UInt16)9, values[1]);
    }

    [Fact]
    public void Dispose_InvalidatesManagedViewsAndCapacityIsChecked()
    {
        NativeStateBlock state = new(4);
        NativeFlag flag = state.AllocateFlag();

        Assert.Throws<InvalidOperationException>(() => state.AllocateArray<Int32>(1));

        state.Dispose();

        Assert.True(state.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = flag.Value);
        Assert.Throws<ObjectDisposedException>(() => state.AllocateFlag());
    }

    private delegate int SevenInt(int a, int b, int c, int d, int e, int f, int index);
}
