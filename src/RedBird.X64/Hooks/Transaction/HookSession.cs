using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction.Descriptors;

namespace RedBird.X64.Hooks.Transaction;

/// <summary>
/// Owns a scan region and a set of transactions, and commits them together.
/// </summary>
public sealed class HookSession(
    ScanRegion region,
    ILoggerFactory? loggerFactory = null,
    HookTransactionOptions? options = null,
    bool ownsRegion = false) : IDisposable
{
    private readonly ScanRegion _region = region ?? throw new ArgumentNullException(nameof(region));
    private readonly ILogger _logger = loggerFactory?.CreateLogger(nameof(HookSession)) ?? NullLogger.Instance;
    private readonly List<HookTransaction> _transactions = [];
    private readonly List<IHook> _hooks = [];
    private bool _disposed;

    /// <summary>Policy applied to every transaction created by this session.</summary>
    public HookTransactionOptions Options { get; } = options ?? HookTransactionOptions.Default;

    /// <summary>Every hook installed through this session.</summary>
    public IReadOnlyList<IHook> Hooks => new ReadOnlyCollection<IHook>(_hooks);

    /// <summary>Result of the last <see cref="Commit"/>.</summary>
    public CommitResult? Result { get; private set; }

    /// <summary>Creates a transaction bound to this session's region and options.</summary>
    public HookTransaction BeginTransaction([CallerMemberName] string name = "")
    {
        ThrowIfDisposed();

        HookTransaction transaction = new(_region, loggerFactory, Options, name);
        _transactions.Add(transaction);
        return transaction;
    }

    /// <summary>
    /// Commits every transaction that is still building, sharing a single scan across all of them.
    /// </summary>
    public CommitResult Commit()
    {
        ThrowIfDisposed();

        List<HookTransaction> pending = [];
        List<HookDescriptor> descriptors = [];

        foreach (HookTransaction transaction in _transactions)
        {
            if (transaction.State != TransactionState.Building || transaction.Descriptors.Count == 0)
                continue;

            transaction.BeginSessionCommit();
            pending.Add(transaction);
            descriptors.AddRange(transaction.Descriptors);
        }

        if (descriptors.Count == 0)
        {
            _logger.LogInformation("Nothing to commit: no transaction has pending registrations.");
            Result = new CommitResult([], [], 0, 0, 0);
            return Result;
        }

        _logger.LogInformation("Committing {Descriptors} hooks from {Transactions} transactions against {Region}.", descriptors.Count, pending.Count, _region);

        bool rolledBack = false;
        try
        {
            Result = HookCommitPipeline.Execute(_region, descriptors, Options, loggerFactory, _logger, _hooks);
            return Result;
        }
        catch
        {
            rolledBack = true;
            throw;
        }
        finally
        {
            // Route each hook back to the transaction that registered it, so per-transaction
            // Enable/Disable/Dispose keeps working exactly as it does for a standalone commit.
            foreach (HookTransaction transaction in pending)
            {
                foreach (HookDescriptor descriptor in transaction.Descriptors)
                {
                    if (descriptor.Hook is not null)
                        transaction.HookSink.Add(descriptor.Hook);
                }

                transaction.CompleteSessionCommit(Result ?? new CommitResult([], [], descriptors.Count, 0, 0), rolledBack);
            }
        }
    }

    /// <summary>Restores original bytes for every hook in the session, newest first.</summary>
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

    /// <summary>Disables and releases every hook, then the region if this session owns it.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

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

        foreach (HookTransaction transaction in _transactions)
            transaction.Dispose();

        _transactions.Clear();

        if (ownsRegion)
            _region.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HookSession));
    }
}
