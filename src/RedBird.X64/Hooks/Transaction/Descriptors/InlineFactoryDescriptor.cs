using System;
using Microsoft.Extensions.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;

namespace RedBird.X64.Hooks.Transaction.Descriptors;

/// <summary>Prepares an inline generator against its actual transaction-owned hook.</summary>
internal sealed class InlineFactoryDescriptor(
    string name,
    HookTarget target,
    HookHandle<X64InlineHook> handle,
    Func<X64InlineHook, InlineHookGenerator> generatorFactory,
    int hookSize) : HookDescriptor(name, target)
{
    private X64InlineHook? _hook;

    public override void Prepare(UInt64 address, in HookBuildContext context)
    {
        ILogger logger = context.CreateLogger(nameof(X64InlineHook));
        _hook = new X64InlineHook(address, hookSize, logger, Name);
        // The pipeline discards through the base Hook property when preparation
        // throws. Expose this owner before any consumer code can pin or generate.
        Hook = _hook;
        InlineHookGenerator generator = generatorFactory(_hook)
            ?? throw new InvalidOperationException("The inline generator factory returned null.");
        _hook.Generate(generator);
    }

    public override void Publish() => handle.Attach(_hook!, ResolvedAddress);

    public override void Reject(Exception failure) => handle.Reject(failure);
}
