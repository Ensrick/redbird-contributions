using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX86;
using RedBird.Core.Memory;
using RedBird.X86.Extensions;

namespace RedBird.X86.Hooks;

/// <summary>
/// A 32-bit inline hook that replaces whole instructions with a five-byte near jump and executes a caller-generated intermediary stub before returning to the original function.
/// </summary>
public sealed class X86InlineHook : IHook
{
    private const int Bitness = 32;
    private readonly ILogger _logger;
    private readonly UInt64 _targetAddress;
    private readonly Assembler? _assembler;
    private readonly List<Delegate> _pinnedDelegates = [];
    private Instruction[] _overwrittenInstructions = [];
    private byte[] _originalBytes = [];
    private int _displacedByteCount;
    private IntPtr _stubAddress;

    public X86InlineHook(UInt64 targetAddress, int minHookSize = X86JumpFactory.PatchSize, ILogger? logger = null, string? name = null)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
            throw new PlatformNotSupportedException($"{nameof(X86InlineHook)} requires an x86 process.");

        _logger = logger ?? NullLogger.Instance;
        Name = name ?? $"x86-inline@0x{targetAddress:X8}";

        if (targetAddress == 0)
        {
            State = HookState.Failed;
            return;
        }
        if (targetAddress > UInt32.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(targetAddress), "An x86 hook address must fit in 32 bits.");

        _targetAddress = targetAddress;
        _assembler = new Assembler(Bitness);
        DecodeDisplacedInstructions(Math.Max(minHookSize, X86JumpFactory.PatchSize));
        _originalBytes = new byte[_displacedByteCount];
        Marshal.Copy((IntPtr)_targetAddress, _originalBytes, 0, _originalBytes.Length);
    }

    public string Name { get; }
    public UInt64 TargetAddress => _targetAddress;
    public HookState State { get; private set; } = HookState.Created;
    public bool IsInstalled => State == HookState.Installed;
    public IntPtr StubAddress => _stubAddress;
    public int DisplacedByteCount => _displacedByteCount;
    public ReadOnlySpan<Instruction> OverwrittenInstructions => _overwrittenInstructions;

    public void Generate(InlineHookGenerator generator)
    {
        if (generator is null)
            throw new ArgumentNullException(nameof(generator));
        if (_targetAddress == 0 || _assembler is null)
            return;
        if (State is not HookState.Created)
            throw new InvalidOperationException($"Hook [{Name}] has already been generated or disposed.");

        UInt64 returnAddress = (_targetAddress + (UInt64)_displacedByteCount) & UInt32.MaxValue;
        generator(_assembler, _overwrittenInstructions.AsSpan(), returnAddress);
        _assembler.AddUnrestrictedJmp(returnAddress);
        Compile();
        State = HookState.Prepared;
    }

    public void Enable()
    {
        if (State is HookState.Installed or HookState.Disposed or HookState.Failed)
            return;
        if (State != HookState.Prepared || _stubAddress == IntPtr.Zero)
            throw new InvalidOperationException($"Hook [{Name}] has no stub; call Generate before Enable.");

        CodePatch.Write(_targetAddress, X86JumpFactory.BuildDirect(_targetAddress, unchecked((UInt32)_stubAddress.ToInt32()), _displacedByteCount));
        State = HookState.Installed;
        _logger.LogDebug("Enabled x86 inline hook at 0x{Target:X8} via 0x{Stub:X8}.", _targetAddress, _stubAddress.ToInt64());
    }

    public void Disable()
    {
        if (State != HookState.Installed)
            return;

        CodePatch.Write(_targetAddress, _originalBytes);
        State = HookState.Disabled;
    }

    /// <summary>Roots a managed callback for the hook's lifetime and returns its native address.</summary>
    public IntPtr PinDelegate(Delegate callback)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        _pinnedDelegates.Add(callback);
        return Marshal.GetFunctionPointerForDelegate(callback);
    }

    public void Dispose()
    {
        if (State == HookState.Disposed)
            return;

        Disable();
        _pinnedDelegates.Clear();
        _stubAddress = IntPtr.Zero;
        State = HookState.Disposed;
    }

    private unsafe void DecodeDisplacedInstructions(int minimumBytes)
    {
        MemoryCodeReader reader = new((byte*)_targetAddress, minimumBytes + 15);
        Decoder decoder = Decoder.Create(Bitness, reader, _targetAddress);
        List<Instruction> decoded = [];
        int byteCount = 0;

        while (byteCount < minimumBytes)
        {
            Instruction instruction = decoder.Decode();
            if (decoder.LastError != DecoderError.None || instruction.IsInvalid)
                break;

            decoded.Add(instruction);
            byteCount += instruction.Length;
        }

        if (byteCount < X86JumpFactory.PatchSize)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Only {byteCount} bytes could be decoded at 0x{_targetAddress:X8}; an x86 near jump needs five.");
        }

        _overwrittenInstructions = [.. decoded];
        _displacedByteCount = byteCount;
    }

    private void Compile()
    {
        int estimatedSize = (_assembler!.Instructions.Count * 15) + 32;
        _stubAddress = NativeMemoryManager.AllocateStub(estimatedSize);
        if (_stubAddress == IntPtr.Zero || unchecked((UInt64)_stubAddress.ToInt64()) > UInt32.MaxValue)
        {
            State = HookState.Failed;
            throw new InvalidOperationException("Could not allocate a 32-bit executable stub.");
        }

        using MemoryStream stream = new();
        StreamCodeWriter writer = new(stream);
        if (!_assembler.TryAssemble(writer, unchecked((UInt32)_stubAddress.ToInt32()), out string? error, out _))
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Iced failed to assemble x86 inline stub: {error}");
        }

        byte[] stub = stream.ToArray();
        if (stub.Length > estimatedSize || !NativeMemoryManager.WriteStub(_stubAddress, stub))
        {
            State = HookState.Failed;
            throw new InvalidOperationException("Could not write x86 inline stub.");
        }
    }
}
