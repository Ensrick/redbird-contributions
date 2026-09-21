---
title: Getting started
description: Install RedBird and create a first managed native detour.
---

# Getting started

## Install

Install the meta-package when the application may encounter both architectures:

```shell
dotnet add package RedBird
```

For a smaller dependency graph, reference `RedBird.X64`, `RedBird.X86`, or a backend package directly. Calling-convention translation is entirely optional:

```shell
dotnet add package RedBird.Extensions.CallingConventions
```

## Define the delegate

The delegate is the ABI contract between native and managed code. 
Match parameter widths, return type, blittability, and calling convention. 
Keep the delegate and hook object alive for as long as native code can reach the patch.

```csharp
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
private delegate int BinaryInt(int left, int right);
```

## Create and enable a direct detour

```csharp
NativeDetour<BinaryInt>? hook = null;
hook = (NativeDetour<BinaryInt>)NativeDetourBackend.Instance.CreateDetour(
    new DetourRequest<BinaryInt>
    {
        Name = "native-add",
        TargetAddress = functionAddress,
        Callback = (left, right) => hook!.Original(left, right) * 10,
    });

hook.Enable();
```

`Original` points at the relocated prologue trampoline. 
Dispose hooks in reverse installation order, or use a transaction/session that owns their lifecycle.

## Configure safe patching

The native Windows backends and the NativeX64 backend on Linux x86-64 suspend peer threads during enable/disable by default. 
A thread stopped on an original instruction boundary inside the replaced prologue is redirected to the equivalent trampoline instruction before the bytes change.

```csharp
var backend = new NativeDetourBackend(new NativeDetourOptions
{
    SuspendThreadsDuringPatch = true,
    Chaining = HookChainingPolicy.Allow,
    SafeUnhook = true,
});
```

The option affects installation/removal latency only. 
On Linux x86-64, the packaged native companion reserves one otherwise-unused real-time signal for the process lifetime.

On Unity Mono profiles where `GC.TryStartNoGCRegion` exists but throws `NotImplementedException`, RedBird continues through its allocation-free critical patch routine without no-GC mode. 
A runtime that implements the API but refuses the requested budget is still treated as a pre-install failure.

It fails without writing if a peer blocks that signal, another component replaces its handler, thread
enumeration fails, or every peer cannot be parked within five seconds. 

Be aware that `/proc` must be mounted, the option defaults to false in a constrained Linux environment where `/proc/self/maps` is unavailable.

## Next steps

- Use [transactions](hooks-and-backends.md) to resolve patterns, RVAs, exports, and addresses in one
  shared scan.
- Use [managed-updatable native state](native-state.md) for settings read directly by assembly stubs.
- Use [calling-convention adapters](calling-conventions.md) for X86 custom ABIs or X64 vectorcall.
- Use [executable functions](executable-functions.md) for native test targets and generated adapters.
