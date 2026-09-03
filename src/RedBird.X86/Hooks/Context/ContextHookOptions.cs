using System;
using System.Collections.Generic;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX86;
using RedBird.Core.Memory;
using RedBird.X86.Assembly;

namespace RedBird.X86.Hooks.Context;

/// <summary>Configuration for a 32-bit context hook.</summary>
public sealed record ContextHookOptions
{
    public static ContextHookOptions Default { get; } = new();
    public X86SmartCPUContextRegs Registers { get; init; } = X86SmartCPUContextRegs.All;
    public int HookSize { get; init; } = X86JumpFactory.PatchSize;
    public Func<IReadOnlyList<Instruction>, IEnumerable<Instruction>>? InstructionSelector { get; init; }
    public CallbackErrorMode ErrorMode { get; init; } = CallbackErrorMode.LogAndContinue;
    public Action<NativePointer<X86SmartCPUContext>, Exception>? RecoveryAction { get; init; }
    public OverwrittenInstructionPlacement Placement { get; init; } = OverwrittenInstructionPlacement.AfterCallback;
}
