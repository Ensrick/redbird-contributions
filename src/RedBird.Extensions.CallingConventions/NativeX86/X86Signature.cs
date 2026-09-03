using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.CallingConventions;

namespace RedBird.Extensions.CallingConventions.NativeX86;

internal sealed class X86Signature
{
    // Leaves room for the emitted instructions inside NativeMemoryManager's 64 KiB stub slab.
    private const int MaximumStackBytes = 32000;

    private X86Signature(X86CallingConventionOptions options, X86Parameter[] parameters, int managedStackBytes, int nativeStackBytes, Register? returnRegister)
    {
        Options = options;
        Parameters = parameters;
        ManagedStackBytes = managedStackBytes;
        NativeStackBytes = nativeStackBytes;
        ReturnRegister = returnRegister;
    }

    public X86CallingConventionOptions Options { get; }
    public X86Parameter[] Parameters { get; }
    public int ManagedStackBytes { get; }
    public int NativeStackBytes { get; }
    public Register? ReturnRegister { get; }

    public static X86Signature Create<TDelegate>(X86CallingConventionOptions? explicitOptions) where TDelegate : Delegate
    {
        Type delegateType = typeof(TDelegate);
        MethodInfo invoke = delegateType.GetMethod("Invoke")
            ?? throw new ArgumentException($"{delegateType.FullName} is not an invokable delegate type.");

        UnmanagedFunctionPointerAttribute? unmanaged = delegateType.GetCustomAttribute<UnmanagedFunctionPointerAttribute>();
        if (unmanaged is null || unmanaged.CallingConvention != CallingConvention.Cdecl)
        {
            throw new ArgumentException($"{delegateType.FullName} must declare [UnmanagedFunctionPointer(CallingConvention.Cdecl)]. The intermediary's managed-facing x86 ABI is cdecl.");
        }

        X86CallingConventionOptions options = explicitOptions ?? OptionsFromAttribute(delegateType);
        if (invoke.ReturnType != typeof(void) && !IsScalar(invoke.ReturnType))
        {
            throw new NotSupportedException("By-value aggregate returns are not supported by the x86 intermediary. Use an explicit result pointer so hidden return-buffer arguments cannot alter the ABI layout.");
        }

        ParameterInfo[] reflected = invoke.GetParameters();
        X86Parameter[] parameters = new X86Parameter[reflected.Length];

        int managedOffset = 8;
        for (int index = 0; index < reflected.Length; index++)
        {
            ParameterInfo parameter = reflected[index];
            int valueSize = GetNativeValueSize(parameter.ParameterType);
            int stackSize = Align4(Math.Max(valueSize, 4));
            parameters[index] = new X86Parameter(index, parameter, valueSize, stackSize, managedOffset);
            managedOffset = checked(managedOffset + stackSize);
        }

        AssignRegisters(options, parameters);
        int nativeStackBytes = AssignNativeStackOffsets(options.ArgumentOrder, parameters);
        int managedStackBytes = managedOffset - 8;

        if (managedStackBytes > MaximumStackBytes || nativeStackBytes > MaximumStackBytes)
            throw new NotSupportedException("x86 intermediary stack frames larger than 32000 bytes are not supported.");

        Register? returnRegister = ReadReturnRegister(options, invoke.ReturnParameter);
        return new X86Signature(options, parameters, managedStackBytes, nativeStackBytes, returnRegister);
    }

    private static X86CallingConventionOptions OptionsFromAttribute(Type delegateType)
    {
        X86CallingConventionAttribute attribute = delegateType.GetCustomAttribute<X86CallingConventionAttribute>()
            ?? throw new ArgumentException($"{delegateType.FullName} needs an [X86CallingConvention] attribute or explicit options.");

        return attribute.Convention == X86CallingConvention.Custom
            ? new X86CallingConventionOptions(attribute.StackCleanup, attribute.ArgumentOrder)
            : new X86CallingConventionOptions(attribute.Convention);
    }

    private static void AssignRegisters(X86CallingConventionOptions options, X86Parameter[] parameters)
    {
        bool hasExplicitMappings = parameters.Any(parameter => parameter.ExplicitRegister is not null);
        if (options.Convention != X86CallingConvention.Custom && hasExplicitMappings)
        {
            throw new ArgumentException("[X86RegisterArgument] can only be used with X86CallingConvention.Custom.");
        }

        switch (options.Convention)
        {
            case X86CallingConvention.Fastcall:
            {
                Register[] registers = [Register.ECX, Register.EDX];
                int next = 0;
                foreach (X86Parameter parameter in parameters)
                {
                    if (next == registers.Length)
                        break;
                    if (!parameter.IsFastcallRegisterEligible)
                        continue;

                    parameter.NativeRegister = registers[next++];
                }
                break;
            }
            case X86CallingConvention.Thiscall:
                if (parameters.Length == 0 || !parameters[0].IsFastcallRegisterEligible)
                {
                    throw new ArgumentException("An x86 thiscall signature must begin with a pointer-sized scalar 'this' argument.");
                }

                parameters[0].NativeRegister = Register.ECX;
                break;
            case X86CallingConvention.Custom:
            {
                HashSet<Register> used = [];
                foreach (X86Parameter parameter in parameters)
                {
                    if (parameter.ExplicitRegister is not Register register)
                        continue;

                    ValidateRegister(register, parameter.Parameter.Name ?? $"argument {parameter.Index}");
                    if (parameter.StackSize != 4 || !parameter.IsScalar)
                    {
                        throw new ArgumentException($"Custom register argument '{parameter.Parameter.Name}' must be a scalar no larger than 32 bits.");
                    }

                    if (!used.Add(register))
                        throw new ArgumentException($"Register {register} is assigned to more than one argument.");

                    parameter.NativeRegister = register;
                }
                break;
            }
        }
    }

    private static int AssignNativeStackOffsets(X86ArgumentOrder order, X86Parameter[] parameters)
    {
        IEnumerable<X86Parameter> memoryOrder = order == X86ArgumentOrder.RightToLeft
            ? parameters
            : System.Linq.Enumerable.Reverse(parameters);

        int offset = 8;
        foreach (X86Parameter parameter in memoryOrder)
        {
            if (parameter.NativeRegister is not null)
                continue;

            parameter.NativeStackOffset = offset;
            offset = checked(offset + parameter.StackSize);
        }

        return offset - 8;
    }

    private static Register? ReadReturnRegister(X86CallingConventionOptions options, ParameterInfo returnParameter)
    {
        X86RegisterArgumentAttribute? attribute = returnParameter.GetCustomAttribute<X86RegisterArgumentAttribute>();
        if (attribute is null)
            return null;

        if (options.Convention != X86CallingConvention.Custom)
            throw new ArgumentException("A custom return register can only be used with X86CallingConvention.Custom.");
        if (returnParameter.ParameterType == typeof(void))
            throw new ArgumentException("A void return value cannot have an x86 register mapping.");

        int size = GetNativeValueSize(returnParameter.ParameterType);
        if (size > 4 || !IsScalar(returnParameter.ParameterType))
            throw new ArgumentException("A custom x86 return register requires a scalar value no larger than 32 bits.");

        ValidateRegister(attribute.Register, "return value");
        return attribute.Register;
    }

    private static void ValidateRegister(Register register, string owner)
    {
        if (register is not (Register.EAX or Register.EBX or Register.ECX or Register.EDX or
            Register.ESI or Register.EDI or Register.EBP))
        {
            throw new ArgumentException($"The x86 register mapping for {owner} must use EAX, EBX, ECX, EDX, ESI, EDI, or EBP; got {register}.");
        }
    }

    private static int GetNativeValueSize(Type type)
    {
        if (type.IsByRef || type.IsPointer || !type.IsValueType)
            return 4;
        if (type == typeof(IntPtr) || type == typeof(UIntPtr))
            return 4;
        if (type.IsEnum)
            return Marshal.SizeOf(Enum.GetUnderlyingType(type));

        try
        {
            return Marshal.SizeOf(type);
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"Type {type.FullName} has no marshalable x86 layout.", exception);
        }
    }

    private static bool IsScalar(Type type)
    {
        if (type.IsByRef || type.IsPointer || !type.IsValueType)
            return true;
        return type.IsPrimitive || type.IsEnum || type == typeof(IntPtr) || type == typeof(UIntPtr);
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    internal sealed class X86Parameter
    {
        public X86Parameter(int index, ParameterInfo parameter, int valueSize, int stackSize, int managedStackOffset)
        {
            Index = index;
            Parameter = parameter;
            ValueSize = valueSize;
            StackSize = stackSize;
            ManagedStackOffset = managedStackOffset;
            ExplicitRegister = parameter.GetCustomAttribute<X86RegisterArgumentAttribute>()?.Register;
            IsScalar = X86Signature.IsScalar(parameter.ParameterType);
        }

        public int Index { get; }
        public ParameterInfo Parameter { get; }
        public int ValueSize { get; }
        public int StackSize { get; }
        public int ManagedStackOffset { get; }
        public int NativeStackOffset { get; set; }
        public Register? ExplicitRegister { get; }
        public Register? NativeRegister { get; set; }
        public bool IsScalar { get; }

        public bool IsFastcallRegisterEligible =>
            StackSize == 4 &&
            IsScalar &&
            Parameter.ParameterType != typeof(float) &&
            Parameter.ParameterType != typeof(double);
    }
}
