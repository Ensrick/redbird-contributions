---
title: Managed-updatable native state
description: Share flags and unmanaged values between managed callbacks and generated assembly.
---

# Managed-updatable native state

`NativeStateBlock` stores values in stable read/write native memory. 
Managed callbacks can update them without rebuilding a hook, while generated assembly reads them without a managed/native transition.

```csharp
using RedBird.Core.Memory;

using var state = new NativeStateBlock();
NativeFlag enabled = state.AllocateFlag();
NativeFlagArray enabledForPlayer = state.AllocateFlags(8);

enabled.Value = true;
enabledForPlayer[2] = true;
```

Flags occupy one byte and are always stored as `0` or `1`. For other unmanaged data, use
`AllocateValue<T>` or `AllocateArray<T>`.

## Reading a flag from Iced

The X64 and X86 assembler extensions can compare a flag with zero:

```csharp
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

Label disabled = asm.CreateLabel();

asm.TestNativeFlag(enabled, r11);
asm.jz(disabled);
```

An array element known while generating the stub uses the same helper:

```csharp
asm.TestNativeFlag(enabledForPlayer, playerIndex, r11);
asm.jz(disabled);
```

`TestNativeFlag` preserves its scratch register by default, but leaves the comparison flags modified for the following `jz` or `jnz`. 

Branch immediately after the helper. 
If earlier flags must survive the complete decision, restore them separately on both outgoing paths after the flag-dependent branch.

The state block must outlive every native stub containing one of its addresses. 
Remove those hooks before disposing the block.
