using Iced.Intel;
using Microsoft.Extensions.Logging;
using System;
using RedBird.Core.Logging;

namespace RedBird.Core.Memory;

/// <summary>
/// Memory code writer for Iced's enoder to write
/// from any arbitrary memory location via pointers.
/// </summary>
public unsafe sealed class RawMemoryCodeWriter : CodeWriter
{
    public byte* data;
    public int length;
    private int currentPosition = 0;
    private readonly ILogger? _logger;

    public RawMemoryCodeWriter(byte* data, int length, ILogger? logger = null)
    {
        _logger?.CodeWriterCreated((IntPtr)data, length);
        this.data = data;
        this.length = length;
        _logger = logger;
    }

    public RawMemoryCodeWriter(ReadOnlySpan<byte> data, ILogger? logger = null)
    {
        fixed (byte* ptr = data)
        {
            _logger?.CodeWriterCreated((IntPtr)ptr, length);
            this.data = ptr;
        }
        length = data.Length;
        _logger = logger;
    }

    public override void WriteByte(byte b)
    {
        _logger?.CodeWriterWriteByte(b, currentPosition, length, (IntPtr)data);
        if (currentPosition >= length)
            return;
        data[currentPosition++] = b;
    }

    public int Written => currentPosition;
}
