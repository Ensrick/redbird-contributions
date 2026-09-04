using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.Core.Memory.Scanners;
using RedBird.X64.Assembly;
using RedBird.X64.Hooks.Context;
using RedBird.X64.Hooks.Transaction.Descriptors;

namespace RedBird.X64.Hooks.Transaction;

/// <summary>
/// A batch of hooks that resolve, build and install together.
/// <para>
/// <b>Usage.</b>
/// <code>
/// private static readonly DetourHandle&lt;BuildWallFn&gt; BuildWall = new();
///
/// using ScanRegion region = ScanRegion.FromModule(moduleHandle);
/// using HookTransaction tx = new(region, loggerFactory);
///
/// tx.AddDetour(BuildWall, "48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56", OnBuildWall);
/// tx.AddDetour(ImportAiv, HookTarget.FromExport("DLL_ImportAIV"), OnImportAiv);
/// tx.AddContextHook(SiegeRally, HookTarget.FromPattern(pattern, offset: 5), OnSiegeRally,
///     new ContextHookOptions { Registers = X64SmartCPUContextRegs.Volatile });
///
/// CommitResult result = tx.Commit();
///
/// // ... and inside the callback:
/// BuildWall.Original(tileManager, playerId, x, y, mappers);
/// </code>
/// </para>
/// <para>
/// <b>Concurrency.</b> Registration is not thread-safe; build the batch on one thread. 
/// The scan phase is internally parallel and read-only, and installation is serial so overlapping instruction ranges cannot be patched simultaneously.
/// </para>
/// </summary>
public sealed class HookTransaction : IDisposable
{
    private readonly ScanRegion _region;
    private readonly bool _ownsRegion;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;
    private readonly List<HookDescriptor> _descriptors = [];
    private readonly List<IHook> _hooks = [];
    private bool _disposed;

    /// <summary>Creates a transaction over <paramref name="region"/>.</summary>
    /// <param name="region">Memory to scan, plus the base address its offsets are relative to.</param>
    /// <param name="loggerFactory">Optional; hooks get a category logger derived from it.</param>
    /// <param name="options">Failure policy, detour backend, install timing.</param>
    /// <param name="name">Diagnostic label, defaults to the caller's member name.</param>
    public HookTransaction(
        ScanRegion region,
        ILoggerFactory? loggerFactory = null,
        HookTransactionOptions? options = null,
        [CallerMemberName] string name = "")
    {
        _region = region ?? throw new ArgumentNullException(nameof(region));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger($"{nameof(HookTransaction)}[{name}]") ?? NullLogger.Instance;
        Options = options ?? HookTransactionOptions.Default;
        Name = name;
    }

    /// <summary>
    /// Compatibility constructor. Copies <paramref name="memory"/> onto the heap, because a
    /// <see cref="ReadOnlySpan{T}"/> cannot be stored in a field.
    /// </summary>
    [Obsolete("Copies the whole region. Use ScanRegion.FromModule/FromPointer to wrap live memory instead, and share one region across all your transactions.")]
    public HookTransaction(
        ReadOnlySpan<byte> memory,
        UInt64 imageBase,
        ILoggerFactory? loggerFactory = null,
        TransactionFailureMode failureMode = TransactionFailureMode.ContinueOnError)
        : this(ScanRegion.Copy(memory, imageBase), loggerFactory, new HookTransactionOptions { FailureMode = failureMode })
    {
        _ownsRegion = true;
    }

    /// <summary>Diagnostic label.</summary>
    public string Name { get; }

    /// <summary>Policy in effect for this transaction.</summary>
    public HookTransactionOptions Options { get; }

    /// <summary>Lifecycle position.</summary>
    public TransactionState State { get; private set; } = TransactionState.Building;

    /// <summary>Result of the last commit, or <see langword="null"/> if it has not run.</summary>
    public CommitResult? Result { get; private set; }

    /// <summary>Hooks produced by this transaction, in installation order.</summary>
    public IReadOnlyList<IHook> Hooks => new ReadOnlyCollection<IHook>(_hooks);

    /// <summary>Descriptors registered so far, for a session-level commit.</summary>
    internal IReadOnlyList<HookDescriptor> Descriptors => _descriptors;

    /// <summary>Sink the pipeline appends installed hooks to.</summary>
    internal List<IHook> HookSink => _hooks;

    /// <summary>Closes registration when a session takes over the commit.</summary>
    internal void BeginSessionCommit()
    {
        ThrowIfNotBuilding();
        State = TransactionState.Committing;
    }

    /// <summary>Records the outcome of a session-level commit.</summary>
    internal void CompleteSessionCommit(CommitResult result, bool rolledBack = false)
    {
        Result = result;
        State = rolledBack ? TransactionState.RolledBack : TransactionState.Committed;
    }

    // =========================================================================
    // Registration
    // =========================================================================

    /// <summary>Registers a full-function detour.</summary>
    /// <typeparam name="TFunction">Delegate matching the target's signature.</typeparam>
    /// <param name="handle">Handle populated once the detour is live.</param>
    /// <param name="target">Pattern, address, RVA or export.</param>
    /// <param name="callback">Managed replacement.</param>
    /// <param name="name">Defaults to the handle's expression text.</param>
    public DetourHandle<TFunction> AddDetour<TFunction>(
        DetourHandle<TFunction> handle,
        HookTarget target,
        TFunction callback,
        [CallerArgumentExpression(nameof(handle))] string name = "")
        where TFunction : Delegate
    {
        DetourHandleNullThrow(handle, name);
        ArgumentNullThrow(callback, nameof(callback));

        Register(new DetourDescriptor<TFunction>(name, target, handle, callback), handle, name);
        return handle;
    }

    /// <summary>Registers a detour and returns a fresh handle, for callers who do not keep a field.</summary>
    public DetourHandle<TFunction> AddDetour<TFunction>(
        HookTarget target,
        TFunction callback,
        [CallerArgumentExpression(nameof(callback))] string name = "")
        where TFunction : Delegate
        => AddDetour(new DetourHandle<TFunction>(), target, callback, name);

    /// <summary>
    /// Registers a full-function detour whose inbound callback and outbound original-call paths are wrapped by architecture-specific intermediary stubs.
    /// </summary>
    public DetourHandle<TFunction> AddDetour<TFunction>(
        DetourHandle<TFunction> handle,
        HookTarget target,
        TFunction callback,
        IDetourIntermediaryFactory intermediaryFactory,
        [CallerArgumentExpression(nameof(handle))] string name = "")
        where TFunction : Delegate
    {
        DetourHandleNullThrow(handle, name);
        ArgumentNullThrow(callback, nameof(callback));
        ArgumentNullThrow(intermediaryFactory, nameof(intermediaryFactory));

        Register(
            new DetourDescriptor<TFunction>(name, target, handle, callback, intermediaryFactory),
            handle,
            name);
        return handle;
    }

    /// <summary>
    /// Registers a detour with intermediary stubs and returns a fresh handle.
    /// </summary>
    public DetourHandle<TFunction> AddDetour<TFunction>(
        HookTarget target,
        TFunction callback,
        IDetourIntermediaryFactory intermediaryFactory,
        [CallerArgumentExpression(nameof(callback))] string name = "")
        where TFunction : Delegate
        => AddDetour(
            new DetourHandle<TFunction>(),
            target,
            callback,
            intermediaryFactory,
            name);

    /// <summary>Registers a raw inline hook whose stub body you generate yourself.</summary>
    /// <param name="handle">Handle populated once the hook is live.</param>
    /// <param name="target">Pattern, address, RVA or export.</param>
    /// <param name="generator">Emits the stub body via Iced.</param>
    /// <param name="hookSize">Minimum bytes to displace; clamped to at least 14.</param>
    /// <param name="name">Defaults to the handle's expression text.</param>
    public HookHandle<X64InlineHook> AddInline(
        HookHandle<X64InlineHook> handle,
        HookTarget target,
        InlineHookGenerator generator,
        int hookSize = 14,
        [CallerArgumentExpression(nameof(handle))] string name = "")
    {
        ArgumentNullThrow(handle, nameof(handle));
        ArgumentNullThrow(generator, nameof(generator));

        Register(new InlineDescriptor(name, target, handle, generator, hookSize), handle, name);
        return handle;
    }

    /// <summary>
    /// Registers an in-place assembly patch: the generated body is written directly over the target's instructions, overwriting them, with no stub or return path.
    /// </summary>
    /// <param name="handle">Handle populated once the patch is live.</param>
    /// <param name="target">Pattern, address, RVA or export.</param>
    /// <param name="generator">Emits the replacement code via Iced, encoded for the target address.</param>
    /// <param name="maxByteCount">
    /// Maximum bytes the patch may overwrite, or <c>0</c> for no limit. When set and the body
    /// (rounded up to the next instruction boundary) does not fit, the transaction fails this hook
    /// during the prepare phase, before any byte of the target is written.
    /// </param>
    /// <param name="name">Defaults to the handle's expression text.</param>
    public HookHandle<X64AssemblyPatch> AddAssemblyPatch(
        HookHandle<X64AssemblyPatch> handle,
        HookTarget target,
        AssemblyPatchGenerator generator,
        int maxByteCount = 0,
        [CallerArgumentExpression(nameof(handle))] string name = "")
    {
        ArgumentNullThrow(handle, nameof(handle));
        ArgumentNullThrow(generator, nameof(generator));

        Register(new AssemblyPatchDescriptor(name, target, handle, generator, maxByteCount), handle, name);
        return handle;
    }

    /// <summary>
    /// Registers an in-place assembly patch and returns a fresh handle, for callers who do not keep a field.
    /// </summary>
    public HookHandle<X64AssemblyPatch> AddAssemblyPatch(
        HookTarget target,
        AssemblyPatchGenerator generator,
        int maxByteCount = 0,
        [CallerArgumentExpression(nameof(generator))] string name = "")
        => AddAssemblyPatch(new HookHandle<X64AssemblyPatch>(), target, generator, maxByteCount, name);

    /// <summary>
    /// Registers an in-place patch that writes <paramref name="bytes"/> verbatim over the target, for pre-assembled or hand-written machine code (for example a run of <c>NOP</c>s that neuters a check).
    /// </summary>
    /// <param name="handle">Handle populated once the patch is live.</param>
    /// <param name="target">Pattern, address, RVA or export.</param>
    /// <param name="bytes">Raw machine code to write. Copied at registration.</param>
    /// <param name="maxByteCount">Maximum bytes the patch may overwrite, or <c>0</c> for no limit.</param>
    /// <param name="name">Defaults to the handle's expression text.</param>
    public HookHandle<X64AssemblyPatch> AddBytePatch(
        HookHandle<X64AssemblyPatch> handle,
        HookTarget target,
        ReadOnlySpan<byte> bytes,
        int maxByteCount = 0,
        [CallerArgumentExpression(nameof(handle))] string name = "")
    {
        ArgumentNullThrow(handle, nameof(handle));
        if (bytes.IsEmpty)
            throw new ArgumentException("The byte patch is empty; there is nothing to write.", nameof(bytes));

        byte[] copy = bytes.ToArray();
        return AddAssemblyPatch(handle, target, (asm, _) => asm.db(copy), maxByteCount, name);
    }

    /// <summary>Registers a context hook that hands your callback the captured CPU state.</summary>
    /// <param name="handle">Handle populated once the hook is live.</param>
    /// <param name="target">Pattern, address, RVA or export.</param>
    /// <param name="callback">Receives a pointer to the captured context.</param>
    /// <param name="options">Register set, displacement placement, error handling.</param>
    /// <param name="name">Defaults to the handle's expression text.</param>
    public HookHandle<X64InlineHook> AddContextHook(
        HookHandle<X64InlineHook> handle,
        HookTarget target,
        ContextHookDelegate callback,
        ContextHookOptions? options = null,
        [CallerArgumentExpression(nameof(handle))] string name = "")
    {
        ArgumentNullThrow(handle, nameof(handle));
        ArgumentNullThrow(callback, nameof(callback));

        Register(new ContextHookDescriptor(name, target, handle, callback, options ?? ContextHookOptions.Default), handle, name);
        return handle;
    }

    /// <summary>
    /// Registers an x64 context hook and returns a fresh handle, for callers who do not keep a field.
    /// The transaction retains the installed hook for its own lifetime, so callers may ignore the returned handle when they do not need to inspect or control that hook individually.
    /// </summary>
    public HookHandle<X64InlineHook> AddContextHook(
        HookTarget target,
        ContextHookDelegate callback,
        ContextHookOptions? options = null,
        [CallerArgumentExpression(nameof(callback))] string name = "")
        => AddContextHook(new HookHandle<X64InlineHook>(), target, callback, options, name);

    /// <summary>Registers a function-clone hook.</summary>
    /// <param name="handle">Handle populated once the clone is live.</param>
    /// <param name="target">Pattern, address, RVA or export identifying the function entry point.</param>
    /// <param name="patches">Patch sites applied to the clone.</param>
    /// <param name="name">Defaults to the handle's expression text.</param>
    public HookHandle<X64FunctionCloneHook> AddCloneHook(
        HookHandle<X64FunctionCloneHook> handle,
        HookTarget target,
        IEnumerable<FunctionClonePatch> patches,
        [CallerArgumentExpression(nameof(handle))] string name = "")
    {
        ArgumentNullThrow(handle, nameof(handle));
        ArgumentNullThrow(patches, nameof(patches));

        Register(new CloneHookDescriptor(name, target, handle, patches), handle, name);
        return handle;
    }

    private void Register<THook>(HookDescriptor descriptor, HookHandle<THook> handle, string name)
        where THook : class, IHook
    {
        ThrowIfNotBuilding();

        // Compile the pattern now: a typo fails here, at the registration call site, instead of silently producing a shorter pattern that matches something else at commit time.
        if (descriptor.Target.RequiresScan)
            descriptor.Pattern = CompiledPattern.Parse(descriptor.Target.Text!);

        handle.Register(name);
        _descriptors.Add(descriptor);
    }

    // =========================================================================
    // Commit
    // =========================================================================

    /// <summary>
    /// Resolves, prepares, installs and publishes every registered hook.
    /// </summary>
    /// <returns>Per-hook outcome, including which patterns went missing.</returns>
    /// <exception cref="HookTransactionException">
    /// A hook failed and <see cref="HookTransactionOptions.FailureMode"/> is
    /// <see cref="TransactionFailureMode.RollbackAndThrow"/>.
    /// </exception>
    public CommitResult Commit()
    {
        ThrowIfNotBuilding();
        State = TransactionState.Committing;

        try
        {
            Result = HookCommitPipeline.Execute(_region, _descriptors, Options, _loggerFactory, _logger, _hooks);
            State = TransactionState.Committed;
            return Result;
        }
        catch
        {
            State = TransactionState.RolledBack;
            throw;
        }
    }

    // =========================================================================
    // Post-commit control
    // =========================================================================

    /// <summary>Installs every prepared hook. Pairs with <see cref="HookTransactionOptions.InstallOnCommit"/> set to false.</summary>
    public void EnableAll()
    {
        foreach (IHook hook in _hooks)
        {
            try
            {
                hook.Enable();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Name}] failed to enable.", hook.Name);
            }
        }
    }

    /// <summary>Restores original bytes for every hook, leaving them re-enableable.</summary>
    public void DisableAll()
    {
        for (int i = _hooks.Count - 1; i >= 0; i--)
        {
            try
            {
                _hooks[i].Disable();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Name}] failed to disable.", _hooks[i].Name);
            }
        }
    }

    /// <summary>
    /// Disposes the transaction. Hooks are only torn down when <see cref="HookTransactionOptions.OwnsHooks"/> is set, by default they outlive the transaction, which is what a <c>using</c> block around registration expects.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (Options.OwnsHooks)
        {
            for (int i = _hooks.Count - 1; i >= 0; i--)
            {
                try
                {
                    _hooks[i].Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Name}] failed to dispose.", _hooks[i].Name);
                }
            }

            _hooks.Clear();
        }

        if (_ownsRegion)
            _region.Dispose();
    }

    private void ThrowIfNotBuilding()
    {
        if (State != TransactionState.Building)
            throw new InvalidOperationException($"Hook transaction [{Name}] is {State}; registrations are closed and it cannot be committed again.");
    }

    private static void ArgumentNullThrow(object? value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
    }

    private static void DetourHandleNullThrow<TFunction>(DetourHandle<TFunction>? handle, string expression)
        where TFunction : Delegate
    {
        if (handle is not null)
            return;

        string field = string.IsNullOrWhiteSpace(expression) ? "the supplied handle" : $"[{expression}]";
        throw new ArgumentNullException("handle", $"Detour handle {field} is null. Initialize the field with new DetourHandle<{typeof(TFunction).Name}>(), or call the overload which starts with HookTarget and use its returned handle. Registration validates the handle before resolving the target, so an export target has not been looked up yet.");
    }
}
