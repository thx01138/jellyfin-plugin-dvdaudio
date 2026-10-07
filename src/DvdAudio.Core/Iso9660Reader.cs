using System.Text;

namespace DvdAudio.Core;

internal static class Iso9660Reader
{
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
    }

    private static List<ImageFile> Read(SectorImage image)
    {
        var pvd = new byte[SectorImage.UserDataSize];
        image.ReadSector(16, pvd);
        if (pvd[0] != 1 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D')
        {
            throw new DvdAudioException("ISO 9660 primary volume descriptor was not found.");
        }

        if (!Binary.Fits(pvd, 156, 34))
        {
            throw new DvdAudioException("ISO 9660 primary volume descriptor is truncated.");
        }

        var rootSector = Binary.Both32(pvd, 156 + 2);
        var rootLength = Binary.Both32(pvd, 156 + 10);
        var files = new List<ImageFile>();
        Walk(image, rootSector, rootLength, string.Empty, files, depth: 0);
        return files;
    }

    private static void Walk(SectorImage image, long sector, long length, string directory, List<ImageFile> files, int depth)
    {
        if (depth > 8 || length <= 0 || length > 2 * 1024 * 1024)
        {
            return;
        }

        var data = image.ReadBytes(sector, length);
        var pendingName = string.Empty;
        var pendingExtents = new List<FileExtent>();
        var position = 0;
        while (position + 1 < data.Length)
        {
            var recordLength = data[position];
            if (recordLength == 0)
            {
                var aligned = (position + SectorImage.UserDataSize) & ~(SectorImage.UserDataSize - 1);
                position = aligned;
                continue;
            }

            if (recordLength < 34 || position + recordLength > data.Length)
            {
                break;
            }

            var record = data.AsSpan(position, recordLength);
            position += recordLength;
            var nameLength = record[32];
            if (33 + nameLength > record.Length)
            {
                continue;
            }

            var flags = record[25];
            var extent = Binary.Both32(record, 2);
            var size = Binary.Both32(record, 10);
            if (nameLength == 1 && (record[33] == 0 || record[33] == 1))
            {
                continue;
            }

            var name = Encoding.ASCII.GetString(record.Slice(33, nameLength));
            var version = name.IndexOf(';');
            if (version >= 0)
            {
                name = name[..version];
            }

            name = name.Trim().TrimEnd('.');
            if (name.Length == 0)
            {
                continue;
            }

            var isDirectory = (flags & 0x02) != 0;
            var continues = (flags & 0x80) != 0;
            if (isDirectory)
            {
                Flush(directory, pendingName, pendingExtents, files);
                pendingName = string.Empty;
                pendingExtents.Clear();
                var child = directory + "/" + name;
                Walk(image, extent, size, child, files, depth + 1);
                continue;
            }

            if ((flags & 0x04) != 0)
            {
                continue;
            }

            if (!name.Equals(pendingName, StringComparison.OrdinalIgnoreCase))
            {
                Flush(directory, pendingName, pendingExtents, files);
                pendingName = name;
                pendingExtents.Clear();
            }

            pendingExtents.Add(new FileExtent(extent, size));
            if (!continues)
            {
                Flush(directory, pendingName, pendingExtents, files);
                pendingName = string.Empty;
                pendingExtents.Clear();
            }
        }

        Flush(directory, pendingName, pendingExtents, files);
    }

    private static void Flush(string directory, string name, List<FileExtent> extents, List<ImageFile> files)
    {
        if (name.Length == 0 || extents.Count == 0)
        {
            return;
        }

        long length = 0;
        foreach (var extent in extents)
        {
            length += extent.Length;
        }

        files.Add(new ImageFile(directory + "/" + name, length, extents.ToArray()));
    }
}
