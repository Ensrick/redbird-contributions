using Microsoft.Extensions.Logging;
using System;

namespace RedBird.Core.Logging;

public static partial class X64FunctionCloneHookLog
{
    [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Information,
    Message = "[{Name}] allocated clone stub at 0x{CloneAddress:X16}, (~{EstimatedSize} bytes) for function 0x{FunctionAddress:X16}.")]
    public static partial void CloneStubAllocated(this ILogger logger, string name, UInt64 cloneAddress, int estimatedSize, UInt64 functionAddress);

    [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Warning,
    Message = "[{Name}] clone stub ({StubBytesLength} b) exceeded estimate ({EstimatedSize} b). Adjacent slab data may be corrupted: increase per-instruction budget.")]
    public static partial void CloneStubExceeded(this ILogger logger, string name, int stubBytesLength, int estimatedSize);

    [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Information,
    Message = "[{Name}] clone stub written: {StubBytesLength} bytes.")]
    public static partial void CloneStubWritten(this ILogger logger, string name, int stubBytesLength);

    [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Information,
    Message = "[{Name}] enabled: 0x{FunctionAddress:X16}, clone 0x{CloneAddress:X16}.")]
    public static partial void CloneHookEnabled(this ILogger logger, string name, UInt64 functionAddress, UInt64 cloneAddress);

    [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Error,
    Message = "[{Name}] failed to enable: 0x{FunctionAddress:X16}, clone 0x{CloneAddress:X16}.")]
    public static partial void CloneHookFailedToEnable(this ILogger logger, string name, UInt64 functionAddress, UInt64 cloneAddress);

    [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Information,
    Message = "[{Name}] disabled: 0x{FunctionAddress:X16} restored")]
    public static partial void CloneHookDisabled(this ILogger logger, string name, UInt64 functionAddress);

    [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Information,
    Message = "[{Name}] disassembled {InstructionCount} instructions ({ByteCount} bytes) from 0x{FunctionAddress:X16}.")]
    public static partial void CloneHookDisassembledInstructions(this ILogger logger, string name, int instructionCount, int byteCount, ulong functionAddress);

    [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Information,
    Message = "[{Name}] island for 0x{OriginalIP:X16} at 0x{IslandAddress:X16} ({IslandBytesLength} b, suppress={SuppressOriginal}).")]
    public static partial void CloneHookPatchIslandCreated(this ILogger logger, string name, UInt64 originalIP, UInt64 islandAddress, int islandBytesLength, bool suppressOriginal);

    [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Debug,
    Message = "[{Name}] BlockEncoder produced {ByteCount} bytes at 0x{CloneBase:X16}.")]
    public static partial void CloneHookBlockEncoderResult(this ILogger logger, string name, int byteCount, UInt64 cloneBase);
}
