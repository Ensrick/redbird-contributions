using System;
using System.Collections.Generic;

namespace RedBird.Core.Memory.Managed;

/// <summary>
/// A dictionary wrapper that supports Stack-based value overrides (Push/Pop) and Base value modification.
/// Useful for managed game data that mimics the behavior of ManagedNativeArray.
/// </summary>
public class ManagedDictionary<TKey, TValue> where TKey : notnull, IEquatable<TKey>
{
    private readonly Dictionary<TKey, Stack<TValue>> _store = [];
    private readonly TValue _defaultValue;

    public ManagedDictionary(TValue defaultValue = default!)
    {
        _defaultValue = defaultValue;
    }

    public ManagedDictionary(IDictionary<TKey, TValue> initialValues, TValue defaultValue = default!)
    {
        _defaultValue = defaultValue;
        foreach (KeyValuePair<TKey, TValue> kvp in initialValues)
        {
            Stack<TValue> s = new();
            s.Push(kvp.Value); // Initialize Base
            _store[kvp.Key] = s;
        }
    }

    public TValue GetValue(TKey key)
    {
        if (_store.TryGetValue(key, out Stack<TValue>? stack) && stack.Count > 0)
            return stack.Peek();
        return _defaultValue;
    }

    /// <summary>
    /// Sets the current active value (Top of stack).
    /// If the stack is empty (untracked), initializes it with Default as base, then replaces it.
    /// </summary>
    public void SetValue(TKey key, TValue value)
    {
        if (!_store.TryGetValue(key, out Stack<TValue>? stack))
        {
            stack = new Stack<TValue>();
            stack.Push(_defaultValue); // Implicit Base
            _store[key] = stack;
        }

        // Replace top
        if (stack.Count > 0)
            stack.Pop();
        stack.Push(value);
    }

    public void Push(TKey key, TValue value)
    {
        if (!_store.TryGetValue(key, out Stack<TValue>? stack))
        {
            stack = new Stack<TValue>();
            stack.Push(_defaultValue); // Implicit Base
            _store[key] = stack;
        }
        stack.Push(value);
    }

    public TValue Pop(TKey key)
    {
        if (_store.TryGetValue(key, out Stack<TValue>? stack) && stack.Count > 1)
        {
            stack.Pop();
            return stack.Peek();
        }

        // Fallback to current (likely Base or Default)
        return GetValue(key);
    }

    public void SetBase(TKey key, TValue value)
    {
        if (!_store.TryGetValue(key, out Stack<TValue>? stack))
        {
            stack = new Stack<TValue>();
            stack.Push(value);
            _store[key] = stack;
            return;
        }

        TValue[] items = [.. stack]; // [Top, ..., OldBase]
        stack.Clear();
        stack.Push(value); // New Base

        // Push overrides back (Reverse order of ToArray)
        for (int i = items.Length - 2; i >= 0; i--)
        {
            stack.Push(items[i]);
        }
    }

    /// <summary>
    /// Removes all overrides, resetting values to their Base.
    /// </summary>
    public void ClearOverrides()
    {
        foreach (Stack<TValue> stack in _store.Values)
        {
            // Safe guard: Reduce until 1. If 1 or 0, loop terminates.
            while (stack.Count > 1)
                stack.Pop();
        }
    }
    public void ResetToOriginal() => ClearOverrides();
}
