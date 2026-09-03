using Microsoft.Extensions.Logging;
using System;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X86.Assembly;
using RedBird.X86.Hooks.Context;

namespace RedBird.X86.Hooks.Transaction.Descriptors;

/// <summary>Inline hook that captures CPU state, calls managed code, and restores registers.</summary>
internal sealed class ContextHookDescriptor(
    string name,
    HookTarget target,
    HookHandle<X86InlineHook> handle,
    ContextHookDelegate callback,
    ContextHookOptions options) : HookDescriptor(name, target)
{
    private X86InlineHook? _hook;

    public override void Prepare(UInt64 address, in HookBuildContext context)
    {
        ILogger logger = context.CreateLogger(nameof(X86InlineHook));

        _hook = new X86InlineHook(address, options.HookSize, logger, Name);

        ContextHookDelegate guarded = ContextStub.Guard(callback, options, address, logger);
        _hook.Generate(ContextStub.CreateGenerator(_hook, guarded, options));

        Hook = _hook;
    }

    public override void Publish() => handle.Attach(_hook!, ResolvedAddress);

    public override void Reject(Exception failure) => handle.Reject(failure);
}
