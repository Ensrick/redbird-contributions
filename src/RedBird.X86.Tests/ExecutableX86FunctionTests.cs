using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using Xunit;
using RedBird.X86.Tests.Infrastructure;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Tests;

/// <summary>Tests the public 32-bit runtime-assembled function independently of detouring.</summary>
public sealed class ExecutableX86FunctionTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BinaryInt(int left, int right);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NullaryInt();

    [Fact]
    public void Create_ProducesCallableCodeAndAnAccurateSnapshot()
    {
        Assert.Equal(Architecture.X86, RuntimeInformation.ProcessArchitecture);

        using ExecutableX86Function function = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, __dword_ptr[esp + 4]);
            assembler.add(eax, __dword_ptr[esp + 8]);
            assembler.ret();
        });

        Assert.Equal(7, function.GetDelegate<BinaryInt>()(3, 4));
        Assert.NotEqual(0UL, function.Address);
        Assert.Equal(new IntPtr(unchecked((int)function.Address)), function.EntryPoint);
        Assert.Equal(function.OriginalBytes.Length, function.CodeLength);
        Assert.Equal(function.OriginalBytes, function.ReadCurrentBytes(function.CodeLength));
        Assert.True(function.MatchesOriginal(function.CodeLength));
    }

    [Fact]
    public void Layout_DataSlotCanSeedAbsoluteAddressCode()
    {
        using ExecutableX86Function function = ExecutableX86Function.Create(
            (assembler, layout) =>
            {
                assembler.mov(eax, __dword_ptr[(uint)layout.DataAddress]);
                assembler.ret();
            },
            layout => layout.SetData(654321));

        Assert.Equal(function.Address + 2048, function.DataAddress);
        Assert.Equal(654321, function.DataValue);
        Assert.Equal(654321, function.GetDelegate<NullaryInt>()());
    }

    [Fact]
    public void DisposeAndInvalidBuildersFailClearly()
    {
        ExecutableX86Function function = ExecutableX86Function.Create(assembler => assembler.ret());
        function.Dispose();

        Assert.Throws<ObjectDisposedException>(() => function.GetDelegate<NullaryInt>());
        Assert.Throws<ObjectDisposedException>(() => function.ReadCurrentBytes(1));
        Assert.Throws<ArgumentNullException>(() => ExecutableX86Function.Create((Action<Assembler>)null!));
        Assert.Throws<InvalidOperationException>(() => ExecutableX86Function.Create(assembler =>
        {
            for (int index = 0; index < 2049; index++)
                assembler.nop();
        }));
    }
}
