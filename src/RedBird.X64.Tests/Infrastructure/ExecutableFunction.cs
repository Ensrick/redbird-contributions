global using ExecutableFunction = RedBird.X64.Assembly.ExecutableFunction;

using Iced.Intel;
using RedBird.Core.Platform;

namespace RedBird.X64.Tests.Infrastructure;

/// <summary>Argument registers for whichever x64 ABI this process uses.</summary>
public static class AbiRegisters
{
    public static AssemblerRegister32 Arg0D =>
        PlatformInfo.IsWindows ? AssemblerRegisters.ecx : AssemblerRegisters.edi;

    public static AssemblerRegister32 Arg1D =>
        PlatformInfo.IsWindows ? AssemblerRegisters.edx : AssemblerRegisters.esi;

    public static AssemblerRegister32 Arg2D =>
        PlatformInfo.IsWindows ? AssemblerRegisters.r8d : AssemblerRegisters.edx;

    public static AssemblerRegister32 Arg3D =>
        PlatformInfo.IsWindows ? AssemblerRegisters.r9d : AssemblerRegisters.ecx;
}
