using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.Core.Memory.Scanners;
using RedBird.Core.Utilities;
using RedBird.X64.Hooks.Transaction.Descriptors;

namespace RedBird.X64.Hooks.Transaction;

/// <summary>
/// The commit algorithm, shared by <see cref="HookTransaction"/> and <see cref="HookSession"/>.
/// <para>
/// <b>Four phases, in order.</b>
/// </para>
/// <list type="number">
///   <item><b>Resolve</b>: one multi-pattern sweep over the region resolves every AOB target at once; addresses, RVAs and exports resolve directly.</item>
///   <item><b>Prepare</b>: stubs allocated, code assembled, trampolines built. Expensive, failure-prone, and completely invisible to the target: nothing has been written to its code yet.</item>
///   <item><b>Install</b>: the only phase that mutates live instructions, and now the shortest one. Failures here are rare because everything that could fail already did.</item>
///   <item><b>Publish</b>: handles are populated, so a consumer can never observe a handle pointing at a half-built hook.</item>
/// </list>
/// </summary>
internal static class HookCommitPipeline
{
    public static CommitResult Execute(
        ScanRegion region,
        IReadOnlyList<HookDescriptor> descriptors,
        HookTransactionOptions options,
        ILoggerFactory? loggerFactory,
        ILogger logger,
        List<IHook> installedSink)
    {
        Stopwatch total = Stopwatch.StartNew();
        List<HookFailure> failures = [];
        List<HookDescriptor> prepared = [];
        List<HookDescriptor> installed = [];

        HookBuildContext context = new(options.ResolveBackend(), loggerFactory);
        bool rollbackOnFailure = options.FailureMode == TransactionFailureMode.RollbackAndThrow;

        void Fail(HookDescriptor descriptor, HookFailurePhase phase, string message, Exception? ex = null)
        {
            HookFailure failure = new(descriptor.Name, descriptor.Target, phase, message, ex);
            failures.Add(failure);
            descriptor.Reject(ex ?? new HookTransactionException(descriptor.Name, message));

            if (ex is null)
                logger.LogWarning("[{Name}] {Phase} failed: {Message}", descriptor.Name, phase, message);
            else
                logger.LogError(ex, "[{Name}] {Phase} failed: {Message}", descriptor.Name, phase, message);

            if (!rollbackOnFailure)
                return;

            Rollback(prepared, installed, installedSink, logger);
            throw new HookTransactionException(descriptor.Name, message, ex);
        }

        // ---------------------------------------------------------------------
        // Phase 1: Resolve
        // ---------------------------------------------------------------------
        long scanMilliseconds = ResolveAll(region, descriptors, options, logger);

        List<HookDescriptor> resolved = new(descriptors.Count);
        foreach (HookDescriptor descriptor in descriptors)
        {
            if (descriptor.ResolvedAddress == 0)
            {
                Fail(descriptor, HookFailurePhase.Resolve, DescribeResolveFailure(descriptor));
                continue;
            }

            resolved.Add(descriptor);
        }

        // ---------------------------------------------------------------------
        // Phase 2: Prepare (no target memory is modified here)
        // ---------------------------------------------------------------------
        foreach (HookDescriptor descriptor in resolved)
        {
            try
            {
                descriptor.Prepare(descriptor.ResolvedAddress, context);

                if (descriptor.Hook is null)
                    throw new InvalidOperationException("Descriptor produced no hook.");

                prepared.Add(descriptor);
                logger.LogTrace("[{Name}] prepared for 0x{Address:X16}.", descriptor.Name, descriptor.ResolvedAddress);
            }
            catch (Exception ex)
            {
                descriptor.Discard();
                Fail(descriptor, HookFailurePhase.Prepare, "Hook preparation failed.", ex);
            }
        }

        // ---------------------------------------------------------------------
        // Phase 3: Install
        // ---------------------------------------------------------------------
        if (options.InstallOnCommit)
        {
            foreach (HookDescriptor descriptor in prepared)
            {
                try
                {
                    descriptor.Hook!.Enable();
                    installed.Add(descriptor);
                    installedSink.Add(descriptor.Hook!);
                }
                catch (Exception ex)
                {
                    descriptor.Discard();
                    Fail(descriptor, HookFailurePhase.Install, "Writing the patch failed.", ex);
                }
            }
        }
        else
        {
            // Prepared-only commit: hand the hooks over so EnableAll can flip them later.
            foreach (HookDescriptor descriptor in prepared)
            {
                installed.Add(descriptor);
                installedSink.Add(descriptor.Hook!);
            }
        }

        // ---------------------------------------------------------------------
        // Phase 4: Publish
        // ---------------------------------------------------------------------
        foreach (HookDescriptor descriptor in installed)
            descriptor.Publish();

        total.Stop();

        List<IHook> hooks = new(installed.Count);
        foreach (HookDescriptor descriptor in installed)
            hooks.Add(descriptor.Hook!);

        CommitResult result = new(hooks, failures, descriptors.Count, scanMilliseconds, total.ElapsedMilliseconds);
        logger.LogInformation("Commit: {Result}", result);
        return result;
    }

    /// <summary>
    /// Fills <see cref="HookDescriptor.ResolvedAddress"/> for every descriptor. All AOB patterns share a single sweep of the region.
    /// </summary>
    private static long ResolveAll(
        ScanRegion region,
        IReadOnlyList<HookDescriptor> descriptors,
        HookTransactionOptions options,
        ILogger logger)
    {
        List<HookDescriptor> scanTargets = [];
        List<CompiledPattern> patterns = [];

        foreach (HookDescriptor descriptor in descriptors)
        {
            descriptor.ResolvedAddress = 0;

            if (!descriptor.Target.RequiresScan || descriptor.Pattern is null)
                continue;

            scanTargets.Add(descriptor);
            patterns.Add(descriptor.Pattern);
        }

        long scanMilliseconds = 0;

        if (patterns.Count > 0)
        {
            Stopwatch scanWatch = Stopwatch.StartNew();

            long[] offsets = new long[patterns.Count];
            MultiPatternScanner.FindAll(region.Span, patterns, offsets, options.ParallelScan);

            scanWatch.Stop();
            scanMilliseconds = scanWatch.ElapsedMilliseconds;

            int found = 0;
            for (int i = 0; i < offsets.Length; i++)
            {
                if (offsets[i] < 0)
                    continue;

                found++;
                scanTargets[i].ResolvedAddress = ApplyOffset(region.ToAddress(offsets[i]), scanTargets[i].Target.Offset);
            }

            logger.LogInformation("Scanned {PatternCount} patterns across {RegionSize} bytes in one pass: {Found} resolved, {Missing} missing, {Elapsed}ms.", patterns.Count, region.Length, found, patterns.Count - found, scanMilliseconds);
        }

        foreach (HookDescriptor descriptor in descriptors)
        {
            HookTarget target = descriptor.Target;

            switch (target.Kind)
            {
                case HookTargetKind.Pattern:
                    break; // Already handled above.

                case HookTargetKind.Address:
                    descriptor.ResolvedAddress = ApplyOffset(target.Value, target.Offset);
                    break;

                case HookTargetKind.RelativeVirtualAddress:
                    descriptor.ResolvedAddress = ApplyOffset(region.BaseAddress + target.Value, target.Offset);
                    break;

                case HookTargetKind.Export:
                    IntPtr module = target.Module != IntPtr.Zero
                        ? target.Module
                        : (IntPtr)unchecked((long)region.BaseAddress);

                    if (NativeExports.TryResolve(module, target.Text!, out UInt64 exportAddress))
                        descriptor.ResolvedAddress = ApplyOffset(exportAddress, target.Offset);
                    break;
            }
        }

        return scanMilliseconds;
    }

    private static UInt64 ApplyOffset(UInt64 address, int offset)
        => offset == 0 ? address : unchecked((UInt64)((long)address + offset));

    private static string DescribeResolveFailure(HookDescriptor descriptor) => descriptor.Target.Kind switch
    {
        HookTargetKind.Pattern => $"Pattern not found: {descriptor.Target.Text}",
        HookTargetKind.Export => $"Export not found: {descriptor.Target.Text}",
        _ => $"Target resolved to a null address: {descriptor.Target}",
    };

    /// <summary>
    /// Undoes a partially applied commit, newest first, so overlapping patches unwind in the reverse order they were written.
    /// </summary>
    private static void Rollback(
        List<HookDescriptor> prepared,
        List<HookDescriptor> installed,
        List<IHook> installedSink,
        ILogger logger)
    {
        logger.LogWarning("Rolling back {Installed} installed and {Prepared} prepared hooks.", installed.Count, prepared.Count - installed.Count);

        for (int i = installed.Count - 1; i >= 0; i--)
        {
            try
            {
                installed[i].Hook?.Disable();
                installedSink.Remove(installed[i].Hook!);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Name}] failed to disable during rollback.", installed[i].Name);
            }
        }

        // Dispose every prepared hook, installed or not. 
        for (int i = prepared.Count - 1; i >= 0; i--)
        {
            try
            {
                prepared[i].Discard();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Name}] failed to release resources during rollback.", prepared[i].Name);
            }
        }

        prepared.Clear();
        installed.Clear();
    }
}
