using Iced.Intel;
using System;
using System.Collections.Generic;
using System.Text;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Memory;
using RedBird.X64.Assembly;

namespace RedBird.X64.Hooks.Context;

/// <summary>
/// Configuration for a context hook.
/// <code>
/// tx.AddContextHook(handle, HookTarget.FromPattern(pattern, offset: 5), OnSiegeRallypoint,
///     new ContextHookOptions { Registers = X64SmartCPUContextRegs.Volatile });
/// </code>
/// </summary>
public sealed record ContextHookOptions
{
    /// <summary>Shared instance for the all-defaults case.</summary>
    public static ContextHookOptions Default { get; } = new();

    /// <summary>
    /// Which registers to capture into the context struct and write back afterwards.
    /// Narrowing this to <see cref="X64SmartCPUContextRegs.Volatile"/> shortens the prologue and epilogue of every stub invocation, which matters on hooks that fire per frame.
    /// </summary>
    public X64SmartCPUContextRegs Registers { get; init; } = X64SmartCPUContextRegs.All;

    /// <summary>Minimum number of bytes to displace; never less than the 14-byte absolute JMP.</summary>
    public int HookSize { get; init; } = 14;

    /// <summary>Optional filter over which displaced instructions get re-emitted in the stub.</summary>
    public Func<IReadOnlyList<Instruction>, IEnumerable<Instruction>>? InstructionSelector { get; init; }

    /// <summary>How exceptions escaping the callback are handled.</summary>
    public CallbackErrorMode ErrorMode { get; init; } = CallbackErrorMode.LogAndContinue;

    /// <summary>Invoked on failure when <see cref="ErrorMode"/> is <see cref="CallbackErrorMode.LogAndContinueWithRecovery"/>.</summary>
    public Action<NativePointer<X64SmartCPUContext>, Exception>? RecoveryAction { get; init; }

    /// <summary>Where displaced instructions run relative to the callback.</summary>
    public OverwrittenInstructionPlacement Placement { get; init; } = OverwrittenInstructionPlacement.AfterCallback;
}
