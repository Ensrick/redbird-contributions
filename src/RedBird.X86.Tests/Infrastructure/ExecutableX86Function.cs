global using ExecutableX86Function = RedBird.X86.Assembly.ExecutableX86Function;

using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Tests.Infrastructure;

/// <summary>Small instruction bodies shared by x86 tests; allocation belongs to the public type.</summary>
internal static class X86TestFunctions
{
    public static ExecutableX86Function Add()
        => ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, __dword_ptr[esp + 4]);
            assembler.add(eax, __dword_ptr[esp + 8]);
            Pad(assembler, 24);
            assembler.ret();
        });

    public static void Pad(Assembler assembler, int count)
    {
        for (int index = 0; index < count; index++)
            assembler.nop();
    }
}
