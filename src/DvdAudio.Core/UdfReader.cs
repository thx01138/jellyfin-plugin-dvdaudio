using System.Text;

namespace DvdAudio.Core;

/// <summary>
/// Reads the UDF 1.02 subset used by DVD-Audio images: anchor, partition, logical volume,
/// file set, file entries, and file identifier descriptors.
/// </summary>
internal static class UdfReader
{
    private const ushort TagAnchor = 2;
    private const ushort TagPartition = 5;
    private const ushort TagLogicalVolume = 6;
    private const ushort TagFileSet = 256;
    private const ushort TagFileIdentifier = 257;
    private const ushort TagFileEntry = 261;

    public static List<ImageFile>? TryRead(SectorImage image)
    {
        try
        {
            return Read(image);
        }
        catch (DvdAudioException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static List<ImageFile> Read(SectorImage image)
    {
        var anchorSector = FindAnchor(image);
        var anchor = ReadSector(image, anchorSector);
        var volumeLength = Binary.Le32(anchor, 16);
        var volumeSector = Binary.Le32(anchor, 20);
        if (volumeLength < SectorImage.UserDataSize || volumeSector == 0)
        {
            throw new DvdAudioException("UDF anchor does not point at a volume descriptor sequence.");
        }

        uint partitionStart = 0;
        var havePartition = false;
        long fileSetLogical = -1;
        var descriptorCount = Math.Min(volumeLength / SectorImage.UserDataSize, 64);
        for (var i = 0; i < descriptorCount; i++)
        {
            var sector = ReadSector(image, volumeSector + i);
            var tag = Binary.Le16(sector, 0);
            if (tag == TagPartition)
            {
                partitionStart = Binary.Le32(sector, 188);
                havePartition = true;
            }
            else if (tag == TagLogicalVolume && sector.Length >= 264)
            {
                fileSetLogical = Binary.Le32(sector, 252);
            }
        }

        if (!havePartition || fileSetLogical < 0)
        {
            throw new DvdAudioException("UDF logical volume is incomplete.");
        }

        var fileSet = ReadSector(image, partitionStart + fileSetLogical);
        if (Binary.Le16(fileSet, 0) != TagFileSet || fileSet.Length < 416)
        {
            throw new DvdAudioException("UDF file set descriptor was not found.");
        }

        var rootLogical = Binary.Le32(fileSet, 404);
        var files = new List<ImageFile>();
        var seen = new HashSet<long>();
        ReadEntry(image, partitionStart, rootLogical, string.Empty, files, seen, depth: 0);
        return files;
    }

    private static long FindAnchor(SectorImage image)
    {
        foreach (var candidate in new long[] { 256, 512 })
        {
            if (candidate >= image.SectorCount)
            {
                continue;
            }

            var sector = ReadSector(image, candidate);
            if (Binary.Le16(sector, 0) == TagAnchor)
            {
                return candidate;
            }
        }

        throw new DvdAudioException("UDF anchor volume descriptor was not found.");
    }

    private static void ReadEntry(
        SectorImage image,
        uint partitionStart,
        long logicalBlock,
        string path,
        List<ImageFile> files,
        HashSet<long> seen,
        int depth)
    {
        if (depth > 8 || logicalBlock < 0 || !seen.Add(logicalBlock) || files.Count > 10000)
        {
            return;
        }

        var entry = ReadSector(image, partitionStart + logicalBlock);
        if (entry.Length < 176 || Binary.Le16(entry, 0) != TagFileEntry)
        {
            return;
        }

        var fileType = entry[27];
        var flags = Binary.Le16(entry, 34);
        var informationLength = Binary.Le64(entry, 56);
        var attributeLength = Binary.Le32(entry, 168);
        var allocationLength = Binary.Le32(entry, 172);
        if (informationLength > int.MaxValue || attributeLength > 64 * 1024 || allocationLength > entry.Length)
        {
            return;
        }

        var allocationOffset = 176 + (int)attributeLength;
        if (allocationOffset < 0 || allocationOffset > entry.Length)
        {
            return;
        }

        var allocation = entry.AsSpan(allocationOffset, Math.Min((int)allocationLength, entry.Length - allocationOffset));
        var adType = flags & 0x7;
        if (fileType == 4)
        {
            var directory = ReadAllocated(image, partitionStart, allocation, adType, (long)informationLength);
            WalkDirectory(image, partitionStart, directory, path, files, seen, depth);
            return;
        }

        if (fileType != 5 || path.Length == 0)
        {
            return;
        }

        if (adType == 3)
        {
            var inline = allocation[..Math.Min(allocation.Length, (int)informationLength)].ToArray();
            files.Add(new ImageFile(path, inline.Length, [], inlineData: inline));
            return;
        }

        var extents = ReadExtents(partitionStart, allocation, adType);
        files.Add(new ImageFile(path, (long)informationLength, extents));
    }

    private static void WalkDirectory(
        SectorImage image,
        uint partitionStart,
        byte[] directory,
        string path,
        List<ImageFile> files,
        HashSet<long> seen,
        int depth)
    {
        var position = 0;
        while (position + 38 <= directory.Length)
        {
            var sectorEnd = ((position / SectorImage.UserDataSize) + 1) * SectorImage.UserDataSize;
            var tag = Binary.Le16(directory, position);
            if (tag == 0 || position + 38 > sectorEnd || position + 38 > directory.Length)
            {
                position = Math.Min(sectorEnd, directory.Length);
                continue;
            }

            if (tag != TagFileIdentifier)
            {
                position = Math.Min(sectorEnd, directory.Length);
                continue;
            }

            var start = position;
            var characteristics = directory[start + 18];
            var nameLength = directory[start + 19];
            var implementationLength = Binary.Le16(directory, start + 36);
            var total = 38 + implementationLength + nameLength;
            if (total < 38 || start + total > directory.Length || start + total > sectorEnd)
            {
                break;
            }

            var deleted = (characteristics & 0x04) != 0;
            var parent = (characteristics & 0x08) != 0;
            var logical = Binary.Le32(directory, start + 24);
            var padded = (total + 3) & ~3;
            position = start + padded;
            if (deleted || parent || nameLength == 0)
            {
                continue;
            }

            var nameBytes = directory.AsSpan(start + 38 + implementationLength, nameLength);
            var name = DecodeName(nameBytes);
            if (name.Length == 0 || name is "." or "..")
            {
                continue;
            }

            ReadEntry(image, partitionStart, logical, path + "/" + name, files, seen, depth + 1);
        }
    }

    private static List<FileExtent> ReadExtents(uint partitionStart, ReadOnlySpan<byte> allocation, int adType)
    {
        var extents = new List<FileExtent>();
        if (adType == 0)
        {
            for (var offset = 0; offset + 8 <= allocation.Length; offset += 8)
            {
                var rawLength = Binary.Le32(allocation, offset);
                var type = rawLength >> 30;
                var length = rawLength & 0x3FFFFFFF;
                var logical = Binary.Le32(allocation, offset + 4);
                if (type is 0 or 1 && length > 0)
                {
                    extents.Add(new FileExtent(partitionStart + logical, length));
                }
            }
        }
        else if (adType == 1)
        {
            for (var offset = 0; offset + 16 <= allocation.Length; offset += 16)
            {
                var rawLength = Binary.Le32(allocation, offset);
                var type = rawLength >> 30;
                var length = rawLength & 0x3FFFFFFF;
                var logical = Binary.Le32(allocation, offset + 4);
                if (type is 0 or 1 && length > 0)
                {
                    extents.Add(new FileExtent(partitionStart + logical, length));
                }
            }
        }

        return extents;
    }

    private static byte[] ReadAllocated(SectorImage image, uint partitionStart, ReadOnlySpan<byte> allocation, int adType, long length)
    {
        if (length <= 0 || length > 2 * 1024 * 1024)
        {
            return [];
        }

        using var output = new MemoryStream((int)length);
        if (adType == 3)
        {
            var take = (int)Math.Min(length, allocation.Length);
            output.Write(allocation[..take]);
        }
        else
        {
            foreach (var extent in ReadExtents(partitionStart, allocation, adType))
            {
                var remaining = length - output.Length;
                if (remaining <= 0)
                {
                    break;
                }

                var take = Math.Min(remaining, extent.Length);
                image.CopyUserBytes(extent.Sector, 0, take, output);
            }
        }

        return output.ToArray();
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (bytes[0] == 8 && bytes.Length > 1)
        {
            return Encoding.Latin1.GetString(bytes[1..]);
        }

        if (bytes[0] == 16 && bytes.Length > 2)
        {
            var payload = bytes[1..];
            if ((payload.Length & 1) != 0)
            {
                payload = payload[..^1];
            }

            return Encoding.BigEndianUnicode.GetString(payload);
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private static byte[] ReadSector(SectorImage image, long sector)
    {
        var buffer = new byte[SectorImage.UserDataSize];
        image.ReadSector(sector, buffer);
        return buffer;
    }
}
