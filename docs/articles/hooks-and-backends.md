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

### Owner-aware X64 inline generators

Use `AddInlineFactory` when an inline generator needs the actual hook that will own
its stub and pinned delegates. This distinct registration method leaves existing
`AddInline` and `AddContextHook` calls unchanged. It accepts the same explicit
handle, target, minimum `hookSize` (default 14) and diagnostic name as `AddInline`,
plus a `Func<X64InlineHook, InlineHookGenerator>` factory.

For example, compose the existing guarded context generator against that owner:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Context;

var handle = new HookHandle<X64InlineHook>();
var contextOptions = new ContextHookOptions();
transaction.AddInlineFactory(
    handle,
    HookTarget.FromAddress(targetAddress),
    owner => ContextStub.CreateGenerator(
        owner,
        ContextStub.Guard(OnContext, contextOptions, owner.TargetAddress,
            NullLogger.Instance, owner.Name),
        contextOptions),
    hookSize: 14,
    name: "owned-context");
```

The factory runs synchronously on the `Commit` caller's thread, once for each site
whose preparation is reached. It runs after resolution and real hook construction,
which reads and decodes the target bytes, and before generation or target patching.
It does not run at registration or as an invocation by the installed hook. A failed resolution or
earlier rollback can prevent invocation. Do not construct a dummy or competing hook
as an owner: this factory receives the one the transaction will publish.

The factory may pin callbacks with `owner.PinDelegate` or return an owner-bound
generator such as the example. The library alone calls `Generate`, exactly once.
Neither factory nor generator may call `Generate`, `Enable`, `Disable` or `Dispose`
on the owner, patch or invoke the target, or publish the handle. Callback ABI,
register/flag preservation and generated-code validity remain the caller's duties.
Pinned callbacks stay rooted until hook disposal; retaining the supplied owner
does not transfer its ownership or authorize releasing resources early.

A null factory is rejected at registration. A factory that throws or returns null,
or a generator that throws, fails that site's prepare phase. The real owner is
already visible to transaction cleanup, so it is disposed without publishing its
handle; `ContinueOnError` and `RollbackAndThrow` retain their existing meanings.
This new descriptor does not change the preparation paths of other hook kinds.

The newly prepared hook is not attached to the handle inside the factory or
generator; use the supplied owner instead of `handle.Require()`. Attachment occurs
in the publish phase. With `InstallOnCommit = false`, a successful commit publishes
the prepared hook but leaves the target untouched: `handle.Success` is true while
`handle.IsInstalled` is false. `EnableAll` performs activation later without running
the factory or generator again. Keep the hook alive for as long as its generated
code or pinned callbacks can be reached; transaction disposal releases committed
hooks only when `OwnsHooks` is true.

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

### Persistent AOB cache

Set `HookTransactionOptions.AobCache` to reuse AOB matches across process runs:

```csharp
using RedBird.Core.Memory.Scanners;

string cacheDirectory = "MyCacheDir";

AobCacheOptions aobCache = new(Path.Combine(cacheDirectory, "game-module.aobcache.json"));

var options = new HookTransactionOptions
{
    Backend = NativeDetourBackend.Instance,
    AobCache = aobCache,
};

DataScanner scanner = DataScanner.Create(
    region,
    logger: null,
    aobCache: aobCache);
```

RedBird creates the parent directory and writes a compact JSON document containing AOB-to-RVA maps.
A complete cache hit performs no pattern scan. When only some entries are present, the transaction scans
just those misses and atomically rewrites the cache. Stored RVAs avoid carrying absolute addresses between
processes.

Both the x64 and x86 `DataScanner` support the same cache through their three-argument `Create`
overload. `Scan` caches its first match. `ScanAll` caches a complete RVA array only when enumeration
finishes before its limit; a truncated set is never mistaken for a complete result. One scanner instance
loads the document once for all of its fluent calls. Cached overloads
of `MultiPatternScanner.FindFirst` and `FindAll` are available to core scanner callers as well.

Use a separate path for each module or scan region. Cached entries are used without validating the region,
pattern bytes, or RVA, so delete the cache when the target binary or signatures change. Malformed files and
I/O failures fall back to the normal scanner and do not fail the transaction. The default store uses
`System.Text.Json` with a plain `AobCacheDocument`. To use MessagePack or another format, implement
`IAobCacheStore` and assign it to `AobCacheOptions.Store`.

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
