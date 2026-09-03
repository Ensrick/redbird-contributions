---
title: RedBird documentation
description: Managed X64/X86 hooking, calling-convention adapters, and executable native code for .NET.
---

# RedBird

RedBird.NET is a managed native-hooking toolkit for .NET 10 and .NET Framework 4.8. It provides
architecture-specific hook libraries, swappable detour backends, transaction-based installation,
runtime-assembled executable functions, and ABI intermediary stubs.

Start with [Getting started](articles/getting-started.md), then choose the guide matching your use
case:

- [Hooks and backends](articles/hooks-and-backends.md)
- [Calling-convention adapters](articles/calling-conventions.md)
- [Executable functions](articles/executable-functions.md)
- [Migration from CallingConventionDispatcher](articles/migration-from-dispatcher.md)
- [CI and releases](articles/ci-and-releases.md)
- [API reference](api/index.md)

> [!WARNING]
> A native hook runs inside its host process. An incorrect address, delegate signature, convention,
> or lifetime can terminate or corrupt that process. Only hook software you are authorized to
> inspect or modify.
