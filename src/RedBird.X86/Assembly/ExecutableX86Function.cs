using System;
using Iced.Intel;
using RedBird.Core.Memory;

namespace RedBird.X86.Assembly;

/// <summary>A real 32-bit x86 function, assembled into executable memory at run time.</summary>
/// <remarks>
/// Only usable in a 32-bit process: the body is assembled for an address the code itself has to be able to hold, so creation fails on a region above 4 GiB.
/// </remarks>
/// <example>
/// <code>
/// using ExecutableX86Function add = ExecutableX86Function.Create(assembler =>
/// {
///     assembler.mov(eax, __dword_ptr[esp + 4]);
///     assembler.add(eax, __dword_ptr[esp + 8]);
///     assembler.ret();
/// });
///
/// int sum = add.GetDelegate&lt;BinaryInt&gt;()(3, 4);
/// </code>
/// </example>
public sealed class ExecutableX86Function : ExecutableCode
{
    private const int Bitness = 32;

    private ExecutableX86Function(
        Action<Assembler, ExecutableCodeLayout> build,
        Action<ExecutableCodeLayout>? configure)
        : base(Bitness, requires32BitAddress: true, build, configure)
    {
    }

    /// <summary>Assembles a 32-bit function that references its own region.</summary>
    /// <param name="build">Emits the body. The assembler is positioned at the entry point.</param>
    /// <param name="configure">Optional hook to seed the data slot before the code is written.</param>
    public static ExecutableX86Function Create(
        Action<Assembler, ExecutableCodeLayout> build,
        Action<ExecutableCodeLayout>? configure = null)
        => new(build, configure);

    /// <summary>Assembles a 32-bit function that needs no address information.</summary>
    public static ExecutableX86Function Create(Action<Assembler> build)
    {
        if (build is null)
            throw new ArgumentNullException(nameof(build));

        return new ExecutableX86Function((assembler, _) => build(assembler), null);
    }
}
