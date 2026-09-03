using System;
using System.Collections.Generic;

namespace RedBird.Core.Memory.Managed;

/// <summary>
/// Represents a managed single value with Stack-based overrides (Push/Pop)  and Base/Default value modification.
/// <para>Mirrors the behavior of <see cref="ManagedNativeArray{T}"/> but for a single scalar value.</para>
/// </summary>
/// <typeparam name="T">The value type (e.g. int, float, byte, uint)</typeparam>
public class ManagedValue<T> where T : struct
{
    private readonly Stack<T> _stack = new();
    private readonly T _defaultValue;

    /// <summary>
    /// The currently active value (top of stack).
    /// </summary>
    public T Value => _stack.Count > 0 ? _stack.Peek() : _defaultValue;

    /// <param name="initialValue">The initial Base value. Defaults to default(T) if not provided.</param>
    public ManagedValue(T initialValue = default)
    {
        _defaultValue = initialValue;
        _stack.Push(initialValue); // Initialize Base
    }

    //
    // --- Core Access ---
    //

    /// <summary>
    /// Gets the currently active value (top of stack).
    /// </summary>
    public T GetValue() => Value;

    /// <summary>
    /// Sets the current active value, replacing the top of the stack.
    /// Does not affect any values beneath it (overrides remain layered).
    /// </summary>
    public void SetValue(T value)
    {
        if (_stack.Count > 0)
            _stack.Pop();

        _stack.Push(value);
    }

    //
    // --- Stack API ---
    //

    /// <summary>
    /// Pushes a new override value onto the stack.
    /// The previous value is preserved and can be restored via <see cref="Pop"/>.
    /// </summary>
    public void Push(T value)
    {
        _stack.Push(value);
    }

    /// <summary>
    /// Pops the top override, restoring the previous value.
    /// Will not pop below the Base (minimum stack depth of 1).
    /// </summary>
    /// <returns>The new active value after popping.</returns>
    public T Pop()
    {
        if (_stack.Count > 1)
            _stack.Pop();

        return Value;
    }

    /// <summary>
    /// Modifies the Base value (bottom of stack).
    /// If there are no overrides, the active value changes immediately.
    /// If overrides exist, they remain on top — Base is updated underneath them.
    /// </summary>
    public void SetBase(T value)
    {
        T[] items = [.. _stack]; // [Top, ..., Base]
        _stack.Clear();
        _stack.Push(value); // New Base

        // Re-push all overrides in original order (ToArray returns Top-first)
        for (int i = items.Length - 2; i >= 0; i--)
        {
            _stack.Push(items[i]);
        }
    }

    /// <summary>
    /// Gets the Base value (bottom of stack), without modifying state.
    /// </summary>
    public T GetBase()
    {
        T[] items = [.. _stack]; // [Top, ..., Base]
        return items.Length > 0 ? items[^1] : _defaultValue;
    }

    /// <summary>
    /// Removes all overrides, reverting to the configured Base value.
    /// Does NOT undo <see cref="SetBase"/> changes.
    /// </summary>
    public void ClearOverrides()
    {
        while (_stack.Count > 1)
            _stack.Pop();
    }

    /// <summary>
    /// Resets the value completely to the original value passed at construction.
    /// Undoes both overrides AND <see cref="SetBase"/> changes.
    /// </summary>
    public void ResetToOriginal()
    {
        _stack.Clear();
        _stack.Push(_defaultValue);
    }

    /// <summary>
    /// Returns the number of values currently on the stack (1 = Base only, no overrides).
    /// </summary>
    public int StackDepth => _stack.Count;

    public override string ToString() => $"ManagedValue<{typeof(T).Name}>({Value})";

    // Implicit conversion for ergonomic use: int x = myManagedInt;
    public static implicit operator T(ManagedValue<T> mv) => mv.Value;
}
