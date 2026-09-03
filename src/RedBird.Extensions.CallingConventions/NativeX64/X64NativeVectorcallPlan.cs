using System;

namespace RedBird.Extensions.CallingConventions.NativeX64;

internal sealed class X64NativeVectorcallPlan
{
    private X64NativeVectorcallPlan(bool isWindows, NativeArgumentLocation[] parameters, NativeReturnLocation returnValue, int outgoingStackBytes)
    {
        IsWindows = isWindows;
        Parameters = parameters;
        ReturnValue = returnValue;
        OutgoingStackBytes = outgoingStackBytes;
    }

    public bool IsWindows { get; }
    public NativeArgumentLocation[] Parameters { get; }
    public NativeReturnLocation ReturnValue { get; }
    public int OutgoingStackBytes { get; }

    public static X64NativeVectorcallPlan Create(X64VectorcallSignature signature, bool isWindows)
        => isWindows ? CreateWindows(signature) : CreateLinux(signature);

    private static X64NativeVectorcallPlan CreateWindows(X64VectorcallSignature signature)
    {
        NativeArgumentLocation[] locations = new NativeArgumentLocation[signature.Parameters.Length];
        bool[] usedVectorRegisters = new bool[6];

        foreach (X64VectorcallSignature.X64VectorcallParameter parameter in signature.Parameters)
        {
            int position = parameter.Index;
            if (parameter.IsFloatingPoint)
            {
                if (position < 6)
                {
                    locations[position] = NativeArgumentLocation.VectorFragments(
                        position,
                        [new NativeVectorFragment(0, parameter.NativeSize, position, -1)]);
                    usedVectorRegisters[position] = true;
                }
                else
                {
                    locations[position] = NativeArgumentLocation.VectorFragments(
                        position,
                        [new NativeVectorFragment(0, parameter.NativeSize, -1, WindowsStackOffset(position))]);
                }
            }
            else if (parameter.IsVector)
            {
                if (position < 6)
                {
                    locations[position] = NativeArgumentLocation.VectorFragments(
                        position,
                        [new NativeVectorFragment(0, parameter.NativeSize, position, -1)]);
                    usedVectorRegisters[position] = true;
                }
                else
                {
                    locations[position] = NativeArgumentLocation.Indirect(position, WindowsStackOffset(position));
                }
            }
            else if (!parameter.IsHva)
            {
                locations[position] = parameter.IsAggregate && parameter.NativeSize > 8
                    ? NativeArgumentLocation.Indirect(position, position < 4 ? -1 : WindowsStackOffset(position))
                    : NativeArgumentLocation.Integer(position, position < 4 ? -1 : WindowsStackOffset(position));
            }
        }

        // Microsoft vectorcall allocates all positional scalar/vector values first. HVAs then consume any remaining XMM/YMM registers in ascending order, including gaps.
        foreach (X64VectorcallSignature.X64VectorcallParameter parameter in signature.Parameters)
        {
            if (!parameter.IsHva)
                continue;

            int free = 0;
            foreach (bool used in usedVectorRegisters)
            {
                if (!used)
                    free++;
            }

            if (free < parameter.ElementCount)
            {
                locations[parameter.Index] = NativeArgumentLocation.Indirect(
                    parameter.Index,
                    parameter.Index < 4 ? -1 : WindowsStackOffset(parameter.Index));
                continue;
            }

            NativeVectorFragment[] fragments = new NativeVectorFragment[parameter.ElementCount];
            int destination = 0;
            for (int register = 0; register < usedVectorRegisters.Length && destination < fragments.Length; register++)
            {
                if (usedVectorRegisters[register])
                    continue;

                fragments[destination] = new NativeVectorFragment(
                    destination * parameter.ElementSize,
                    parameter.ElementSize,
                    register,
                    -1);
                usedVectorRegisters[register] = true;
                destination++;
            }
            locations[parameter.Index] = NativeArgumentLocation.VectorFragments(parameter.Index, fragments);
        }

        int outgoingBytes = checked(32 + Math.Max(signature.Parameters.Length - 4, 0) * 8);
        NativeVectorFragment[] returnFragments = new NativeVectorFragment[signature.ReturnValue.ElementCount];
        for (int element = 0; element < returnFragments.Length; element++)
        {
            returnFragments[element] = new NativeVectorFragment(
                element * signature.ReturnValue.ElementSize,
                signature.ReturnValue.ElementSize,
                element,
                -1);
        }
        NativeReturnLocation returnValue = signature.ReturnValue.IsHva
            ? NativeReturnLocation.VectorFragments(returnFragments)
            : NativeReturnLocation.Unchanged;
        return new X64NativeVectorcallPlan(true, locations, returnValue, outgoingBytes);
    }

    private static X64NativeVectorcallPlan CreateLinux(X64VectorcallSignature signature)
    {
        NativeReturnLocation returnValue = CreateLinuxReturn(signature.ReturnValue);
        int position = returnValue.Kind == NativeReturnKind.HiddenBuffer ? 1 : 0;
        NativeArgumentLocation[] locations = new NativeArgumentLocation[signature.Parameters.Length];

        foreach (X64VectorcallSignature.X64VectorcallParameter parameter in signature.Parameters)
        {
            int startPosition = position;
            if (parameter.IsFloatingPoint)
            {
                locations[parameter.Index] = NativeArgumentLocation.VectorFragments(
                    startPosition,
                    [CreateLinuxVectorFragment(0, parameter.NativeSize, position++)]);
                continue;
            }

            if (parameter.IsVector)
            {
                if (position < 6)
                {
                    locations[parameter.Index] = NativeArgumentLocation.VectorFragments(
                        startPosition,
                        [CreateLinuxVectorFragment(0, parameter.NativeSize, position++)]);
                }
                else
                {
                    locations[parameter.Index] = NativeArgumentLocation.Indirect(
                        position,
                        LinuxStackOffset(position));
                    position++;
                }
                continue;
            }

            if (parameter.IsHva)
            {
                int[] chunks = GetLinuxNativeHvaChunks(parameter.ElementSize, parameter.ElementCount);
                bool needsIndirect = chunks.Length == 0 ||
                                     (chunks.Length == 1 && chunks[0] > 8 && position >= 6);
                if (needsIndirect)
                {
                    locations[parameter.Index] = NativeArgumentLocation.Indirect(
                        position,
                        position < 4 ? -1 : LinuxStackOffset(position));
                    position++;
                    continue;
                }

                NativeVectorFragment[] fragments = new NativeVectorFragment[chunks.Length];
                int canonicalOffset = 0;
                for (int chunk = 0; chunk < chunks.Length; chunk++)
                {
                    fragments[chunk] = CreateLinuxVectorFragment(canonicalOffset, chunks[chunk], position++);
                    canonicalOffset += chunks[chunk];
                }
                locations[parameter.Index] = NativeArgumentLocation.VectorFragments(startPosition, fragments);
                continue;
            }

            bool indirect = parameter.IsAggregate && parameter.NativeSize > 8;
            locations[parameter.Index] = indirect
                ? NativeArgumentLocation.Indirect(position, position < 4 ? -1 : LinuxStackOffset(position))
                : NativeArgumentLocation.Integer(position, position < 4 ? -1 : LinuxStackOffset(position));
            position++;
        }

        int outgoingBytes = position <= 4 ? 0 : checked((position - 4) * 8);
        return new X64NativeVectorcallPlan(false, locations, returnValue, outgoingBytes);
    }

    private static NativeReturnLocation CreateLinuxReturn(X64VectorcallSignature.X64VectorcallReturn returnValue)
    {
        if (!returnValue.IsHva)
            return NativeReturnLocation.Unchanged;

        int[] chunks = GetLinuxNativeHvaChunks(returnValue.ElementSize, returnValue.ElementCount);
        if (chunks.Length == 0)
            return NativeReturnLocation.HiddenBuffer;

        NativeVectorFragment[] fragments = new NativeVectorFragment[chunks.Length];
        int canonicalOffset = 0;
        for (int index = 0; index < chunks.Length; index++)
        {
            fragments[index] = new NativeVectorFragment(canonicalOffset, chunks[index], index, -1);
            canonicalOffset += chunks[index];
        }
        return NativeReturnLocation.VectorFragments(fragments);
    }

    private static int[] GetLinuxNativeHvaChunks(int elementSize, int elementCount)
    {
        // Clang's ELF vectorcall lowering uses System V eightbyte packing for scalar HFAs,
        // while a single genuine SIMD member remains one XMM/YMM value. Aggregates containing
        // multiple SIMD members exceed System V's two-eightbyte aggregate limit and are indirect.
        return elementSize switch
        {
            4 => elementCount switch
            {
                1 => [4],
                2 => [8],
                3 => [8, 4],
                4 => [8, 8],
                _ => [],
            },
            8 => elementCount switch
            {
                1 => [8],
                2 => [8, 8],
                _ => [],
            },
            16 => elementCount == 1 ? [16] : [],
            32 => elementCount == 1 ? [32] : [],
            _ => [],
        };
    }

    private static NativeVectorFragment CreateLinuxVectorFragment(int canonicalOffset, int size, int position)
        => position < 6
            ? new NativeVectorFragment(canonicalOffset, size, position, -1)
            : new NativeVectorFragment(canonicalOffset, size, -1, LinuxStackOffset(position));

    private static int WindowsStackOffset(int position)
        => checked(32 + (position - 4) * 8);

    private static int LinuxStackOffset(int position)
        => checked((position - 4) * 8);

    internal readonly struct NativeArgumentLocation
    {
        private NativeArgumentLocation(
            NativeArgumentKind kind,
            int position,
            int stackOffset,
            NativeVectorFragment[] fragments)
        {
            Kind = kind;
            Position = position;
            StackOffset = stackOffset;
            Fragments = fragments;
        }

        public NativeArgumentKind Kind { get; }
        public int Position { get; }
        public int StackOffset { get; }
        public NativeVectorFragment[] Fragments { get; }

        public static NativeArgumentLocation Integer(int position, int stackOffset)
            => new(NativeArgumentKind.Integer, position, stackOffset, []);

        public static NativeArgumentLocation Indirect(int position, int stackOffset)
            => new(NativeArgumentKind.Indirect, position, stackOffset, []);

        public static NativeArgumentLocation VectorFragments(int position, NativeVectorFragment[] fragments)
            => new(NativeArgumentKind.VectorFragments, position, -1, fragments);
    }

    internal readonly struct NativeVectorFragment
    {
        public NativeVectorFragment(int canonicalOffset, int size, int register, int stackOffset)
        {
            CanonicalOffset = canonicalOffset;
            Size = size;
            Register = register;
            StackOffset = stackOffset;
        }

        public int CanonicalOffset { get; }
        public int Size { get; }
        public int Register { get; }
        public int StackOffset { get; }
        public bool IsRegister => Register >= 0;
    }

    internal readonly struct NativeReturnLocation
    {
        public static NativeReturnLocation Unchanged { get; } = new(NativeReturnKind.Unchanged, []);
        public static NativeReturnLocation HiddenBuffer { get; } = new(NativeReturnKind.HiddenBuffer, []);

        private NativeReturnLocation(NativeReturnKind kind, NativeVectorFragment[] fragments)
        {
            Kind = kind;
            Fragments = fragments;
        }

        public NativeReturnKind Kind { get; }
        public NativeVectorFragment[] Fragments { get; }

        public static NativeReturnLocation VectorFragments(NativeVectorFragment[] fragments)
            => new(NativeReturnKind.VectorFragments, fragments);
    }

    internal enum NativeArgumentKind
    {
        Integer,
        Indirect,
        VectorFragments,
    }

    internal enum NativeReturnKind
    {
        Unchanged,
        VectorFragments,
        HiddenBuffer,
    }
}
