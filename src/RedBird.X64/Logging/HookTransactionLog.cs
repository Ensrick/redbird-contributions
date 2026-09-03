using Microsoft.Extensions.Logging;
using System;

namespace RedBird.Core.Logging;

public static partial class HookTransactionLog
{
    [LoggerMessage(
    EventId = 17,
    Level = LogLevel.Trace,
    Message = "[{Name}] direct at 0x{Address:X16}.")]
    public static partial void HookTransactionActivateDirect(this ILogger logger, string name, UInt64 address);

    [LoggerMessage(
    EventId = 18,
    Level = LogLevel.Trace,
    Message = "[{Name}] aob at 0x{Address:X16}.")]
    public static partial void HookTransactionActivateAOB(this ILogger logger, string name, UInt64 address);

    [LoggerMessage(
    EventId = 19,
    Level = LogLevel.Trace,
    Message = "[{Name}] activating at 0x{Address:X16}.")]
    public static partial void HookTransactionActivate(this ILogger logger, string name, UInt64 address);

    [LoggerMessage(
    EventId = 20,
    Level = LogLevel.Information,
    Message = "[{Name}] installed at 0x{Address:X16}.")]
    public static partial void HookTransactionInstall(this ILogger logger, string name, UInt64 address);

    [LoggerMessage(
    EventId = 21,
    Level = LogLevel.Information,
    Message = "Commit complete - [{ActivatedCount}] active, [{DescriptorsCount}] registered. [{ElapsedMilliseconds}]ms total.")]
    public static partial void HookTransactionCommitComplete(this ILogger logger, int activatedCount, int descriptorsCount, long elapsedMilliseconds);

    [LoggerMessage(
    EventId = 22,
    Level = LogLevel.Information,
    Message = "[{ActivatedCount}] hooks to rollback.")]
    public static partial void HookTransactionRollback(this ILogger logger, int activatedCount);

    [LoggerMessage(
    EventId = 23,
    Level = LogLevel.Information,
    Message = "{PatternDescriptors} patterns scanned in {ElapsedMilliseconds}ms.")]
    public static partial void HookTransactionScanPatterns(this ILogger logger, int patternDescriptors, long elapsedMilliseconds);
}
