using System;
using System.Runtime.CompilerServices;
using RedBird.X64.Assembly.Stateful;

namespace RedBird.X64.Memory;

/// <summary>
/// A value in the target's memory, addressed as <c>baseAddress + rva</c> where the RVA itself comes from a patched constant.
/// </summary>
/// <typeparam name="T">Unmanaged type of the value being read or written.</typeparam>
public sealed unsafe class RVAMemoryValue<T> where T : unmanaged
{
    private readonly Func<UInt64> _resolve;
    private readonly UInt64 _snapshot;
    private readonly bool _live;

    /// <summary>Binds to an RVA held in <paramref name="site"/>.</summary>
    /// <typeparam name="TRva">Integral type of the RVA constant.</typeparam>
    /// <param name="site">The patched constant holding the RVA.</param>
    /// <param name="baseAddress">Module base the RVA is relative to.</param>
    /// <param name="live">
    /// When true the address is recomputed on each access, so overriding the RVA
    /// moves this value with it. When false the address is frozen at construction.
    /// </param>
    public static RVAMemoryValue<T> From<TRva>(IAssemblyValueSite<TRva> site, UInt64 baseAddress, bool live = false)
        where TRva : unmanaged
    {
        if (site is null)
            throw new ArgumentNullException(nameof(site));

        return new RVAMemoryValue<T>(() => baseAddress + ToUnsigned(site.GetValue()), live);
    }

    /// <summary>Binds to a fixed address.</summary>
    public static RVAMemoryValue<T> FromAddress(UInt64 address)
    {
        if (address == 0)
            throw new ArgumentOutOfRangeException(nameof(address));

        return new RVAMemoryValue<T>(() => address, live: false);
    }

    private RVAMemoryValue(Func<UInt64> resolve, bool live)
    {
        _resolve = resolve;
        _live = live;
        _snapshot = resolve();

        if (_snapshot == 0)
            throw new InvalidOperationException("The resolved address is zero; the RVA site probably did not resolve.");
    }

    /// <summary>The absolute address currently in effect.</summary>
    public UInt64 Address => _live ? _resolve() : _snapshot;

    /// <summary>Reads or writes the value at <see cref="Address"/>.</summary>
    /// <remarks>
    /// A direct pointer access. Writing to a page the loader mapped read-only: a constant in <c>.rdata</c>, say: faults; use <see cref="TryWrite"/> there, which raises protection for the duration.
    /// </remarks>
    public T Value
    {
        get => *(T*)Address;
        set => *(T*)Address = value;
    }

    /// <summary>Reads the value without throwing on a bad address.</summary>
    public bool TryRead(out T value)
    {
        try
        {
            value = *(T*)Address;
            return true;
        }
        catch (Exception)
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Writes through <c>CodePatch</c>, so a read-only page is made writable and restored
    /// afterwards.
    /// </summary>
    public bool TryWrite(T value)
    {
        try
        {
            ReadOnlySpan<byte> bytes = new(&value, Unsafe.SizeOf<T>());
            Core.Memory.CodePatch.Write(Address, bytes);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static UInt64 ToUnsigned<TRva>(TRva value) where TRva : unmanaged
    {
        UInt64 raw = 0;
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt64, byte>(ref raw), value);

        // An RVA read through a signed type must not sign-extend into the high bits, or the resulting address lands 4 GB away.
        return Unsafe.SizeOf<TRva>() switch
        {
            1 => raw & 0xFF,
            2 => raw & 0xFFFF,
            4 => raw & 0xFFFF_FFFF,
            _ => raw,
        };
    }

    /// <inheritdoc/>
    public override string ToString() => $"0x{Address:X16} -> {typeof(T).Name}";
}
