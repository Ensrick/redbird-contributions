using System;

namespace RedBird.Core.Memory;

/// <summary>
/// Platform-agnostic memory protection flags for use with <see cref="IMemoryManager"/> implementations.
/// Each <see cref="IMemoryManager"/> is responsible for translating these flags into whatever its underlying platform API expects (e.g. Win32 PAGE_* constants, or POSIX PROT_* flags).
/// </summary>
[Flags]
public enum MemoryProtection : uint
{
    /// <summary>
    /// The memory is not accessible at all (equivalent to PAGE_NOACCESS / PROT_NONE).
    /// </summary>
    NoAccess = 0,

    /// <summary>
    /// The memory can be read.
    /// </summary>
    Read = 1 << 0,

    /// <summary>
    /// The memory can be written to.
    /// </summary>
    Write = 1 << 1,

    /// <summary>
    /// The memory can be executed.
    /// </summary>
    Execute = 1 << 2,

    /// <summary>
    /// Copy-on-write semantics (Windows-specific concept; treated as <see cref="Write"/> on POSIX platforms).
    /// </summary>
    WriteCopy = 1 << 3,

    /// <summary>
    /// Readable and writable memory. This is the most common permission for data buffers.
    /// </summary>
    ReadWrite = Read | Write,

    /// <summary>
    /// Readable and executable memory (e.g. for code that does not self-modify).
    /// </summary>
    ExecuteRead = Execute | Read,

    /// <summary>
    /// Readable, writable, and executable memory. Typically used for JIT-style stubs that are written to and then executed, such as the dispatcher's generated code.
    /// </summary>
    ExecuteReadWrite = Execute | Read | Write,

    /// <summary>
    /// Execute + copy-on-write (Windows-specific concept; treated as <see cref="ExecuteReadWrite"/> on POSIX platforms).
    /// </summary>
    ExecuteWriteCopy = Execute | WriteCopy,

    /// <summary>
    /// Raises a one-shot access violation when the page is first touched (Win32 PAGE_GUARD).
    /// Ignored on POSIX.
    /// </summary>
    Guard = 1 << 4,

    /// <summary>Requests uncached memory access (Win32 PAGE_NOCACHE). Ignored on POSIX.</summary>
    NoCache = 1 << 5,

    /// <summary>Requests write-combined memory (Win32 PAGE_WRITECOMBINE). Ignored on POSIX.</summary>
    WriteCombine = 1 << 6,

    /// <summary>
    /// Controls Win32 Control Flow Guard targets: PAGE_TARGETS_INVALID during allocation and PAGE_TARGETS_NO_UPDATE during a protection change. Ignored on POSIX.
    /// </summary>
    ControlFlowGuard = 1 << 7,
}
