namespace DvdAudio.Core;

/// <summary>
/// A random-access disc image addressed in 2048-byte user-data sectors.
/// Mode 1 raw images (2352 bytes per sector) are accepted.
/// </summary>
public sealed class SectorImage : IDisposable
{
    public const int UserDataSize = 2048;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _stride;
    private readonly int _header;

    private SectorImage(Stream stream, bool leaveOpen, int stride, int header, long sectorCount)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _stride = stride;
        _header = header;
        SectorCount = sectorCount;
    }

    public long SectorCount { get; }

    public static SectorImage Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static SectorImage Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new DvdAudioException("The disc image stream must be readable and seekable.");
        }

        if (TryDetect(stream, out var stride, out var header))
        {
            var count = Math.Max(0, (stream.Length - header) / stride);
            return new SectorImage(stream, leaveOpen, stride, header, count);
        }

        throw new DvdAudioException("The file is not an ISO 9660 disc image.");
    }

    public static SectorImage FromSectors(byte[] image, int stride = UserDataSize, int header = 0)
    {
        var stream = new MemoryStream(image, writable: false);
        var count = Math.Max(0, (image.Length - header) / stride);
        return new SectorImage(stream, leaveOpen: false, stride, header, count);
    }

    public void ReadSector(long sector, Span<byte> destination)
    {
        if (destination.Length < UserDataSize)
        {
            throw new ArgumentException("The destination must hold a full sector.", nameof(destination));
        }

        if (sector < 0 || sector >= SectorCount)
        {
            destination[..UserDataSize].Clear();
            return;
        }

        var position = (sector * _stride) + _header;
        _stream.Position = position;
        var read = 0;
        while (read < UserDataSize)
        {
            var n = _stream.Read(destination.Slice(read, UserDataSize - read));
            if (n == 0)
            {
                destination.Slice(read, UserDataSize - read).Clear();
                return;
            }

            read += n;
        }
    }

    public byte[] ReadBytes(long sector, long length)
    {
        if (length < 0 || length > int.MaxValue)
        {
            throw new DvdAudioException($"Refusing to buffer {length} bytes from the disc image.");
        }

        var buffer = new byte[length];
        var remaining = length;
        var offset = 0;
        var index = sector;
        Span<byte> one = stackalloc byte[UserDataSize];
        while (remaining > 0)
        {
            ReadSector(index, one);
            var take = (int)Math.Min(remaining, UserDataSize);
            one[..take].CopyTo(buffer.AsSpan(offset, take));
            offset += take;
            remaining -= take;
            index++;
        }

        return buffer;
    }

    public void CopyUserBytes(long startSector, long byteOffset, long count, Stream destination)
    {
        if (count < 0 || byteOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var sector = startSector + (byteOffset / UserDataSize);
        var skip = (int)(byteOffset % UserDataSize);
        var left = count;
        var one = new byte[UserDataSize];
        while (left > 0)
        {
            ReadSector(sector, one);
            var take = (int)Math.Min(left, UserDataSize - skip);
            destination.Write(one, skip, take);
            left -= take;
            skip = 0;
            sector++;
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static bool TryDetect(Stream stream, out int stride, out int header)
    {
        if (HasVolume(stream, 16L * UserDataSize))
        {
            stride = UserDataSize;
            header = 0;
            return true;
        }

        // 2352-byte raw sectors store 2048 bytes of user data after a 16-byte header.
        if (HasVolume(stream, (16L * 2352) + 16))
        {
            stride = 2352;
            header = 16;
            return true;
        }

        stride = 0;
        header = 0;
        return false;
    }

    private static bool HasVolume(Stream stream, long offset)
    {
        if (offset < 0 || offset + 6 > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        Span<byte> id = stackalloc byte[6];
        return stream.Read(id) == 6
            && id[0] == 1
            && id[1] == (byte)'C'
            && id[2] == (byte)'D'
            && id[3] == (byte)'0'
            && id[4] == (byte)'0'
            && id[5] == (byte)'1';
    }
}
