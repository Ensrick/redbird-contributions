using System;

namespace RedBird.Backends.NativeX64;

/// <summary>
/// How the jump to the callback is written into the target's prologue.
/// </summary>
/// <remarks>
/// Ported from PolyHook2's <c>detour_scheme_t</c>. The trade-off is always the same: fewer bytes overwritten means a wider range of hookable functions, at the cost of either an extra allocation or clobbered registers.
/// </remarks>
[Flags]
public enum DetourScheme
{
    /// <summary>Nothing. Not a valid selection.</summary>
    None = 0,

    /// <summary>
    /// <c>jmp qword ptr [rip+disp32]</c>, <b>6 bytes</b>, with the 8-byte destination in a slot
    /// allocated near the target.
    /// <para>
    /// The best option in nearly every case: smallest prologue footprint, no registers touched, no stack touched. 
    /// PolyHook calls this VALLOC2 after the Win32 API it uses to place the slot, here it comes out of the same near-allocator the trampoline uses, so it works on POSIX too.
    /// </para>
    /// </summary>
    Indirect = 1 << 0,

    /// <summary>
    /// <c>jmp qword ptr [rip+0]</c> followed by the inline 8-byte destination, <b>14 bytes</b>,
    /// no allocation, no registers or stack touched.
    /// <para>
    /// The fallback when no memory can be placed within +/-2 GB. Needs a prologue long enough to absorb 14 bytes, which excludes a fair number of small functions.
    /// </para>
    /// </summary>
    Absolute = 1 << 1,

    /// <summary>
    /// <c>mov rax, imm64; push rax; ret</c>, <b>12 bytes</b>, clobbers <c>rax</c> and writes below the stack pointer.
    /// <para>
    /// Two bytes shorter than <see cref="Absolute"/>, which could make the difference on a tight prologue. <c>rax</c> is caller-saved in both x64 ABIs and holds no incoming argument, so clobbering it at function entry is safe.
    /// A callback that wants the original <c>rax</c> will not see it.
    /// </para>
    /// <para>
    /// <b>Not selected by default on System V</b>, see <see cref="PushRetRedZoneSafe"/>.
    /// </para>
    /// </summary>
    PushRet = 1 << 2,

    /// <summary>
    /// The red-zone-preserving form of <see cref="PushRet"/>: <c>lea rsp, [rsp-0x80]; push rax; mov rax, imm64; xchg [rsp], rax; ret 0x80</c>, <b>23 bytes</b>.
    /// <para>
    /// This is PolyHook's default INPLACE scheme, and the reason it exists is the System V red zone: a bare <c>push</c> at function entry writes 8 bytes below <c>rsp</c>, which a leaf caller is entitled to be using. 
    /// Stepping over the 128-byte red zone first avoids that, at nearly twice the byte cost.
    /// </para>
    /// <para>
    /// Since it is longer than <see cref="Absolute"/>, which touches no stack at all, it is selected only when asked for explicitly.
    /// </para>
    /// </summary>
    PushRetRedZoneSafe = 1 << 3,

    /// <summary>The default ordering: try <see cref="Indirect"/>, then <see cref="Absolute"/>, then <see cref="PushRet"/>.</summary>
    Recommended = Indirect | Absolute | PushRet,

    /// <summary>Every scheme, including the red-zone-safe variant.</summary>
    All = Indirect | Absolute | PushRet | PushRetRedZoneSafe,
}
