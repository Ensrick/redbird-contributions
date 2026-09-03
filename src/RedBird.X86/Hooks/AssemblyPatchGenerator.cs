using Iced.Intel;
using System;

namespace RedBird.X86.Hooks;

/// <summary>
/// Emits the machine code that an <see cref="X86AssemblyPatch"/> writes <em>directly over</em> the target's instructions.
/// <para>
/// Unlike <see cref="InlineHookGenerator"/>, there is no stub, no trampoline and no automatic jump back: whatever this delegate emits becomes the code at <paramref name="address"/>, replacing whatever was there. 
/// The original instructions are neither preserved nor relocated, so if you need to keep any of them, re-emit them yourself.
/// </para>
/// </summary>
/// <param name="asm">
/// The Iced assembler to emit into, configured for 32-bit x86. The body is encoded for <paramref name="address"/>, so a near <c>CALL</c>/<c>JMP</c> and any other EIP-relative operand resolve to their final in-place location.
/// </param>
/// <param name="address">
/// The absolute 32-bit address the patch will occupy and execute from. Use it to compute absolute targets (for example via <c>AssemblyUtil.TryDecode</c>) when a fresh instruction needs to reference the surrounding code.
/// </param>
public delegate void AssemblyPatchGenerator(Assembler asm, UInt64 address);
