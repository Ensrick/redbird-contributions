using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;

namespace RedBird.X64.Assembly.InstructionWalker;

/// <summary>
/// Traverses a function's instructions and extracts operand values: offsets, immediates, branch targets, RIP-relative addresses.
/// <para>
/// Two modes, decided by how many rules the config carries:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>One rule</b>: the walker acts as an enumerator. Each <see cref="TryWalk{T}"/> call returns the next match; it returns false once the stream is exhausted. <see cref="Reset"/> rewinds.
///   </item>
///   <item>
///     <b>Several rules</b>: a one-shot walk. Each rule locates an instruction; a rule without an extractor follows the instruction (into the callee for a <c>call</c>, otherwise to the next address) and hands control to the next rule.
///   </item>
/// </list>
/// <para>
/// <see cref="Matches"/> exposes the same traversal as a plain <see cref="IEnumerable{T}"/>, which composes with LINQ and does not touch enumeration state, usually easier than driving <see cref="TryWalk{T}"/> in a loop.
/// </para>
/// </summary>
/// <remarks>
/// Not thread-safe: <see cref="TryWalk{T}"/> and <see cref="TryWalkContext"/> advance a cursor. Use <see cref="Matches"/> (stateless) from multiple threads, or one walker per thread.
/// </remarks>
public sealed class InstructionWalker(UInt64 startAddress, InstructionWalkerConfig config, ILogger? logger = null)
{
    private readonly UInt64 _startAddress = startAddress;
    private readonly InstructionWalkerConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger? _logger = logger;

    private IReadOnlyList<Instruction>? _instructions;
    private int _cursor;
    private bool _cursorInitialised;
    private bool _multiRuleWalkStarted;

    /// <summary>Address the walk starts from.</summary>
    public UInt64 StartAddress => _startAddress;

    /// <summary>
    /// The disassembled function, decoded on first access and cached for the walker's lifetime.
    /// </summary>
    public IReadOnlyList<Instruction> Instructions => _instructions ??= Disassemble(_startAddress);

    /// <summary>Rewinds enumeration so the next call starts from the beginning again.</summary>
    public void Reset()
    {
        _cursor = 0;
        _cursorInitialised = false;
        _multiRuleWalkStarted = false;
    }

    /// <summary>Drops the cached disassembly as well as the cursor. Use after the target's code changed.</summary>
    public void Invalidate()
    {
        _instructions = null;
        Reset();
    }

    // -------------------------------------------------------------------------
    // Enumerable surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enumerates every instruction matching <paramref name="rule"/>, honouring its start mode and skip. Independent of the <see cref="TryWalk{T}"/> cursor, so it can be called at any time and composed with LINQ.
    /// </summary>
    public IEnumerable<InstructionWalkContext> Matches(InstructionWalkRule rule)
    {
        if (rule is null)
            throw new ArgumentNullException(nameof(rule));

        IReadOnlyList<Instruction> instructions = Instructions;
        bool backwards = rule.StartFrom == InstructionWalkerStartMode.End;
        int index = ResolveStartIndex(rule, instructions);

        while (index >= 0 && index < instructions.Count)
        {
            Instruction instruction = instructions[index];
            index += backwards ? -1 : 1;

            if (rule.Predicate is null || rule.Predicate(instruction))
                yield return new InstructionWalkContext { Address = instruction.IP, Instruction = instruction };
        }
    }

    /// <summary>
    /// Enumerates every successfully extracted value for <paramref name="rule"/>.
    /// </summary>
    public IEnumerable<T> Extract<T>(InstructionWalkRule rule) where T : unmanaged
    {
        if (rule?.Extractor is null)
            throw new ArgumentException("Rule must carry an extractor.", nameof(rule));

        foreach (InstructionWalkContext context in Matches(rule))
        {
            ExtractionResult extraction = rule.Extractor(context.Instruction);
            if (extraction.Success)
                yield return ConvertValue<T>(extraction.Value);
        }
    }

    // -------------------------------------------------------------------------
    // Cursor-based surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the next extracted value. See the type remarks for single- versus multi-rule behaviour.
    /// </summary>
    public bool TryWalk<T>(out T result) where T : unmanaged
    {
        result = default;

        if (_config.WalkRules.Length == 0)
        {
            _logger?.LogWarning("No walk rules configured for 0x{StartAddress:X16}.", _startAddress);
            return false;
        }

        return _config.WalkRules.Length == 1
            ? TryWalkSingleRule(_config.WalkRules[0], out result)
            : TryWalkMultiRule(out result);
    }

    /// <summary>
    /// Returns the next matching instruction without extracting from it. Single-rule configs only.
    /// </summary>
    public bool TryWalkContext(out InstructionWalkContext context)
    {
        context = default;

        if (_config.WalkRules.Length != 1)
        {
            _logger?.LogWarning("TryWalkContext requires exactly one walk rule; this config has {RuleCount}.", _config.WalkRules.Length);
            return false;
        }

        InstructionWalkRule rule = _config.WalkRules[0];

        return TryAdvance(rule, out Instruction instruction)
            && Assign(instruction, out context);

        static bool Assign(in Instruction instruction, out InstructionWalkContext context)
        {
            context = new InstructionWalkContext { Address = instruction.IP, Instruction = instruction };
            return true;
        }
    }

    private bool TryWalkSingleRule<T>(InstructionWalkRule rule, out T result) where T : unmanaged
    {
        result = default;

        while (TryAdvance(rule, out Instruction instruction))
        {
            if (rule.Extractor is null)
            {
                _logger?.LogWarning("Single-rule walk matched at 0x{InstructionAddress:X16} but the rule has no extractor.", instruction.IP);
                return false;
            }

            ExtractionResult extraction = rule.Extractor(instruction);
            if (extraction.Success)
            {
                result = ConvertValue<T>(extraction.Value);
                return true;
            }

            // The extractor rejected this instruction (wrong operand shape, say): keeps searching.
            _logger?.LogTrace("Extractor rejected 0x{InstructionAddress:X16}; continuing.", instruction.IP);
        }

        return false;
    }

    /// <summary>
    /// Advances the shared cursor to the next instruction matching <paramref name="rule"/>.
    /// This is the single positioning routine both cursor-based entry points build on.
    /// </summary>
    private bool TryAdvance(InstructionWalkRule rule, out Instruction instruction)
    {
        instruction = default;

        IReadOnlyList<Instruction> instructions = Instructions;
        if (instructions.Count == 0)
        {
            _logger?.LogWarning("Nothing decoded at 0x{StartAddress:X16}.", _startAddress);
            return false;
        }

        if (!_cursorInitialised)
        {
            _cursor = ResolveStartIndex(rule, instructions);
            _cursorInitialised = true;
        }

        bool backwards = rule.StartFrom == InstructionWalkerStartMode.End;

        while (_cursor >= 0 && _cursor < instructions.Count)
        {
            Instruction candidate = instructions[_cursor];
            _cursor += backwards ? -1 : 1;

            if (rule.Predicate is null || rule.Predicate(candidate))
            {
                instruction = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryWalkMultiRule<T>(out T result) where T : unmanaged
    {
        result = default;

        if (_multiRuleWalkStarted)
            return false; // Multi-rule walks are one-shot; Reset() to run again.

        _multiRuleWalkStarted = true;
        UInt64 currentAddress = _startAddress;

        foreach (InstructionWalkRule rule in _config.WalkRules)
        {
            IReadOnlyList<Instruction> instructions = currentAddress == _startAddress
                ? Instructions
                : Disassemble(currentAddress);

            if (instructions.Count == 0)
            {
                _logger?.LogWarning("Nothing decoded at 0x{CurrentAddress:X16}; aborting walk.", currentAddress);
                return false;
            }

            int index = ResolveStartIndex(rule, instructions);
            if (index < 0 || index >= instructions.Count)
            {
                _logger?.LogWarning("Skip of {InstructionSkip} put the cursor out of bounds ({Index} of {Count}); aborting walk.", rule.InstructionSkip, index, instructions.Count);
                return false;
            }

            bool backwards = rule.StartFrom == InstructionWalkerStartMode.End;
            Instruction? found = null;

            while (index >= 0 && index < instructions.Count)
            {
                Instruction candidate = instructions[index];
                index += backwards ? -1 : 1;

                if (rule.Predicate is null || rule.Predicate(candidate))
                {
                    found = candidate;
                    break;
                }
            }

            if (found is null)
            {
                _logger?.LogWarning("No instruction at 0x{CurrentAddress:X16} matched the rule predicate; aborting walk.", currentAddress);
                return false;
            }

            Instruction instruction = found.Value;

            if (rule.Extractor is not null)
            {
                ExtractionResult extraction = rule.Extractor(instruction);
                if (!extraction.Success)
                {
                    _logger?.LogWarning("Extractor failed at 0x{InstructionAddress:X16}.", instruction.IP);
                    return false;
                }

                result = ConvertValue<T>(extraction.Value);
                return true;
            }

            currentAddress = instruction.FlowControl == FlowControl.Call
                ? instruction.NearBranchTarget
                : instruction.NextIP;
        }

        _logger?.LogWarning("All {RuleCount} rules ran but none extracted a value.", _config.WalkRules.Length);
        return false;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Turns a rule's start mode and skip into an index.
    /// </summary>
    private static int ResolveStartIndex(InstructionWalkRule rule, IReadOnlyList<Instruction> instructions)
    {
        if (rule.StartFrom != InstructionWalkerStartMode.End)
            return Math.Max(0, rule.InstructionSkip);

        int endIndex = instructions.Count - 1;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].Code == Code.Int3)
            {
                endIndex = i;
                break;
            }
        }

        return Math.Max(0, endIndex + rule.InstructionSkip);
    }

    /// <summary>
    /// Reinterprets the low bytes of <paramref name="value"/> as <typeparamref name="T"/>.
    /// </summary>
    private static T ConvertValue<T>(UInt64 value) where T : unmanaged
    {
        if (typeof(T) != typeof(UInt64) && typeof(T) != typeof(UInt32) && typeof(T) != typeof(UInt16) && typeof(T) != typeof(byte) &&
            typeof(T) != typeof(Int64) && typeof(T) != typeof(Int32) && typeof(T) != typeof(Int16) && typeof(T) != typeof(sbyte) &&
            typeof(T) != typeof(IntPtr) && typeof(T) != typeof(UIntPtr))
        {
            throw new NotSupportedException($"Extraction to {typeof(T)} is not supported; use an integral type.");
        }

        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Extraction assumes a little-endian layout.");

        return Unsafe.ReadUnaligned<T>(ref Unsafe.As<UInt64, byte>(ref value));
    }

    private IReadOnlyList<Instruction> Disassemble(UInt64 address)
        => FunctionBoundary.Disassemble(address, _config.ToScanOptions(), _logger).Instructions;
}
