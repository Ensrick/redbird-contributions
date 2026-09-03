using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RedBird.Extensions.CallingConventions.NativeX64;

internal sealed class X64ManagedAbiPlan
{
    private X64ManagedAbiPlan(bool isWindows, ManagedArgumentLocation[] parameters, ManagedReturnLocation returnValue, int outgoingStackBytes)
    {
        IsWindows = isWindows;
        Parameters = parameters;
        ReturnValue = returnValue;
        OutgoingStackBytes = outgoingStackBytes;
    }

    public bool IsWindows { get; }
    public ManagedArgumentLocation[] Parameters { get; }
    public ManagedReturnLocation ReturnValue { get; }
    public int OutgoingStackBytes { get; }

    public static X64ManagedAbiPlan Create(X64VectorcallSignature signature)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return CreateWindows(signature);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return CreateSystemV(signature);

        throw new PlatformNotSupportedException("x64 vectorcall intermediary stubs are supported on Windows and System V Linux.");
    }

    private static X64ManagedAbiPlan CreateWindows(X64VectorcallSignature signature)
    {
        ManagedReturnLocation returnValue = CreateWindowsReturn(signature.ReturnValue);
        int positionShift = returnValue.Kind == ManagedReturnKind.HiddenBuffer ? 1 : 0;
        ManagedArgumentLocation[] locations = new ManagedArgumentLocation[signature.Parameters.Length];

        for (int index = 0; index < locations.Length; index++)
        {
            X64VectorcallSignature.X64VectorcallParameter parameter = signature.Parameters[index];
            int position = checked(index + positionShift);

            if (parameter.IsFloatingPoint)
            {
                locations[index] = position < 4
                    ? ManagedArgumentLocation.VectorRegisters([position], [parameter.NativeSize])
                    : ManagedArgumentLocation.StackValue(WindowsOutputStackOffset(position));
            }
            else if (RequiresWindowsIndirect(parameter))
            {
                locations[index] = position < 4
                    ? ManagedArgumentLocation.IndirectIntegerRegister(position)
                    : ManagedArgumentLocation.IndirectStack(WindowsOutputStackOffset(position));
            }
            else
            {
                locations[index] = position < 4
                    ? ManagedArgumentLocation.IntegerRegister(position)
                    : ManagedArgumentLocation.StackValue(WindowsOutputStackOffset(position));
            }
        }

        int logicalArgumentCount = checked(signature.Parameters.Length + positionShift);
        int outgoingBytes = checked(32 + Math.Max(logicalArgumentCount - 4, 0) * 8);
        return new X64ManagedAbiPlan(true, locations, returnValue, outgoingBytes);
    }

    private static ManagedReturnLocation CreateWindowsReturn(X64VectorcallSignature.X64VectorcallReturn returnValue)
    {
        if (!returnValue.IsHva)
            return ManagedReturnLocation.Unchanged;

        return returnValue.NativeSize <= 8
            ? new ManagedReturnLocation(ManagedReturnKind.IntegerRegister, [])
            : new ManagedReturnLocation(ManagedReturnKind.HiddenBuffer, []);
    }

    private static bool RequiresWindowsIndirect(X64VectorcallSignature.X64VectorcallParameter parameter)
    {
        if (parameter.IsVector)
            return true;
        if (parameter.IsHva)
            return parameter.NativeSize > 8;
        return parameter.IsAggregate && parameter.NativeSize > 8;
    }

    private static X64ManagedAbiPlan CreateSystemV(X64VectorcallSignature signature)
    {
        ManagedReturnLocation returnValue = CreateSystemVReturn(signature.ReturnValue);
        int integerCursor = returnValue.Kind == ManagedReturnKind.HiddenBuffer ? 1 : 0;
        int vectorCursor = 0;
        ManagedArgumentLocation[] locations = new ManagedArgumentLocation[signature.Parameters.Length];
        List<int> stackParameters = [];

        for (int index = 0; index < locations.Length; index++)
        {
            X64VectorcallSignature.X64VectorcallParameter parameter = signature.Parameters[index];
            if (parameter.IsFloatingPoint)
            {
                if (vectorCursor < 8)
                    locations[index] = ManagedArgumentLocation.VectorRegisters([vectorCursor++], [parameter.NativeSize]);
                else
                    stackParameters.Add(index);
                continue;
            }

            if (parameter.IsVector || parameter.IsHva)
            {
                int[] chunks = GetSystemVVectorChunks(parameter.Type, parameter.NativeSize);
                if (chunks.Length != 0 && vectorCursor + chunks.Length <= 8)
                {
                    int[] registers = new int[chunks.Length];
                    for (int register = 0; register < registers.Length; register++)
                        registers[register] = vectorCursor++;
                    locations[index] = ManagedArgumentLocation.VectorRegisters(registers, chunks);
                }
                else
                {
                    stackParameters.Add(index);
                }
                continue;
            }

            if (parameter.IsAggregate)
            {
                throw new NotSupportedException(
                    $"System V classification for unannotated aggregate argument {index} ({parameter.Type.FullName}) is ambiguous. " +
                    "Annotate homogeneous aggregates with VectorcallHvaAttribute, SIMD values with " +
                    "VectorcallArgumentAttribute, or pass the value by pointer.");
            }

            if (integerCursor < 6)
                locations[index] = ManagedArgumentLocation.IntegerRegister(integerCursor++);
            else
                stackParameters.Add(index);
        }

        int stackCursor = 0;
        foreach (int index in stackParameters)
        {
            X64VectorcallSignature.X64VectorcallParameter parameter = signature.Parameters[index];
            int alignment = GetSystemVStackAlignment(parameter);
            stackCursor = Align(stackCursor, alignment);
            locations[index] = ManagedArgumentLocation.StackValue(stackCursor);
            stackCursor = checked(stackCursor + Align(parameter.NativeSize, 8));
        }

        return new X64ManagedAbiPlan(false, locations, returnValue, Align(stackCursor, 16));
    }

    private static ManagedReturnLocation CreateSystemVReturn(X64VectorcallSignature.X64VectorcallReturn returnValue)
    {
        if (!returnValue.IsHva)
            return ManagedReturnLocation.Unchanged;

        int[] chunks = GetSystemVVectorChunks(returnValue.Type, returnValue.NativeSize);
        if (chunks.Length == 0)
            return new ManagedReturnLocation(ManagedReturnKind.HiddenBuffer, []);

        int[] registers = new int[chunks.Length];
        for (int index = 0; index < registers.Length; index++)
            registers[index] = index;
        return new ManagedReturnLocation(ManagedReturnKind.VectorRegisters, registers, chunks);
    }

    internal static int[] GetSystemVVectorChunks(Type type, int nativeSize)
    {
        if (IsIntrinsicVector(type, nativeSize))
            return [nativeSize];
        if (nativeSize > 16)
            return [];

        List<ScalarField> fields = [];
        if (!TryFlattenScalarFields(type, 0, fields) || fields.Count == 0)
            return [];

        Type scalarType = fields[0].Type;
        if (scalarType != typeof(float) && scalarType != typeof(double))
            return [];
        int scalarSize = scalarType == typeof(float) ? 4 : 8;
        if (fields.Count * scalarSize != nativeSize)
            return [];
        for (int index = 0; index < fields.Count; index++)
        {
            if (fields[index].Type != scalarType || fields[index].Offset != index * scalarSize)
                return [];
        }

        return nativeSize switch
        {
            4 => [4],
            8 => [8],
            12 => [8, 4],
            16 => [8, 8],
            _ => [],
        };
    }

    private static bool TryFlattenScalarFields(Type type, int baseOffset, List<ScalarField> destination)
    {
        if (type == typeof(float) || type == typeof(double))
        {
            destination.Add(new ScalarField(type, baseOffset));
            return true;
        }
        if (!type.IsValueType)
            return false;

        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Array.Sort(fields, (left, right) => GetFieldOffset(type, left).CompareTo(GetFieldOffset(type, right)));
        foreach (FieldInfo field in fields)
        {
            int fieldOffset;
            try
            {
                fieldOffset = checked(baseOffset + (int)Marshal.OffsetOf(type, field.Name));
            }
            catch
            {
                return false;
            }
            if (!TryFlattenScalarFields(field.FieldType, fieldOffset, destination))
                return false;
        }
        return true;
    }

    private static int GetFieldOffset(Type owner, FieldInfo field)
    {
        try
        {
            return checked((int)Marshal.OffsetOf(owner, field.Name));
        }
        catch
        {
            return int.MaxValue;
        }
    }

    private static bool IsIntrinsicVector(Type type, int nativeSize)
    {
        if (!type.IsGenericType)
            return false;

        string? name = type.GetGenericTypeDefinition().FullName;
        return (nativeSize == 16 && name == "System.Runtime.Intrinsics.Vector128`1") ||
               (nativeSize == 32 && name == "System.Runtime.Intrinsics.Vector256`1");
    }

    private static int GetSystemVStackAlignment(X64VectorcallSignature.X64VectorcallParameter parameter)
    {
        if (parameter.IsVector)
            return parameter.NativeSize;
        if (parameter.IsHva && parameter.ElementSize is 16 or 32)
            return parameter.ElementSize;
        return 8;
    }

    private static int WindowsOutputStackOffset(int position)
        => checked(32 + (position - 4) * 8);

    private static int Align(int value, int alignment)
        => checked((value + alignment - 1) & ~(alignment - 1));

    internal readonly struct ManagedArgumentLocation
    {
        private ManagedArgumentLocation(
            ManagedArgumentKind kind,
            int[] registers,
            int[] vectorChunkSizes,
            int stackOffset)
        {
            Kind = kind;
            Registers = registers;
            VectorChunkSizes = vectorChunkSizes;
            StackOffset = stackOffset;
        }

        public ManagedArgumentKind Kind { get; }
        public int[] Registers { get; }
        public int[] VectorChunkSizes { get; }
        public int StackOffset { get; }

        public static ManagedArgumentLocation IntegerRegister(int register)
            => new(ManagedArgumentKind.IntegerRegister, [register], [], 0);

        public static ManagedArgumentLocation IndirectIntegerRegister(int register)
            => new(ManagedArgumentKind.IndirectIntegerRegister, [register], [], 0);

        public static ManagedArgumentLocation VectorRegisters(int[] registers, int[] vectorChunkSizes)
            => new(ManagedArgumentKind.VectorRegisters, registers, vectorChunkSizes, 0);

        public static ManagedArgumentLocation StackValue(int offset)
            => new(ManagedArgumentKind.StackValue, [], [], offset);

        public static ManagedArgumentLocation IndirectStack(int offset)
            => new(ManagedArgumentKind.IndirectStack, [], [], offset);
    }

    internal readonly struct ManagedReturnLocation
    {
        public static ManagedReturnLocation Unchanged { get; } = new(ManagedReturnKind.Unchanged, [], []);

        public ManagedReturnLocation(ManagedReturnKind kind, int[] registers, int[]? vectorChunkSizes = null)
        {
            Kind = kind;
            Registers = registers;
            VectorChunkSizes = vectorChunkSizes ?? [];
        }

        public ManagedReturnKind Kind { get; }
        public int[] Registers { get; }
        public int[] VectorChunkSizes { get; }
    }

    internal enum ManagedArgumentKind
    {
        IntegerRegister,
        IndirectIntegerRegister,
        VectorRegisters,
        StackValue,
        IndirectStack,
    }

    internal enum ManagedReturnKind
    {
        Unchanged,
        IntegerRegister,
        VectorRegisters,
        HiddenBuffer,
    }

    private readonly struct ScalarField
    {
        public ScalarField(Type type, int offset)
        {
            Type = type;
            Offset = offset;
        }

        public Type Type { get; }
        public int Offset { get; }
    }
}
