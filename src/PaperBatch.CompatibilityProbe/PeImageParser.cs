using System.Buffers.Binary;
using System.Text;

namespace PaperBatch.CompatibilityProbe;

public sealed record PeImageInfo(string Architecture, IReadOnlySet<string> ExportNames);

public static class PeImageParser
{
    private const int DosHeaderSize = 64;
    private const int FileHeaderSize = 20;
    private const int SectionHeaderSize = 40;
    private const int ExportDirectorySize = 40;
    private const int MaximumExportNames = 1_000_000;
    private const int MaximumExportNameLength = 4096;

    public static PeImageInfo Parse(ReadOnlySpan<byte> image)
    {
        RequireRange(image, 0, DosHeaderSize, "DOS header");
        if (image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            throw new InvalidDataException("The file is not a PE image.");
        }

        var peOffset = ReadInt32(image, 0x3C, "PE header offset");
        if (peOffset < DosHeaderSize)
        {
            throw new InvalidDataException("The PE header offset is invalid.");
        }

        RequireRange(image, peOffset, 4 + FileHeaderSize, "PE file header");
        if (image[peOffset] != (byte)'P' || image[peOffset + 1] != (byte)'E' || image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
        {
            throw new InvalidDataException("The PE signature is missing.");
        }

        var machine = ReadUInt16(image, peOffset + 4, "machine type");
        var sectionCount = ReadUInt16(image, peOffset + 6, "section count");
        var optionalHeaderSize = ReadUInt16(image, peOffset + 20, "optional header size");
        var optionalOffset = checked(peOffset + 4 + FileHeaderSize);
        RequireRange(image, optionalOffset, optionalHeaderSize, "optional header");

        var optionalMagic = ReadUInt16(image, optionalOffset, "optional header magic");
        var (dataDirectoryOffset, numberOfRvaAndSizesOffset, sizeOfHeadersOffset, requiredOptionalSize) = optionalMagic switch
        {
            0x10B => (96, 92, 60, 104),
            0x20B => (112, 108, 60, 120),
            _ => throw new InvalidDataException("The PE optional header format is unsupported.")
        };

        var architecture = ArchitectureForHeader(machine, optionalMagic);

        if (optionalHeaderSize < requiredOptionalSize)
        {
            throw new InvalidDataException("The PE optional header is truncated.");
        }

        var sizeOfHeaders = ReadUInt32(image, optionalOffset + sizeOfHeadersOffset, "header size");
        var numberOfRvaAndSizes = ReadUInt32(image, optionalOffset + numberOfRvaAndSizesOffset, "data directory count");
        var availableDirectoryCount = (optionalHeaderSize - dataDirectoryOffset) / sizeof(ulong);
        if (numberOfRvaAndSizes > availableDirectoryCount)
        {
            throw new InvalidDataException("The PE data directory count exceeds the optional header size.");
        }

        var exportRva = ReadUInt32(image, optionalOffset + dataDirectoryOffset, "export table RVA");
        var exportSize = ReadUInt32(image, optionalOffset + dataDirectoryOffset + 4, "export table size");
        var sectionOffset = checked(optionalOffset + optionalHeaderSize);
        RequireRange(image, sectionOffset, checked(sectionCount * SectionHeaderSize), "section table");
        var sections = ReadSections(image, sectionOffset, sectionCount);

        if (numberOfRvaAndSizes == 0)
        {
            if (exportRva != 0 || exportSize != 0)
            {
                throw new InvalidDataException("The PE export directory is present but NumberOfRvaAndSizes does not declare it.");
            }

            return new PeImageInfo(architecture, new HashSet<string>(StringComparer.Ordinal));
        }

        if (exportRva == 0 && exportSize == 0)
        {
            return new PeImageInfo(architecture, new HashSet<string>(StringComparer.Ordinal));
        }

        if (exportRva == 0 || exportSize == 0)
        {
            throw new InvalidDataException("The PE export directory RVA and size must either both be zero or both be nonzero.");
        }

        if (exportSize < ExportDirectorySize)
        {
            throw new InvalidDataException("The PE export directory is truncated.");
        }

        if (exportSize > int.MaxValue)
        {
            throw new InvalidDataException("The PE export directory exceeds the supported size.");
        }

        var exportOffset = RvaToOffset(image, exportRva, (int)exportSize, sizeOfHeaders, sections, "export directory");
        var functionCount = ReadUInt32(image, exportOffset + 20, "export function count");
        var nameCount = ReadUInt32(image, exportOffset + 24, "export name count");
        var functionsRva = ReadUInt32(image, exportOffset + 28, "export functions RVA");
        var namesRva = ReadUInt32(image, exportOffset + 32, "export names RVA");
        var ordinalsRva = ReadUInt32(image, exportOffset + 36, "export name ordinals RVA");
        if (functionCount > MaximumExportNames || nameCount > MaximumExportNames)
        {
            throw new InvalidDataException("The PE export function or name count exceeds the supported limit.");
        }

        if (functionCount == 0)
        {
            if (nameCount != 0)
            {
                throw new InvalidDataException("The PE export names reference an empty function table.");
            }

            return new PeImageInfo(architecture, new HashSet<string>(StringComparer.Ordinal));
        }

        if (functionsRva == 0)
        {
            throw new InvalidDataException("The PE export functions table is missing.");
        }

        var functionTableSize = checked((int)functionCount * sizeof(uint));
        var functionTableOffset = RvaToOffset(image, functionsRva, functionTableSize, sizeOfHeaders, sections, "export functions table");
        if (nameCount == 0)
        {
            return new PeImageInfo(architecture, new HashSet<string>(StringComparer.Ordinal));
        }

        if (namesRva == 0 || ordinalsRva == 0)
        {
            throw new InvalidDataException("The PE export names or ordinals table is missing.");
        }

        var nameTableSize = checked((int)nameCount * sizeof(uint));
        var nameTableOffset = RvaToOffset(image, namesRva, nameTableSize, sizeOfHeaders, sections, "export names table");
        var ordinalTableSize = checked((int)nameCount * sizeof(ushort));
        var ordinalTableOffset = RvaToOffset(image, ordinalsRva, ordinalTableSize, sizeOfHeaders, sections, "export name ordinals table");
        var exports = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < nameCount; index++)
        {
            var nameRva = ReadUInt32(image, nameTableOffset + checked((int)index * sizeof(uint)), "export name RVA");
            if (nameRva == 0)
            {
                throw new InvalidDataException("A PE export name RVA is zero.");
            }

            var functionIndex = ReadUInt16(image, ordinalTableOffset + checked((int)index * sizeof(ushort)), "export name ordinal");
            if (functionIndex >= functionCount)
            {
                throw new InvalidDataException("A PE export name ordinal points outside the functions table.");
            }

            var functionRva = ReadUInt32(image, functionTableOffset + checked(functionIndex * sizeof(uint)), "export function RVA");
            if (functionRva == 0)
            {
                throw new InvalidDataException("A PE export name points to an empty function entry.");
            }

            var functionOffset = RvaToOffset(image, functionRva, 1, sizeOfHeaders, sections, "export function");
            var exportDirectoryEnd = (ulong)exportRva + exportSize;
            if (functionRva >= exportRva && (ulong)functionRva < exportDirectoryEnd)
            {
                var forwarderBytes = Math.Min(
                    (ulong)AvailableBytes(image, functionRva, sizeOfHeaders, sections),
                    exportDirectoryEnd - functionRva);
                ReadExportName(image, functionOffset, checked((int)forwarderBytes));
            }

            var nameOffset = RvaToOffset(image, nameRva, 1, sizeOfHeaders, sections, "export name");
            exports.Add(ReadExportName(image, nameOffset, AvailableBytes(image, nameRva, sizeOfHeaders, sections)));
        }

        return new PeImageInfo(architecture, exports);
    }

    private static IReadOnlyList<Section> ReadSections(ReadOnlySpan<byte> image, int offset, ushort count)
    {
        var sections = new List<Section>(count);
        for (var index = 0; index < count; index++)
        {
            var current = checked(offset + (index * SectionHeaderSize));
            sections.Add(new Section(
                VirtualSize: ReadUInt32(image, current + 8, "section virtual size"),
                VirtualAddress: ReadUInt32(image, current + 12, "section virtual address"),
                RawSize: ReadUInt32(image, current + 16, "section raw size"),
                RawPointer: ReadUInt32(image, current + 20, "section raw pointer")));
        }

        return sections;
    }

    private static int RvaToOffset(
        ReadOnlySpan<byte> image,
        uint rva,
        int requiredLength,
        uint sizeOfHeaders,
        IReadOnlyList<Section> sections,
        string description)
    {
        if (rva <= int.MaxValue && rva < sizeOfHeaders && (ulong)rva + (uint)requiredLength <= sizeOfHeaders)
        {
            var headerOffset = checked((int)rva);
            RequireRange(image, headerOffset, requiredLength, description);
            return headerOffset;
        }

        foreach (var section in sections)
        {
            var spanSize = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || (ulong)rva >= (ulong)section.VirtualAddress + spanSize)
            {
                continue;
            }

            var delta = rva - section.VirtualAddress;
            if ((ulong)delta + (uint)requiredLength > section.RawSize)
            {
                throw new InvalidDataException($"The PE {description} points outside the file-backed section data.");
            }

            var fileOffset = (ulong)section.RawPointer + delta;
            if (fileOffset > int.MaxValue || fileOffset + (uint)requiredLength > (ulong)image.Length)
            {
                throw new InvalidDataException($"The PE {description} points outside the file.");
            }

            return (int)fileOffset;
        }

        throw new InvalidDataException($"The PE {description} RVA is outside the headers and sections.");
    }

    private static int AvailableBytes(ReadOnlySpan<byte> image, uint rva, uint sizeOfHeaders, IReadOnlyList<Section> sections)
    {
        if (rva <= int.MaxValue && rva < sizeOfHeaders)
        {
            return checked((int)Math.Min(sizeOfHeaders - rva, (uint)(image.Length - checked((int)rva))));
        }

        foreach (var section in sections)
        {
            var spanSize = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || (ulong)rva >= (ulong)section.VirtualAddress + spanSize)
            {
                continue;
            }

            var delta = rva - section.VirtualAddress;
            var fileOffset = (ulong)section.RawPointer + delta;
            if (fileOffset >= (ulong)image.Length)
            {
                return 0;
            }

            var sectionRemaining = (ulong)(section.RawSize - delta);
            var imageRemaining = (ulong)image.Length - fileOffset;
            return checked((int)Math.Min(sectionRemaining, imageRemaining));
        }

        return 0;
    }

    private static string ReadExportName(ReadOnlySpan<byte> image, int offset, int availableBytes)
    {
        var length = Math.Min(Math.Min(availableBytes, MaximumExportNameLength), image.Length - offset);
        var terminator = image.Slice(offset, length).IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("A PE export name is not null-terminated within the supported limit.");
        }

        var name = Encoding.ASCII.GetString(image.Slice(offset, terminator));
        if (name.Length == 0)
        {
            throw new InvalidDataException("A PE export name is empty.");
        }

        return name;
    }

    private static string ArchitectureForHeader(ushort machine, ushort optionalMagic) => machine switch
    {
        0x014C when optionalMagic == 0x10B => "x86",
        0x8664 when optionalMagic == 0x20B => "x64",
        0xAA64 when optionalMagic == 0x20B => "arm64",
        0x014C or 0x8664 or 0xAA64 => throw new InvalidDataException("The PE machine type and optional-header bitness disagree."),
        _ => $"unknown-0x{machine:X4}"
    };

    private static int ReadInt32(ReadOnlySpan<byte> image, int offset, string description)
    {
        RequireRange(image, offset, sizeof(int), description);
        return BinaryPrimitives.ReadInt32LittleEndian(image.Slice(offset, sizeof(int)));
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> image, int offset, string description)
    {
        RequireRange(image, offset, sizeof(ushort), description);
        return BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset, sizeof(ushort)));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> image, int offset, string description)
    {
        RequireRange(image, offset, sizeof(uint), description);
        return BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset, sizeof(uint)));
    }

    private static void RequireRange(ReadOnlySpan<byte> image, int offset, int length, string description)
    {
        if (offset < 0 || length < 0 || (ulong)offset + (ulong)length > (ulong)image.Length)
        {
            throw new InvalidDataException($"The PE {description} is truncated or invalid.");
        }
    }

    private sealed record Section(uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer);
}

public sealed record PdfiumApiCapabilities(
    bool CoreApi,
    bool PageImportApi,
    bool FormFillApi,
    IReadOnlyList<string> RelevantExports);

public static class PdfiumCapabilityClassifier
{
    private static readonly string[] RelevantNames =
    [
        "FPDF_InitLibrary",
        "FPDF_LoadDocument",
        "FPDF_ImportPages",
        "FPDF_ImportPagesByIndex",
        "FPDFDOC_InitFormFillEnvironment",
        "FPDF_FFLDraw"
    ];

    public static PdfiumApiCapabilities Classify(IReadOnlySet<string> exports)
    {
        var relevant = RelevantNames.Where(exports.Contains).ToArray();
        return new PdfiumApiCapabilities(
            CoreApi: exports.Contains("FPDF_InitLibrary") && exports.Contains("FPDF_LoadDocument"),
            PageImportApi: exports.Contains("FPDF_ImportPages") || exports.Contains("FPDF_ImportPagesByIndex"),
            FormFillApi: exports.Contains("FPDFDOC_InitFormFillEnvironment") && exports.Contains("FPDF_FFLDraw"),
            RelevantExports: relevant);
    }
}
