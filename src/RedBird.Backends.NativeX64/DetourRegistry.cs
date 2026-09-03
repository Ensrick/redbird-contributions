using System;
using System.Collections.Generic;

namespace RedBird.Backends.NativeX64;

/// <summary>
/// Tracks how many layers this engine has stacked on each address.
/// </summary>
/// <remarks>
/// Simple bookkeeping. 
/// It exists so a detour can report its position in the chain, so <see cref="HookChainingPolicy.WarnAndAllow"/> has something to warn about, and so <see cref="HookChainingPolicy.Refuse"/> has something to check. 
/// It does not see hooks installed by another engine or another process, inspecting the prologue is what covers those, as far as anything can.
/// </remarks>
internal static class DetourRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<UInt64, int> Layers = [];

    /// <summary>How many layers this engine currently holds at <paramref name="address"/>.</summary>
    public static int GetDepth(UInt64 address)
    {
        lock (Gate)
            return Layers.TryGetValue(address, out int depth) ? depth : 0;
    }

    /// <summary>Records a new layer and returns its depth, 1 being the first.</summary>
    public static int AddLayer(UInt64 address)
    {
        lock (Gate)
        {
            int depth = Layers.TryGetValue(address, out int existing) ? existing + 1 : 1;
            Layers[address] = depth;
            return depth;
        }
    }

    /// <summary>Removes a layer.</summary>
    public static void RemoveLayer(UInt64 address)
    {
        lock (Gate)
        {
            if (!Layers.TryGetValue(address, out int depth))
                return;

            if (depth <= 1)
                Layers.Remove(address);
            else
                Layers[address] = depth - 1;
        }
    }
}
