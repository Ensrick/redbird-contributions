using Microsoft.Extensions.Logging;
using System;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;

namespace RedBird.X86.Hooks.Transaction.Descriptors;

/// <summary>
/// In-place assembly patch driven by a user-supplied Iced generator. 
/// Overwrites the target's instructions rather than detouring around them.
/// </summary>
internal sealed class AssemblyPatchDescriptor(
    string name,
    HookTarget target,
    HookHandle<X86AssemblyPatch> handle,
    AssemblyPatchGenerator generator,
    int maxByteCount) : HookDescriptor(name, target)
{
    private X86AssemblyPatch? _patch;

    public override void Prepare(UInt64 address, in HookBuildContext context)
    {
        ILogger logger = context.CreateLogger(nameof(X86AssemblyPatch));

        _patch = new X86AssemblyPatch(address, maxByteCount, logger, Name);
        _patch.Generate(generator);
        Hook = _patch;
    }

    public override void Publish() => handle.Attach(_patch!, ResolvedAddress);

    public override void Reject(Exception failure) => handle.Reject(failure);
}
