---
title: Executable functions
description: Assemble and call standalone native X64 and X86 functions at runtime.
---

# Executable functions

You can allocate a native region, assemble an Iced instruction body at its real address, expose a delegate, retain the original bytes, and release the region on disposal.

## X64

```csharp
using ExecutableFunction function = ExecutableFunction.Create((assembler, layout) =>
{
    assembler.AddInstruction(Instruction.Create(
        Code.Mov_r32_rm32,
        Register.EAX,
        new MemoryOperand(Register.RIP, (long)layout.DataAddress)));
    assembler.ret();
}, layout => layout.SetData(42));

int value = function.GetDelegate<NullaryInt>()();
```

The layout callback runs while the page is writable and can initialize the nearby data slot. 
This also works on hosts enforcing W^X memory policy.

## X86

`ExecutableX86Function` has the same lifecycle and API but must be created in a 32-bit process. 
It refuses an allocation above 4 GiB instead of truncating the address.