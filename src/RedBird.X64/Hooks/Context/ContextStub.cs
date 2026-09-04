using Iced.Intel;
using Microsoft.Extensions.Logging;
using System;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Memory;
using RedBird.X64.Assembly;
using RedBird.X64.Extensions;

namespace RedBird.X64.Hooks.Context;

/// <summary>
/// Builds the assembly stub and the exception guard for a context hook.
/// </summary>
public static class ContextStub
{
    /// <summary>
    /// Wraps <paramref name="callback"/> so an exception cannot unwind into native code.
    /// Returns the callback unchanged for <see cref="CallbackErrorMode.Rethrow"/>.
    /// </summary>
    public static ContextHookDelegate Guard(
        ContextHookDelegate callback,
        ContextHookOptions options,
        UInt64 hookAddress,
        ILogger logger,
        string? name = null)
    {
        if (options.ErrorMode == CallbackErrorMode.Rethrow)
            return callback;

        string displayName = name ?? $"0x{hookAddress:X16}";
        CallbackErrorMode mode = options.ErrorMode;
        Action<NativePointer<X64SmartCPUContext>, Exception>? recovery = options.RecoveryAction;

        return context =>
        {
            try
            {
                callback(context);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Name}] unhandled exception in context hook callback at 0x{HookAddress:X16}. Registers keep their pre-callback values.", displayName, hookAddress);

                if (mode != CallbackErrorMode.LogAndContinueWithRecovery || recovery is null)
                    return;

                try
                {
                    recovery(context, ex);
                }
                catch (Exception recoveryEx)
                {
                    // A throwing recovery action must never reach native code either.
                    logger.LogError(recoveryEx, "[{Name}] exception in recovery action for context hook at 0x{HookAddress:X16}.", displayName, hookAddress);
                }
            }
        };
    }

    /// <summary>
    /// Produces the generator that emits: optional displaced instructions, the context
    /// save/call/restore sequence, then the remaining displaced instructions.
    /// </summary>
    /// <param name="hook">Owning hook; used to pin the callback for its lifetime.</param>
    /// <param name="callback">Already guarded via <see cref="Guard"/>.</param>
    /// <param name="options">Register set, selector and placement.</param>
    public static InlineHookGenerator CreateGenerator(
        X64InlineHook hook,
        ContextHookDelegate callback,
        ContextHookOptions options)
    {
        return (asm, original, returnAddress) =>
        {
            IntPtr callbackPointer = hook.PinDelegate(callback);

            Instruction[] displaced = options.InstructionSelector is null
                ? original.ToArray()
                : [.. options.InstructionSelector(original.ToArray())];

            if (options.Placement == OverwrittenInstructionPlacement.BeforeCallback)
                asm.AddInstructions(displaced);

            ContextAssemblyGenerator.Generate(asm, callbackPointer, options.Registers);

            if (options.Placement == OverwrittenInstructionPlacement.AfterCallback)
                asm.AddInstructions(displaced);

            // Suppress: emit nothing, fall through to the trampoline jump appended by the hook.
        };
    }
}
