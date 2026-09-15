using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Core.Memory;
using RedBird.Core.Memory.Scanners;
using RedBird.X86.Assembly;
using RedBird.X86.Assembly.Stateful;

namespace RedBird.X86.Memory.Scanners;

/// <summary>
/// Fluent locator for hardcoded constants: scan for a pattern, step to the instruction, wrap it in something that can be changed and put back.
/// </summary>
/// <example>
/// <code>
/// if (!scanner
///         .Scan("BB ?? ?? ?? ?? EB ?? 41 83 F8 ?? 75 ?? BB ?? ?? ?? ?? EB ?? 41 83 F8 ?? 75 ?? 41 8B DA")
///         .TryGetManagedImmediate(out BallistaDamageToCatapult, operand: 1, name: "gBallistaDamageToCatapult"))
/// {
///     LogHelper.Error("Could not find gBallistaDamageToCatapult");
/// }
///
/// stateManager.Register("gBallistaDamageToCatapult", BallistaDamageToCatapult);
/// </code>
/// </example>
public sealed class DataScanner
{
    private readonly ScanRegion _region;
    private readonly ILogger _logger;
    private readonly AobCacheResolver? _cacheResolver;

    private UInt64 _currentAddress;
    private bool _scanSuccess;
    private string? _lastPattern;

    private DataScanner(ScanRegion region, ILogger? logger, AobCacheOptions? aobCache)
    {
        _region = region ?? throw new ArgumentNullException(nameof(region));
        _logger = logger ?? NullLogger.Instance;
        _cacheResolver = aobCache is null ? null : new AobCacheResolver(_region, aobCache, _logger);
    }

    /// <summary>Creates a scanner over <paramref name="region"/>.</summary>
    public static DataScanner Create(ScanRegion region, ILogger? logger = null) => new(region, logger, aobCache: null);

    /// <summary>Creates a scanner over <paramref name="region"/> with a persistent AOB-to-RVA cache.</summary>
    public static DataScanner Create(ScanRegion region, ILogger? logger, AobCacheOptions aobCache)
        => new(region, logger, aobCache ?? throw new ArgumentNullException(nameof(aobCache)));

    /// <summary>Creates a scanner over a copied span.</summary>
    [Obsolete("Copies the whole region. Use ScanRegion.FromModule/FromPointer and the ScanRegion overload.")]
    public static DataScanner Create(ReadOnlySpan<byte> memory, UInt64 imageBase, ILogger? logger = null)
        => new(ScanRegion.Copy(memory, imageBase), logger, aobCache: null);

    /// <summary>Creates a cached scanner over a copied span.</summary>
    [Obsolete("Copies the whole region. Use ScanRegion.FromModule/FromPointer and the ScanRegion overload.")]
    public static DataScanner Create(ReadOnlySpan<byte> memory, UInt64 imageBase, ILogger? logger, AobCacheOptions aobCache)
        => new(ScanRegion.Copy(memory, imageBase), logger, aobCache ?? throw new ArgumentNullException(nameof(aobCache)));

    /// <summary>Absolute virtual address the scanner is currently pointing at.</summary>
    public UInt64 CurrentAddress => _currentAddress;

    /// <summary>
    /// Signed byte offset of <see cref="CurrentAddress"/> from the scan region's base address.
    /// Returns zero after a failed scan; inspect <see cref="Found"/> to distinguish that state from a successful match at offset zero.
    /// </summary>
    public long CurrentOffset => _scanSuccess ? _region.ToOffset(_currentAddress) : 0;

    /// <summary>Whether the last <see cref="Scan(string, int)"/> found its pattern.</summary>
    public bool Found => _scanSuccess;

    // =========================================================================
    // Positioning
    // =========================================================================

    /// <summary>Scans for an AOB pattern and points at the match, plus <paramref name="offset"/>.</summary>
    public DataScanner Scan(string pattern, int offset = 0)
        => Scan(CompiledPattern.Parse(pattern), offset);

    /// <summary>Scans with a pattern that was parsed once and can be reused.</summary>
    public DataScanner Scan(CompiledPattern pattern, int offset = 0)
    {
        if (pattern is null)
            throw new ArgumentNullException(nameof(pattern));

        _lastPattern = pattern.Source;

        long found;
        bool cacheHit = false;

        if (_cacheResolver is null)
        {
            found = MultiPatternScanner.FindFirst(_region.Span, pattern);
        }
        else
        {
            AobCacheResolution resolution = _cacheResolver.Resolve([pattern], allowParallel: false);
            found = resolution.Offsets[0];
            cacheHit = resolution.CacheHitCount == 1;
        }

        if (found < 0)
        {
            _logger.LogError("Pattern not found: {Pattern}", pattern.Source);
            _scanSuccess = false;
            _currentAddress = 0;
            return this;
        }

        _currentAddress = unchecked((UInt64)((long)_region.ToAddress(found) + offset));
        _scanSuccess = true;

        if (cacheHit)
            _logger.LogDebug("Pattern loaded from AOB cache: {Pattern}", pattern.Source);

        _logger.LogDebug("Pattern matched at 0x{Address:X16}: {Pattern}", _currentAddress, pattern.Source);
        return this;
    }

    /// <summary>Points at an address directly, bypassing the scan.</summary>
    public DataScanner At(UInt64 address)
    {
        _currentAddress = address;
        _scanSuccess = address != 0;
        _lastPattern = null;
        return this;
    }

    /// <summary>Moves the cursor by <paramref name="bytes"/>.</summary>
    public DataScanner Advance(int bytes)
    {
        if (_scanSuccess)
            _currentAddress = unchecked((UInt64)((long)_currentAddress + bytes));

        return this;
    }

    /// <summary>
    /// Enumerates every address matching <paramref name="pattern"/>, for constants the compiler
    /// inlined into several places.
    /// </summary>
    public IReadOnlyList<UInt64> ScanAll(string pattern, int offset = 0, int limit = 64)
    {
        CompiledPattern compiled = CompiledPattern.Parse(pattern);
        List<UInt64> matches = [];

        if (_cacheResolver is not null)
        {
            AobCacheAllResolution resolution = _cacheResolver.ResolveAll(compiled, limit);
            foreach (long matchOffset in resolution.Offsets)
                matches.Add(unchecked((UInt64)((long)_region.ToAddress(matchOffset) + offset)));

            if (resolution.CacheHit)
                _logger.LogDebug("Loaded {Count} matches from AOB cache: {Pattern}", matches.Count, pattern);

            _logger.LogDebug("Pattern matched {Count} times: {Pattern}", matches.Count, pattern);
            return matches;
        }

        ReadOnlySpan<byte> haystack = _region.Span;
        int cursor = 0;

        while (matches.Count < limit && cursor < haystack.Length)
        {
            long found = MultiPatternScanner.FindFirst(haystack.Slice(cursor), compiled);
            if (found < 0)
                break;

            long absoluteOffset = cursor + found;
            matches.Add(unchecked((UInt64)((long)_region.ToAddress(absoluteOffset) + offset)));
            cursor = (int)absoluteOffset + 1;
        }

        _logger.LogDebug("Pattern matched {Count} times: {Pattern}", matches.Count, pattern);
        return matches;
    }

    // =========================================================================
    // Reading
    // =========================================================================

    /// <summary>Reads the target of a CALL or JMP at the cursor.</summary>
    public bool TryReadFlowControlTarget(out UInt64 target, FlowControl type = FlowControl.Call)
    {
        target = 0;

        if (!_scanSuccess)
            return false;

        if (AssemblyUtil.TryGetFlowControlTarget(_currentAddress, type, out target))
        {
            _logger.LogDebug("Flow target 0x{Target:X16} read at 0x{Address:X16}.", target, _currentAddress);
            return true;
        }

        _logger.LogWarning("Failed to decode a {Type} target at 0x{Address:X16}.", type, _currentAddress);
        return false;
    }

    /// <summary>Resolves a CALL or JMP target into a delegate.</summary>
    public bool TryGetFunction<T>(out T? function) where T : Delegate
    {
        function = null;

        if (!TryReadFlowControlTarget(out UInt64 target))
            return false;

        function = Marshal.GetDelegateForFunctionPointer<T>((IntPtr)target);
        return true;
    }

    /// <summary>Converts the cursor itself to a delegate, without following a CALL.</summary>
    public bool TryGetFunctionAtCursor<T>(out T? function) where T : Delegate
    {
        function = null;
        if (!_scanSuccess || _currentAddress == 0 || _currentAddress > UInt32.MaxValue)
            return false;

        function = Marshal.GetDelegateForFunctionPointer<T>((IntPtr)_currentAddress);
        return true;
    }

    /// <summary>
    /// Reads the absolute address encoded by a direct x86 memory operand, e.g. <c>mov eax,[0x401000]</c>.
    /// </summary>
    public bool TryReadAbsoluteAddress(out UInt64 address, int operand = -1)
    {
        address = 0;

        if (!_scanSuccess)
            return false;

        if (AssemblyUtil.TryGetAbsoluteMemoryAddress(_currentAddress, out address, operand))
        {
            _logger.LogDebug("Absolute x86 address 0x{Target:X8} read at 0x{Address:X8}.", address, _currentAddress);
            return true;
        }

        _logger.LogWarning("Failed to decode an absolute x86 memory operand at 0x{Address:X8}.", _currentAddress);
        return false;
    }

    /// <summary>Reads a 32-bit pointer directly from the cursor.</summary>
    public unsafe bool TryReadPointer(out UInt32 pointer)
    {
        pointer = 0;
        if (!_scanSuccess || _currentAddress == 0 || _currentAddress > UInt32.MaxValue)
            return false;

        try
        {
            pointer = *(UInt32*)_currentAddress;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads a memory operand's displacement without wrapping it.</summary>
    public bool TryReadDisplacement(out UInt64 displacement, int operandIndex = 1)
    {
        displacement = 0;

        if (!_scanSuccess)
            return false;

        if (AssemblyUtil.TryGetDisplacementFromInstructionMemory(_currentAddress, out displacement, operandIndex))
        {
            _logger.LogDebug("Displacement 0x{Displacement:X} read at 0x{Address:X16}.", displacement, _currentAddress);
            return true;
        }

        _logger.LogWarning("Failed to decode a displacement at 0x{Address:X16}.", _currentAddress);
        return false;
    }

    // =========================================================================
    // Wrapping
    // =========================================================================

    /// <summary>Wraps the immediate at the cursor so it can be overridden and restored.</summary>
    /// <param name="site">The wrapper, or null on failure.</param>
    /// <param name="operand">Index of the immediate operand.</param>
    /// <param name="constantOffset">Added to the logical value to produce the encoded one.</param>
    /// <param name="name">Label for diagnostics; defaults to the pattern that found it.</param>
    public bool TryGetManagedImmediate<T>(
        out ManagedAssemblyImmediate<T>? site,
        int operand = 0,
        long constantOffset = 0,
        string? name = null)
        where T : unmanaged
    {
        site = null;

        if (!_scanSuccess)
            return false;

        try
        {
            site = new ManagedAssemblyImmediate<T>((IntPtr)_currentAddress, operand, constantOffset, _logger, DescribeSite(name));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to wrap the immediate at 0x{Address:X16}.", _currentAddress);
            return false;
        }
    }

    /// <summary>Wraps the memory displacement at the cursor.</summary>
    /// <param name="site">The wrapper, or null on failure.</param>
    /// <param name="operand">Index of the memory operand. Defaults to 1, matching the wrapper.</param>
    /// <param name="constantOffset">Added to the logical value to produce the encoded one.</param>
    /// <param name="name">Label for diagnostics; defaults to the pattern that found it.</param>
    public bool TryGetManagedDisplacement<T>(
        out ManagedAssemblyDisplacement<T>? site,
        int operand = 1,
        long constantOffset = 0,
        string? name = null)
        where T : unmanaged
    {
        site = null;

        if (!_scanSuccess)
            return false;

        try
        {
            site = new ManagedAssemblyDisplacement<T>((IntPtr)_currentAddress, operand, constantOffset, _logger, DescribeSite(name));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to wrap the displacement at 0x{Address:X16}.", _currentAddress);
            return false;
        }
    }

    /// <summary>
    /// Scans several patterns and gathers the immediates into one value.
    /// </summary>
    /// <param name="patterns">One pattern per site.</param>
    /// <param name="operands">Operand index per pattern. Must be the same length as <paramref name="patterns"/>.</param>
    /// <param name="site">The composite, or null if any pattern or wrapper failed.</param>
    /// <param name="constantOffset">Applied to every site.</param>
    /// <param name="name">Label for diagnostics.</param>
    public bool TryGetMultiManagedImmediate<T>(
        string[] patterns,
        int[] operands,
        out ManagedAssemblyMultiValue<T>? site,
        long constantOffset = 0,
        string? name = null)
        where T : unmanaged
        => TryGetMulti(patterns, operands, out site, constantOffset, name, immediates: true);

    /// <summary>Scans several patterns and gathers the displacements into one value.</summary>
    public bool TryGetMultiManagedDisplacement<T>(
        string[] patterns,
        int[] operands,
        out ManagedAssemblyMultiValue<T>? site,
        long constantOffset = 0,
        string? name = null)
        where T : unmanaged
        => TryGetMulti(patterns, operands, out site, constantOffset, name, immediates: false);

    /// <summary>
    /// Gathers every match of a single pattern into one value, the usual shape when a compiler has
    /// inlined the same constant repeatedly.
    /// </summary>
    public bool TryGetMultiFromPattern<T>(
        string pattern,
        out ManagedAssemblyMultiValue<T>? site,
        int operand = 0,
        long constantOffset = 0,
        string? name = null)
        where T : unmanaged
    {
        site = null;

        IReadOnlyList<UInt64> matches = ScanAll(pattern);
        if (matches.Count == 0)
            return false;

        ManagedAssemblyMultiValue<T> composite = new(name ?? pattern);

        foreach (UInt64 address in matches)
        {
            At(address);

            if (!TryGetManagedImmediate(out ManagedAssemblyImmediate<T>? single, operand, constantOffset, name) || single is null)
            {
                composite.Dispose();
                return false;
            }

            composite.Add(single);
        }

        site = composite;
        return true;
    }

    private bool TryGetMulti<T>(
        string[] patterns,
        int[] operands,
        out ManagedAssemblyMultiValue<T>? site,
        long constantOffset,
        string? name,
        bool immediates)
        where T : unmanaged
    {
        site = null;

        if (patterns is null)
            throw new ArgumentNullException(nameof(patterns));
        if (operands is null)
            throw new ArgumentNullException(nameof(operands));

        if (patterns.Length != operands.Length)
        {
            _logger.LogError("{PatternCount} patterns but {OperandCount} operand indices.", patterns.Length, operands.Length);
            return false;
        }

        ManagedAssemblyMultiValue<T> composite = new(name);

        for (int i = 0; i < patterns.Length; i++)
        {
            Scan(patterns[i]);

            if (!_scanSuccess)
            {
                _logger.LogError("Multi-scan failed at index {Index} for pattern {Pattern}.", i, patterns[i]);
                composite.Dispose();
                return false;
            }

            bool ok = immediates
                ? TryGetManagedImmediate(out ManagedAssemblyImmediate<T>? immediate, operands[i], constantOffset, name) && Add(composite, immediate)
                : TryGetManagedDisplacement(out ManagedAssemblyDisplacement<T>? displacement, operands[i], constantOffset, name) && Add(composite, displacement);

            if (!ok)
            {
                _logger.LogError("Failed to wrap index {Index} at 0x{Address:X16}.", i, _currentAddress);
                composite.Dispose();
                return false;
            }
        }

        site = composite;
        return true;

        static bool Add(ManagedAssemblyMultiValue<T> target, IAssemblyValueSite<T>? candidate)
        {
            if (candidate is null)
                return false;

            target.Add(candidate);
            return true;
        }
    }

    private string DescribeSite(string? name)
        => name ?? _lastPattern ?? $"0x{_currentAddress:X16}";
}
