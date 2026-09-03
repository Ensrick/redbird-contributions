using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Extensions.CallingConventions.Internal;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.Extensions.CallingConventions.NativeX64;

/// <summary>
/// Translates between the managed platform x64 ABI and native <c>__vectorcall</c> in both detour directions.
/// </summary>
/// <remarks>
/// Windows uses the Microsoft x64 ABI for managed delegates; Linux uses the System V AMD64 ABI.
/// Scalar floating-point values, explicitly marked SIMD vectors, and explicitly marked homogeneous
/// vector aggregates are supported. Exceptions must not escape a callback because generated
/// intermediary code has no platform unwind metadata.
/// </remarks>
public sealed class X64VectorcallIntermediaryFactory<TDelegate> : IDetourIntermediaryFactory
    where TDelegate : Delegate
{
    private readonly X64VectorcallSignature _signature = X64VectorcallSignature.Create<TDelegate>();

    /// <inheritdoc/>
    public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
    {
        if (context.Architecture != DetourArchitecture.X64)
        {
            throw new NotSupportedException($"The x64 vectorcall factory cannot emit {context.Architecture} intermediary stubs.");
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
             !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)))
        {
            throw new PlatformNotSupportedException("x64 vectorcall translation requires an x64 Windows or Linux process.");
        }

        X64ManagedAbiPlan managedAbi = X64ManagedAbiPlan.Create(_signature);
        X64NativeVectorcallPlan nativeAbi = X64NativeVectorcallPlan.Create(_signature, managedAbi.IsWindows);
        FrameLayout inboundFrame = FrameLayout.Create(_signature, managedAbi.OutgoingStackBytes, managedAbi, nativeAbi);
        FrameLayout outboundFrame = FrameLayout.Create(_signature, nativeAbi.OutgoingStackBytes, managedAbi, nativeAbi);

        Assembler inbound = BuildAdapter((UInt64)context.CallbackAddress, managedAbi, nativeAbi, inboundFrame, vectorcallToManaged: true);
        Assembler outbound = BuildAdapter((UInt64)context.TrampolineAddress, managedAbi, nativeAbi, outboundFrame, vectorcallToManaged: false);

        int capacity = checked(4096 + _signature.Parameters.Length * 192 +
                               (inboundFrame.CanonicalBytes + outboundFrame.CanonicalBytes) * 8);
        IntPtr hookEntry = IntermediaryStubWriter.WriteX64(inbound, context.TargetAddress, capacity);
        IntPtr originalEntry = IntermediaryStubWriter.WriteX64(outbound, context.TargetAddress, capacity);
        return new DetourIntermediaryStubs(hookEntry, originalEntry);
    }

    private Assembler BuildAdapter(UInt64 destination, X64ManagedAbiPlan managedAbi, X64NativeVectorcallPlan nativeAbi, FrameLayout frame, bool vectorcallToManaged)
    {
        Assembler assembler = new(64);
        assembler.push(rbp);
        assembler.mov(rbp, rsp);
        if (frame.FrameSize != 0)
        {
            // System V requires 32-byte alignment when a spilled AVX value is present. Always aligning here also keeps every canonical SIMD slot naturally aligned without
            // making the signature planner depend on the caller's unknown modulo-32 position.
            assembler.sub(rsp, checked(frame.FrameSize + 31));
            assembler.and(rsp, -32);
        }

        if (vectorcallToManaged)
        {
            SaveNativeHiddenReturnBuffer(assembler, nativeAbi, frame);
            foreach (X64VectorcallSignature.X64VectorcallParameter parameter in _signature.Parameters)
                CaptureVectorcallArgument(assembler, nativeAbi, frame, parameter);

            SetManagedHiddenReturnBuffer(assembler, managedAbi, frame);
            foreach (X64VectorcallSignature.X64VectorcallParameter parameter in _signature.Parameters)
                PublishManagedArgument(assembler, managedAbi, frame, parameter);
        }
        else
        {
            SaveManagedHiddenReturnBuffer(assembler, managedAbi, frame);
            foreach (X64VectorcallSignature.X64VectorcallParameter parameter in _signature.Parameters)
                CaptureManagedArgument(assembler, managedAbi, frame, parameter);

            SetNativeHiddenReturnBuffer(assembler, nativeAbi, frame);
            foreach (X64VectorcallSignature.X64VectorcallParameter parameter in _signature.Parameters)
                PublishVectorcallArgument(assembler, nativeAbi, frame, parameter);
        }

        assembler.mov(r11, destination);
        assembler.call(r11);

        if (_signature.ReturnValue.IsHva)
        {
            if (vectorcallToManaged)
            {
                CaptureManagedHvaReturn(assembler, managedAbi, frame);
                PublishVectorcallHvaReturn(assembler, nativeAbi, frame);
            }
            else
            {
                CaptureVectorcallHvaReturn(assembler, nativeAbi, frame);
                PublishManagedHvaReturn(assembler, managedAbi, frame);
            }
        }

        assembler.mov(rsp, rbp);
        assembler.pop(rbp);
        assembler.ret();
        return assembler;
    }

    private static void CaptureVectorcallArgument(Assembler assembler, X64NativeVectorcallPlan plan, FrameLayout frame, X64VectorcallSignature.X64VectorcallParameter parameter)
    {
        int destination = frame.ParameterOffsets[parameter.Index];
        X64NativeVectorcallPlan.NativeArgumentLocation location = plan.Parameters[parameter.Index];
        switch (location.Kind)
        {
            case X64NativeVectorcallPlan.NativeArgumentKind.Integer:
                if (location.Position < 4)
                    assembler.mov(__qword_ptr[rsp + destination], NativeIntegerRegisters[location.Position]);
                else
                    CopyMemory(assembler, rbp, location.StackOffset + 16, rsp, destination, 8);
                return;

            case X64NativeVectorcallPlan.NativeArgumentKind.Indirect:
                if (location.Position < 4)
                    assembler.mov(r10, NativeIntegerRegisters[location.Position]);
                else
                    assembler.mov(r10, __qword_ptr[rbp + location.StackOffset + 16]);
                CopyMemory(assembler, r10, 0, rsp, destination, parameter.NativeSize);
                return;

            case X64NativeVectorcallPlan.NativeArgumentKind.VectorFragments:
                foreach (X64NativeVectorcallPlan.NativeVectorFragment fragment in location.Fragments)
                {
                    if (fragment.IsRegister)
                    {
                        StoreVectorElement(assembler, fragment.Size, fragment.Register, rsp, destination + fragment.CanonicalOffset);
                    }
                    else
                    {
                        CopyMemory(assembler, rbp, fragment.StackOffset + 16, rsp, destination + fragment.CanonicalOffset, fragment.Size);
                    }
                }
                return;
        }
    }

    private static void PublishVectorcallArgument(Assembler assembler, X64NativeVectorcallPlan plan, FrameLayout frame, X64VectorcallSignature.X64VectorcallParameter parameter)
    {
        int source = frame.ParameterOffsets[parameter.Index];
        X64NativeVectorcallPlan.NativeArgumentLocation location = plan.Parameters[parameter.Index];
        switch (location.Kind)
        {
            case X64NativeVectorcallPlan.NativeArgumentKind.Integer:
                if (location.Position < 4)
                    assembler.mov(NativeIntegerRegisters[location.Position], __qword_ptr[rsp + source]);
                else
                    CopyMemory(assembler, rsp, source, rsp, location.StackOffset, 8);
                return;

            case X64NativeVectorcallPlan.NativeArgumentKind.Indirect:
                assembler.lea(rax, __qword_ptr[rsp + source]);
                if (location.Position < 4)
                    assembler.mov(NativeIntegerRegisters[location.Position], rax);
                else
                    assembler.mov(__qword_ptr[rsp + location.StackOffset], rax);
                return;

            case X64NativeVectorcallPlan.NativeArgumentKind.VectorFragments:
                foreach (X64NativeVectorcallPlan.NativeVectorFragment fragment in location.Fragments)
                {
                    if (fragment.IsRegister)
                    {
                        LoadVectorElement(assembler, fragment.Size, fragment.Register, rsp, source + fragment.CanonicalOffset);
                    }
                    else
                    {
                        CopyMemory(assembler, rsp, source + fragment.CanonicalOffset, rsp, fragment.StackOffset, fragment.Size);
                    }
                }
                return;
        }
    }

    private static void CaptureManagedArgument(Assembler assembler, X64ManagedAbiPlan plan, FrameLayout frame, X64VectorcallSignature.X64VectorcallParameter parameter)
    {
        X64ManagedAbiPlan.ManagedArgumentLocation location = plan.Parameters[parameter.Index];
        int destination = frame.ParameterOffsets[parameter.Index];
        switch (location.Kind)
        {
            case X64ManagedAbiPlan.ManagedArgumentKind.IntegerRegister:
                assembler.mov(__qword_ptr[rsp + destination], ManagedIntegerRegisters(plan)[location.Registers[0]]);
                break;

            case X64ManagedAbiPlan.ManagedArgumentKind.IndirectIntegerRegister:
                assembler.mov(r10, ManagedIntegerRegisters(plan)[location.Registers[0]]);
                CopyMemory(assembler, r10, 0, rsp, destination, parameter.NativeSize);
                break;

            case X64ManagedAbiPlan.ManagedArgumentKind.VectorRegisters:
                StoreManagedVectorChunks(assembler, location.Registers, location.VectorChunkSizes, rsp, destination);
                break;

            case X64ManagedAbiPlan.ManagedArgumentKind.StackValue:
                CopyMemory(assembler, rbp, ManagedInputStackOffset(plan, location.StackOffset), rsp, destination, ManagedStackCopySize(plan, parameter));
                break;

            case X64ManagedAbiPlan.ManagedArgumentKind.IndirectStack:
                assembler.mov(r10, __qword_ptr[rbp + ManagedInputStackOffset(plan, location.StackOffset)]);
                CopyMemory(assembler, r10, 0, rsp, destination, parameter.NativeSize);
                break;
        }
    }

    private static void PublishManagedArgument(Assembler assembler, X64ManagedAbiPlan plan, FrameLayout frame, X64VectorcallSignature.X64VectorcallParameter parameter)
    {
        X64ManagedAbiPlan.ManagedArgumentLocation location = plan.Parameters[parameter.Index];
        int source = frame.ParameterOffsets[parameter.Index];
        switch (location.Kind)
        {
            case X64ManagedAbiPlan.ManagedArgumentKind.IntegerRegister:
                assembler.mov(ManagedIntegerRegisters(plan)[location.Registers[0]], __qword_ptr[rsp + source]);
                break;

            case X64ManagedAbiPlan.ManagedArgumentKind.IndirectIntegerRegister:
                assembler.lea(ManagedIntegerRegisters(plan)[location.Registers[0]], __qword_ptr[rsp + source]);
                break;

            case X64ManagedAbiPlan.ManagedArgumentKind.VectorRegisters:
                LoadManagedVectorChunks(assembler, location.Registers, location.VectorChunkSizes, rsp, source);
                break;

            case X64ManagedAbiPlan.ManagedArgumentKind.StackValue:
                CopyMemory(assembler, rsp, source, rsp, location.StackOffset, ManagedStackCopySize(plan, parameter));
                break;

            case X64ManagedAbiPlan.ManagedArgumentKind.IndirectStack:
                assembler.lea(rax, __qword_ptr[rsp + source]);
                assembler.mov(__qword_ptr[rsp + location.StackOffset], rax);
                break;
        }
    }

    private void SetManagedHiddenReturnBuffer(Assembler assembler, X64ManagedAbiPlan plan, FrameLayout frame)
    {
        if (plan.ReturnValue.Kind != X64ManagedAbiPlan.ManagedReturnKind.HiddenBuffer)
            return;

        assembler.lea(ManagedIntegerRegisters(plan)[0], __qword_ptr[rsp + frame.ReturnOffset]);
    }

    private static void SaveManagedHiddenReturnBuffer(Assembler assembler, X64ManagedAbiPlan plan, FrameLayout frame)
    {
        if (plan.ReturnValue.Kind != X64ManagedAbiPlan.ManagedReturnKind.HiddenBuffer)
            return;

        assembler.mov(__qword_ptr[rsp + frame.SavedReturnPointerOffset], ManagedIntegerRegisters(plan)[0]);
    }

    private static void SetNativeHiddenReturnBuffer(Assembler assembler, X64NativeVectorcallPlan plan, FrameLayout frame)
    {
        if (plan.ReturnValue.Kind != X64NativeVectorcallPlan.NativeReturnKind.HiddenBuffer)
            return;

        assembler.lea(rcx, __qword_ptr[rsp + frame.ReturnOffset]);
    }

    private static void SaveNativeHiddenReturnBuffer(Assembler assembler, X64NativeVectorcallPlan plan, FrameLayout frame)
    {
        if (plan.ReturnValue.Kind != X64NativeVectorcallPlan.NativeReturnKind.HiddenBuffer)
            return;

        assembler.mov(__qword_ptr[rsp + frame.SavedNativeReturnPointerOffset], rcx);
    }

    private void CaptureManagedHvaReturn(Assembler assembler, X64ManagedAbiPlan plan, FrameLayout frame)
    {
        switch (plan.ReturnValue.Kind)
        {
            case X64ManagedAbiPlan.ManagedReturnKind.HiddenBuffer:
                return;
            case X64ManagedAbiPlan.ManagedReturnKind.IntegerRegister:
                assembler.mov(__qword_ptr[rsp + frame.ReturnOffset], rax);
                return;
            case X64ManagedAbiPlan.ManagedReturnKind.VectorRegisters:
                StoreManagedVectorChunks(assembler, plan.ReturnValue.Registers, plan.ReturnValue.VectorChunkSizes, rsp, frame.ReturnOffset);
                return;
        }
    }

    private void PublishManagedHvaReturn(Assembler assembler, X64ManagedAbiPlan plan, FrameLayout frame)
    {
        switch (plan.ReturnValue.Kind)
        {
            case X64ManagedAbiPlan.ManagedReturnKind.HiddenBuffer:
                assembler.mov(r10, __qword_ptr[rsp + frame.SavedReturnPointerOffset]);
                CopyMemory(assembler, rsp, frame.ReturnOffset, r10, 0, _signature.ReturnValue.NativeSize);
                assembler.mov(rax, r10);
                return;
            case X64ManagedAbiPlan.ManagedReturnKind.IntegerRegister:
                assembler.mov(rax, __qword_ptr[rsp + frame.ReturnOffset]);
                return;
            case X64ManagedAbiPlan.ManagedReturnKind.VectorRegisters:
                LoadManagedVectorChunks(assembler, plan.ReturnValue.Registers, plan.ReturnValue.VectorChunkSizes, rsp, frame.ReturnOffset);
                return;
        }
    }

    private void CaptureVectorcallHvaReturn(Assembler assembler, X64NativeVectorcallPlan plan, FrameLayout frame)
    {
        if (plan.ReturnValue.Kind == X64NativeVectorcallPlan.NativeReturnKind.HiddenBuffer)
            return;

        foreach (X64NativeVectorcallPlan.NativeVectorFragment fragment in plan.ReturnValue.Fragments)
        {
            StoreVectorElement(
                assembler,
                fragment.Size,
                fragment.Register,
                rsp,
                frame.ReturnOffset + fragment.CanonicalOffset);
        }
    }

    private void PublishVectorcallHvaReturn(Assembler assembler, X64NativeVectorcallPlan plan, FrameLayout frame)
    {
        if (plan.ReturnValue.Kind == X64NativeVectorcallPlan.NativeReturnKind.HiddenBuffer)
        {
            assembler.mov(r10, __qword_ptr[rsp + frame.SavedNativeReturnPointerOffset]);
            CopyMemory(assembler, rsp, frame.ReturnOffset, r10, 0, _signature.ReturnValue.NativeSize);
            assembler.mov(rax, r10);
            return;
        }

        foreach (X64NativeVectorcallPlan.NativeVectorFragment fragment in plan.ReturnValue.Fragments)
        {
            LoadVectorElement(
                assembler,
                fragment.Size,
                fragment.Register,
                rsp,
                frame.ReturnOffset + fragment.CanonicalOffset);
        }
    }

    private static void StoreManagedVectorChunks(Assembler assembler, int[] registers, int[] chunkSizes, AssemblerRegister64 destinationBase, int destination)
    {
        int offset = 0;
        for (int index = 0; index < registers.Length; index++)
        {
            StoreVectorElement(assembler, chunkSizes[index], registers[index], destinationBase, destination + offset);
            offset += chunkSizes[index];
        }
    }

    private static void LoadManagedVectorChunks(Assembler assembler, int[] registers, int[] chunkSizes, AssemblerRegister64 sourceBase, int source)
    {
        int offset = 0;
        for (int index = 0; index < registers.Length; index++)
        {
            LoadVectorElement(assembler, chunkSizes[index], registers[index], sourceBase, source + offset);
            offset += chunkSizes[index];
        }
    }

    private static void StoreVectorElement(Assembler assembler, int size, int register, AssemblerRegister64 destinationBase, int destination)
    {
        switch (size)
        {
            case 4:
                assembler.movss(__dword_ptr[destinationBase + destination], ManagedVector128Registers[register]);
                break;
            case 8:
                assembler.movsd(__qword_ptr[destinationBase + destination], ManagedVector128Registers[register]);
                break;
            case 16:
                assembler.movups(__xmmword_ptr[destinationBase + destination], ManagedVector128Registers[register]);
                break;
            case 32:
                assembler.vmovups(__ymmword_ptr[destinationBase + destination], ManagedVector256Registers[register]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(size));
        }
    }

    private static void LoadVectorElement(Assembler assembler, int size, int register, AssemblerRegister64 sourceBase, int source)
    {
        switch (size)
        {
            case 4:
                assembler.movss(ManagedVector128Registers[register], __dword_ptr[sourceBase + source]);
                break;
            case 8:
                assembler.movsd(ManagedVector128Registers[register], __qword_ptr[sourceBase + source]);
                break;
            case 16:
                assembler.movups(ManagedVector128Registers[register], __xmmword_ptr[sourceBase + source]);
                break;
            case 32:
                assembler.vmovups(ManagedVector256Registers[register], __ymmword_ptr[sourceBase + source]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(size));
        }
    }

    private static void CopyMemory(Assembler assembler, AssemblerRegister64 sourceBase, int sourceOffset, AssemblerRegister64 destinationBase, int destinationOffset, int size)
    {
        int copied = 0;
        while (size - copied >= 8)
        {
            assembler.mov(rax, __qword_ptr[sourceBase + sourceOffset + copied]);
            assembler.mov(__qword_ptr[destinationBase + destinationOffset + copied], rax);
            copied += 8;
        }
        if (size - copied >= 4)
        {
            assembler.mov(eax, __dword_ptr[sourceBase + sourceOffset + copied]);
            assembler.mov(__dword_ptr[destinationBase + destinationOffset + copied], eax);
            copied += 4;
        }
        if (size - copied >= 2)
        {
            assembler.mov(ax, __word_ptr[sourceBase + sourceOffset + copied]);
            assembler.mov(__word_ptr[destinationBase + destinationOffset + copied], ax);
            copied += 2;
        }
        if (size != copied)
        {
            assembler.mov(al, __byte_ptr[sourceBase + sourceOffset + copied]);
            assembler.mov(__byte_ptr[destinationBase + destinationOffset + copied], al);
        }
    }

    private static int ManagedInputStackOffset(X64ManagedAbiPlan plan, int outputOffset)
        => checked(outputOffset + 16);

    private static int ManagedStackCopySize(X64ManagedAbiPlan plan, X64VectorcallSignature.X64VectorcallParameter parameter)
        => plan.IsWindows ? 8 : parameter.NativeSize;

    private static AssemblerRegister64[] ManagedIntegerRegisters(X64ManagedAbiPlan plan)
        => plan.IsWindows ? WindowsIntegerRegisters : SystemVIntegerRegisters;

    private static int Align(int value, int alignment)
        => checked((value + alignment - 1) & ~(alignment - 1));

    private sealed class FrameLayout
    {
        private FrameLayout(
            int frameSize,
            int canonicalBytes,
            int[] parameterOffsets,
            int returnOffset,
            int savedReturnPointerOffset,
            int savedNativeReturnPointerOffset)
        {
            FrameSize = frameSize;
            CanonicalBytes = canonicalBytes;
            ParameterOffsets = parameterOffsets;
            ReturnOffset = returnOffset;
            SavedReturnPointerOffset = savedReturnPointerOffset;
            SavedNativeReturnPointerOffset = savedNativeReturnPointerOffset;
        }

        public int FrameSize { get; }
        public int CanonicalBytes { get; }
        public int[] ParameterOffsets { get; }
        public int ReturnOffset { get; }
        public int SavedReturnPointerOffset { get; }
        public int SavedNativeReturnPointerOffset { get; }

        public static FrameLayout Create(X64VectorcallSignature signature, int outgoingStackBytes, X64ManagedAbiPlan managedAbi, X64NativeVectorcallPlan nativeAbi)
        {
            int cursor = Align(outgoingStackBytes, 32);
            int canonicalStart = cursor;
            int[] offsets = new int[signature.Parameters.Length];
            foreach (X64VectorcallSignature.X64VectorcallParameter parameter in signature.Parameters)
            {
                int alignment = Math.Min(Math.Max(parameter.IsHva ? parameter.ElementSize : parameter.NativeSize, 8), 32);
                cursor = Align(cursor, alignment);
                offsets[parameter.Index] = cursor;
                cursor = checked(cursor + Align(Math.Max(parameter.NativeSize, 8), 8));
            }

            int returnOffset = cursor;
            if (signature.ReturnValue.IsHva)
            {
                cursor = Align(cursor, Math.Min(Math.Max(signature.ReturnValue.ElementSize, 8), 32));
                returnOffset = cursor;
                cursor = checked(cursor + Align(signature.ReturnValue.NativeSize, 8));
            }

            int savedReturnPointerOffset = cursor;
            if (managedAbi.ReturnValue.Kind == X64ManagedAbiPlan.ManagedReturnKind.HiddenBuffer)
                cursor = checked(cursor + 8);

            int savedNativeReturnPointerOffset = cursor;
            if (nativeAbi.ReturnValue.Kind == X64NativeVectorcallPlan.NativeReturnKind.HiddenBuffer)
                cursor = checked(cursor + 8);

            return new FrameLayout(
                Align(cursor, 16),
                cursor - canonicalStart,
                offsets,
                returnOffset,
                savedReturnPointerOffset,
                savedNativeReturnPointerOffset);
        }
    }

    private static readonly AssemblerRegister64[] NativeIntegerRegisters = [rcx, rdx, r8, r9];
    private static readonly AssemblerRegister64[] WindowsIntegerRegisters = [rcx, rdx, r8, r9];
    private static readonly AssemblerRegister64[] SystemVIntegerRegisters = [rdi, rsi, rdx, rcx, r8, r9];
    private static readonly AssemblerRegisterXMM[] ManagedVector128Registers = [xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7];
    private static readonly AssemblerRegisterYMM[] ManagedVector256Registers = [ymm0, ymm1, ymm2, ymm3, ymm4, ymm5, ymm6, ymm7];
}
