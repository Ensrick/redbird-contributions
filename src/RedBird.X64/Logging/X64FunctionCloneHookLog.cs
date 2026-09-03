using Microsoft.Extensions.Logging;
using System;

namespace RedBird.Core.Logging;

public static partial class X64FunctionCloneHookLog
{
    [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Information,
    Message = "Allocated clone stub at 0x{CloneAddress:X16}, (~{EstimatedSize} bytes) for function 0x{FunctionAddress:X16}.")]
    public static partial void CloneStubAllocated(this ILogger logger, IntPtr cloneAddress, int estimatedSize, ulong functionAddress);

    [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Warning,
    Message = "Clone stub ({StubBytesLength} b) exceeded estimate ({EstimatedSize} b). Adjacent slab data may be corrupted: increase per-instruction budget.")]
    public static partial void CloneStubExceeded(this ILogger logger, int stubBytesLength, int estimatedSize);

    [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Information,
    Message = "Clone stub written: {StubBytesLength} bytes.")]
    public static partial void CloneStubWritten(this ILogger logger, int stubBytesLength);

    [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Information,
    Message = "Enabled: 0x{FunctionAddress:X16}, clone 0x{CloneAddress:X16}.")]
    public static partial void CloneHookEnabled(this ILogger logger, ulong functionAddress, IntPtr cloneAddress);

    [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Error,
    Message = "Failed to enable: 0x{FunctionAddress:X16}, clone 0x{CloneAddress:X16}.")]
    public static partial void CloneHookFailedToEnable(this ILogger logger, ulong functionAddress, IntPtr cloneAddress);

    [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Information,
    Message = "Disabled: 0x{FunctionAddress:X16} restored")]
    public static partial void CloneHookDisabled(this ILogger logger, ulong functionAddress);

    [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Information,
    Message = "Disassembled {InstructionCount} instructions ({ByteCount} bytes) from 0x{FunctionAddress:X16}.")]
    public static partial void CloneHookDisassembledInstructions(this ILogger logger, int instructionCount, int byteCount, ulong functionAddress);

    [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Information,
    Message = "Island for 0x{OriginalIP:X16} at 0x{IslandAddress:X16} ({IslandBytesLength} b, suppress={SuppressOriginal}).")]
    public static partial void CloneHookPatchIslandCreated(this ILogger logger, ulong originalIP, IntPtr islandAddress, int islandBytesLength, bool suppressOriginal);

    [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Debug,
    Message = "BlockEncoder produced {ByteCount} bytes at 0x{CloneBase:X16}.")]
    public static partial void CloneHookBlockEncoderResult(this ILogger logger, int byteCount, ulong cloneBase);
}
