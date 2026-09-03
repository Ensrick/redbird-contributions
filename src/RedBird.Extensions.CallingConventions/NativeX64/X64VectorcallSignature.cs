using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RedBird.Extensions.CallingConventions.NativeX64;

internal sealed class X64VectorcallSignature
{
    private const int MaximumParameters = 512;

    private X64VectorcallSignature(X64VectorcallParameter[] parameters, X64VectorcallReturn returnValue)
    {
        Parameters = parameters;
        ReturnValue = returnValue;
    }

    public X64VectorcallParameter[] Parameters { get; }
    public X64VectorcallReturn ReturnValue { get; }

    public static X64VectorcallSignature Create<TDelegate>() where TDelegate : Delegate
    {
        Type delegateType = typeof(TDelegate);
        MethodInfo invoke = delegateType.GetMethod("Invoke")
            ?? throw new ArgumentException($"{delegateType.FullName} is not an invokable delegate type.");
        ParameterInfo[] reflected = invoke.GetParameters();
        if (reflected.Length > MaximumParameters)
            throw new NotSupportedException($"x64 vectorcall adapters support at most {MaximumParameters} parameters.");

        X64VectorcallParameter[] parameters = new X64VectorcallParameter[reflected.Length];
        for (int index = 0; index < reflected.Length; index++)
            parameters[index] = CreateParameter(index, reflected[index]);

        return new X64VectorcallSignature(parameters, CreateReturn(invoke));
    }

    private static X64VectorcallParameter CreateParameter(int index, ParameterInfo parameter)
    {
        VectorcallArgumentAttribute? vector = parameter.GetCustomAttribute<VectorcallArgumentAttribute>();
        VectorcallHvaAttribute? hva = parameter.GetCustomAttribute<VectorcallHvaAttribute>();
        if (vector is not null && hva is not null)
            throw new ArgumentException($"Argument '{parameter.Name}' cannot be both a SIMD vector and an HVA.");

        if (vector is not null)
        {
            ValidateByValue(parameter, "vector");
            ValidateSizedValue(parameter.ParameterType, vector.Size, parameter.Name ?? $"argument {index}");
            return new X64VectorcallParameter(index, parameter.ParameterType, vector.Size == 16 ? X64VectorcallValueKind.Vector128 : X64VectorcallValueKind.Vector256, vector.Size);
        }

        if (hva is not null)
        {
            ValidateByValue(parameter, "HVA");
            ValidateHva(parameter.ParameterType, hva, parameter.Name ?? $"argument {index}");
            return new X64VectorcallParameter(index, parameter.ParameterType, X64VectorcallValueKind.Hva, checked(hva.ElementSize * hva.ElementCount), hva.ElementSize, hva.ElementCount);
        }

        Type type = parameter.ParameterType;
        if (type == typeof(float))
            return new X64VectorcallParameter(index, type, X64VectorcallValueKind.Float32, 4);
        if (type == typeof(double))
            return new X64VectorcallParameter(index, type, X64VectorcallValueKind.Float64, 8);

        return new X64VectorcallParameter(index, type, X64VectorcallValueKind.IntegerOrPointer, GetNativeSize(type));
    }

    private static X64VectorcallReturn CreateReturn(MethodInfo invoke)
    {
        ParameterInfo parameter = invoke.ReturnParameter;
        VectorcallArgumentAttribute? vector = parameter.GetCustomAttribute<VectorcallArgumentAttribute>();
        VectorcallHvaAttribute? hva = parameter.GetCustomAttribute<VectorcallHvaAttribute>();
        if (vector is not null && hva is not null)
            throw new ArgumentException("The return value cannot be both a SIMD vector and an HVA.");

        if (hva is not null)
        {
            ValidateHva(invoke.ReturnType, hva, "return value");
            return new X64VectorcallReturn(invoke.ReturnType, X64VectorcallValueKind.Hva, checked(hva.ElementSize * hva.ElementCount), hva.ElementSize, hva.ElementCount);
        }

        if (vector is not null)
        {
            ValidateSizedValue(invoke.ReturnType, vector.Size, "return value");
            throw new NotSupportedException("A raw by-value SIMD return cannot be represented portably by a managed delegate. Wrap it in a one-member HVA or use an explicit result pointer.");
        }

        ValidateOrdinaryReturn(invoke.ReturnType);
        return new X64VectorcallReturn(invoke.ReturnType, ClassifyScalar(invoke.ReturnType), GetNativeSize(invoke.ReturnType));
    }

    private static X64VectorcallValueKind ClassifyScalar(Type type)
        => type == typeof(float)
            ? X64VectorcallValueKind.Float32
            : type == typeof(double)
                ? X64VectorcallValueKind.Float64
                : X64VectorcallValueKind.IntegerOrPointer;

    private static void ValidateByValue(ParameterInfo parameter, string classification)
    {
        if (parameter.ParameterType.IsByRef || parameter.IsOut)
            throw new ArgumentException($"Vectorcall {classification} argument '{parameter.Name}' must be passed by value.");
    }

    private static void ValidateHva(Type type, VectorcallHvaAttribute hva, string owner)
    {
        int expectedSize = checked(hva.ElementSize * hva.ElementCount);
        ValidateSizedValue(type, expectedSize, owner);

        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fields.Length != hva.ElementCount)
        {
            throw new ArgumentException($"Vectorcall HVA {owner} declares {hva.ElementCount} members, but its native type has {fields.Length} instance fields.");
        }

        try
        {
            Array.Sort(
                fields,
                (left, right) => ((int)Marshal.OffsetOf(type, left.Name)).CompareTo((int)Marshal.OffsetOf(type, right.Name)));
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"Vectorcall HVA {owner} has no stable native field layout.", exception);
        }

        for (int index = 0; index < fields.Length; index++)
        {
            FieldInfo field = fields[index];
            int fieldSize = GetNativeSize(field.FieldType);
            int fieldOffset;
            try
            {
                fieldOffset = checked((int)Marshal.OffsetOf(type, field.Name));
            }
            catch (Exception exception)
            {
                throw new ArgumentException($"Vectorcall HVA {owner} has no stable native field layout.", exception);
            }

            if (fieldSize != hva.ElementSize || fieldOffset != index * hva.ElementSize)
            {
                throw new ArgumentException($"Vectorcall HVA {owner} must contain {hva.ElementCount} consecutive {hva.ElementSize}-byte members.");
            }
            if ((hva.ElementSize == 4 && field.FieldType != typeof(float)) ||
                (hva.ElementSize == 8 && field.FieldType != typeof(double)))
            {
                throw new ArgumentException($"Vectorcall HVA {owner} must use {(hva.ElementSize == 4 ? "Single" : "Double")} members.");
            }
        }
    }

    private static void ValidateSizedValue(Type type, int expectedSize, string owner)
    {
        if (!type.IsValueType || type.IsByRef)
            throw new ArgumentException($"Vectorcall {owner} must be a blittable value type.");

        int actualSize = GetNativeSize(type);
        if (actualSize != expectedSize)
        {
            throw new ArgumentException($"Vectorcall {owner} is marked as {expectedSize} bytes but its native layout is {actualSize} bytes.");
        }
    }

    private static int GetNativeSize(Type type)
    {
        if (type == typeof(void))
            return 0;
        if (type.IsByRef || type.IsPointer || !type.IsValueType || type == typeof(IntPtr) || type == typeof(UIntPtr))
            return 8;

        try
        {
            return Marshal.SizeOf(type.IsEnum ? Enum.GetUnderlyingType(type) : type);
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"Type '{type.FullName}' has no marshalable native layout.", exception);
        }
    }

    private static void ValidateOrdinaryReturn(Type type)
    {
        if (type == typeof(void) || type.IsByRef || type.IsPointer || !type.IsValueType ||
            type.IsPrimitive || type.IsEnum || type == typeof(IntPtr) || type == typeof(UIntPtr))
            return;

        throw new NotSupportedException("By-value aggregate returns must be annotated with VectorcallHvaAttribute or replaced by an explicit result pointer.");
    }

    internal sealed class X64VectorcallParameter
    {
        public X64VectorcallParameter(int index, Type type, X64VectorcallValueKind kind, int nativeSize, int elementSize = 0, int elementCount = 0)
        {
            Index = index;
            Type = type;
            Kind = kind;
            NativeSize = nativeSize;
            ElementSize = elementSize;
            ElementCount = elementCount;
        }

        public int Index { get; }
        public Type Type { get; }
        public X64VectorcallValueKind Kind { get; }
        public int NativeSize { get; }
        public int ElementSize { get; }
        public int ElementCount { get; }
        public bool IsFloatingPoint => Kind is X64VectorcallValueKind.Float32 or X64VectorcallValueKind.Float64;
        public bool IsVector => Kind is X64VectorcallValueKind.Vector128 or X64VectorcallValueKind.Vector256;
        public bool IsDirectVectorValue => IsFloatingPoint || IsVector;
        public bool IsHva => Kind == X64VectorcallValueKind.Hva;
        public bool IsAggregate => Type.IsValueType && !Type.IsPrimitive && !Type.IsEnum &&
                                   Type != typeof(IntPtr) && Type != typeof(UIntPtr);
    }

    internal sealed class X64VectorcallReturn
    {
        public X64VectorcallReturn(Type type, X64VectorcallValueKind kind, int nativeSize, int elementSize = 0, int elementCount = 0)
        {
            Type = type;
            Kind = kind;
            NativeSize = nativeSize;
            ElementSize = elementSize;
            ElementCount = elementCount;
        }

        public Type Type { get; }
        public X64VectorcallValueKind Kind { get; }
        public int NativeSize { get; }
        public int ElementSize { get; }
        public int ElementCount { get; }
        public bool IsHva => Kind == X64VectorcallValueKind.Hva;
    }

    internal enum X64VectorcallValueKind
    {
        IntegerOrPointer,
        Float32,
        Float64,
        Vector128,
        Vector256,
        Hva,
    }
}
