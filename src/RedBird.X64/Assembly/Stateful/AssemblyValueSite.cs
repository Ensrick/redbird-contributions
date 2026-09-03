using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Core.Memory;

namespace RedBird.X64.Assembly.Stateful;

/// <summary>
/// Shared machinery for a patched constant: decode once, keep the original, track overrides, and re-encode safely.
/// </summary>
/// <typeparam name="T">Integral value type of the constant.</typeparam>
/// <remarks>
/// Range is validated against the operand's encoded width before anything is written, so assigning 300 to an <c>imm8</c> is a exception rather than a truncation or a length change.
/// <para>
/// <b>Thread safety.</b> All mutation is under a per-site lock, since the whole point is that several independent parties touch the same site. 
/// The write itself is not atomic with respect to a thread executing that instruction; patching a constant is a single-instruction rewrite, so the exposure is small, but it is not zero.
/// </para>
/// </remarks>
public abstract unsafe class AssemblyValueSite<T> : IAssemblyValueSite<T> where T : unmanaged
{
    /// <summary>Owner used by <see cref="SetValue"/> and by the legacy stack API.</summary>
    private static readonly object AmbientOwner = new();

    private readonly object _gate = new();
    private readonly List<AssemblyValueOverride<T>> _overrides = [];
    private readonly Stack<object> _legacyStack = new();
    private readonly int _instructionLength;

    private long _sequence;
    private bool _disposed;

    /// <summary>The decoded instruction, kept in sync with what is in memory.</summary>
    protected Instruction Instruction;

    /// <summary>Logger for this site.</summary>
    protected readonly ILogger Logger;

    /// <summary>Value added to the logical value to produce the encoded one.</summary>
    /// <remarks>
    /// <b>Sign convention changed.</b> <c>ManagedAssemblyImmediate</c> used
    /// <c>encoded = logical + offset</c> while <c>ManagedAssemblyDisplacement</c> used
    /// <c>encoded = logical - offset</c> the same constructor parameter name meaning opposite
    /// things. Both now use the immediate's convention. Any call site that passes a non-zero
    /// offset to a displacement needs its sign flipped.
    /// </remarks>
    protected readonly Int64 ConstantValueOffset;

    /// <summary>Decodes the instruction at <paramref name="instructionAddress"/> and captures its original value.</summary>
    protected AssemblyValueSite(IntPtr instructionAddress, Int64 constantValueOffset = 0, ILogger? logger = null, string? description = null)
    {
        if (instructionAddress == IntPtr.Zero)
            throw new ArgumentNullException(nameof(instructionAddress));

        if (Unsafe.SizeOf<T>() > sizeof(UInt64))
            throw new NotSupportedException($"{typeof(T).Name} is wider than 8 bytes and cannot be an instruction constant.");

        Logger = logger ?? NullLogger.Instance;
        InstructionAddress = instructionAddress;
        ConstantValueOffset = constantValueOffset;

        MemoryCodeReader reader = new((byte*)instructionAddress, 16);
        Decoder decoder = Decoder.Create(64, reader, (UInt64)instructionAddress);
        Instruction = decoder.Decode();

        if (Instruction.IsInvalid)
            throw new ArgumentException($"No valid instruction could be decoded at 0x{(UInt64)instructionAddress:X16}.", nameof(instructionAddress));

        _instructionLength = Instruction.Length;
        Description = description ?? $"{Instruction} @ 0x{(UInt64)instructionAddress:X16}";
    }

    /// <summary>
    /// Reads the original value. Called once by <see cref="Initialise"/>, which derived constructors must invoke after their own validation.
    /// </summary>
    protected void Initialise()
    {
        OriginalValue = ReadFromInstruction(in Instruction);
        CurrentValue = OriginalValue;
    }

    /// <inheritdoc/>
    public IntPtr InstructionAddress { get; }

    /// <inheritdoc/>
    public string Description { get; }

    /// <inheritdoc/>
    public T OriginalValue { get; private set; }

    /// <inheritdoc/>
    public T CurrentValue { get; private set; }

    /// <inheritdoc/>
    public int OverrideCount
    {
        get { lock (_gate) return _overrides.Count; }
    }

    /// <inheritdoc/>
    public event Action<T>? ValueChanged;

    /// <inheritdoc/>
    public T GetValue() => CurrentValue;

    // -------------------------------------------------------------------------
    // Derived-class contract
    // -------------------------------------------------------------------------

    /// <summary>Extracts the encoded constant, already adjusted by <see cref="ConstantValueOffset"/>.</summary>
    protected abstract T ReadFromInstruction(in Instruction instruction);

    /// <summary>Writes the constant into a copy of the instruction, applying <see cref="ConstantValueOffset"/>.</summary>
    protected abstract void WriteToInstruction(ref Instruction instruction, T value);

    // -------------------------------------------------------------------------
    // Overrides
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public AssemblyValueOverrideScope SetOverride(object owner, T value, int priority = 0, string? reason = null)
    {
        if (owner is null)
            throw new ArgumentNullException(nameof(owner));

        lock (_gate)
        {
            ThrowIfDisposed();

            AssemblyValueOverride<T>? existing = FindOverride(owner, priority);

            if (existing is not null)
            {
                existing.Value = value;
            }
            else
            {
                _overrides.Add(new AssemblyValueOverride<T>(owner, value, priority, _sequence++, reason));
            }

            Reevaluate();
        }

        return new AssemblyValueOverrideScope(() => ClearOverride(owner, priority));
    }

    /// <inheritdoc/>
    public void SetValue(T value) => SetOverride(AmbientOwner, value, priority: 0, reason: "SetValue");

    /// <inheritdoc/>
    public bool ClearOverrides(object owner)
    {
        if (owner is null)
            throw new ArgumentNullException(nameof(owner));

        lock (_gate)
        {
            if (_disposed)
                return false;

            int removed = _overrides.RemoveAll(o => ReferenceEquals(o.Owner, owner));
            if (removed == 0)
                return false;

            Reevaluate();
            return true;
        }
    }

    private bool ClearOverride(object owner, int priority)
    {
        lock (_gate)
        {
            if (_disposed)
                return false;

            int removed = _overrides.RemoveAll(o => ReferenceEquals(o.Owner, owner) && o.Priority == priority);
            if (removed == 0)
                return false;

            Reevaluate();
            return true;
        }
    }

    /// <inheritdoc/>
    public void RestoreOriginal()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            _overrides.Clear();
            _legacyStack.Clear();
            Reevaluate();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> DescribeOverrides()
    {
        lock (_gate)
        {
            if (_overrides.Count == 0)
                return [];

            List<AssemblyValueOverride<T>> ordered = new(_overrides);
            ordered.Sort(CompareByEffectiveness);
            ordered.Reverse();

            List<string> lines = new(ordered.Count);
            foreach (AssemblyValueOverride<T> entry in ordered)
                lines.Add(entry.ToString());

            return lines;
        }
    }

    private AssemblyValueOverride<T>? FindOverride(object owner, int priority)
    {
        foreach (AssemblyValueOverride<T> entry in _overrides)
        {
            if (ReferenceEquals(entry.Owner, owner) && entry.Priority == priority)
                return entry;
        }

        return null;
    }

    private static int CompareByEffectiveness(AssemblyValueOverride<T> left, AssemblyValueOverride<T> right)
    {
        int byPriority = left.Priority.CompareTo(right.Priority);
        return byPriority != 0 ? byPriority : left.Sequence.CompareTo(right.Sequence);
    }

    /// <summary>Recomputes the winning value and writes it if it differs from what is encoded.</summary>
    private void Reevaluate()
    {
        T effective = OriginalValue;

        if (_overrides.Count > 0)
        {
            AssemblyValueOverride<T> winner = _overrides[0];
            for (int i = 1; i < _overrides.Count; i++)
            {
                if (CompareByEffectiveness(_overrides[i], winner) > 0)
                    winner = _overrides[i];
            }

            effective = winner.Value;
        }

        if (EqualityComparer<T>.Default.Equals(effective, CurrentValue))
            return;

        WriteValue(effective);
        CurrentValue = effective;

        ValueChanged?.Invoke(effective);
    }

    // -------------------------------------------------------------------------
    // Legacy stack API
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public void Push(T value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            // Each push gets its own owner token, so the resulting overrides stack in registration order without any of them being able to clobber another party's claim.
            object token = new();
            _legacyStack.Push(token);
            _overrides.Add(new AssemblyValueOverride<T>(token, value, priority: 0, _sequence++, "Push"));

            Reevaluate();
        }
    }

    /// <inheritdoc/>
    public T Pop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_legacyStack.Count == 0)
            {
                Logger.LogDebug("Pop on '{Description}' with an empty stack; value stays {CurrentValue}.", Description, CurrentValue);
                return CurrentValue;
            }

            object token = _legacyStack.Pop();
            _overrides.RemoveAll(o => ReferenceEquals(o.Owner, token));

            Reevaluate();
            return CurrentValue;
        }
    }

    void IAssemblyValueSite.Pop() => Pop();

    // -------------------------------------------------------------------------
    // Encoding
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-encodes the instruction with <paramref name="value"/> and writes it, refusing anything that would change the instruction's length.
    /// </summary>
    private void WriteValue(T value)
    {
        Instruction modified = Instruction;
        WriteToInstruction(ref modified, value);

        BufferCodeWriter writer = new();
        Encoder encoder = Encoder.Create(64, writer);

        if (!encoder.TryEncode(modified, (UInt64)InstructionAddress, out uint encodedLength, out string? error))
            throw new InvalidOperationException($"Failed to encode '{modified}' for {Description}: {error}");

        if (encodedLength != (uint)_instructionLength)
        {
            throw new InvalidOperationException(
                $"Setting {Description} to {value} re-encodes to {encodedLength} bytes but the original instruction is {_instructionLength}. Writing it would overwrite the following instruction. Pick a value that fits the original encoding, or use an inline hook if the site needs a wider constant.");
        }

        CodePatch.Write((UInt64)InstructionAddress, writer.AsSpan());
        Instruction = modified;

        Logger.LogDebug("{Description} set to {Value}.", Description, value);
    }

    // -------------------------------------------------------------------------
    // Value helpers for derived classes
    // -------------------------------------------------------------------------

    /// <summary>Reinterprets <typeparamref name="T"/> as raw bits, zero-extended.</summary>
    protected static UInt64 ToRaw(T value)
    {
        UInt64 raw = 0;
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt64, byte>(ref raw), value);
        return raw;
    }

    /// <summary>Reinterprets the low bytes of <paramref name="raw"/> as <typeparamref name="T"/>.</summary>
    protected static T FromRaw(UInt64 raw) => Unsafe.ReadUnaligned<T>(ref Unsafe.As<UInt64, byte>(ref raw));

    /// <summary>Sign- or zero-extends the value to 64 bits according to <typeparamref name="T"/>.</summary>
    protected static Int64 ToLogical(T value)
    {
        UInt64 raw = ToRaw(value);

        return Unsafe.SizeOf<T>() switch
        {
            1 => IsSigned ? (sbyte)raw : (byte)raw,
            2 => IsSigned ? (Int16)raw : (Int64)(UInt16)raw,
            4 => IsSigned ? (Int32)raw : (Int64)(UInt32)raw,
            _ => unchecked((Int64)raw),
        };
    }

    /// <summary>True when <typeparamref name="T"/> is a signed integral type.</summary>
    protected static bool IsSigned { get; } =
        typeof(T) == typeof(sbyte) || typeof(T) == typeof(Int16) ||
        typeof(T) == typeof(Int32) || typeof(T) == typeof(Int64) ||
        typeof(T) == typeof(IntPtr);

    /// <summary>
    /// Throws when <paramref name="value"/> does not fit in <paramref name="bits"/>, before
    /// anything is encoded.
    /// </summary>
    protected void EnsureFits(Int64 value, int bits, string what)
    {
        if (bits >= 64)
            return;

        Int64 min = IsSigned ? -(1L << (bits - 1)) : 0;
        Int64 max = IsSigned ? (1L << (bits - 1)) - 1 : (bits == 64 ? Int64.MaxValue : (1L << bits) - 1);

        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"{Description}: {value} does not fit the {bits}-bit {what} (allowed {min}..{max}).");
        }
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Withdraws every override, restoring the value the target shipped with, and detaches
    /// subscribers.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                _overrides.Clear();
                _legacyStack.Clear();
                Reevaluate();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to restore {Description} during dispose.", Description);
            }

            _disposed = true;
            ValueChanged = null;
        }

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(Description);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Description} = {CurrentValue} (original {OriginalValue}, {OverrideCount} overrides)";

    /// <summary>Collects encoder output so the result can be measured before it is written anywhere.</summary>
    private sealed class BufferCodeWriter : CodeWriter
    {
        private readonly byte[] _buffer = new byte[16];
        private int _length;

        public override void WriteByte(byte value)
        {
            if (_length >= _buffer.Length)
                throw new InvalidOperationException("Encoded instruction exceeded the 15-byte x86-64 maximum.");

            _buffer[_length++] = value;
        }

        public ReadOnlySpan<byte> AsSpan() => new(_buffer, 0, _length);
    }
}
