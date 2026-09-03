using Iced.Intel;
using System;

namespace RedBird.Core.Memory;

/// <summary>
/// Memory code reader for Iced's decoder to read from any arbitrary memory location via pointers.
/// </summary>
public unsafe sealed class MemoryCodeReader : CodeReader
{
    public byte* data;
    public int length;
    private int currentPosition = 0;

    public MemoryCodeReader(byte* data, int length)
    {
        this.data = data;
        this.length = length;
    }

    public MemoryCodeReader(ReadOnlySpan<byte> data)
    {
        fixed (byte* ptr = data)
        {
            this.data = ptr;
        }
        length = data.Length;
    }

    public override int ReadByte()
    {
        if (currentPosition >= length)
            return -1;
        return data[currentPosition++];
    }
}
