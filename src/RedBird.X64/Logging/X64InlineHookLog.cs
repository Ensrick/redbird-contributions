using Microsoft.Extensions.Logging;
using System;

namespace RedBird.Core.Logging;

public static partial class X64InlineHookLog
{
    [LoggerMessage(
    EventId = 25,
    Level = LogLevel.Information,
    Message = "[{Name}] compiled stub at 0x{AllocatedStubAddress:X16}, size: {StubCodeLength} bytes (target: 0x{TargetAddress:X16}).")]
    public static partial void InlineHookStubCompiled(this ILogger logger, string name, UInt64 allocatedStubAddress, int stubCodeLength, UInt64 targetAddress);

    [LoggerMessage(
    EventId = 26,
    Level = LogLevel.Information,
    Message = "[{Name}] hook enabled at 0x{TargetAddress:X16}, stub 0x{AllocatedStubAddress:X16}.")]
    public static partial void InlineHookEnabled(this ILogger logger, string name, UInt64 targetAddress, UInt64 allocatedStubAddress);

    [LoggerMessage(
    EventId = 27,
    Level = LogLevel.Information,
    Message = "[{Name}] hook disabled at 0x{TargetAddress:X16}.")]
    public static partial void InlineHookDisabled(this ILogger logger, string name, UInt64 targetAddress);
}
