using System;
using Iced.Intel;
using RedBird.Core.Memory;

namespace RedBird.X64.Assembly;

/// <summary>A real x86-64 function, assembled into executable memory at run time.</summary>
/// <remarks>
/// Useful wherever a genuine native function is needed without shipping a native binary: hook targets, trampoline experiments, calling-convention adapters, or a quick reproduction of a prologue shape that a disassembler alone cannot express.
/// </remarks>
/// <example>
/// <code>
/// using ExecutableFunction add = ExecutableFunction.Create(assembler =>
/// {
///     assembler.mov(eax, AbiRegisters.Arg0D);
///     assembler.add(eax, AbiRegisters.Arg1D);
///     assembler.ret();
/// });
///
/// int sum = add.GetDelegate&lt;BinaryInt&gt;()(3, 4);
/// </code>
/// </example>
public sealed class ExecutableFunction : ExecutableCode
{
    private const int Bitness = 64;

    private ExecutableFunction(
        Action<Assembler, ExecutableCodeLayout> build,
        Action<ExecutableCodeLayout>? configure)
        : base(Bitness, requires32BitAddress: false, build, configure)
    {
    }

    /// <summary>Assembles a 64-bit function that references its own region.</summary>
    /// <param name="build">Emits the body. The assembler is positioned at the entry point.</param>
    /// <param name="configure">Optional hook to seed the data slot before the code is written.</param>
    public static ExecutableFunction Create(
        Action<Assembler, ExecutableCodeLayout> build,
        Action<ExecutableCodeLayout>? configure = null)
        => new(build, configure);

    /// <summary>Assembles a 64-bit function that needs no address information.</summary>
    public static ExecutableFunction Create(Action<Assembler> build)
    {
        if (build is null)
            throw new ArgumentNullException(nameof(build));

        return new ExecutableFunction((assembler, _) => build(assembler), null);
    }
}
