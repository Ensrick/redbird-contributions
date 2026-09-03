using Microsoft.Extensions.Logging;
using System;

namespace RedBird.Core.Logging;

public static partial class X64InlineHookLog
{
    [LoggerMessage(
    EventId = 25,
    Level = LogLevel.Information,
    Message = "Compiled stub at 0x{AllocatedStubAddress}, size: {StubCodeLength} bytes (target: 0x{TargetAddress}).")]
    public static partial void InlineHookStubCompiled(this ILogger logger, IntPtr allocatedStubAddress, int stubCodeLength, UInt64 targetAddress);

    [LoggerMessage(
    EventId = 26,
    Level = LogLevel.Information,
    Message = "Hook enabled at 0x{TargetAddress}, stub 0x{AllocatedStubAddress}.")]
    public static partial void InlineHookEnabled(this ILogger logger, UInt64 targetAddress, IntPtr allocatedStubAddress);

    [LoggerMessage(
    EventId = 27,
    Level = LogLevel.Information,
    Message = "Hook disabled at 0x{TargetAddress}.")]
    public static partial void InlineHookDisabled(this ILogger logger, UInt64 targetAddress);
}
