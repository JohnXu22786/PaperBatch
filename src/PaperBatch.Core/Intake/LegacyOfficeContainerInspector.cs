using System.Buffers.Binary;
using System.Text;

namespace PaperBatch.Core.Intake;

internal static class LegacyOfficeContainerInspector
{
    private const uint FreeSector = uint.MaxValue;
    private const uint EndOfChain = uint.MaxValue - 1;
    private const uint FatSector = uint.MaxValue - 2;
    private const uint DifatSector = uint.MaxValue - 3;
    private const uint NoStream = uint.MaxValue;
    private const int MaximumExcelEncryptionScanBytes = 64 * 1024;

    public static bool HasEncryptedPackageStreams(Stream input, long length, long maximumMetadataBytes)
    {
        Span<byte> header = stackalloc byte[512];
        if (length < header.Length || !ReadExactlyAt(input, 0, header) ||
            !header[..8].SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 })) return false;

        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[26..28]);
        var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]);
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[30..32]);
        var sectorSize = sectorShift switch { 9 => 512, 12 => 4096, _ => 0 };
        if (byteOrder != 0xfffe || majorVersion is not 3 and not 4 ||
            majorVersion == 3 && sectorShift != 9 || majorVersion == 4 && sectorShift != 12 || sectorSize == 0 ||
            length < sectorSize || (length - sectorSize) % sectorSize != 0 ||
            majorVersion == 3 && BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]) != 0)
        {
            return false;
        }

        var sectorCount = (length - sectorSize) / sectorSize;
        if (sectorCount <= 0 || sectorCount > Math.Min(int.MaxValue, maximumMetadataBytes / sizeof(uint))) return false;
        var numberOfFatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[44..48]);
        var firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header[48..52]);
        var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[68..72]);
        var numberOfDifatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[72..76]);
        if (numberOfFatSectors == 0 || numberOfFatSectors > sectorCount || firstDirectorySector >= sectorCount ||
            numberOfFatSectors * (uint)(sectorSize / sizeof(uint)) < sectorCount ||
            numberOfDifatSectors == 0 && firstDifatSector != EndOfChain ||
            numberOfDifatSectors > 0 && (firstDifatSector >= sectorCount || numberOfDifatSectors > sectorCount)) return false;

        var fatSectors = new List<uint>(checked((int)numberOfFatSectors));
        var fatSectorIds = new HashSet<uint>();
        for (var index = 0; index < 109; index++)
        {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(76 + index * 4, 4));
            if (sector != FreeSector && !AddFatSector(sector, sectorCount, fatSectors, fatSectorIds)) return false;
        }

        var difatSectors = new HashSet<uint>();
        var currentDifat = firstDifatSector;
        var entriesPerSector = sectorSize / sizeof(uint);
        for (var index = 0; index < numberOfDifatSectors; index++)
        {
            if (currentDifat >= sectorCount || fatSectorIds.Contains(currentDifat) || !difatSectors.Add(currentDifat)) return false;
            var difat = new byte[sectorSize];
            if (!ReadSector(input, currentDifat, sectorSize, difat)) return false;
            for (var item = 0; item < entriesPerSector - 1; item++)
            {
                var sector = BinaryPrimitives.ReadUInt32LittleEndian(difat.AsSpan(item * 4, 4));
                if (sector != FreeSector && !AddFatSector(sector, sectorCount, fatSectors, fatSectorIds)) return false;
            }

            var nextDifat = BinaryPrimitives.ReadUInt32LittleEndian(difat.AsSpan((entriesPerSector - 1) * 4, 4));
            if (index == numberOfDifatSectors - 1)
            {
                if (nextDifat != EndOfChain) return false;
            }
            else if (nextDifat >= sectorCount)
            {
                return false;
            }

            currentDifat = nextDifat;
        }

        if (fatSectors.Count != numberOfFatSectors || fatSectors.Any(difatSectors.Contains)) return false;
        var fat = new uint[checked((int)sectorCount)];
        var fatIndex = 0;
        foreach (var fatSectorId in fatSectors)
        {
            var bytes = new byte[sectorSize];
            if (!ReadSector(input, fatSectorId, sectorSize, bytes)) return false;
            for (var item = 0; item < entriesPerSector && fatIndex < fat.Length; item++, fatIndex++)
            {
                fat[fatIndex] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(item * 4, 4));
            }
        }

        if (fatIndex < fat.Length || fatSectors.Any(sector => fat[sector] != FatSector) ||
            difatSectors.Any(sector => fat[sector] != DifatSector)) return false;
        var reservedSectors = fatSectors.Concat(difatSectors).ToHashSet();
        var chainState = new SectorChainTraversalState(sectorCount);
        if (!TryReadSectorChain(firstDirectorySector, fat, sectorCount, reservedSectors, chainState, out var directorySectors)) return false;
        var directoryLength = (long)directorySectors.Count * sectorSize;
        if (directoryLength > maximumMetadataBytes) return false;
        var directoryBytes = new byte[checked((int)directoryLength)];
        for (var index = 0; index < directorySectors.Count; index++)
        {
            if (!ReadSector(input, directorySectors[index], sectorSize, directoryBytes.AsSpan(index * sectorSize, sectorSize))) return false;
        }

        if (!TryReadDirectoryEntries(directoryBytes, majorVersion, sectorSize, sectorCount, fat, reservedSectors, chainState, out var entries) ||
            entries.Count == 0 || entries[0].ObjectType != 5 || !IsReachableDirectoryTree(entries)) return false;

        var names = entries.Where(entry => entry.ObjectType == 2).Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("EncryptionInfo") && names.Contains("EncryptedPackage");
    }

    public static bool IsValidOfficeDocument(Stream input, long length, SourceFormat expected, long maximumMetadataBytes, out bool isEncrypted)
    {
        isEncrypted = false;
        Span<byte> header = stackalloc byte[512];
        if (length < header.Length || !ReadExactlyAt(input, 0, header)) return false;

        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[26..28]);
        var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]);
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[30..32]);
        var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]);
        var sectorSize = sectorShift switch { 9 => 512, 12 => 4096, _ => 0 };
        if (byteOrder != 0xfffe || majorVersion is not 3 and not 4 ||
            majorVersion == 3 && sectorShift != 9 || majorVersion == 4 && sectorShift != 12 ||
            miniSectorShift != 6 || sectorSize == 0 || length < sectorSize || (length - sectorSize) % sectorSize != 0 ||
            majorVersion == 3 && BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[56..60]) != 4096)
        {
            return false;
        }

        var sectorCount = (length - sectorSize) / sectorSize;
        var maximumSectors = Math.Min(int.MaxValue, maximumMetadataBytes / sizeof(uint));
        if (sectorCount <= 0 || sectorCount > maximumSectors) return false;

        var numberOfFatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[44..48]);
        var firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header[48..52]);
        var firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[60..64]);
        var numberOfMiniFatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[64..68]);
        var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[68..72]);
        var numberOfDifatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[72..76]);
        if (numberOfFatSectors == 0 || numberOfFatSectors > sectorCount || firstDirectorySector >= sectorCount ||
            numberOfFatSectors * (uint)(sectorSize / sizeof(uint)) < sectorCount ||
            numberOfMiniFatSectors == 0 && firstMiniFatSector != EndOfChain ||
            numberOfMiniFatSectors > 0 && (firstMiniFatSector >= sectorCount || numberOfMiniFatSectors > sectorCount ||
                                            (long)numberOfMiniFatSectors * sectorSize > maximumMetadataBytes) ||
            numberOfDifatSectors == 0 && firstDifatSector != EndOfChain ||
            numberOfDifatSectors > 0 && (firstDifatSector >= sectorCount || numberOfDifatSectors > sectorCount))
        {
            return false;
        }

        var fatSectors = new List<uint>(checked((int)numberOfFatSectors));
        var fatSectorIds = new HashSet<uint>();
        for (var index = 0; index < 109; index++)
        {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(76 + index * 4, 4));
            if (sector != FreeSector && !AddFatSector(sector, sectorCount, fatSectors, fatSectorIds)) return false;
        }

        var difatSectors = new HashSet<uint>();
        var currentDifat = firstDifatSector;
        var entriesPerSector = sectorSize / sizeof(uint);
        for (var index = 0; index < numberOfDifatSectors; index++)
        {
            if (currentDifat >= sectorCount || fatSectorIds.Contains(currentDifat) || !difatSectors.Add(currentDifat)) return false;
            var difat = new byte[sectorSize];
            if (!ReadSector(input, currentDifat, sectorSize, difat)) return false;
            for (var item = 0; item < entriesPerSector - 1; item++)
            {
                var sector = BinaryPrimitives.ReadUInt32LittleEndian(difat.AsSpan(item * 4, 4));
                if (sector != FreeSector && !AddFatSector(sector, sectorCount, fatSectors, fatSectorIds)) return false;
            }

            var nextDifat = BinaryPrimitives.ReadUInt32LittleEndian(difat.AsSpan((entriesPerSector - 1) * 4, 4));
            if (index == numberOfDifatSectors - 1)
            {
                if (nextDifat != EndOfChain) return false;
            }
            else if (nextDifat >= sectorCount)
            {
                return false;
            }

            currentDifat = nextDifat;
        }

        if (fatSectors.Count != numberOfFatSectors || fatSectors.Any(difatSectors.Contains)) return false;
        var fat = new uint[checked((int)sectorCount)];
        var fatIndex = 0;
        foreach (var fatSectorId in fatSectors)
        {
            var bytes = new byte[sectorSize];
            if (!ReadSector(input, fatSectorId, sectorSize, bytes)) return false;
            for (var item = 0; item < entriesPerSector && fatIndex < fat.Length; item++, fatIndex++)
            {
                fat[fatIndex] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(item * 4, 4));
            }
        }

        if (fatIndex < fat.Length || fatSectors.Any(sector => fat[sector] != FatSector) ||
            difatSectors.Any(sector => fat[sector] != DifatSector)) return false;
        for (var index = 0; index < fat.Length; index++)
        {
            var value = fat[index];
            if (value >= sectorCount && value is not FreeSector and not EndOfChain and not FatSector and not DifatSector) return false;
        }

        var reservedSectors = fatSectors.Concat(difatSectors).ToHashSet();
        var chainState = new SectorChainTraversalState(sectorCount);
        if (!TryReadSectorChain(firstDirectorySector, fat, sectorCount, reservedSectors, chainState, out var directorySectors)) return false;
        if (majorVersion == 4 && BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]) != directorySectors.Count) return false;
        var directoryLength = (long)directorySectors.Count * sectorSize;
        if (directoryLength > maximumMetadataBytes) return false;

        var directoryBytes = new byte[checked((int)directoryLength)];
        for (var index = 0; index < directorySectors.Count; index++)
        {
            if (!ReadSector(input, directorySectors[index], sectorSize, directoryBytes.AsSpan(index * sectorSize, sectorSize))) return false;
        }

        if (!TryReadDirectoryEntries(directoryBytes, majorVersion, sectorSize, sectorCount, fat, reservedSectors, chainState, out var entries) ||
            entries.Count == 0 || entries[0].ObjectType != 5 || !IsReachableDirectoryTree(entries)) return false;

        var root = entries[0];
        List<uint> miniFatSectors = [];
        uint[] miniFat = [];
        if (numberOfMiniFatSectors > 0)
        {
            if (!TryReadSectorChain(firstMiniFatSector, fat, sectorCount, reservedSectors, chainState, out miniFatSectors) ||
                miniFatSectors.Count != numberOfMiniFatSectors) return false;
            miniFat = new uint[miniFatSectors.Count * sectorSize / sizeof(uint)];
            var miniFatIndex = 0;
            foreach (var miniFatSector in miniFatSectors)
            {
                var bytes = new byte[sectorSize];
                if (!ReadSector(input, miniFatSector, sectorSize, bytes)) return false;
                for (var index = 0; index < sectorSize / sizeof(uint); index++)
                {
                    miniFat[miniFatIndex++] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4, 4));
                }
            }
        }

        List<uint> rootMiniStreamSectors = [];
        if (root.StreamLength > 0)
        {
            var requiredRootSectors = (root.StreamLength + sectorSize - 1) / sectorSize;
            if (!TryReadSectorChain(root.FirstSector, fat, sectorCount, reservedSectors, chainState, out rootMiniStreamSectors) ||
                rootMiniStreamSectors.Count != requiredRootSectors) return false;
        }

        var expectedNames = expected switch
        {
            SourceFormat.Doc => new[] { "WordDocument" },
            SourceFormat.Xls => new[] { "Workbook", "Book" },
            SourceFormat.Ppt => new[] { "PowerPoint Document" },
            _ => Array.Empty<string>()
        };
        var familyStream = FindRootStorageStream(entries, expectedNames);
        if (familyStream is null) return false;

        if (familyStream.StreamLength < 4096 &&
            (root.StreamLength == 0 || numberOfMiniFatSectors == 0 ||
             !TryReadMiniStreamChain(familyStream.FirstSector, familyStream.StreamLength, root.StreamLength, miniFat)))
        {
            return false;
        }

        if (expected is SourceFormat.Doc or SourceFormat.Xls)
        {
            var prefixLength = expected == SourceFormat.Doc ? 32 : MaximumExcelEncryptionScanBytes;
            if (!TryReadStreamPrefix(input, familyStream, sectorSize, rootMiniStreamSectors, miniFat, prefixLength, out var prefix)) return false;
            if (expected == SourceFormat.Doc &&
                (prefix.Length < 32 || BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(0, 2)) != 0xa5ec)) return false;
            isEncrypted = expected == SourceFormat.Doc ? HasWordEncryptionFlag(prefix) : HasExcelFilePassRecord(prefix);
        }

        return true;
    }

    private static bool AddFatSector(uint sector, long sectorCount, List<uint> fatSectors, HashSet<uint> fatSectorIds)
    {
        if (sector >= sectorCount || !fatSectorIds.Add(sector)) return false;
        fatSectors.Add(sector);
        return true;
    }

    private static bool TryReadSectorChain(
        uint firstSector,
        uint[] fat,
        long sectorCount,
        HashSet<uint> reservedSectors,
        SectorChainTraversalState chainState,
        out List<uint> chain)
    {
        chain = [];
        var visited = new HashSet<uint>();
        var current = firstSector;
        while (current != EndOfChain)
        {
            if (current >= sectorCount || reservedSectors.Contains(current) || !visited.Add(current) ||
                !chainState.TryClaim(current) || chain.Count >= fat.Length) return false;
            chain.Add(current);
            current = fat[current];
            if (current is FreeSector or FatSector or DifatSector) return false;
        }

        return chain.Count > 0;
    }

    private static bool TryReadMiniStreamChain(uint firstSector, long streamLength, long miniStreamLength, uint[] miniFat)
    {
        var requiredSectors = (streamLength + 63) / 64;
        var availableMiniSectors = (miniStreamLength + 63) / 64;
        if (requiredSectors <= 0 || requiredSectors > availableMiniSectors) return false;
        var visited = new HashSet<uint>();
        var current = firstSector;
        for (long index = 0; index < requiredSectors; index++)
        {
            if (current >= miniFat.Length || current >= availableMiniSectors || !visited.Add(current)) return false;
            var next = miniFat[current];
            if (index == requiredSectors - 1)
            {
                return next == EndOfChain;
            }

            if (next >= miniFat.Length) return false;
            current = next;
        }

        return false;
    }

    private static bool TryReadStreamPrefix(
        Stream input,
        DirectoryEntry entry,
        int sectorSize,
        List<uint> rootMiniStreamSectors,
        uint[] miniFat,
        int maximumBytes,
        out byte[] prefix)
    {
        var prefixLength = checked((int)Math.Min(entry.StreamLength, maximumBytes));
        prefix = new byte[prefixLength];
        if (prefixLength == 0) return true;

        if (entry.StreamLength >= 4096)
        {
            if (entry.RegularSectors is null) return false;
            var copied = 0;
            var sectorBytes = new byte[sectorSize];
            foreach (var sector in entry.RegularSectors)
            {
                if (copied == prefixLength) break;
                if (!ReadSector(input, sector, sectorSize, sectorBytes)) return false;
                var count = Math.Min(sectorSize, prefixLength - copied);
                Buffer.BlockCopy(sectorBytes, 0, prefix, copied, count);
                copied += count;
            }

            return copied == prefixLength;
        }

        var miniSector = entry.FirstSector;
        var copiedMini = 0;
        var visited = new HashSet<uint>();
        var rootSectorBytes = new byte[sectorSize];
        while (copiedMini < prefixLength)
        {
            if (miniSector >= miniFat.Length || !visited.Add(miniSector)) return false;
            var miniOffset = (long)miniSector * 64;
            var rootSectorIndex = checked((int)(miniOffset / sectorSize));
            var offsetInRootSector = checked((int)(miniOffset % sectorSize));
            if (rootSectorIndex >= rootMiniStreamSectors.Count || !ReadSector(input, rootMiniStreamSectors[rootSectorIndex], sectorSize, rootSectorBytes)) return false;

            var count = Math.Min(64, prefixLength - copiedMini);
            Buffer.BlockCopy(rootSectorBytes, offsetInRootSector, prefix, copiedMini, count);
            copiedMini += count;
            if (copiedMini < prefixLength)
            {
                var nextMiniSector = miniFat[miniSector];
                if (nextMiniSector >= miniFat.Length) return false;
                miniSector = nextMiniSector;
            }
        }

        return true;
    }

    private static bool HasWordEncryptionFlag(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(prefix[..2]) != 0xa5ec) return false;
        const ushort encryptedOrObfuscated = 0x0100 | 0x8000;
        return (BinaryPrimitives.ReadUInt16LittleEndian(prefix[10..12]) & encryptedOrObfuscated) != 0;
    }

    private static bool HasExcelFilePassRecord(ReadOnlySpan<byte> prefix)
    {
        var offset = 0;
        var firstRecord = true;
        while (prefix.Length - offset >= 4)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(offset, 2));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(offset + 2, 2));
            if (firstRecord && type is not 0x0009 and not 0x0209 and not 0x0409 and not 0x0809) return false;
            if (type == 0x002f) return length >= 2 && offset + 4 + length <= prefix.Length;

            var next = (long)offset + 4 + length;
            if (next > prefix.Length) return false;
            offset = (int)next;
            firstRecord = false;
            if (type == 0x000a) return false;
        }

        return false;
    }

    private static bool TryReadDirectoryEntries(
        byte[] bytes,
        ushort majorVersion,
        int sectorSize,
        long sectorCount,
        uint[] fat,
        HashSet<uint> reservedSectors,
        SectorChainTraversalState chainState,
        out List<DirectoryEntry> entries)
    {
        entries = new List<DirectoryEntry>(bytes.Length / 128);
        for (var offset = 0; offset < bytes.Length; offset += 128)
        {
            var objectType = bytes[offset + 66];
            if (objectType == 0)
            {
                entries.Add(new DirectoryEntry(string.Empty, 0, NoStream, NoStream, NoStream, EndOfChain, 0));
                continue;
            }

            if (objectType is not 1 and not 2 and not 5) return false;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 64, 2));
            if (nameLength is < 2 or > 64 || nameLength % 2 != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + nameLength - 2, 2)) != 0)
                return false;
            var name = Encoding.Unicode.GetString(bytes, offset, nameLength - 2);
            var left = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 68, 4));
            var right = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 72, 4));
            var child = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 76, 4));
            var firstSector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 116, 4));
            var streamSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 120, 8));
            if (majorVersion == 3 && streamSize >> 32 != 0 || streamSize > long.MaxValue ||
                !IsValidDirectoryPointer(left, bytes.Length / 128) || !IsValidDirectoryPointer(right, bytes.Length / 128) ||
                !IsValidDirectoryPointer(child, bytes.Length / 128)) return false;

            List<uint>? regularSectors = null;
            if (objectType == 2 && streamSize > 0 && streamSize >= 4096)
            {
                var expectedSectors = ((long)streamSize + sectorSize - 1) / sectorSize;
                if (firstSector >= sectorCount || !TryReadSectorChain(firstSector, fat, sectorCount, reservedSectors, chainState, out var streamChain) ||
                    streamChain.Count != expectedSectors) return false;
                regularSectors = streamChain;
            }

            entries.Add(new DirectoryEntry(name, objectType, left, right, child, firstSector, (long)streamSize, regularSectors));
        }

        return true;
    }

    private static bool IsValidDirectoryPointer(uint pointer, int entryCount) => pointer == NoStream || pointer < entryCount;

    private sealed class SectorChainTraversalState(long sectorCount)
    {
        private readonly bool[] _claimedSectors = new bool[checked((int)sectorCount)];
        private int _steps;

        public bool TryClaim(uint sector)
        {
            if (sector >= (uint)_claimedSectors.Length || _steps >= _claimedSectors.Length || _claimedSectors[(int)sector]) return false;
            _claimedSectors[(int)sector] = true;
            _steps++;
            return true;
        }
    }

    private static bool IsReachableDirectoryTree(List<DirectoryEntry> entries)
    {
        if (entries[0].ObjectType != 5 || entries[0].Left != NoStream || entries[0].Right != NoStream) return false;
        var visited = new HashSet<uint> { 0 };
        var pending = new Stack<uint>();
        if (entries[0].Child != NoStream) pending.Push(entries[0].Child);
        while (pending.TryPop(out var index))
        {
            if (index >= entries.Count || !visited.Add(index)) return false;
            var entry = entries[(int)index];
            if (entry.ObjectType is not 1 and not 2) return false;
            if (entry.Left != NoStream) pending.Push(entry.Left);
            if (entry.Right != NoStream) pending.Push(entry.Right);
            if (entry.ObjectType == 1)
            {
                if (entry.Child != NoStream) pending.Push(entry.Child);
            }
            else if (entry.Child != NoStream)
            {
                return false;
            }
        }

        return entries.Select((entry, index) => (entry, index)).All(item => item.entry.ObjectType == 0 || visited.Contains((uint)item.index));
    }

    private static DirectoryEntry? FindRootStorageStream(List<DirectoryEntry> entries, string[] expectedNames)
    {
        var pending = new Stack<uint>();
        var visited = new HashSet<uint>();
        if (entries[0].Child != NoStream) pending.Push(entries[0].Child);
        while (pending.TryPop(out var index))
        {
            if (index >= entries.Count || !visited.Add(index)) return null;
            var entry = entries[(int)index];
            if (entry.ObjectType == 2 && entry.StreamLength > 0 &&
                expectedNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                return entry;
            }

            if (entry.Left != NoStream) pending.Push(entry.Left);
            if (entry.Right != NoStream) pending.Push(entry.Right);
        }

        return null;
    }

    private static bool ReadSector(Stream input, uint sector, int sectorSize, Span<byte> buffer)
    {
        var offset = checked(((long)sector + 1) * sectorSize);
        input.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = input.Read(buffer[read..]);
            if (count == 0) return false;
            read += count;
        }

        return true;
    }

    private static bool ReadExactlyAt(Stream input, long offset, Span<byte> buffer)
    {
        input.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = input.Read(buffer[read..]);
            if (count == 0) return false;
            read += count;
        }

        return true;
    }

    private sealed record DirectoryEntry(
        string Name,
        byte ObjectType,
        uint Left,
        uint Right,
        uint Child,
        uint FirstSector,
        long StreamLength,
        List<uint>? RegularSectors = null);
}
