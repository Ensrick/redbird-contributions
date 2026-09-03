using System;
using System.Runtime.InteropServices;
using RedBird.Core.Memory;
using RedBird.Core.Memory.Scanners;
using RedBird.Core.Utilities;

namespace RedBird.Core.Interop;

/// <summary>
/// A native function located at run time and callable through a managed delegate.
/// </summary>
/// <typeparam name="TDelegate">
/// Delegate matching the target's signature. Mark it with
/// <see cref="UnmanagedFunctionPointerAttribute"/> when the calling convention is not the default.
/// </typeparam>
public sealed class NativeFunction<TDelegate> where TDelegate : Delegate
{
    private NativeFunction(UInt64 address, TDelegate function, string description)
    {
        Address = address;
        Invoke = function;
        Description = description;
    }

    /// <summary>Absolute address of the function.</summary>
    public UInt64 Address { get; }

    /// <summary>
    /// The callable delegate.
    /// </summary>
    /// <remarks>
    /// Held for the lifetime of this object, which is what keeps the marshalling stub alive. 
    /// Do not let this instance be collected while native code might still call back through it.
    /// </remarks>
    public TDelegate Invoke { get; }

    /// <summary>How the function was located, for diagnostics.</summary>
    public string Description { get; }

    /// <summary>Wraps a known address.</summary>
    public static NativeFunction<TDelegate> FromAddress(UInt64 address, string? description = null)
    {
        if (address == 0)
            throw new ArgumentOutOfRangeException(nameof(address), "Function address is zero.");

        return new NativeFunction<TDelegate>(address, Marshal.GetDelegateForFunctionPointer<TDelegate>((IntPtr)address), description ?? $"0x{address:X16}");
    }

    /// <summary>
    /// Finds a function by AOB pattern within <paramref name="region"/>.
    /// </summary>
    /// <param name="region">Memory to scan, with the base address its offsets are relative to.</param>
    /// <param name="pattern">AOB pattern matching the function's prologue.</param>
    /// <param name="function">The resolved function, or null.</param>
    /// <param name="offset">Byte offset applied to the match, for patterns anchored slightly off the entry point.</param>
    /// <returns>False when the pattern is not found. Nothing is constructed in that case.</returns>
    public static bool TryScan(ScanRegion region, string pattern, out NativeFunction<TDelegate>? function, int offset = 0)
    {
        if (region is null)
            throw new ArgumentNullException(nameof(region));

        function = null;

        long found = MultiPatternScanner.FindFirst(region.Span, CompiledPattern.Parse(pattern));
        if (found < 0)
            return false;

        UInt64 address = unchecked((UInt64)((long)region.ToAddress(found) + offset));
        function = FromAddress(address, $"pattern[{pattern}]");
        return true;
    }

    /// <summary>Finds a function by exported symbol name.</summary>
    public static bool TryFromExport(string moduleName, string exportName, out NativeFunction<TDelegate>? function)
    {
        function = null;

        if (!NativeExports.TryResolveByModuleName(moduleName, exportName, out UInt64 address))
            return false;

        function = FromAddress(address, $"{moduleName}!{exportName}");
        return true;
    }

    /// <summary>Finds a function by RVA from a module base.</summary>
    public static NativeFunction<TDelegate> FromRva(UInt64 moduleBase, UInt64 rva)
        => FromAddress(moduleBase + rva, $"0x{moduleBase:X16}+0x{rva:X}");

    /// <summary>Implicit conversion so the wrapper can be called directly.</summary>
    public static implicit operator TDelegate(NativeFunction<TDelegate> function) => function.Invoke;

    /// <inheritdoc/>
    public override string ToString() => $"{Description} -> 0x{Address:X16}";
}
