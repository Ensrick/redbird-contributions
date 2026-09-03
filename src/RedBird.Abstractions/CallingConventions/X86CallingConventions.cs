namespace RedBird.Abstractions.CallingConventions;

/// <summary>Native 32-bit x86 calling conventions understood by RedBird.</summary>
public enum X86CallingConvention
{
    /// <summary>Arguments are pushed right-to-left and the caller removes them.</summary>
    Cdecl = 0,

    /// <summary>GCC i386 cdecl: right-to-left stack arguments removed by the caller.</summary>
    GccCdecl = 1,

    /// <summary>Arguments are pushed right-to-left and the callee removes them.</summary>
    Stdcall = 2,

    /// <summary>The first two eligible scalar arguments use ECX and EDX; the callee removes stack arguments.</summary>
    Fastcall = 3,

    /// <summary>The first argument is the ECX <c>this</c> pointer; the callee removes stack arguments.</summary>
    Thiscall = 4,

    /// <summary>GCC i386 thiscall: the explicit this pointer remains on the caller-clean stack.</summary>
    GccThiscall = 5,

    /// <summary>Arguments use explicit register annotations plus configurable stack rules.</summary>
    Custom = 6,

    /// <summary>Arguments are pushed left-to-right and the callee removes them.</summary>
    Pascal = 7,
}
