using System.Buffers.Binary;
using System.Text;

namespace BinCueExplorer;

internal static class Iso9660Reader
{
    private const int RawSectorSize = 2352;
    private const int UserDataOffset = 16;
    private const int UserDataSize = 2048;
    private const uint PrimaryVolumeDescriptorSector = 16;

    public static IReadOnlyList<IsoFileEntry> ReadFiles(string imagePath)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, UserDataSize, FileOptions.RandomAccess);

        if (stream.Length % RawSectorSize != 0)
        {
            throw new InvalidDataException("The BIN file size is not a whole number of 2352-byte sectors.");
        }

        var root = ReadRootDirectory(stream);
        var files = new List<IsoFileEntry>();
        var pendingDirectories = new Stack<(uint Sector, uint Size, string Path)>();
        var visitedDirectories = new HashSet<uint>();
        pendingDirectories.Push((root.Sector, root.Size, string.Empty));

        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            if (!visitedDirectories.Add(directory.Sector))
            {
                continue;
            }

            ReadDirectory(stream, directory.Sector, directory.Size, directory.Path, files, pendingDirectories);
        }

        return files;
    }

    public static void ExtractFiles(string imagePath, IReadOnlyList<IsoFileEntry> files, string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(destinationRoot) + Path.DirectorySeparatorChar;

        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, UserDataSize, FileOptions.SequentialScan);
        var sectorData = new byte[UserDataSize];

        foreach (var file in files)
        {
            var relativePath = GetSafeRelativePath(file.Path);
            var outputPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
            if (!outputPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The ISO contains an unsafe file path: {file.Path}");
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (outputDirectory is not null)
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, UserDataSize, FileOptions.SequentialScan);
            uint remaining = file.Size;
            uint sector = file.StartSector;

            while (remaining > 0)
            {
                var rawOffset = checked((long)sector * RawSectorSize + UserDataOffset);
                if (rawOffset + UserDataSize > stream.Length)
                {
                    throw new InvalidDataException($"File '{file.Path}' references data outside the BIN image.");
                }

                stream.Position = rawOffset;
                stream.ReadExactly(sectorData);
                var bytesToWrite = (int)Math.Min((uint)UserDataSize, remaining);
                output.Write(sectorData, 0, bytesToWrite);
                remaining -= (uint)bytesToWrite;
                sector = checked(sector + 1);
            }
        }
    }

    private static string GetSafeRelativePath(string isoPath)
    {
        var segments = isoPath.Split('/');
        if (segments.Length == 0 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException($"The ISO contains an unsafe file path: {isoPath}");
        }

        return Path.Combine(segments);
    }

    private static (uint Sector, uint Size) ReadRootDirectory(FileStream stream)
    {
        for (uint sector = PrimaryVolumeDescriptorSector; ; sector++)
        {
            var data = ReadUserData(stream, sector);
            if (!HasVolumeDescriptorSignature(data))
            {
                throw new InvalidDataException("The BIN file does not contain a valid ISO9660 volume descriptor.");
            }

            if (data[0] == 255)
            {
                break;
            }

            if (data[0] != 1)
            {
                continue;
            }

            var logicalBlockSize = ReadBothEndian16(data.AsSpan(128, 4));
            if (logicalBlockSize != UserDataSize)
            {
                throw new InvalidDataException($"Unsupported ISO logical block size: {logicalBlockSize} bytes.");
            }

            var rootRecord = data.AsSpan(156);
            if (rootRecord[0] < 34 || rootRecord[32] != 1 || rootRecord[33] != 0)
            {
                throw new InvalidDataException("The ISO9660 root directory record is invalid.");
            }

            return (ReadBothEndian32(rootRecord.Slice(2, 8)), ReadBothEndian32(rootRecord.Slice(10, 8)));
        }

        throw new InvalidDataException("No ISO9660 primary volume descriptor was found.");
    }

    private static void ReadDirectory(
        FileStream stream,
        uint startSector,
        uint directorySize,
        string parentPath,
        List<IsoFileEntry> files,
        Stack<(uint Sector, uint Size, string Path)> pendingDirectories)
    {
        uint position = 0;
        while (position < directorySize)
        {
            var sector = checked(startSector + position / UserDataSize);
            var sectorData = ReadUserData(stream, sector);
            var bytesInSector = (int)Math.Min(UserDataSize, directorySize - position);
            var offset = 0;

            while (offset < bytesInSector)
            {
                var recordLength = sectorData[offset];
                if (recordLength == 0)
                {
                    break;
                }

                if (recordLength < 34 || offset + recordLength > bytesInSector)
                {
                    throw new InvalidDataException("An ISO9660 directory record is invalid.");
                }

                var record = sectorData.AsSpan(offset, recordLength);
                var identifierLength = record[32];
                if (33 + identifierLength > record.Length)
                {
                    throw new InvalidDataException("An ISO9660 filename record is invalid.");
                }

                var identifier = record.Slice(33, identifierLength);
                if (!(identifierLength == 1 && (identifier[0] == 0 || identifier[0] == 1)))
                {
                    var name = DecodeIdentifier(identifier);
                    var extent = ReadBothEndian32(record.Slice(2, 8));
                    var size = ReadBothEndian32(record.Slice(10, 8));
                    var isDirectory = (record[25] & 0x02) != 0;
                    var recordedDate = TryReadRecordingDate(record.Slice(18, 7));
                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

                    if (isDirectory)
                    {
                        pendingDirectories.Push((extent, size, path));
                    }
                    else
                    {
                        files.Add(new IsoFileEntry(name, path, extent, size, recordedDate));
                    }
                }

                offset += recordLength;
            }

            position += (uint)bytesInSector;
        }
    }

    private static byte[] ReadUserData(FileStream stream, uint sector)
    {
        var rawOffset = checked((long)sector * RawSectorSize + UserDataOffset);
        if (rawOffset + UserDataSize > stream.Length)
        {
            throw new InvalidDataException($"Sector {sector} is outside the BIN file.");
        }

        var data = new byte[UserDataSize];
        stream.Position = rawOffset;
        stream.ReadExactly(data);
        return data;
    }

    private static bool HasVolumeDescriptorSignature(ReadOnlySpan<byte> data) =>
        data.Length >= UserDataSize && data.Slice(1, 5).SequenceEqual("CD001"u8) && data[6] == 1;

    private static uint ReadBothEndian32(ReadOnlySpan<byte> value)
    {
        var littleEndian = BinaryPrimitives.ReadUInt32LittleEndian(value[..4]);
        var bigEndian = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
        if (littleEndian != bigEndian)
        {
            throw new InvalidDataException("An ISO9660 both-endian integer is inconsistent.");
        }

        return littleEndian;
    }

    private static ushort ReadBothEndian16(ReadOnlySpan<byte> value)
    {
        var littleEndian = BinaryPrimitives.ReadUInt16LittleEndian(value[..2]);
        var bigEndian = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        if (littleEndian != bigEndian)
        {
            throw new InvalidDataException("An ISO9660 both-endian integer is inconsistent.");
        }

        return littleEndian;
    }

    private static string DecodeIdentifier(ReadOnlySpan<byte> identifier)
    {
        var name = Encoding.ASCII.GetString(identifier);
        var versionSeparator = name.LastIndexOf(';');
        if (versionSeparator >= 0)
        {
            name = name[..versionSeparator];
        }

        return name.TrimEnd('.');
    }

    private static DateTimeOffset? TryReadRecordingDate(ReadOnlySpan<byte> value)
    {
        if (value.Length < 7 || value[..7].IndexOfAnyExcept((byte)0) < 0)
        {
            return null;
        }

        var timezoneOffset = unchecked((sbyte)value[6]);
        if (timezoneOffset is < -48 or > 52)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(
                value[0] + 1900,
                value[1],
                value[2],
                value[3],
                value[4],
                value[5],
                TimeSpan.FromMinutes(timezoneOffset * 15));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
