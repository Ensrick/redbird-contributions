using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.Examples;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BinaryInt(int left, int right);

    private static void Main()
    {
        using ExecutableFunction target = ExecutableFunction.Create(assembler =>
        {
            if (OperatingSystem.IsWindows())
            {
                assembler.mov(eax, ecx);
                assembler.add(eax, edx);
            }
            else
            {
                assembler.mov(eax, edi);
                assembler.add(eax, esi);
            }
            for (int index = 0; index < 20; index++)
                assembler.nop();
            assembler.ret();
        });

        BinaryInt call = target.GetDelegate<BinaryInt>();
        IDetour<BinaryInt>? hook = null;
        hook = NativeDetourBackend.Instance.CreateDetour(
            new DetourRequest<BinaryInt>
            {
                Name = "multiply-add-result",
                TargetAddress = target.Address,
                Callback = (first, second) => hook!.Original(first, second) * 10,
            });

        using (hook)
        {
            Console.WriteLine($"Before hook: {call(3, 4)}");
            hook.Enable();
            Console.WriteLine($"After hook:  {call(3, 4)}");
        }

        Console.WriteLine($"Restored:    {call(3, 4)}");
    }
}
