using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.CallingConventions;
using RedBird.Abstractions.Hooks;
using RedBird.Extensions.CallingConventions.Internal;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.Extensions.CallingConventions.NativeX86;

/// <summary>
/// Builds the paired x86 stubs that translate a native convention to a cdecl managed callback and translate cdecl <c>Original</c> calls back to the native convention.
/// </summary>
/// <typeparam name="TDelegate">
/// Cdecl delegate describing every logical argument, including a thiscall <c>this</c> pointer.
/// </typeparam>
public sealed class X86CallingConventionIntermediaryFactory<TDelegate> : IDetourIntermediaryFactory where TDelegate : Delegate
{
    private readonly X86Signature _signature;

    /// <summary>Creates a factory from the delegate's <see cref="X86CallingConventionAttribute"/>.</summary>
    public X86CallingConventionIntermediaryFactory()
    {
        _signature = X86Signature.Create<TDelegate>(null);
    }

    /// <summary>Creates a factory for a standard convention.</summary>
    public X86CallingConventionIntermediaryFactory(X86CallingConvention convention)
        : this(new X86CallingConventionOptions(convention))
    {
    }

    /// <summary>Creates a factory with explicit immutable options.</summary>
    public X86CallingConventionIntermediaryFactory(X86CallingConventionOptions options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        _signature = X86Signature.Create<TDelegate>(options);
    }

    /// <summary>The native convention represented by this factory.</summary>
    public X86CallingConvention Convention => _signature.Options.Convention;

    /// <inheritdoc/>
    public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
    {
        if (context.Architecture != DetourArchitecture.X86)
        {
            throw new NotSupportedException($"The x86 calling-convention factory cannot emit {context.Architecture} intermediary stubs.");
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
            throw new PlatformNotSupportedException("x86 calling-convention stubs can only be created in a 32-bit x86 process.");

        bool isMicrosoftOnly = Convention is X86CallingConvention.Stdcall or
            X86CallingConvention.Fastcall or
            X86CallingConvention.Thiscall or
            X86CallingConvention.Pascal;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && isMicrosoftOnly)
        {
            throw new PlatformNotSupportedException($"Microsoft x86 {Convention} translation is only supported on Windows.");
        }

        UInt32 callback = ToX86Address(context.CallbackAddress, nameof(context.CallbackAddress));
        UInt32 trampoline = ToX86Address(context.TrampolineAddress, nameof(context.TrampolineAddress));

        // A cdecl target already has the exact ABI used by the managed-facing delegate.
        if (Convention is X86CallingConvention.Cdecl or
            X86CallingConvention.GccCdecl or
            X86CallingConvention.GccThiscall)
            return new DetourIntermediaryStubs(context.CallbackAddress, context.TrampolineAddress);

        Assembler inbound = BuildInbound(callback);
        Assembler outbound = BuildOutbound(trampoline);
        int inboundCapacity = EstimateCapacity(_signature.ManagedStackBytes);
        int outboundCapacity = EstimateCapacity(_signature.NativeStackBytes);

        IntPtr hookEntry = IntermediaryStubWriter.WriteX86(inbound, inboundCapacity);
        IntPtr originalEntry = IntermediaryStubWriter.WriteX86(outbound, outboundCapacity);
        return new DetourIntermediaryStubs(hookEntry, originalEntry);
    }

    private Assembler BuildInbound(UInt32 callbackAddress)
    {
        Assembler assembler = new(32);
        assembler.push(ebp);
        assembler.mov(ebp, esp);

        // A cdecl call frame is always right-to-left. EBP keeps native stack sources stable while pushes grow the temporary managed frame.
        for (int index = _signature.Parameters.Length - 1; index >= 0; index--)
        {
            X86Signature.X86Parameter parameter = _signature.Parameters[index];
            for (int chunk = parameter.StackSize - 4; chunk >= 0; chunk -= 4)
            {
                if (parameter.NativeRegister is Register register)
                {
                    if (chunk != 0)
                        throw new InvalidOperationException("A register argument unexpectedly occupied multiple stack slots.");

                    // EBP now points at our frame; its incoming value is the saved dword at [EBP].
                    if (register == Register.EBP)
                        assembler.push(__dword_ptr[ebp]);
                    else
                        assembler.push(new AssemblerRegister32(register));
                }
                else
                {
                    assembler.push(__dword_ptr[ebp + parameter.NativeStackOffset + chunk]);
                }
            }
        }

        assembler.call(callbackAddress);
        if (_signature.ManagedStackBytes != 0)
            assembler.add(esp, _signature.ManagedStackBytes);

        // Restore the real frame pointer before deliberately using EBP as a custom return register.
        assembler.pop(ebp);
        if (_signature.ReturnRegister is Register returnRegister && returnRegister != Register.EAX)
            assembler.mov(new AssemblerRegister32(returnRegister), eax);

        if (_signature.Options.StackCleanup == X86StackCleanup.Callee && _signature.NativeStackBytes != 0)
            assembler.ret((short)_signature.NativeStackBytes);
        else
            assembler.ret();

        return assembler;
    }

    private Assembler BuildOutbound(UInt32 trampolineAddress)
    {
        Assembler assembler = new(32);
        assembler.push(ebp);
        assembler.mov(ebp, esp);

        Register[] savedRegisters = GetModifiedNonVolatileRegisters();
        foreach (Register register in savedRegisters)
            assembler.push(new AssemblerRegister32(register));

        IEnumerable<X86Signature.X86Parameter> pushOrder =
            _signature.Options.ArgumentOrder == X86ArgumentOrder.RightToLeft
                ? Enumerable.Reverse(_signature.Parameters)
                : _signature.Parameters;

        foreach (X86Signature.X86Parameter parameter in pushOrder)
        {
            if (parameter.NativeRegister is not null)
                continue;

            // Chunks within one by-value argument are also pushed high-to-low so its in-memory representation remains low-to-high at the callee.
            for (int chunk = parameter.StackSize - 4; chunk >= 0; chunk -= 4)
                assembler.push(__dword_ptr[ebp + parameter.ManagedStackOffset + chunk]);
        }

        // Keep EBP as the stable source base until every other register argument has been loaded.
        foreach (X86Signature.X86Parameter parameter in _signature.Parameters)
        {
            if (parameter.NativeRegister is not Register register || register == Register.EBP)
                continue;

            assembler.mov(new AssemblerRegister32(register), __dword_ptr[ebp + parameter.ManagedStackOffset]);
        }

        X86Signature.X86Parameter? ebpArgument = _signature.Parameters.FirstOrDefault(
            parameter => parameter.NativeRegister == Register.EBP);
        if (ebpArgument is not null)
            assembler.mov(ebp, __dword_ptr[ebp + ebpArgument.ManagedStackOffset]);

        assembler.call(trampolineAddress);
        if (_signature.Options.StackCleanup == X86StackCleanup.Caller && _signature.NativeStackBytes != 0)
            assembler.add(esp, _signature.NativeStackBytes);

        // Convert a custom native scalar result before restoring a nonvolatile result register.
        if (_signature.ReturnRegister is Register returnRegister && returnRegister != Register.EAX)
            assembler.mov(eax, new AssemblerRegister32(returnRegister));

        for (int index = savedRegisters.Length - 1; index >= 0; index--)
            assembler.pop(new AssemblerRegister32(savedRegisters[index]));
        assembler.pop(ebp);
        assembler.ret();
        return assembler;
    }

    private Register[] GetModifiedNonVolatileRegisters()
    {
        HashSet<Register> registers = [];
        foreach (X86Signature.X86Parameter parameter in _signature.Parameters)
        {
            if (parameter.NativeRegister is Register.EBX or Register.ESI or Register.EDI)
                registers.Add(parameter.NativeRegister.Value);
        }

        if (_signature.ReturnRegister is Register.EBX or Register.ESI or Register.EDI)
            registers.Add(_signature.ReturnRegister.Value);

        Register[] stableOrder = [Register.EBX, Register.ESI, Register.EDI];
        return stableOrder.Where(registers.Contains).ToArray();
    }

    private static UInt32 ToX86Address(IntPtr address, string parameterName)
    {
        UInt64 value = unchecked((UInt32)address.ToInt32());
        if (address == IntPtr.Zero)
            throw new ArgumentOutOfRangeException(parameterName, "An intermediary destination cannot be zero.");
        return (UInt32)value;
    }

    private static int EstimateCapacity(int stackBytes)
        => checked(256 + stackBytes * 2);
}
