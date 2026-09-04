using Iced.Intel;
using Microsoft.Extensions.Logging;
using System;
using RedBird.Core.Logging;

namespace RedBird.Core.Memory;

/// <summary>
/// Memory code writer for Iced's enoder to write from any arbitrary memory location via pointers.
/// </summary>
public unsafe sealed class OSMemoryCodeWriter : CodeWriter, IDisposable
{
    public byte* data;
    public int length;
    private int currentPosition = 0;
    private readonly ILogger? _logger;
    private MemoryProtectionScope? _protectionScope;
    private bool _disposed;
    public OSMemoryCodeWriter(byte* data, int length, ILogger? logger = null)
    {
        _logger = logger;
        this.data = data;
        this.length = length;
        _logger?.CodeWriterCreated((UInt64)data, length);
        SetReadPermissions();
    }

    private void SetReadPermissions()
    {
        if (!MemoryProtectionScope.TryAcquire((IntPtr)data, length, MemoryProtection.ExecuteReadWrite, out _protectionScope))
        {
            _logger?.LogDebug($"Error while setting write permissions!");
        }
    }

    public OSMemoryCodeWriter(ReadOnlySpan<byte> data, ILogger? logger = null)
    {
        _logger = logger;
        fixed (byte* ptr = data)
        {
            _logger?.CodeWriterCreated((UInt64)ptr, data.Length);
            this.data = ptr;
        }
        length = data.Length;
        SetReadPermissions();
    }

    public override void WriteByte(byte b)
    {
        _logger?.CodeWriterWriteByte(b, currentPosition, length, (UInt64)data);
        if (currentPosition >= length)
            return;
        data[currentPosition++] = b;
    }

    public void SetPosition(int position)
    {
        this.currentPosition = position;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _protectionScope?.Dispose();
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogError(ex, "Error while restoring write permissions!");
        }

        _disposed = true;
    }

    public int Written => currentPosition;
}
