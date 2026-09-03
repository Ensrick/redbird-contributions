using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Memory;

namespace RedBird.Backends.NativeX86;

/// <summary>A managed x86 detour with a relocated-original trampoline.</summary>
public sealed unsafe class X86Detour<TFunction> : IDetour<TFunction> where TFunction : Delegate
{
    private readonly ILogger _logger;
    private readonly X86DetourOptions _options;
    private readonly TFunction _callback;
    private readonly byte[] _originalBytes;
    private readonly byte[] _patchBytes;
    private readonly InstructionPointerMap? _instructionPointers;
    private readonly object _gate = new();

    private TFunction? _original;
    private DetourIntermediaryStubs? _intermediary;
    private UInt64 _registeredAddress;

    internal X86Detour(in DetourRequest<TFunction> request, X86DetourOptions options)
    {
        Name = request.Name;
        _options = options;
        _callback = request.Callback;
        _logger = request.Logger ?? NullLogger.Instance;

        UInt64 requested = request.TargetAddress;
        IntPtr callbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);

        if (!X86PrologueAnalyzer.TryAnalyze(requested, options, _logger, out UInt64 resolved, out List<Instruction> instructions, out Dictionary<UInt64, List<Instruction>> branchSources))
        {
            throw new NotSupportedException($"Could not analyse x86 function 0x{requested:X8} for '{Name}'.");
        }

        TargetAddress = resolved;
        bool prologueLooksHooked = LooksAlreadyHooked(instructions);
        int existingLayers = X86DetourRegistry.GetDepth(resolved);

        if ((prologueLooksHooked || existingLayers > 0) && options.Chaining == X86HookChainingPolicy.Refuse)
        {
            throw new NotSupportedException($"0x{resolved:X8} is already detoured ({existingLayers} known x86 layer(s)) and chaining is refused.");
        }

        if (prologueLooksHooked || existingLayers > 0)
        {
            LogLevel level = options.Chaining == X86HookChainingPolicy.WarnAndAllow
                ? LogLevel.Warning
                : LogLevel.Information;

            _logger.Log(level, "[{Name}] is layering on top of an existing x86 detour at 0x{Address:X8} ({Layers} known layer(s)). Unhook in reverse order.", Name, resolved, existingLayers);
        }

        if (!X86PrologueAnalyzer.TrySelectPrologue(resolved, instructions, branchSources, X86JumpFactory.PatchSize, _logger, out X86PrologueSelection? prologue) || prologue is null)
        {
            State = HookState.Failed;
            throw new NotSupportedException($"The x86 function at 0x{resolved:X8} has fewer than five safely relocatable prologue bytes.");
        }

        IntPtr trampoline = X86TrampolineBuilder.Build(prologue, _logger, out int trampolineSize, out InstructionPointerMap? instructionPointers);
        if (trampoline == IntPtr.Zero)
        {
            State = HookState.Failed;
            throw new NotSupportedException($"Could not build an x86 trampoline for 0x{resolved:X8}.");
        }

        IntPtr hookEntryPoint = callbackPointer;
        IntPtr originalEntryPoint = trampoline;
        if (request.IntermediaryFactory is not null)
        {
            DetourIntermediaryContext context = new(DetourArchitecture.X86, resolved, callbackPointer, trampoline);
            _intermediary = request.IntermediaryFactory.Create(context) ?? throw new InvalidOperationException( $"The intermediary factory returned null for [{Name}] at 0x{resolved:X8}.");

            hookEntryPoint = _intermediary.HookEntryPoint;
            originalEntryPoint = _intermediary.OriginalEntryPoint;
        }

        UInt64 hookAddress = ToX86Address(hookEntryPoint, nameof(DetourIntermediaryStubs.HookEntryPoint));
        ToX86Address(originalEntryPoint, nameof(DetourIntermediaryStubs.OriginalEntryPoint));

        byte[] jump = X86JumpFactory.BuildDirect(resolved, hookAddress);

        TrampolineAddress = trampoline;
        TrampolineSize = trampolineSize;
        HookEntryPointAddress = hookEntryPoint;
        OriginalEntryPointAddress = originalEntryPoint;
        Scheme = X86DetourScheme.Direct;
        DisplacedByteCount = prologue.Length;
        _instructionPointers = instructionPointers;

        _originalBytes = CodePatch.Read(resolved, prologue.Length);
        _patchBytes = new byte[prologue.Length];
        Array.Copy(jump, _patchBytes, jump.Length);
        for (int i = jump.Length; i < _patchBytes.Length; i++)
            _patchBytes[i] = 0x90;

        _original = Marshal.GetDelegateForFunctionPointer<TFunction>(originalEntryPoint);
        _registeredAddress = resolved;
        ChainDepth = X86DetourRegistry.AddLayer(resolved);
        State = HookState.Prepared;

        _logger.LogInformation("[{Name}] prepared at 0x{Address:X8}: {Displaced} bytes displaced, trampoline 0x{Trampoline:X8}.", Name, resolved, prologue.Length, ToX86Address(trampoline, nameof(trampoline)));
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

    /// <summary>Generated trampoline byte count.</summary>
    public int TrampolineSize { get; }

    /// <summary>Effective destination of the target's entry jump.</summary>
    public IntPtr HookEntryPointAddress { get; }

    /// <summary>Effective address marshalled as <see cref="Original"/>.</summary>
    public IntPtr OriginalEntryPointAddress { get; }

    /// <summary>The x86 entry-patch scheme.</summary>
    public X86DetourScheme Scheme { get; }

    /// <summary>Number of whole prologue bytes displaced.</summary>
    public int DisplacedByteCount { get; }

    /// <summary>Known managed-engine layer depth at this address.</summary>
    public int ChainDepth { get; private set; }

    /// <summary>Peer threads suspended by the most recent protected patch operation.</summary>
    internal int LastPatchSuspendedThreadCount { get; private set; }

    /// <summary>Threads redirected from the replaced prologue during the most recent install.</summary>
    internal int LastPatchRelocatedThreadCount { get; private set; }

    /// <inheritdoc/>
    public TFunction Original => _original ?? throw new InvalidOperationException(
        $"Detour [{Name}] at 0x{TargetAddress:X8} has no trampoline.");

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
                _logger.LogError(
                    "[{Name}] cannot uninstall from 0x{Address:X8}: a newer hook layer owns the entry point. Unhook in reverse order.",
                    Name,
                    TargetAddress);
                return;
            }

            ThreadPatchResult patchResult = ApplyPatch(installing: false, _originalBytes);
            LastPatchSuspendedThreadCount = patchResult.SuspendedThreadCount;
            LastPatchRelocatedThreadCount = 0;
            State = HookState.Disabled;
        }
    }

    /// <inheritdoc/>
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
            catch (Exception exception)
            {
                _logger.LogError(exception, "'{Name}' failed to uninstall during dispose.", Name);
            }

            // SafeUnhook deliberately leaves the detour live. Retain its delegate and generated intermediary until callers remove the layer above and dispose this hook again.
            if (State == HookState.Installed &&
                _registeredAddress != 0 &&
                X86DetourRegistry.GetDepth(_registeredAddress) > ChainDepth)
                return;

            _original = null;
            _intermediary?.Dispose();
            _intermediary = null;
            State = HookState.Disposed;

            if (_registeredAddress != 0)
            {
                X86DetourRegistry.RemoveLayer(_registeredAddress);
                _registeredAddress = 0;
            }
        }

        GC.SuppressFinalize(this);
    }

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

    private static bool LooksAlreadyHooked(List<Instruction> instructions)
        => instructions.Count > 0 &&
           instructions[0].FlowControl is FlowControl.UnconditionalBranch or FlowControl.IndirectBranch;

    private static UInt64 ToX86Address(IntPtr address, string parameterName)
    {
        if (address == IntPtr.Zero)
            throw new ArgumentOutOfRangeException(parameterName, "An x86 entry point cannot be zero.");

        return unchecked((UInt32)address.ToInt32());
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Name} @ 0x{TargetAddress:X8} [{Scheme}, {DisplacedByteCount} bytes, {State}]";
}
