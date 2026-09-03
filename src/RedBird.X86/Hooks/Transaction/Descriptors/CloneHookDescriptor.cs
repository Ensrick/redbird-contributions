using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X86.Assembly;

namespace RedBird.X86.Hooks.Transaction.Descriptors;

/// <summary>Disassembles the target function, clones it with patches applied, redirects the entry point.</summary>
internal sealed class CloneHookDescriptor(
    string name,
    HookTarget target,
    HookHandle<X86FunctionCloneHook> handle,
    IEnumerable<FunctionClonePatch> patches,
    FunctionScanOptions? scanOptions = null) : HookDescriptor(name, target)
{
    private X86FunctionCloneHook? _hook;

    public override void Prepare(UInt64 address, in HookBuildContext context)
    {
        ILogger logger = context.CreateLogger(nameof(X86FunctionCloneHook));

        _hook = new X86FunctionCloneHook(address, patches, logger, scanOptions, Name);
        _hook.Compile();
        Hook = _hook;
    }

    public override void Publish() => handle.Attach(_hook!, ResolvedAddress);

    public override void Reject(Exception failure) => handle.Reject(failure);
}
