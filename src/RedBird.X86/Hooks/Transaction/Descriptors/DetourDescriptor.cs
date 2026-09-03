
using System;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
namespace RedBird.X86.Hooks.Transaction.Descriptors;

/// <summary>Full-function detour, built through whichever <see cref="IHookBackend"/> the transaction uses.</summary>
internal sealed class DetourDescriptor<TFunction>(
    string name,
    HookTarget target,
    DetourHandle<TFunction> handle,
    TFunction callback,
    IDetourIntermediaryFactory? intermediaryFactory = null) : HookDescriptor(name, target)
    where TFunction : Delegate
{
    private IDetour<TFunction>? _detour;

    public override void Prepare(UInt64 address, in HookBuildContext context)
    {
        _detour = context.Backend.CreateDetour(new DetourRequest<TFunction>
        {
            Name = Name,
            TargetAddress = address,
            Callback = callback,
            IntermediaryFactory = intermediaryFactory,
            Logger = context.CreateLogger($"Detour:{Name}"),
        });

        Hook = _detour;
    }

    public override void Publish() => handle.Attach(_detour!, ResolvedAddress);

    public override void Reject(Exception failure) => handle.Reject(failure);
}
