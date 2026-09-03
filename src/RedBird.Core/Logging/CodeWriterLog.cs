using Microsoft.Extensions.Logging;
using System;

namespace RedBird.Core.Logging;

public static partial class CodeWriterLog
{
    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Information,
        Message = "Creating with target {Address:X16}, length={Length}")]
    public static partial void CodeWriterCreated(this ILogger logger, IntPtr address, int length);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Debug,
        Message = "Written byte {ByteValue} to {Position}/{Length} at {Address:X16}")]
    public static partial void CodeWriterWriteByte(this ILogger logger, byte byteValue, int position, int length, IntPtr address);
}
