using System.Runtime.InteropServices;
using RedBird.Core.Memory;

namespace RedBird.X64.Assembly;

/// <summary>
/// A delegate for hooks that require full CPU context access.
/// </summary>
/// <param name="context">Pointer to the captured CPU state (registers and flags).</param>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ContextHookDelegate(NativePointer<X64SmartCPUContext> context);
