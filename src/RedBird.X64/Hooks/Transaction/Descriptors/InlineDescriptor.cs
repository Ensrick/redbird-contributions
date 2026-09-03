using Microsoft.Extensions.Logging;
using System;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;

namespace RedBird.X64.Hooks.Transaction.Descriptors;

/// <summary>Raw inline (code-cave) hook driven by a user-supplied Iced generator.</summary>
internal sealed class InlineDescriptor(
    string name,
    HookTarget target,
    HookHandle<X64InlineHook> handle,
    InlineHookGenerator generator,
    int hookSize) : HookDescriptor(name, target)
{
    private X64InlineHook? _hook;

    public override void Prepare(UInt64 address, in HookBuildContext context)
    {
        ILogger logger = context.CreateLogger(nameof(X64InlineHook));

        _hook = new X64InlineHook(address, hookSize, logger, Name);

        // Generate allocates the stub near the target and assembles into it. Nothing is written to the target's own instruction stream until Enable() runs in the install phase.
        _hook.Generate(generator);

        Hook = _hook;
    }

    public override void Publish() => handle.Attach(_hook!, ResolvedAddress);

    public override void Reject(Exception failure) => handle.Reject(failure);
}
