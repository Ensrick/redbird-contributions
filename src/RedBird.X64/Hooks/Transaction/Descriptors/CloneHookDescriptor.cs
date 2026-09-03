using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Assembly;

namespace RedBird.X64.Hooks.Transaction.Descriptors;

/// <summary>Disassembles the target function, clones it with patches applied, redirects the entry point.</summary>
internal sealed class CloneHookDescriptor(string name,
    HookTarget target,
    HookHandle<X64FunctionCloneHook> handle,
    IEnumerable<FunctionClonePatch> patches,
    FunctionScanOptions? scanOptions = null) : HookDescriptor(name, target)
{
    private X64FunctionCloneHook? _hook;

    public override void Prepare(UInt64 address, in HookBuildContext context)
    {
        ILogger logger = context.CreateLogger(nameof(X64FunctionCloneHook));

        _hook = new X64FunctionCloneHook(address, patches, logger, scanOptions, Name);

        // Compile does the disassembly, island generation and relocation, by far the most expensive and most failure-prone step of any hook kind here, and exactly the reason preparation is separated from installation.
        _hook.Compile();
        Hook = _hook;
    }

    public override void Publish() => handle.Attach(_hook!, ResolvedAddress);

    public override void Reject(Exception failure) => handle.Reject(failure);
}
