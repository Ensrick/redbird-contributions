using RedBird.Abstractions.CallingConventions;
using System;

namespace RedBird.Extensions.CallingConventions.NativeX86;

/// <summary>Immutable configuration for an x86 calling-convention intermediary.</summary>
public sealed class X86CallingConventionOptions
{
    /// <summary>Creates options for a standard convention.</summary>
    public X86CallingConventionOptions(X86CallingConvention convention)
    {
        Convention = convention;
        (StackCleanup, ArgumentOrder) = convention switch
        {
            X86CallingConvention.Cdecl => (X86StackCleanup.Caller, X86ArgumentOrder.RightToLeft),
            X86CallingConvention.GccCdecl => (X86StackCleanup.Caller, X86ArgumentOrder.RightToLeft),
            X86CallingConvention.Stdcall => (X86StackCleanup.Callee, X86ArgumentOrder.RightToLeft),
            X86CallingConvention.Fastcall => (X86StackCleanup.Callee, X86ArgumentOrder.RightToLeft),
            X86CallingConvention.Thiscall => (X86StackCleanup.Callee, X86ArgumentOrder.RightToLeft),
            X86CallingConvention.GccThiscall => (X86StackCleanup.Caller, X86ArgumentOrder.RightToLeft),
            X86CallingConvention.Pascal => (X86StackCleanup.Callee, X86ArgumentOrder.LeftToRight),
            X86CallingConvention.Custom => throw new ArgumentException(
                "Custom conventions require explicit stack-cleanup and argument-order settings.",
                nameof(convention)),
            _ => throw new ArgumentOutOfRangeException(nameof(convention)),
        };
    }

    /// <summary>Creates options for a custom convention.</summary>
    public X86CallingConventionOptions(X86StackCleanup stackCleanup, X86ArgumentOrder argumentOrder)
    {
        Convention = X86CallingConvention.Custom;
        StackCleanup = stackCleanup;
        ArgumentOrder = argumentOrder;
    }

    internal X86CallingConventionOptions(
        X86CallingConvention convention,
        X86StackCleanup stackCleanup,
        X86ArgumentOrder argumentOrder)
    {
        Convention = convention;
        StackCleanup = stackCleanup;
        ArgumentOrder = argumentOrder;
    }

    /// <summary>The native convention.</summary>
    public X86CallingConvention Convention { get; }

    /// <summary>Who removes native stack arguments.</summary>
    public X86StackCleanup StackCleanup { get; }

    /// <summary>Native stack push order.</summary>
    public X86ArgumentOrder ArgumentOrder { get; }
}
