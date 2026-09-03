using System;
using System.Collections.Generic;

namespace RedBird.Core.Platform;

/// <summary>
/// Determines the in-memory extent of a loaded module from its own headers.
/// <para>
/// <c>ScanRegion.FromModule</c> read <c>SizeOfImage</c> out of a PE optional header, which is correct on Windows and throws <c>BadImageFormatException</c> on everything else, a Linux
/// module starts with <c>\x7fELF</c>, not <c>MZ</c>. ELF sadly has no single "size of image" field, so the span is computed from the program headers; Mach-O likewise from its load commands.
/// </para>
/// </summary>
public static unsafe class ModuleImageLayout
{
    /// <summary>Executable container formats this can measure.</summary>
    public enum ImageFormat
    {
        /// <summary>Unrecognised magic.</summary>
        Unknown,

        /// <summary>Windows PE / COFF.</summary>
        PortableExecutable,

        /// <summary>ELF (Linux, the BSDs).</summary>
        Elf,

        /// <summary>Mach-O 64-bit (macOS).</summary>
        MachO,
    }

    /// <summary>Identifies the container format at <paramref name="baseAddress"/>.</summary>
    public static ImageFormat DetectFormat(IntPtr baseAddress)
    {
        if (baseAddress == IntPtr.Zero)
            return ImageFormat.Unknown;

        byte* p = (byte*)baseAddress;

        if (p[0] == 0x4D && p[1] == 0x5A)   // "MZ"
            return ImageFormat.PortableExecutable;

        if (p[0] == 0x7F && p[1] == 0x45 && p[2] == 0x4C && p[3] == 0x46) // "\x7fELF"
            return ImageFormat.Elf;

        uint magic = *(uint*)p;
        if (magic is 0xFEEDFACF or 0xCFFAEDFE)  // MH_MAGIC_64 / MH_CIGAM_64
            return ImageFormat.MachO;

        return ImageFormat.Unknown;
    }

    /// <summary>
    /// Returns the number of bytes the module occupies in memory, starting at
    /// <paramref name="baseAddress"/>.
    /// </summary>
    public static bool TryGetImageSize(IntPtr baseAddress, out int size)
    {
        size = 0;

        return DetectFormat(baseAddress) switch
        {
            ImageFormat.PortableExecutable => TryGetPeSize(baseAddress, out size),
            ImageFormat.Elf => TryGetElfSize(baseAddress, out size),
            ImageFormat.MachO => TryGetMachOSize(baseAddress, out size),
            _ => false,
        };
    }

    /// <summary>
    /// Returns the executable sections or load segments of a loaded module. The scanner remains
    /// region-agnostic; callers can pass each returned <see cref="ModuleImageRegion.Span"/> to it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="baseAddress"/> is zero.</exception>
    /// <exception cref="BadImageFormatException">The image format is unsupported or malformed.</exception>
    public static IReadOnlyList<ModuleImageRegion> GetExecutableRegions(IntPtr baseAddress)
    {
        if (baseAddress == IntPtr.Zero)
            throw new ArgumentNullException(nameof(baseAddress));

        if (!TryGetExecutableRegions(baseAddress, out IReadOnlyList<ModuleImageRegion> regions))
            throw new BadImageFormatException("The module image format is unsupported or malformed.");

        return regions;
    }

    /// <summary>Attempts to describe every executable region in a loaded module image.</summary>
    public static bool TryGetExecutableRegions(
        IntPtr baseAddress,
        out IReadOnlyList<ModuleImageRegion> regions)
    {
        if (baseAddress == IntPtr.Zero)
        {
            regions = Array.Empty<ModuleImageRegion>();
            return false;
        }

        List<ModuleImageRegion> found = [];
        bool success = DetectFormat(baseAddress) switch
        {
            ImageFormat.PortableExecutable => TryGetPeExecutableRegions(baseAddress, found),
            ImageFormat.Elf => TryGetElfExecutableRegions(baseAddress, found),
            ImageFormat.MachO => TryGetMachOExecutableRegions(baseAddress, found),
            _ => false,
        };

        regions = success ? found : Array.Empty<ModuleImageRegion>();
        return success;
    }

    private static bool TryGetPeExecutableRegions(IntPtr baseAddress, List<ModuleImageRegion> regions)
    {
        byte* image = (byte*)baseAddress;
        int ntOffset = *(int*)(image + 0x3C);
        if (ntOffset <= 0)
            return false;

        byte* nt = image + ntOffset;
        if (*(uint*)nt != 0x0000_4550) // "PE\0\0"
            return false;

        ushort sectionCount = *(ushort*)(nt + 6);
        ushort optionalHeaderSize = *(ushort*)(nt + 20);
        int imageSize = *(int*)(nt + 24 + 56);
        if (sectionCount == 0 || optionalHeaderSize < 60 || imageSize <= 0)
            return false;

        byte* section = nt + 24 + optionalHeaderSize;
        const uint ImageScnMemExecute = 0x2000_0000;

        for (int i = 0; i < sectionCount; i++, section += 40)
        {
            uint characteristics = *(uint*)(section + 36);
            if ((characteristics & ImageScnMemExecute) == 0)
                continue;

            uint virtualSize = *(uint*)(section + 8);
            uint virtualAddress = *(uint*)(section + 12);
            uint rawSize = *(uint*)(section + 16);
            uint requestedSize = Math.Max(virtualSize, rawSize);

            if (requestedSize == 0 || virtualAddress >= (uint)imageSize)
                continue;

            int offset = (int)virtualAddress;
            int length = (int)Math.Min(requestedSize, (uint)(imageSize - offset));
            if (length <= 0)
                continue;

            string name = ReadFixedName(section, 8);
            regions.Add(new ModuleImageRegion((IntPtr)(image + offset), length,
                string.IsNullOrEmpty(name) ? $"section[{i}]" : name));
        }

        return true;
    }

    private static bool TryGetElfExecutableRegions(IntPtr baseAddress, List<ModuleImageRegion> regions)
    {
        byte* image = (byte*)baseAddress;
        if (image[4] != 2) // ELFCLASS64
            return false;

        UInt64 programHeaderOffset = *(UInt64*)(image + 0x20);
        ushort entrySize = *(ushort*)(image + 0x36);
        ushort entryCount = *(ushort*)(image + 0x38);
        if (programHeaderOffset == 0 || entrySize < 0x38 || entryCount == 0)
            return false;

        UInt64 lowestLoadAddress = UInt64.MaxValue;
        for (int i = 0; i < entryCount; i++)
        {
            byte* header = image + programHeaderOffset + ((UInt64)i * entrySize);
            if (*(uint*)header != 1) // PT_LOAD
                continue;

            UInt64 virtualAddress = *(UInt64*)(header + 0x10);
            if (virtualAddress < lowestLoadAddress)
                lowestLoadAddress = virtualAddress;
        }

        if (lowestLoadAddress == UInt64.MaxValue)
            return false;

        for (int i = 0; i < entryCount; i++)
        {
            byte* header = image + programHeaderOffset + ((UInt64)i * entrySize);
            uint type = *(uint*)header;
            uint flags = *(uint*)(header + 4);
            if (type != 1 || (flags & 1) == 0) // PT_LOAD, PF_X
                continue;

            UInt64 virtualAddress = *(UInt64*)(header + 0x10);
            UInt64 memorySize = *(UInt64*)(header + 0x28);
            UInt64 offset = virtualAddress - lowestLoadAddress;
            if (memorySize == 0 || offset > int.MaxValue || memorySize > (UInt64)(int.MaxValue - (int)offset))
                continue;

            regions.Add(new ModuleImageRegion((IntPtr)(image + (int)offset), (int)memorySize, $"PT_LOAD[{i}]"));
        }

        return true;
    }

    private static bool TryGetMachOExecutableRegions(IntPtr baseAddress, List<ModuleImageRegion> regions)
    {
        byte* image = (byte*)baseAddress;
        if (*(uint*)image != 0xFEEDFACF) // MH_MAGIC_64, little-endian
            return false;

        uint commandCount = *(uint*)(image + 0x10);
        uint commandsSize = *(uint*)(image + 0x14);
        byte* commands = image + 0x20;
        byte* commandsEnd = commands + commandsSize;
        UInt64 lowestAddress = UInt64.MaxValue;

        byte* command = commands;
        for (uint i = 0; i < commandCount; i++)
        {
            if (command + 8 > commandsEnd)
                return false;

            uint commandSize = *(uint*)(command + 4);
            if (commandSize < 8 || command + commandSize > commandsEnd)
                return false;

            if (*(uint*)command == 0x19 && commandSize >= 72) // LC_SEGMENT_64
            {
                string name = ReadFixedName(command + 8, 16);
                UInt64 virtualAddress = *(UInt64*)(command + 0x18);
                UInt64 virtualSize = *(UInt64*)(command + 0x20);
                if (name != "__PAGEZERO" && virtualSize != 0 && virtualAddress < lowestAddress)
                    lowestAddress = virtualAddress;
            }

            command += commandSize;
        }

        if (lowestAddress == UInt64.MaxValue)
            return false;

        command = commands;
        for (uint i = 0; i < commandCount; i++)
        {
            uint commandSize = *(uint*)(command + 4);
            if (*(uint*)command == 0x19 && commandSize >= 72)
            {
                int initialProtection = *(int*)(command + 0x3C);
                if ((initialProtection & 4) != 0) // VM_PROT_EXECUTE
                {
                    string name = ReadFixedName(command + 8, 16);
                    UInt64 virtualAddress = *(UInt64*)(command + 0x18);
                    UInt64 virtualSize = *(UInt64*)(command + 0x20);
                    UInt64 offset = virtualAddress - lowestAddress;

                    if (virtualSize != 0 && offset <= int.MaxValue
                        && virtualSize <= (UInt64)(int.MaxValue - (int)offset))
                    {
                        regions.Add(new ModuleImageRegion((IntPtr)(image + (int)offset),
                            (int)virtualSize, string.IsNullOrEmpty(name) ? $"segment[{i}]" : name));
                    }
                }
            }

            command += commandSize;
        }

        return true;
    }

    private static string ReadFixedName(byte* value, int maximumLength)
    {
        int length = 0;
        while (length < maximumLength && value[length] != 0)
            length++;

        if (length == 0)
            return string.Empty;

        char[] characters = new char[length];
        for (int i = 0; i < length; i++)
            characters[i] = (char)value[i];

        return new string(characters);
    }

    private static bool TryGetPeSize(IntPtr baseAddress, out int size)
    {
        size = 0;
        byte* p = (byte*)baseAddress;

        int ntOffset = *(int*)(p + 0x3C);   // IMAGE_DOS_HEADER.e_lfanew
        byte* nt = p + ntOffset;

        if (*(uint*)nt != 0x0000_4550)  // "PE\0\0"
            return false;

        // Signature(4) + IMAGE_FILE_HEADER(20) => optional header at +24.
        // SizeOfImage sits at +56 in both the PE32 and PE32+ optional headers.
        int imageSize = *(int*)(nt + 24 + 56);
        if (imageSize <= 0)
            return false;

        size = imageSize;
        return true;
    }

    /// <summary>
    /// Sums the PT_LOAD segments. For a shared object or PIE the virtual addresses are relative to the load bias, so the span is <c>max(p_vaddr + p_memsz) - min(p_vaddr)</c> either way.
    /// </summary>
    private static bool TryGetElfSize(IntPtr baseAddress, out int size)
    {
        size = 0;
        byte* p = (byte*)baseAddress;

        if (p[4] != 2)  // EI_CLASS: ELFCLASS64
            return false;

        UInt64 programHeaderOffset = *(UInt64*)(p + 0x20);  // e_phoff
        ushort entrySize = *(ushort*)(p + 0x36);    // e_phentsize
        ushort entryCount = *(ushort*)(p + 0x38);   // e_phnum

        if (programHeaderOffset == 0 || entrySize < 0x38 || entryCount == 0)
            return false;

        UInt64 lowest = UInt64.MaxValue;
        UInt64 highest = 0;

        for (int i = 0; i < entryCount; i++)
        {
            byte* header = p + programHeaderOffset + ((UInt64)i * entrySize);

            uint type = *(uint*)header; // p_type
            if (type != 1)  // PT_LOAD
                continue;

            UInt64 virtualAddress = *(UInt64*)(header + 0x10);  // p_vaddr
            UInt64 memorySize = *(UInt64*)(header + 0x28);  // p_memsz

            if (virtualAddress < lowest)
                lowest = virtualAddress;

            UInt64 end = virtualAddress + memorySize;
            if (end > highest)
                highest = end;
        }

        if (lowest == UInt64.MaxValue || highest <= lowest)
            return false;

        UInt64 span = AlignUpToPage(highest - lowest);
        if (span > int.MaxValue)
            return false;

        size = (int)span;
        return true;
    }

    /// <summary>
    /// Sums the LC_SEGMENT_64 load commands, skipping <c>__PAGEZERO</c>, whose multi-gigabyte reservation is not part of the image.
    /// </summary>
    private static bool TryGetMachOSize(IntPtr baseAddress, out int size)
    {
        size = 0;
        byte* p = (byte*)baseAddress;

        if (*(uint*)p != 0xFEEDFACF)    // big-endian images are not supported
            return false;

        uint commandCount = *(uint*)(p + 0x10); // ncmds
        byte* command = p + 0x20;   // sizeof(mach_header_64)

        UInt64 lowest = UInt64.MaxValue;
        UInt64 highest = 0;

        for (uint i = 0; i < commandCount; i++)
        {
            uint cmd = *(uint*)command;
            uint cmdSize = *(uint*)(command + 4);

            if (cmdSize == 0)
                break;

            if (cmd == 0x19)    // LC_SEGMENT_64
            {
                UInt64 vmAddress = *(UInt64*)(command + 0x18);
                UInt64 vmSize = *(UInt64*)(command + 0x20);

                bool isPageZero = *(UInt64*)(command + 8) == 0x455A_4547_4150_5F5FUL; // "__PAGEZE", little-endian
                if (!isPageZero && vmSize != 0)
                {
                    if (vmAddress < lowest)
                        lowest = vmAddress;

                    UInt64 end = vmAddress + vmSize;
                    if (end > highest)
                        highest = end;
                }
            }

            command += cmdSize;
        }

        if (lowest == UInt64.MaxValue || highest <= lowest)
            return false;

        UInt64 span = AlignUpToPage(highest - lowest);
        if (span > int.MaxValue)
            return false;

        size = (int)span;
        return true;
    }

    private static UInt64 AlignUpToPage(UInt64 value)
    {
        UInt64 pageSize = (UInt64)PlatformInfo.PageSize;
        return (value + pageSize - 1) & ~(pageSize - 1);
    }
}
