using System.Runtime.InteropServices;
using RedBird.Core.Memory;

namespace RedBird.X86.Assembly;

/// <summary>Managed callback invoked with a mutable 32-bit register snapshot.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ContextHookDelegate(NativePointer<X86SmartCPUContext> context);
