
using Iced.Intel;
using System;
using System.Collections.Generic;

namespace RedBird.Backends.NativeX64;

/// <summary>Result of deciding which bytes of a function may be overwritten.</summary>
internal sealed class PrologueSelection
{
    /// <summary>Address the prologue starts at, after any thunk following.</summary>
    public required UInt64 FunctionAddress { get; init; }

    /// <summary>Instructions that will be displaced into the trampoline.</summary>
    public required List<Instruction> Instructions { get; init; }

    /// <summary>Total byte length of <see cref="Instructions"/>. Always at least the requested size.</summary>
    public required int Length { get; init; }
}

