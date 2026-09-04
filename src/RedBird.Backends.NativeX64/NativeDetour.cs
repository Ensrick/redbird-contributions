using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Memory;

namespace RedBird.Backends.NativeX64;

/// <summary>
/// A managed x64 function detour: displaced prologue relocated into a nearby trampoline, entry point redirected at the callback.
/// </summary>
/// <typeparam name="TFunction">Delegate matching the target's signature and calling convention.</typeparam>
/// <remarks>
/// </remarks>
public sealed unsafe class NativeDetour<TFunction> : IDetour<TFunction> where TFunction : Delegate
{
    private readonly ILogger _logger;
    private readonly NativeDetourOptions _options;

    // Rooted for the lifetime of the hook: the patch holds a raw pointer to this and the GC has no idea native code is about to call through it.
    private readonly TFunction _callback;

    private readonly byte[] _originalBytes;
    private readonly byte[] _patchBytes;
    private readonly InstructionPointerMap? _instructionPointers;
    private readonly object _gate = new();

    private TFunction? _original;
    private DetourIntermediaryStubs? _intermediary;
    private UInt64 _registeredAddress;

    internal NativeDetour(in DetourRequest<TFunction> request, NativeDetourOptions options)
    {
        Name = request.Name;
        _options = options;
        _callback = request.Callback;
        _logger = request.Logger ?? NullLogger.Instance;

        UInt64 requested = request.TargetAddress;
        IntPtr callbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);

        if (!PrologueAnalyzer.TryAnalyze(requested, options, _logger, out UInt64 resolved, out List<Instruction> instructions, out Dictionary<UInt64, List<Instruction>> branchSources))
            throw new NotSupportedException($"Could not analyse the function at 0x{requested:X16} for [{Name}].");

        TargetAddress = resolved;

        if (resolved != requested)
            _logger.LogInformation("[{Name}]: followed a thunk from 0x{Requested:X16} to 0x{Resolved:X16}.", Name, requested, resolved);

        // A leading jump that PrologueAnalyzer declined to follow points outside every loaded module, so it is a detour rather than a thunk, either one of ours or another engine's.
        bool prologueLooksHooked = LooksAlreadyHooked(instructions);
        int existingLayers = DetourRegistry.GetDepth(resolved);

        if ((prologueLooksHooked || existingLayers > 0) && options.Chaining == HookChainingPolicy.Refuse)
        {
            throw new NotSupportedException($"0x{resolved:X16} is already detoured ({existingLayers} known layer(s) from this engine) and Chaining is set to Refuse.");
        }

        if (prologueLooksHooked || existingLayers > 0)
        {
            // Layering is the normal case, not an error: the existing jump is displaced into this detour's trampoline, so Original reaches the previous callback and the chain composes.
            LogLevel level = options.Chaining == HookChainingPolicy.WarnAndAllow ? LogLevel.Warning : LogLevel.Information;
            _logger.Log(level, "[{Name}] is layering on top of an existing detour at 0x{Address:X16} ({Layers} known layer(s)). Unhook in reverse order.", Name, resolved, existingLayers);
        }

        // Try each scheme in preference order. A scheme fails either because its patch does not fit the prologue or because it needs memory nobody can provide.
        List<string> failures = [];
        foreach (DetourScheme scheme in options.ResolveOrder())
        {
            int patchSize = JumpFactory.GetPatchSize(scheme);

            if (!PrologueAnalyzer.TrySelectPrologue(resolved, instructions, branchSources, patchSize, _logger, out PrologueSelection? prologue) || prologue is null)
            {
                failures.Add($"{scheme}: the function does not expose {patchSize} safe bytes at an instruction boundary");
                _logger.LogDebug("[{Name}]: scheme {Scheme} needs {Size} bytes, which the prologue cannot give.", Name, scheme, patchSize);
                continue;
            }

            IntPtr trampoline = TrampolineBuilder.Build(prologue, _logger, out int trampolineSize, out InstructionPointerMap? instructionPointers, out string? trampolineFailure);
            if (trampoline == IntPtr.Zero)
            {
                failures.Add($"{scheme}: {trampolineFailure ?? "trampoline construction failed"}");
                continue;
            }

            DetourIntermediaryStubs? intermediary = null;
            IntPtr hookEntryPoint = callbackPointer;
            IntPtr originalEntryPoint = trampoline;

            if (request.IntermediaryFactory is not null)
            {
                DetourIntermediaryContext context = new(DetourArchitecture.X64, resolved, callbackPointer, trampoline);
                intermediary = request.IntermediaryFactory.Create(context) ?? throw new InvalidOperationException($"The intermediary factory returned null for [{Name}] at 0x{resolved:X16}.");

                hookEntryPoint = intermediary.HookEntryPoint;
                originalEntryPoint = intermediary.OriginalEntryPoint;
            }

            DetourPatch? patch = JumpFactory.TryBuild(scheme, resolved, (UInt64)hookEntryPoint, out string? patchFailure);
            if (patch is null)
            {
                intermediary?.Dispose();
                failures.Add($"{scheme}: {patchFailure ?? "entry-jump construction failed"}");
                _logger.LogDebug("[{Name}]: scheme {Scheme} could not be built here.", Name, scheme);
                continue;
            }

            TrampolineAddress = trampoline;
            TrampolineSize = trampolineSize;
            HookEntryPointAddress = hookEntryPoint;
            OriginalEntryPointAddress = originalEntryPoint;
            Scheme = scheme;
            DisplacedByteCount = prologue.Length;
            PointerSlot = patch.PointerSlot;
            _intermediary = intermediary;
            _instructionPointers = instructionPointers;

            // Snapshot before anything is written, so Disable restores the exact original bytes rather than a re-encoding of what the disassembler thought was there.
            _originalBytes = CodePatch.Read(resolved, prologue.Length);

            // Pad the tail of the displaced region with NOPs: the last displaced instruction almost never ends exactly where the patch does.
            _patchBytes = new byte[prologue.Length];
            Array.Copy(patch.Bytes, _patchBytes, patch.Bytes.Length);
            for (int i = patch.Bytes.Length; i < _patchBytes.Length; i++)
                _patchBytes[i] = 0x90;

            _original = Marshal.GetDelegateForFunctionPointer<TFunction>(originalEntryPoint);
            _registeredAddress = resolved;
            ChainDepth = DetourRegistry.AddLayer(resolved);
            State = HookState.Prepared;

            _logger.LogInformation("[{Name}] prepared at 0x{Address:X16} using {Scheme}: {Displaced} bytes displaced, trampoline 0x{Trampoline:X16}.", Name, resolved, scheme, prologue.Length, (UInt64)trampoline);

            return;
        }

        State = HookState.Failed;
        string attempts = failures.Count == 0 ? "No schemes were enabled." : $"Attempts: {string.Join("; ", failures)}.";
        throw new NotSupportedException($"No enabled detour scheme could be prepared at 0x{resolved:X16} for [{Name}]. " + attempts);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public UInt64 TargetAddress { get; }

    /// <inheritdoc/>
    public HookState State { get; private set; } = HookState.Created;

    /// <inheritdoc/>
    public bool IsInstalled => State == HookState.Installed;

    /// <inheritdoc/>
    public IntPtr TrampolineAddress { get; }

    /// <summary>Byte length of the generated trampoline.</summary>
    public int TrampolineSize { get; }

    /// <summary>
    /// Effective destination written into the hook patch. This is the managed callback pointer
    /// when no intermediary factory was supplied.
    /// </summary>
    public IntPtr HookEntryPointAddress { get; }

    /// <summary>
    /// Effective address marshalled as <see cref="Original"/>. This is
    /// <see cref="TrampolineAddress"/> when no intermediary factory was supplied.
    /// </summary>
    public IntPtr OriginalEntryPointAddress { get; }

    /// <summary>The scheme that was chosen for this site.</summary>
    public DetourScheme Scheme { get; }

    /// <summary>How many bytes of the prologue were displaced.</summary>
    public int DisplacedByteCount { get; }

    /// <summary>Pointer slot allocated for <see cref="DetourScheme.Indirect"/>, or zero.</summary>
    public IntPtr PointerSlot { get; }

    /// <summary>
    /// Position in the chain at this address, 1 being the first detour this engine installed there.
    /// Counts only this engine's layers.
    /// </summary>
    public int ChainDepth { get; private set; }

    /// <summary>Peer threads suspended by the most recent protected patch operation.</summary>
    internal int LastPatchSuspendedThreadCount { get; private set; }

    /// <summary>Threads redirected from the replaced prologue during the most recent install.</summary>
    internal int LastPatchRelocatedThreadCount { get; private set; }

    /// <inheritdoc/>
    public TFunction Original => _original ?? throw new InvalidOperationException(
        $"Detour [{Name}] at 0x{TargetAddress:X16} has no trampoline.");

    /// <inheritdoc/>
    public void Enable()
    {
        lock (_gate)
        {
            if (State is HookState.Installed or HookState.Disposed or HookState.Failed)
                return;

            ThreadPatchResult patchResult = ApplyPatch(installing: true, _patchBytes);
            LastPatchSuspendedThreadCount = patchResult.SuspendedThreadCount;
            LastPatchRelocatedThreadCount = patchResult.RelocatedThreadCount;
            State = HookState.Installed;

            if (patchResult.RelocatedThreadCount > 0)
            {
                _logger.LogDebug(
                    "[{Name}] redirected {Count} suspended thread(s) from the displaced prologue into its trampoline.",
                    Name,
                    patchResult.RelocatedThreadCount);
            }

            _logger.LogDebug("[{Name}] installed at 0x{Address:X16}.", Name, TargetAddress);
        }
    }

    /// <inheritdoc/>
    public void Disable()
    {
        lock (_gate)
        {
            if (State != HookState.Installed)
                return;

            if (_options.SafeUnhook && !TargetStillHoldsOurPatch())
            {
                // Something went on top after we did. Writing our saved bytes here would overwrite that layer's jump and silently unhook it while it still believes it is installed.
                _logger.LogError("[{Name}] cannot uninstall from 0x{Address:X16}: the entry point no longer holds this detour's patch, so another layer went on top. Unhook in reverse order, or set SafeUnhook to false to restore regardless.", Name, TargetAddress);

                return;
            }

            ThreadPatchResult patchResult = ApplyPatch(installing: false, _originalBytes);
            LastPatchSuspendedThreadCount = patchResult.SuspendedThreadCount;
            LastPatchRelocatedThreadCount = 0;
            State = HookState.Disabled;

            _logger.LogDebug("[{Name}] removed from 0x{Address:X16}.", Name, TargetAddress);
        }
    }

    /// <summary>
    /// Restores the target. 
    /// The trampoline and any pointer slot are intentionally not freed: both are sub-allocations of a shared slab, and releasing one unmaps memory other live hooks execute from.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (State == HookState.Disposed)
                return;

            try
            {
                Disable();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Name}] failed to uninstall during dispose.", Name);
            }

            // SafeUnhook deliberately leaves the machine-code patch live when another layer is above us.
            // Keep the delegate and intermediary rooted: callers can remove the newer layer and dispose this detour again afterwards.
            if (State == HookState.Installed &&
                _registeredAddress != 0 &&
                DetourRegistry.GetDepth(_registeredAddress) > ChainDepth)
                return;

            _original = null;
            _intermediary?.Dispose();
            _intermediary = null;
            State = HookState.Disposed;

            if (_registeredAddress != 0)
            {
                DetourRegistry.RemoveLayer(_registeredAddress);
                _registeredAddress = 0;
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// True when the entry point still contains exactly the bytes this detour wrote, i.e. nothing has layered on top of it.
    /// </summary>
    private bool TargetStillHoldsOurPatch()
    {
        byte* current = (byte*)TargetAddress;

        for (int i = 0; i < _patchBytes.Length; i++)
        {
            if (current[i] != _patchBytes[i])
                return false;
        }

        return true;
    }

    private ThreadPatchResult ApplyPatch(bool installing, byte[] bytes)
    {
        if (!_options.SuspendThreadsDuringPatch)
        {
            CodePatch.Write(TargetAddress, bytes);
            return default;
        }

        return ThreadPatchScope.Apply(_instructionPointers ?? throw new InvalidOperationException("The detour has no instruction-pointer relocation map."), installing, TargetAddress, bytes);
    }

    /// <summary>
    /// A function whose very first instruction jumps away is either a thunk, which <see cref="PrologueAnalyzer"/> would already have followed, or somebody else's hook.
    /// </summary>
    private static bool LooksAlreadyHooked(List<Instruction> instructions)
    {
        if (instructions.Count == 0)
            return false;

        Instruction first = instructions[0];

        if (first.FlowControl is not (FlowControl.UnconditionalBranch or FlowControl.IndirectBranch))
            return false;

        // A register-indirect jump at offset 0 is the classic push/ret or jmp [rip] patch. A near branch would have been followed already, so reaching here means following is off.
        return true;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Name} @ 0x{TargetAddress:X16} [{Scheme}, {DisplacedByteCount} bytes, {State}]";
}
