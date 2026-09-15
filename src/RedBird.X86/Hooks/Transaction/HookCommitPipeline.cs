using Iced.Intel;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.Core.Memory.Scanners;
using RedBird.Core.Utilities;
using RedBird.X86.Hooks.Transaction.Descriptors;

namespace RedBird.X86.Hooks.Transaction;

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
                logger.LogTrace("[{Name}] prepared for 0x{Address:X8}.", descriptor.Name, descriptor.ResolvedAddress);
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
    /// Fills <see cref="HookDescriptor.ResolvedAddress"/> for every descriptor. All AOB patterns
    /// share a single sweep of the region.
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
            descriptor.ResolutionFailure = null;

            if (!descriptor.Target.RequiresScan || descriptor.Pattern is null)
                continue;

            scanTargets.Add(descriptor);
            patterns.Add(descriptor.Pattern);
        }

        long scanMilliseconds = 0;

        if (patterns.Count > 0)
        {
            AobCacheResolution resolution = AobCacheResolver.Resolve(region, patterns, options.AobCache, options.ParallelScan, logger);
            long[] offsets = resolution.Offsets;
            scanMilliseconds = resolution.ScanMilliseconds;

            int found = 0;
            for (int i = 0; i < offsets.Length; i++)
            {
                if (offsets[i] < 0)
                    continue;

                HookDescriptor descriptor = scanTargets[i];
                HookTarget target = descriptor.Target;

                if (target.Kind == HookTargetKind.RelativeCall)
                {
                    if (!TryResolveRelativeCall(region, offsets[i], target.Offset, out UInt64 callAddress, out UInt64 callTarget, out string failure))
                    {
                        descriptor.ResolutionFailure = failure;
                        continue;
                    }

                    descriptor.ResolvedAddress = callTarget;
                    found++;
                    logger.LogDebug("[{Name}] relative CALL at 0x{CallAddress:X8} resolved to 0x{TargetAddress:X8}.", descriptor.Name, callAddress, callTarget);
                    continue;
                }

                descriptor.ResolvedAddress = ApplyOffset(region.ToAddress(offsets[i]), target.Offset);
                found++;
            }

            if (resolution.CacheHitCount > 0)
                logger.LogInformation("Loaded {CacheHitCount} of {PatternCount} AOB results from cache.", resolution.CacheHitCount, patterns.Count);

            if (resolution.ScannedPatternCount > 0)
                logger.LogInformation("Scanned {ScannedPatternCount} of {PatternCount} patterns across {RegionSize} bytes in one pass: {Found} total resolved, {Missing} missing, {Elapsed}ms.", resolution.ScannedPatternCount, patterns.Count, region.Length, found, patterns.Count - found, scanMilliseconds);
        }

        foreach (HookDescriptor descriptor in descriptors)
        {
            HookTarget target = descriptor.Target;

            switch (target.Kind)
            {
                case HookTargetKind.Pattern:
                case HookTargetKind.RelativeCall:
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

    private static bool TryResolveRelativeCall(
        ScanRegion region,
        long patternOffset,
        int callOffset,
        out UInt64 callAddress,
        out UInt64 callTarget,
        out string failure)
    {
        callAddress = 0;
        callTarget = 0;
        failure = string.Empty;

        long instructionOffset;
        try
        {
            instructionOffset = checked(patternOffset + callOffset);
        }
        catch (OverflowException)
        {
            failure = $"Relative CALL offset overflowed for pattern match +{patternOffset} and callOffset {callOffset}.";
            return false;
        }

        if (instructionOffset < 0 || instructionOffset >= region.Length)
        {
            failure = $"Relative CALL offset {callOffset} from pattern match +{patternOffset} falls outside the scan region.";
            return false;
        }

        callAddress = region.ToAddress(instructionOffset);
        int available = Math.Min(15, region.Length - (int)instructionOffset);
        byte[] encoded = region.Span.Slice((int)instructionOffset, available).ToArray();
        Decoder decoder = Decoder.Create(32, new ByteArrayCodeReader(encoded), callAddress);
        Instruction instruction = decoder.Decode();

        if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
        {
            failure = $"Could not decode the expected direct relative CALL at 0x{callAddress:X8}.";
            return false;
        }

        if (instruction.FlowControl != FlowControl.Call ||
            instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32))
        {
            failure = $"Expected a direct relative CALL at 0x{callAddress:X8}, but decoded {instruction.Mnemonic} with operand kind {instruction.Op0Kind}.";
            return false;
        }

        callTarget = instruction.NearBranchTarget & UInt32.MaxValue;
        if (callTarget == 0)
        {
            failure = $"Direct relative CALL at 0x{callAddress:X8} resolved to the null address.";
            return false;
        }

        return true;
    }

    private static string DescribeResolveFailure(HookDescriptor descriptor)
    {
        if (!string.IsNullOrEmpty(descriptor.ResolutionFailure))
            return descriptor.ResolutionFailure!;

        return descriptor.Target.Kind switch
        {
            HookTargetKind.Pattern => $"Pattern not found: {descriptor.Target.Text}",
            HookTargetKind.RelativeCall => $"Relative CALL pattern not found: {descriptor.Target.Text}",
            HookTargetKind.Export => $"Export not found: {descriptor.Target.Text}",
            _ => $"Target resolved to a null address: {descriptor.Target}",
        };
    }

    /// <summary>
    /// Undoes a partially applied commit, newest first, so overlapping patches unwind in the
    /// reverse order they were written.
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
