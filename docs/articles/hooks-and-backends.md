---
title: Hooks and backends
description: Choose a detour backend and install hooks transactionally.
---

# Hooks and backends

## Backend selection

`IHookBackend` separates hook orchestration from the engine that owns a function-entry patch.

| Backend | Architectures | Notes |
|---|---|---|
| `NativeDetourBackend` | X64 | Managed relocation, several jump schemes, chaining, safe Windows and Linux X64 patching |
| `X86DetourBackend` | X86 | Managed X86 relocation and safe Windows patching |
| `PolyHook2Backend` | X64/native dependency | One physical hook per target with managed logical layers |
| `MinHookBackend` | Windows X86/X64 | Adapter over MinHook.NET; upstream relocation limitations still apply |

Register a process-wide default or set a backend on transaction options:

```csharp
HookBackends.Default = NativeDetourBackend.Instance;
```

## Transactions

Transactions prepare hooks before installation, share pattern scans, and apply a consistent failure policy. 

Targets can be absolute addresses, RVAs, exports, byte patterns, or the destination of a direct relative call identified by a byte pattern.

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

When a caller does not need a long-lived field, `AddDetour` and `AddContextHook` also have overloads that allocate and return a fresh handle. 
The transaction still owns the hook, so the return value can
be ignored when no individual control or status inspection is needed.

For lower-level scans, `DataScanner.CurrentAddress` is the resolved virtual address and
`DataScanner.CurrentOffset` is its signed byte offset from `ScanRegion.BaseAddress`.

### Persistent AOB cache

Transactions can persist pattern matches as region-relative virtual addresses and reuse them on the next run:

```csharp
using RedBird.Core.Memory.Scanners;

string cacheDirectory = "MyCacheDir";

var options = new HookTransactionOptions
{
    Backend = NativeDetourBackend.Instance,
    AobCache = new AobCacheOptions(Path.Combine(cacheDirectory, "game-module.aobcache.json")),
};

using var transaction = new HookTransaction(region, options: options);
```

The parent directory is created on the first write. The compact JSON file contains a format version,
the scan-region length, an XXH64 checksum, and an AOB-to-RVA map. 

On a checksum match, cached entries are also checked against the current bytes before use. 
A complete cache hit avoids the multi-pattern scan; partial hits scan only the missing patterns and then update the file. Missing patterns are cached too.

The same cache works with the x64 and x86 `DataScanner`. `Scan` reuses first-match RVAs, while
`ScanAll` persists complete multi-match RVA sets. When `ScanAll` reaches its result limit, that truncated
set is returned but is not reused later as a complete result. A scanner instance loads and checksums its
region only once, then shares that state across its fluent scan calls. Core callers can also pass a
`ScanRegion` and `AobCacheOptions` to the cached `MultiPatternScanner.FindFirst`/`FindAll` overloads.

Use one cache file per module or scan region. A changed checksum, malformed file, unsupported format,
or invalid RVA causes a safe rescan. Cache I/O errors are logged and never fail the hook transaction.

`AobCacheOptions.Store` defaults to `JsonAobCacheStore.Instance`. Implement `IAobCacheStore` and set
that property when a binary format such as MessagePack or otherwise is preferable.

### Relative-call targets

Some functions have no stable entry-point signature, while one of their callers does. 
Use `FromRelativeCall` to scan for that call site and detour the function reached by its direct relative `CALL`:

```csharp
transaction.AddDetour(
    createTribeHook,
    HookTarget.FromRelativeCall(
        "E8 ?? ?? ?? ?? 48 63 F8 89 7C 24 ?? 85 C0"),
    OnCreateTribe);
```

If the signature begins before the call instruction, provide its signed byte offset:

```csharp
HookTarget target = HookTarget.FromRelativeCall(pattern, callOffset: 12);
```

This mode accepts only decoded direct relative calls. An indirect call fails during the resolve
phase with a diagnostic identifying the matched instruction. Ordinary `FromPattern` targets keep
their existing behavior and resolve to the matched address plus their optional offset.

### Export targets and handles

On Windows, the module argument to `HookTarget.FromExport` is an `HMODULE` (the module base address returned by the loader). 
A transaction uses its scan region's module when the argument is omitted.

```csharp
private readonly DetourHandle<LoadMapDelegate> loadMapHook = new();

transaction.AddDetour(
    loadMapHook,
    HookTarget.FromExport("DLL_LoadMapToPlay", moduleHandle),
    OnLoadMap);
```

The explicit-handle overload requires an initialized handle. 
Use `new DetourHandle<TDelegate>()`, as above, or use the overload beginning with `HookTarget` and retain its returned handle.

## Chaining

The native engines layer patches and require reverse-order removal by default.
