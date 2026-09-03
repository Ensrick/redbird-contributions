using System;
using System.Collections.Generic;

namespace RedBird.Core.Memory.Scanners;

/// <summary>
/// Reusable search index for a group of compiled patterns. Build it once when the same signatures will be applied to more than one region.
/// </summary>
/// <remarks>
/// A set snapshots pattern references and their anchor index. Treat the byte and mask arrays of those patterns as immutable for the lifetime of the set.
/// </remarks>
public sealed class CompiledPatternSet
{
    private readonly CompiledPattern[] _patterns;

    /// <summary>Builds a reusable index over <paramref name="patterns"/>.</summary>
    public CompiledPatternSet(IReadOnlyList<CompiledPattern> patterns)
    {
        if (patterns is null)
            throw new ArgumentNullException(nameof(patterns));

        _patterns = new CompiledPattern[patterns.Count];
        for (int i = 0; i < patterns.Count; i++)
        {
            _patterns[i] = patterns[i] ?? throw new ArgumentException($"Pattern at index {i} is null.", nameof(patterns));
        }

        Index = MultiPatternScanner.AnchorIndex.Build(_patterns);
    }

    /// <summary>Number of patterns represented by this index.</summary>
    public int Count => _patterns.Length;

    /// <summary>Gets a pattern by its result index.</summary>
    public CompiledPattern this[int index] => _patterns[index];

    internal MultiPatternScanner.AnchorIndex Index { get; }
}
