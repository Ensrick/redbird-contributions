using Microsoft.Extensions.Logging;
using System;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;

namespace RedBird.X64.Hooks.Transaction.Descriptors;

/// <summary>
/// In-place assembly patch driven by a user-supplied Iced generator. 
/// Overwrites the target's instructions rather than detouring around them.
/// </summary>
internal sealed class AssemblyPatchDescriptor(
    string name,
    HookTarget target,
    HookHandle<X64AssemblyPatch> handle,
    AssemblyPatchGenerator generator,
    int maxByteCount) : HookDescriptor(name, target)
{
    private X64AssemblyPatch? _patch;

    public override void Prepare(UInt64 address, in HookBuildContext context)
    {
        ILogger logger = context.CreateLogger(nameof(X64AssemblyPatch));

        _patch = new X64AssemblyPatch(address, maxByteCount, logger, Name);

        // Assembles the body and snapshots the original bytes. The max size constraint is enforced here, so an over-long patch fails during prepare and never touches the target.
        _patch.Generate(generator);
        Hook = _patch;
    }

    public override void Publish() => handle.Attach(_patch!, ResolvedAddress);

    public override void Reject(Exception failure) => handle.Reject(failure);
}
