using System;
using Iced.Intel;

namespace RedBird.Extensions.CallingConventions.NativeX86;

/// <summary>Maps a custom x86 argument or scalar return value to a 32-bit general-purpose register.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)]
public sealed class X86RegisterArgumentAttribute : Attribute
{
    /// <summary>Creates an explicit register mapping.</summary>
    public X86RegisterArgumentAttribute(Register register)
    {
        Register = register;
    }

    /// <summary>The mapped x86 register.</summary>
    public Register Register { get; }
}
