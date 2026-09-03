using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RedBird.Core.Memory.Managed;

/// <summary>
/// Represents a wrapper around a native array that supports Stack-based overrides (Push/Pop) and Base/Default value modification.
/// </summary>
/// <typeparam name="T">Unmanaged type (int, float, etc)</typeparam>
public unsafe class ManagedNativeArray<T>(void* baseAddress, int length) : IDisposable where T : unmanaged
{
    protected readonly T* _baseAddress = (T*)baseAddress;
    public readonly int Length = length;

    // Sparse storage: only store stacks for indices that are actually modified/pushed.
    private readonly Dictionary<int, Stack<T>> _overrides = [];

    // Stores the value that was in memory before we ever touched it.
    private readonly Dictionary<int, T> _originalValues = [];

    public ManagedNativeArray(IntPtr baseAddress, int length) : this((void*)baseAddress, length)
    {

    }

    //
    // --- Core Access ---
    //

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void EnsureTracked(int index)
    {
        if (!_originalValues.ContainsKey(index))
        {
            _originalValues[index] = _baseAddress[index];   // Snapshot vanilla
            _overrides[index] = new Stack<T>();

            // The stack conceptually starts with the current value as the "Base"
            _overrides[index].Push(_baseAddress[index]);
        }
    }

    public T GetValue(int index)
    {
        if (index < 0 || index >= Length)
            return default;

        // Always read strictly from memory to ensure we catch changes made by the game engine/other hooks
        return _baseAddress[index];
    }

    /// <summary>
    /// Sets the current active value (Top of stack).
    /// If no stack exists, initializes it.
    /// </summary>
    public void SetValue(int index, T value)
    {
        if (index < 0 || index >= Length)
            return;

        EnsureTracked(index);

        // Pop the current top (active) and replace it
        Stack<T> stack = _overrides[index];
        if (stack.Count > 0)
            stack.Pop();

        stack.Push(value);

        // Write to native memory
        _baseAddress[index] = value;
    }

    //
    // --- Stack API ---
    //

    /// <summary>
    /// Pushes a new override value onto the stack for this index.
    /// </summary>
    public void Push(int index, T value)
    {
        if (index < 0 || index >= Length)
            return;

        EnsureTracked(index);
        _overrides[index].Push(value);
        _baseAddress[index] = value;
    }

    /// <summary>
    /// Pops the top value. returns the new active value.
    /// </summary>
    public T Pop(int index)
    {
        if (index < 0 || index >= Length)
            return default;

        if (_overrides.TryGetValue(index, out Stack<T>? stack) && stack.Count > 1)
        {
            stack.Pop();                    // Remove current
            T previous = stack.Peek();      // Get previous
            _baseAddress[index] = previous; // Restore
            return previous;
        }

        // If stack is 1 or 0, we are at base. Do nothing or reset to original.
        return _baseAddress[index];
    }

    /// <summary>
    /// Modifies the "Bottom" of the stack (the Default value).
    /// If the stack has only 1 item, this updates the current memory immediately.
    /// If the stack has overrides, this updates the fallback value underneath them.
    /// </summary>
    public void SetBase(int index, T value)
    {
        if (index < 0 || index >= Length)
            return;

        EnsureTracked(index);

        // We need to replace the bottom-most element
        Stack<T> stack = _overrides[index];
        T[] tempBuffer = [.. stack]; // Returns array with Top at [0]

        // Rebuild stack:
        stack.Clear();
        stack.Push(value); // New Base

        // Push back all overrides (in reverse order of ToArray)
        for (int i = tempBuffer.Length - 2; i >= 0; i--)
        {
            stack.Push(tempBuffer[i]);
        }

        // If we were at the base (no overrides), update memory now
        if (tempBuffer.Length == 1)
        {
            _baseAddress[index] = value;
        }
    }

    /// <summary>
    /// Removes all temporary overrides (Push), reverting to the configured Base.
    /// <para>Safe to call multiple times. Does NOT revert SetBase() changes.</para>
    /// </summary>
    public void ClearOverrides()
    {
        foreach (KeyValuePair<int, Stack<T>> kvp in _overrides)
        {
            Stack<T> stack = kvp.Value;
            if (stack.Count > 1)
            {
                // Pop until only Base remains
                while (stack.Count > 1)
                    stack.Pop();

                // Write Base to memory
                _baseAddress[kvp.Key] = stack.Peek();
            }
        }
    }

    /// <summary>
    /// Reverts completely to the state before this class touched memory (Vanilla).
    /// <para>WARNING: This undoes SetBase() changes.</para>
    /// </summary>
    public void ResetToOriginal()
    {
        foreach (KeyValuePair<int, T> kvp in _originalValues)
        {
            _baseAddress[kvp.Key] = kvp.Value;
        }
        _overrides.Clear();
        _originalValues.Clear();
    }

    public void Dispose()
    {
        ResetToOriginal();
        GC.SuppressFinalize(this);
    }

    public NativePointer<T> GetBaseAddress() => _baseAddress;
    public Span<T> AsSpan() => new(_baseAddress, Length);
}
