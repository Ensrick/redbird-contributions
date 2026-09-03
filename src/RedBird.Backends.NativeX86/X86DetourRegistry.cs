using System;
using System.Collections.Generic;

namespace RedBird.Backends.NativeX86;

/// <summary>Tracks layers installed by the managed x86 engine.</summary>
internal static class X86DetourRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<UInt64, int> Layers = [];

    public static int GetDepth(UInt64 address)
    {
        lock (Gate)
            return Layers.TryGetValue(address, out int depth) ? depth : 0;
    }

    public static int AddLayer(UInt64 address)
    {
        lock (Gate)
        {
            int depth = Layers.TryGetValue(address, out int existing) ? existing + 1 : 1;
            Layers[address] = depth;
            return depth;
        }
    }

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
