using Iced.Intel;
using Microsoft.Extensions.Logging;
using System;
using RedBird.X64.Hooks;

namespace RedBird.Core.Logging;

public static partial class X64JmpTargetPatchLog
{
    [LoggerMessage(
    EventId = 28,
    Level = LogLevel.Information,
    Message = "Retargeted {Encoding} jmp at 0x{JumpAddress} to 0x{NewTargetAddress}.")]
    public static partial void JmpTargetRetargeted(this ILogger logger, X64JmpTargetPatch.JmpEncoding encoding, UInt64 jumpAddress, UInt64 newTargetAddress);

    [LoggerMessage(
    EventId = 29,
    Level = LogLevel.Information,
    Message = "Restored original bytes at 0x{JumpAddress}.")]
    public static partial void JmpTargetRestored(this ILogger logger, UInt64 jumpAddress);

    [LoggerMessage(
    EventId = 30,
    Level = LogLevel.Information,
    Message = "Detected IndirectAbsolute (FF 25) at 0x{Address:X16}.")]
    public static partial void JmpTargetDetectedIndirectAbsolute(this ILogger logger, UInt64 address);

    [LoggerMessage(
    EventId = 31,
    Level = LogLevel.Information,
    Message = "Detected NearRelative (E9) at 0x{Address:X16}.")]
    public static partial void JmpTargetDetectedNearRelative(this ILogger logger, UInt64 address);

    [LoggerMessage(
    EventId = 32,
    Level = LogLevel.Information,
    Message = "Detected ShortRelative (EB) at 0x{Address:X16}.")]
    public static partial void JmpTargetDetectedShortRelative(this ILogger logger, UInt64 address);

    [LoggerMessage(
    EventId = 33,
    Level = LogLevel.Information,
    Message = "Iced fallback: detected {Encoding} at 0x{Address:X16} (mnemonic {Mnemonic}).")]
    public static partial void JmpTargetDetectedIcedFallback(this ILogger logger, X64JmpTargetPatch.JmpEncoding encoding, UInt64 address, Mnemonic mnemonic);

    [LoggerMessage(
    EventId = 34,
    Level = LogLevel.Information,
    Message = "Promoted EB short jmp at 0x{JumpAddress} to E9 near jmp (consumed 3 bytes of INT3 padding).")]
    public static partial void JmpTargetPromotedShortJmp(this ILogger logger, UInt64 jumpAddress);
}
