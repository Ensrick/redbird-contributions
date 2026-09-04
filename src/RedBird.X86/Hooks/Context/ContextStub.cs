using System;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Memory;
using RedBird.X86.Assembly;
using RedBird.X86.Extensions;

namespace RedBird.X86.Hooks.Context;

/// <summary>Builds the intermediary assembly and managed exception guard for an x86 context hook.</summary>
public static class ContextStub
{
    public static ContextHookDelegate Guard(ContextHookDelegate callback, ContextHookOptions options, UInt64 hookAddress, ILogger logger, string? name = null)
    {
        if (options.ErrorMode == CallbackErrorMode.Rethrow)
            return callback;

        string displayName = name ?? $"0x{hookAddress:X8}";

        return context =>
        {
            try
            {
                callback(context);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[{Name}] unhandled exception in x86 context hook at 0x{HookAddress:X8}.", displayName, hookAddress);
                if (options.ErrorMode != CallbackErrorMode.LogAndContinueWithRecovery || options.RecoveryAction is null)
                    return;

                try
                {
                    options.RecoveryAction(context, exception);
                }
                catch (Exception recoveryException)
                {
                    logger.LogError(recoveryException, "[{Name}] exception in x86 context-hook recovery at 0x{HookAddress:X8}.", displayName, hookAddress);
                }
            }
        };
    }

    public static InlineHookGenerator CreateGenerator(X86InlineHook hook, ContextHookDelegate callback, ContextHookOptions options)
    {
        IntPtr callbackPointer = hook.PinDelegate(callback);
        return (assembler, original, _) =>
        {
            Instruction[] displaced = options.InstructionSelector is null
                ? original.ToArray()
                : [.. options.InstructionSelector(original.ToArray())];

            if (options.Placement == OverwrittenInstructionPlacement.BeforeCallback)
                assembler.AddInstructions(displaced);

            ContextAssemblyGenerator.Generate(assembler, callbackPointer, options.Registers);

            if (options.Placement == OverwrittenInstructionPlacement.AfterCallback)
                assembler.AddInstructions(displaced);
        };
    }
}
