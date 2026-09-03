---
title: Calling-convention adapters
description: Bridge managed delegates to X86 conventions and Microsoft X64 vectorcall.
---

# Calling-convention adapters

>If you have worked with the CallingConventionDispatcher nuget library before, this will be quite similiar.

`IDetourIntermediaryFactory` supplies two generated entry points to a native backend:

- native target ABI -> managed callback ABI;
- managed `Original` ABI -> relocated native target ABI.

## X86

The managed-facing delegate must use cdecl and list every argument. 
For thiscall, the first logical argument is the explicit `this` pointer.

```csharp
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int NativeFunction(int first, int second, int third);

var factory = new X86CallingConventionIntermediaryFactory<NativeFunction>(X86CallingConvention.Fastcall);
```

Supported presets include cdecl, stdcall, fastcall, thiscall, GCC variants, and Pascal ordering.
Custom layouts use `[X86CallingConvention]` and `[X86RegisterArgument]` to define register arguments, return registers, push order, and caller/callee cleanup.

## X64 vectorcall

`X64VectorcallIntermediaryFactory<TDelegate>` bridges the platform's normal managed X64 ABI and Microsoft `__vectorcall` in both directions. 
It supports Windows X64 and Clang-compatible System V Linux X64 lowering.

```csharp
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate float Transform(float scale, [VectorcallArgument(16)] Vector128<float> value);

var factory = new X64VectorcallIntermediaryFactory<Transform>();
```

`float` and `double` are detected directly. Annotate opaque SIMD values with `[VectorcallArgument(16)]` or `[VectorcallArgument(32)]`. 
Annotate homogeneous vector aggregates with `[VectorcallHva(elementSize, elementCount)]` when their managed layout cannot communicate that ABI classification by itself.

The adapter validates unsupported signatures before it writes executable memory. Generated native
stubs do not contain platform unwind metadata, so callbacks must catch exceptions before they cross
the native boundary.

## Direct calls from assembler stubs

`X64FastcallSafe` is the standard Windows X64 helper for calling a callback from an arbitrary hook site. 
It preserves volatile state, realigns RSP without borrowing RBP, allocates the 32-byte home space, and can forward stack arguments. 
Set `preserveRAX: false` when the target's integer return value should remain in RAX.

```csharp
assembler.X64FastcallSafe(
    callbackAddress,
    totalArgumentCount: 6,
    prepareArgumentsAction: asm =>
    {
        asm.mov(rcx, r12);
        asm.mov(rdx, r13);
    },
    preserveRAX: false,
    stackArguments:
    [
        new(AssemblerExtensions.X64ArgumentSourceType.FromOriginalStack, 0),
        new(AssemblerExtensions.X64ArgumentSourceType.FromImmediate, 42),
    ]);
```

On x86, `X86CallSafe` is the generic equivalent. 
Convention-specific wrappers are available for cdecl, stdcall, Microsoft thiscall/fastcall, GCC cdecl/thiscall, and Pascal. 
Each descriptor is one 4-byte stack slot, so a by-value struct can be represented by several descriptors. Caller-vs-callee cleanup and Pascal's reversed stack order are handled by the selected convention.

```csharp
assembler.X86FastcallSafe(
    callbackAddress,
    prepareArgumentsAction: asm =>
    {
        asm.mov(ecx, esi);
        asm.mov(edx, edi);
    },
    preserveEAX: false,
    stackArguments:
    [
        new(AssemblerExtensions.X86ArgumentSourceType.FromImmediate, 42),
        new(AssemblerExtensions.X86ArgumentSourceType.FromOriginalStack, 0),
    ]);
```

The `FromOriginalStack` source on both architectures assumes that the helper runs at function entry, before the original function changes RSP or ESP. 
At a later inline-hook site, prepare arguments from a known frame/register context instead.
