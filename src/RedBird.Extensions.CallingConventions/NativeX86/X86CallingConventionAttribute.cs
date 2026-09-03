using RedBird.Abstractions.CallingConventions;
using System;

namespace RedBird.Extensions.CallingConventions.NativeX86;

/// <summary>Declares the native calling convention represented by an x86 delegate signature.</summary>
[AttributeUsage(AttributeTargets.Delegate, AllowMultiple = false, Inherited = false)]
public sealed class X86CallingConventionAttribute : Attribute
{
    /// <summary>Creates a convention declaration.</summary>
    public X86CallingConventionAttribute(X86CallingConvention convention)
    {
        Convention = convention;
    }

    /// <summary>The target native convention.</summary>
    public X86CallingConvention Convention { get; }

    /// <summary>
    /// Stack cleanup for <see cref="X86CallingConvention.Custom"/>. Standard conventions ignore this property and use their ABI-defined behavior.
    /// </summary>
    public X86StackCleanup StackCleanup { get; set; } = X86StackCleanup.Caller;

    /// <summary>
    /// Stack push order for <see cref="X86CallingConvention.Custom"/>. Standard conventions ignore this property and use their ABI-defined behavior.
    /// </summary>
    public X86ArgumentOrder ArgumentOrder { get; set; } = X86ArgumentOrder.RightToLeft;
}
