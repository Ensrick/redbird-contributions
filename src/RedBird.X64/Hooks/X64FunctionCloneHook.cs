using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Logging;
using RedBird.Core.Memory;
using RedBird.X64.Assembly;

namespace RedBird.X64.Hooks;

// ---------------------------------------------------------------------------
// Delegate types
// ---------------------------------------------------------------------------

/// <summary>
/// Called once per matched instruction site during patch generation.
/// Emit any pre-instruction code into <paramref name="asm"/> before returning.
/// The matched instruction itself is re-emitted automatically after the generated code unless
/// <paramref name="suppressOriginal"/> is set to true.
/// </summary>
/// <param name="asm">Assembler targeting the island address space.</param>
/// <param name="original">The matched instruction at its ORIGINAL address (IP is the target's VA).</param>
/// <param name="suppressOriginal">Set true to skip re-emitting the original instruction.</param>
public delegate void FunctionClonePatchSite(Assembler asm, Instruction original, ref bool suppressOriginal);

/// <summary>
/// Predicate that selects which instructions in the cloned function receive a patch callback.
/// </summary>
public delegate bool FunctionClonePatchPredicate(Instruction instruction);

/// <summary>
/// Clones an entire x64 function into executable memory, applies user-defined patches at arbitrary instruction sites within the clone, then redirects the original entry point at the clone with a single 14-byte absolute JMP.
/// <para>
/// Compared to an inline hook this is preferable when you need many injection points inside one
/// function:
/// </para>
/// <list type="bullet">
///   <item>The original body is never modified except at bytes 0-13.</item>
///   <item>Branches, jump targets and RIP-relative references inside the clone are re-encoded by Iced's BlockEncoder.</item>
///   <item>Any number of sites can be patched in one pass with no collision risk between them.</item>
/// </list>
/// <para>
/// <b>Known limitation.</b> A patch injects a <c>call</c> immediately <i>before</i> the matched instruction. 
/// Iced resolves an internal branch by looking the target address up among the original instruction IPs, and an injected instruction carries no original IP, so a branch that
/// jumps directly <i>to</i> a patched instruction lands on the instruction itself and skips the injected call. <see cref="Compile"/> logs a warning listing any patched site that is also a
/// branch target inside the function, so this shows up at build time (hopefully)
/// </para>
/// </summary>
public sealed class X64FunctionCloneHook : IHook
{
    private const int Bitness = 64;

    /// <summary>Slack added on top of the measured blob size before allocating.</summary>
    private const int SizeSlack = 64;

    private readonly ILogger _logger;
    private readonly UInt64 _functionAddress;
    private readonly List<FunctionClonePatch> _patches;
    private readonly List<Delegate> _pinnedDelegates = [];
    private readonly FunctionDisassembly _function;

    private readonly List<PatchSite> _sites = [];

    private IntPtr _cloneAddress = IntPtr.Zero;
    private IntPtr _islandAddress = IntPtr.Zero;
    private byte[] _entryBackup = [];
    private int _entryPatchLength;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>Disassembles the target function and validates the patch set.</summary>
    /// <param name="functionAddress">Absolute VA of the function entry point.</param>
    /// <param name="patches">Patch sites to apply to the clone. At least one is required.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="scanOptions">Disassembly limits and end-detection strategy.</param>
    /// <param name="name">Diagnostic name surfaced through <see cref="IHook.Name"/>.</param>
    public X64FunctionCloneHook(
        UInt64 functionAddress,
        IEnumerable<FunctionClonePatch> patches,
        ILogger? logger = null,
        FunctionScanOptions? scanOptions = null,
        string? name = null)
    {
        if (functionAddress == 0)
            throw new ArgumentOutOfRangeException(nameof(functionAddress), "Function address must be non-zero.");

        _logger = logger ?? NullLogger.Instance;
        _functionAddress = functionAddress;
        _patches = [.. patches];
        Name = name ?? $"clone@0x{functionAddress:X16}";

        if (_patches.Count == 0)
            throw new ArgumentException("At least one patch must be supplied.", nameof(patches));

        _function = FunctionBoundary.Disassemble(functionAddress, scanOptions, _logger);
        _logger.CloneHookDisassembledInstructions(_function.Instructions.Count, _function.ByteLength, functionAddress);
    }

    // -------------------------------------------------------------------------
    // IHook
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public UInt64 TargetAddress => _functionAddress;

    /// <inheritdoc/>
    public HookState State { get; private set; } = HookState.Created;

    /// <inheritdoc/>
    public bool IsInstalled => State == HookState.Installed;

    /// <summary>Address of the compiled clone, or <see cref="IntPtr.Zero"/> before <see cref="Compile"/>.</summary>
    public IntPtr CloneAddress => _cloneAddress;

    /// <summary>Address of the shared blob holding every patch island.</summary>
    public IntPtr IslandAddress => _islandAddress;

    /// <summary>Byte length of the disassembled function.</summary>
    public int FunctionByteLength => _function.ByteLength;

    /// <summary>Instructions decoded from the original function.</summary>
    public IReadOnlyList<Instruction> FunctionInstructions => _function.Instructions;

    // -------------------------------------------------------------------------
    // Compilation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the island blob and the clone body and writes both to executable memory.
    /// Does not redirect the original function, call <see cref="Enable"/> for that.
    /// </summary>
    public void Compile()
    {
        if (State == HookState.Disposed)
            throw new ObjectDisposedException(nameof(X64FunctionCloneHook));
        if (_cloneAddress != IntPtr.Zero)
            throw new InvalidOperationException($"'{Name}' is already compiled.");

        if (_function.Truncated)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Disassembly of 0x{_functionAddress:X16} hit a scan limit without finding the end of the function. Cloning a truncated function produces code that runs off its own end; raise FunctionScanOptions.MaxBytes/MaxInstructions or verify the address really is a function entry point.");
        }

        _entryPatchLength = MeasureEntryPatchLength();

        CollectPatchSites();
        WarnAboutBypassedSites();

        BuildIslands();
        BuildClone();

        State = HookState.Prepared;
    }

    /// <summary>
    /// Number of bytes the entry redirect will overwrite: the absolute jump rounded up to an instruction boundary so the tail of a straddling instruction is NOP-padded.
    /// </summary>
    private int MeasureEntryPatchLength()
    {
        int length = 0;
        foreach (Instruction instruction in _function.Instructions)
        {
            length += instruction.Length;
            if (length >= CodePatch.AbsoluteJumpSize)
                return length;
        }

        State = HookState.Failed;
        throw new InvalidOperationException($"Function at 0x{_functionAddress:X16} is only {length} bytes long; an absolute jump needs {CodePatch.AbsoluteJumpSize}.");
    }

    /// <summary>One instruction in the original function plus every patch that matched it.</summary>
    private sealed class PatchSite
    {
        public required Instruction Instruction { get; init; }
        public required List<FunctionClonePatch> Patches { get; init; }

        /// <summary>Set by a generator that replaces the instruction rather than preceding it.</summary>
        public bool SuppressOriginal { get; set; }

        /// <summary>Label anchoring this site's island; resolved to an address after assembly.</summary>
        public Label Label { get; set; }

        /// <summary>Absolute address of the island, filled in after assembly.</summary>
        public UInt64 IslandRip { get; set; }
    }

    private void CollectPatchSites()
    {
        foreach (Instruction instruction in _function.Instructions)
        {
            List<FunctionClonePatch>? matched = null;

            foreach (FunctionClonePatch patch in _patches)
            {
                if (!patch.Predicate(instruction))
                    continue;

                matched ??= [];
                matched.Add(patch);
            }

            if (matched is not null)
                _sites.Add(new PatchSite { Instruction = instruction, Patches = matched });
        }

        if (_sites.Count == 0)
            _logger.LogWarning("[{Name}] no instruction in 0x{FunctionAddress:X16} matched any patch predicate; the clone will be a plain copy.", Name, _functionAddress);
    }

    /// <summary>
    /// Flags patched sites that are also branch targets inside the function, where the injected call is reachable only by falling through from the previous instruction.
    /// </summary>
    private void WarnAboutBypassedSites()
    {
        if (_sites.Count == 0)
            return;

        HashSet<UInt64> branchTargets = [];
        foreach (Instruction instruction in _function.Instructions)
        {
            if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                branchTargets.Add(instruction.NearBranchTarget);
        }

        foreach (PatchSite site in _sites)
        {
            if (branchTargets.Contains(site.Instruction.IP))
            {
                _logger.LogWarning("[{Name}] the site at 0x{SiteIP:X16} is a branch target inside the function. Control flow arriving via that branch enters the instruction directly and skips the injected patch.", Name, site.Instruction.IP);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Islands
    //
    // Every patch site gets a small blob of generated code ending in RET; the clone reaches it with a near CALL.
    // All islands live in one blob so they cost one allocation and stay within rel32 range of the clone, which shares the same slab.
    // -------------------------------------------------------------------------

    private void BuildIslands()
    {
        if (_sites.Count == 0)
            return;

        Assembler assembler = new(Bitness);

        foreach (PatchSite site in _sites)
        {
            Label label = assembler.CreateLabel();
            assembler.Label(ref label);
            site.Label = label;

            foreach (FunctionClonePatch patch in site.Patches)
            {
                if (patch.PreserveFlags)
                    assembler.pushfq();

                bool suppress = false;
                patch.Generator(assembler, site.Instruction, ref suppress);
                site.SuppressOriginal |= suppress;

                if (patch.PreserveFlags)
                    assembler.popfq();
            }

            assembler.ret();
        }

        // Pass 1: assemble at a provisional base purely to measure. rel32 displacements can change length between bases, hence the slack rather than an exact allocation.
        byte[] probe = AssembleIslands(assembler, _functionAddress);

        _islandAddress = NativeMemoryManager.AllocateStub(_functionAddress, probe.Length + SizeSlack);
        if (_islandAddress == IntPtr.Zero)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Could not allocate {probe.Length + SizeSlack} bytes for patch islands near 0x{_functionAddress:X16}.");
        }

        // Pass 2: assemble at the real address and record where each island landed.
        byte[] islands = AssembleIslands(assembler, (UInt64)_islandAddress, _sites);

        if (islands.Length > probe.Length + SizeSlack)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Island blob for [{Name}] grew from {probe.Length} to {islands.Length} bytes between passes, exceeding the {probe.Length + SizeSlack} byte reservation.");
        }

        // Slab memory: WriteStub handles the write window on W^X hosts and leaves the slab's baseline protection alone, so the next stub handed out is still writable.
        if (!NativeMemoryManager.WriteStub(_islandAddress, islands))
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Could not write the patch islands for [{Name}].");
        }

        foreach (PatchSite site in _sites)
            _logger.CloneHookPatchIslandCreated(site.Instruction.IP, (IntPtr)site.IslandRip, islands.Length, site.SuppressOriginal);

        _logger.LogInformation("[{Name}] {SiteCount} patch islands assembled into {ByteCount} bytes at 0x{IslandAddress:X16}.", Name, _sites.Count, islands.Length, (UInt64)_islandAddress);
    }

    private byte[] AssembleIslands(Assembler assembler, UInt64 rip, List<PatchSite>? recordInto = null)
    {
        using MemoryStream stream = new();

        if (!assembler.TryAssemble(new StreamCodeWriter(stream), rip, out string? error, out AssemblerResult result, BlockEncoderOptions.ReturnNewInstructionOffsets))
            throw new InvalidOperationException($"Island assembly failed for [{Name}] at 0x{rip:X16}: {error}");

        if (recordInto is not null)
        {
            foreach (PatchSite site in recordInto)
            {
                Label label = site.Label;
                site.IslandRip = result.GetLabelRIP(in label);
            }
        }

        return stream.ToArray();
    }

    // -------------------------------------------------------------------------
    // Clone body
    // -------------------------------------------------------------------------

    private void BuildClone()
    {
        List<Instruction> body = BuildCloneInstructions();

        // Same measure-then-allocate-then-encode dance as the islands.
        byte[] probe = Encode(body, _functionAddress);

        _cloneAddress = NativeMemoryManager.AllocateStub(_functionAddress, probe.Length + SizeSlack);
        if (_cloneAddress == IntPtr.Zero)
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Could not allocate {probe.Length + SizeSlack} bytes for the clone of 0x{_functionAddress:X16}.");
        }

        _logger.CloneStubAllocated(_cloneAddress, probe.Length + SizeSlack, _functionAddress);

        byte[] clone = Encode(body, (UInt64)_cloneAddress);

        if (clone.Length > probe.Length + SizeSlack)
        {
            State = HookState.Failed;
            _logger.CloneStubExceeded(clone.Length, probe.Length + SizeSlack);
            throw new InvalidOperationException($"Clone of [{Name}] grew from {probe.Length} to {clone.Length} bytes between passes, exceeding the reservation.");
        }

        if (!NativeMemoryManager.WriteStub(_cloneAddress, clone))
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"Could not write the clone body for [{Name}].");
        }

        _logger.CloneStubWritten(clone.Length);
    }

    /// <summary>
    /// Interleaves the original instruction stream with a <c>call</c> into each site's island.
    /// </summary>
    private List<Instruction> BuildCloneInstructions()
    {
        Dictionary<UInt64, PatchSite> siteByIP = new(_sites.Count);
        foreach (PatchSite site in _sites)
            siteByIP[site.Instruction.IP] = site;

        List<Instruction> body = new(_function.Instructions.Count + _sites.Count);

        foreach (Instruction original in _function.Instructions)
        {
            if (siteByIP.TryGetValue(original.IP, out PatchSite? site))
            {
                // IP is deliberately left at 0. Iced keys its branch-target map on original IPs, throws on duplicate non-zero IPs, and documents 0 as "no original address"
                body.Add(Instruction.CreateBranch(Code.Call_rel32_64, site.IslandRip));

                if (site.SuppressOriginal)
                    continue;
            }

            body.Add(original);
        }

        return body;
    }

    private byte[] Encode(List<Instruction> instructions, UInt64 rip)
    {
        ListCodeWriter writer = new(instructions.Count * 8);
        InstructionBlock block = new(writer, instructions, rip);

        if (!BlockEncoder.TryEncode(Bitness, block, out string? error, out _, BlockEncoderOptions.ReturnNewInstructionOffsets))
        {
            State = HookState.Failed;
            throw new InvalidOperationException($"BlockEncoder failed for [{Name}] (function 0x{_functionAddress:X16}) at 0x{rip:X16}: {error}. This usually means a branch target inside the function lands outside the disassembled range: check for a jump table, or raise FunctionScanOptions.MaxBytes.");
        }

        _logger.CloneHookBlockEncoderResult(writer.Count, rip);
        return writer.ToArray();
    }

    // -------------------------------------------------------------------------
    // Install / uninstall
    // -------------------------------------------------------------------------

    /// <summary>
    /// Redirects the original entry point at the clone with a position-independent absolute jump. <see cref="Compile"/> must have run first.
    /// </summary>
    public void Enable()
    {
        if (State is HookState.Installed or HookState.Disposed or HookState.Failed)
            return;
        if (_cloneAddress == IntPtr.Zero)
            throw new InvalidOperationException($"Call Compile() before Enable() on [{Name}].");

        // Snapshot before the first write so Disable always restores the exact original bytes.
        if (_entryBackup.Length == 0)
            _entryBackup = CodePatch.Read(_functionAddress, _entryPatchLength);

        CodePatch.Write(_functionAddress, CodePatch.BuildAbsoluteJump((UInt64)_cloneAddress, _entryPatchLength));

        State = HookState.Installed;
        _logger.CloneHookEnabled(_functionAddress, _cloneAddress);
    }

    /// <summary>
    /// Restores the original entry bytes. The clone and its islands stay in memory but become unreachable.
    /// </summary>
    public void Disable()
    {
        if (State != HookState.Installed)
            return;

        if (_entryBackup.Length == 0)
        {
            _logger.LogError("[{Name}] no entry byte snapshot for 0x{FunctionAddress:X16}; refusing to restore.", Name, _functionAddress);
            return;
        }

        CodePatch.Write(_functionAddress, _entryBackup);

        State = HookState.Disabled;
        _logger.CloneHookDisabled(_functionAddress);
    }

    // -------------------------------------------------------------------------
    // Delegate lifetime
    // -------------------------------------------------------------------------

    /// <summary>
    /// Roots <paramref name="callback"/> for the lifetime of this hook and returns a stable native function pointer, for islands that call back into managed code.
    /// </summary>
    public IntPtr PinDelegate(Delegate callback)
    {
        _pinnedDelegates.Add(callback);
        return Marshal.GetFunctionPointerForDelegate(callback);
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Restores the entry point and drops references. 
    /// The clone and island blobs are intentionally not freed: both are sub-allocations of a shared slab, and releasing one unmaps memory other live hooks execute from. 
    /// Reclaiming them needs a free-list in the slab allocator.
    /// </summary>
    public void Dispose()
    {
        if (State == HookState.Disposed)
            return;

        try
        {
            Disable();
        }
        finally
        {
            _pinnedDelegates.Clear();
            _cloneAddress = IntPtr.Zero;
            _islandAddress = IntPtr.Zero;
            State = HookState.Disposed;
        }
    }

    // -------------------------------------------------------------------------
    // Inner types
    // -------------------------------------------------------------------------

    /// <summary>Growable <see cref="CodeWriter"/> for BlockEncoder.</summary>
    private sealed class ListCodeWriter(int capacity) : CodeWriter
    {
        private readonly List<byte> _bytes = new(capacity);

        public int Count => _bytes.Count;

        public override void WriteByte(byte value) => _bytes.Add(value);

        public byte[] ToArray() => [.. _bytes];
    }
}
