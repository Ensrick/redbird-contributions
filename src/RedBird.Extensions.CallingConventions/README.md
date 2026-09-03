# RedBird calling-convention adapters

This extension supplies `IDetourIntermediaryFactory` implementations for native conventions that cannot be represented directly by a managed delegate.

## Microsoft x86

The managed-facing delegate must be cdecl and must list every logical argument. For thiscall, the
first argument is the explicit `this` pointer.

```csharp
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int NativeFunction(int first, int second, int third);

var factory = new X86CallingConventionIntermediaryFactory<NativeFunction>(
    X86CallingConvention.Fastcall);

var request = new DetourRequest<NativeFunction>
{
    Name = "native-function",
    TargetAddress = address,
    Callback = callback,
    IntermediaryFactory = factory,
};
```

Supported layouts are cdecl, stdcall, fastcall, thiscall, GCC cdecl/thiscall, Pascal, and custom register/stack layouts. 
Custom layouts use `[X86CallingConvention]` on the delegate and `[X86RegisterArgument]` on scalar parameters or the return value.

## Microsoft x64 vectorcall

`X64VectorcallIntermediaryFactory<TDelegate>` translates ordinary Microsoft x64 fastcall callback frames to vectorcall and back. 
`float` and `double` are detected automatically. 
Mark a blittable SIMD value with `[VectorcallArgument(16)]` or `[VectorcallArgument(32)]`.

Direct scalar and SIMD vector arguments are supported. 
Homogeneous vector aggregates should be flattened into vector parameters or passed by pointer. 
By-value SIMD and large aggregate returns are intentionally rejected because a managed Microsoft-x64 delegate cannot represent their hidden
return-buffer and register rules portably; use an explicit result pointer instead.

The x64 bridge is Windows-only. Generated callbacks must not allow exceptions to escape across the
native boundary because intermediary code does not carry Windows unwind metadata.
