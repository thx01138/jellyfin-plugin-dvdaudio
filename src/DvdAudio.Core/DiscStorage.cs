using System.Text.RegularExpressions;

namespace DvdAudio.Core;

internal sealed class DiscStorage : IDisposable
{
    private static readonly Regex TitlesetIfo = new(@"^/AUDIO_TS/ATS_(\d+)_0\.IFO$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TitlesetAob = new(@"^/AUDIO_TS/ATS_(\d+)_(\d+)\.AOB$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly SectorImage? _image;
    private readonly Dictionary<string, ImageFile> _byPath;
    private FileStream? _local;
    private string? _localPath;

    private DiscStorage(SectorImage? image, IReadOnlyList<ImageFile> files)
    {
        _image = image;
        Files = files;
        _byPath = new Dictionary<string, ImageFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            _byPath[Normalize(file.FullPath)] = file;
        }
    }

    public IReadOnlyList<ImageFile> Files { get; }

    public static DiscStorage Open(string path)
    {
        if (Directory.Exists(path))
        {
            return new DiscStorage(null, ReadDirectory(path));
        }

        var image = SectorImage.Open(path);
        try
        {
            return new DiscStorage(image, ImageFileSystem.Read(image));
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public bool Exists(string discPath)
    {
        return _byPath.ContainsKey(Normalize(discPath));
    }

    public ImageFile? Find(string discPath)
    {
        return _byPath.TryGetValue(Normalize(discPath), out var file) ? file : null;
    }

    public byte[] ReadSmall(string discPath, int maxBytes)
    {
        var file = Find(discPath) ?? throw new DvdAudioException($"'{discPath}' was not found in the disc image.");
        if (file.Length > maxBytes)
        {
            throw new DvdAudioException($"'{discPath}' is larger than the {maxBytes} byte read limit.");
        }

        using var output = new MemoryStream(file.Length > int.MaxValue ? 0 : (int)file.Length);
        CopyBytes(file, 0, file.Length, output);
        return output.ToArray();
    }

    public IReadOnlyList<(int Titleset, ImageFile File)> TitlesetIfos()
    {
        return Matching(TitlesetIfo)
            .Select(file => (int.Parse(TitlesetIfo.Match(Normalize(file.FullPath)).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), file))
            .OrderBy(item => item.Item1)
            .ToArray();
    }

    public IReadOnlyList<ImageFile> Aobs(int titleset)
    {
        return Files
            .Select(file => (File: file, Match: TitlesetAob.Match(Normalize(file.FullPath))))
            .Where(item => item.Match.Success && int.Parse(item.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == titleset)
            .OrderBy(item => int.Parse(item.Match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Select(item => item.File)
            .ToArray();
    }

    public void ReadFileSector(ImageFile file, long sectorIndex, Span<byte> destination)
    {
        if (destination.Length < SectorImage.UserDataSize)
        {
            throw new ArgumentException("Destination must hold one sector.", nameof(destination));
        }

        destination[..SectorImage.UserDataSize].Clear();
        var start = sectorIndex * SectorImage.UserDataSize;
        var filled = 0;
        while (filled < SectorImage.UserDataSize)
        {
            var copied = CopyAt(file, start + filled, destination.Slice(filled, SectorImage.UserDataSize - filled));
            if (copied == 0)
            {
                break;
            }

            filled += copied;
        }
    }

    public void Dispose()
    {
        _local?.Dispose();
        _image?.Dispose();
    }

    private int CopyAt(ImageFile file, long offset, Span<byte> destination)
    {
        if (offset < 0 || destination.IsEmpty)
        {
            return 0;
        }

        if (file.InlineData is not null)
        {
            if (offset >= file.InlineData.Length)
            {
                return 0;
            }

            var take = (int)Math.Min(destination.Length, file.InlineData.Length - offset);
            file.InlineData.AsSpan((int)offset, take).CopyTo(destination);
            return take;
        }

        if (file.LocalPath is not null)
        {
            var stream = OpenLocal(file.LocalPath);
            if (offset >= stream.Length)
            {
                return 0;
            }

            stream.Position = offset;
            return stream.Read(destination);
        }

        long skipped = 0;
        foreach (var extent in file.Extents)
        {
            if (offset >= skipped + extent.Length)
            {
                skipped += extent.Length;
                continue;
            }

            var into = offset - skipped;
            var available = extent.Length - into;
            var take = (int)Math.Min(destination.Length, available);
            if (_image is null || take <= 0)
            {
                return 0;
            }

            using var buffer = new MemoryStream(take);
            _image.CopyUserBytes(extent.Sector, into, take, buffer);
            buffer.GetBuffer().AsSpan(0, take).CopyTo(destination);
            return take;
        }

        return 0;
    }

    private void CopyBytes(ImageFile file, long offset, long count, Stream destination)
    {
        var left = count;
        var buffer = new byte[SectorImage.UserDataSize];
        while (left > 0)
        {
            var take = (int)Math.Min(left, buffer.Length);
            var copied = CopyAt(file, offset, buffer.AsSpan(0, take));
            if (copied == 0)
            {
                break;
            }

            destination.Write(buffer, 0, copied);
            offset += copied;
            left -= copied;
        }
    }

    private FileStream OpenLocal(string path)
    {
        if (_local is not null && string.Equals(_localPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return _local;
        }

        _local?.Dispose();
        _local = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _localPath = path;
        return _local;
    }

    private IEnumerable<ImageFile> Matching(Regex pattern)
    {
        foreach (var file in Files)
        {
            if (pattern.IsMatch(Normalize(file.FullPath)))
            {
                yield return file;
            }
        }
    }

    private static List<ImageFile> ReadDirectory(string path)
    {
        var audio = Path.Combine(path, "AUDIO_TS");
        if (!Directory.Exists(audio))
        {
            return [];
        }

        var files = new List<ImageFile>();
        AddFolder(files, audio, "/AUDIO_TS");
        var video = Path.Combine(path, "VIDEO_TS");
        if (Directory.Exists(video))
        {
            AddFolder(files, video, "/VIDEO_TS");
        }

        return files;
    }

    private static void AddFolder(List<ImageFile> files, string directory, string discDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var info = new FileInfo(path);
            files.Add(new ImageFile(discDirectory + "/" + info.Name, info.Length, [], localPath: info.FullName));
        }
    }

    private static string Normalize(string path)
    {
        var slash = path.Replace('\\', '/');
        if (!slash.StartsWith('/'))
        {
            slash = "/" + slash;
        }

        return slash.ToUpperInvariant();
    }
}
