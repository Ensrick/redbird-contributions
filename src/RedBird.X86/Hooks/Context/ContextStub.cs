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
    public static ContextHookDelegate Guard(ContextHookDelegate callback, ContextHookOptions options, UInt64 hookAddress, ILogger logger)
    {
        if (options.ErrorMode == CallbackErrorMode.Rethrow)
            return callback;

        return context =>
        {
            try
            {
                callback(context);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unhandled exception in x86 context hook at 0x{HookAddress:X8}.", hookAddress);
                if (options.ErrorMode != CallbackErrorMode.LogAndContinueWithRecovery || options.RecoveryAction is null)
                    return;

                try
                {
                    options.RecoveryAction(context, exception);
                }
                catch (Exception recoveryException)
                {
                    logger.LogError(recoveryException, "Exception in x86 context-hook recovery at 0x{HookAddress:X8}.", hookAddress);
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
