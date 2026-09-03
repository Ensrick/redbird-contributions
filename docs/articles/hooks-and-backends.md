---
title: Hooks and backends
description: Choose a detour backend and install hooks transactionally.
---

# Hooks and backends

## Backend selection

`IHookBackend` separates hook orchestration from the engine that owns a function-entry patch.

| Backend | Architectures | Notes |
|---|---|---|
| `NativeDetourBackend` | X64 | Managed relocation, several jump schemes, chaining, safe Windows patching |
| `X86DetourBackend` | X86 | Managed X86 relocation and safe Windows patching |
| `PolyHook2Backend` | X64/native dependency | One physical hook per target with managed logical layers |
| `MinHookBackend` | Windows X86/X64 | Adapter over MinHook.NET; upstream relocation limitations still apply |

Register a process-wide default or set a backend on transaction options:

```csharp
HookBackends.Default = NativeDetourBackend.Instance;
```

## Transactions

Transactions prepare hooks before installation, share pattern scans, and apply a consistent failure policy. 

Targets can be absolute addresses, RVAs, exports, or byte patterns.

```csharp
using ScanRegion region = ScanRegion.FromModule(moduleHandle);
using var transaction = new HookTransaction(
    region,
    options: new HookTransactionOptions
    {
        Backend = NativeDetourBackend.Instance,
        FailureMode = TransactionFailureMode.RollbackAndThrow,
    });

var update = new DetourHandle<UpdateDelegate>();
transaction.AddDetour(
    update,
    HookTarget.FromPattern("48 89 5C 24 ?? 57 48 83 EC ??"),
    OnUpdate);

CommitResult result = transaction.Commit();
```

The handle exposes `Original` after commit. 
Disposal restores installed hooks newest-first.

## Chaining

The native engines layer patches and require reverse-order removal by default.