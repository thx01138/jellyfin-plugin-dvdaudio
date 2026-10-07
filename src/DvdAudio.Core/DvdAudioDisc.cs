using System.Collections.Concurrent;
using System.Globalization;

namespace DvdAudio.Core;

public readonly record struct DiscProbe(bool IsDvdAudio, bool HasVideoZone, bool IsCppmProtected);

public sealed class DvdAudioTrack
{
    public int Number { get; init; }

    public TimeSpan Duration { get; init; }

    public long StartSector { get; init; }

    public long EndSectorInclusive { get; init; }

    public int TitlesetNumber { get; init; }

    public string? Coding { get; set; }

    public int? SampleRate { get; set; }

    public int? BitsPerSample { get; set; }

    public int? Channels { get; set; }

    public string? ChannelLayout { get; set; }
}

public sealed class DvdAudioAlbum
{
    public int GroupNumber { get; init; }

    public int TitlesetNumber { get; init; }

    public int ProgramChain { get; init; }

    public IReadOnlyList<DvdAudioTrack> Tracks { get; init; } = [];
}

public sealed class DvdAudioDisc : IDisposable
{
    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, DiscProbe Probe)> ProbeCache = new();

    private readonly DiscStorage _storage;
    private readonly Dictionary<int, IReadOnlyList<ImageFile>> _aobs;

    private DvdAudioDisc(string sourcePath, DiscStorage storage, bool hasVideoZone, bool cppm, IReadOnlyList<DvdAudioAlbum> albums)
    {
        SourcePath = sourcePath;
        _storage = storage;
        HasVideoZone = hasVideoZone;
        IsCppmProtected = cppm;
        Albums = albums;
        _aobs = new Dictionary<int, IReadOnlyList<ImageFile>>();
        foreach (var album in albums)
        {
            if (!_aobs.ContainsKey(album.TitlesetNumber))
            {
                _aobs[album.TitlesetNumber] = storage.Aobs(album.TitlesetNumber);
            }
        }
    }

    public string SourcePath { get; }

    public bool HasVideoZone { get; }

    public bool IsCppmProtected { get; }

    public IReadOnlyList<DvdAudioAlbum> Albums { get; }

    public static DiscProbe Probe(string path)
    {
        var full = Path.GetFullPath(path);
        var (length, ticks) = Stamp(full);
        if (ProbeCache.TryGetValue(full, out var cached) && cached.Length == length && cached.Ticks == ticks)
        {
            return cached.Probe;
        }

        DiscProbe probe;
        try
        {
            using var storage = DiscStorage.Open(full);
            var audio = storage.TitlesetIfos().Count > 0
                || storage.Exists("/AUDIO_TS/AUDIO_PP.IFO")
                || storage.Exists("/AUDIO_TS/AUDIO_TS.IFO");
            var video = storage.Find("/VIDEO_TS/VIDEO_TS.IFO") is { Length: >= 1024 };
            var cppm = storage.Exists("/AUDIO_TS/DVDAUDIO.MKB");
            probe = new DiscProbe(audio, video, cppm);
        }
        catch (Exception)
        {
            probe = default;
        }

        ProbeCache[full] = (length, ticks, probe);
        return probe;
    }

    public static DvdAudioDisc Open(string path)
    {
        var full = Path.GetFullPath(path);
        var storage = DiscStorage.Open(full);
        try
        {
            var titlesets = storage.TitlesetIfos();
            if (titlesets.Count == 0 && !storage.Exists("/AUDIO_TS/AUDIO_PP.IFO") && !storage.Exists("/AUDIO_TS/AUDIO_TS.IFO"))
            {
                throw new DvdAudioException("No AUDIO_TS titleset was found. The image is not a DVD-Audio disc.");
            }

            var samg = storage.Exists("/AUDIO_TS/AUDIO_PP.IFO")
                ? SamgParser.Parse(storage.ReadSmall("/AUDIO_TS/AUDIO_PP.IFO", 1024 * 1024))
                : [];
            var built = new List<(int Titleset, AtsProgramChain Chain)>();
            foreach (var (titleset, file) in titlesets)
            {
                var chains = AtsParser.Parse(storage.ReadSmall(file.FullPath, 8 * 1024 * 1024));
                foreach (var chain in chains)
                {
                    built.Add((titleset, chain));
                }
            }

            if (built.Count == 0)
            {
                throw new DvdAudioException("The DVD-Audio titleset did not contain any playable programs.");
            }

            var groups = samg.Where(chapter => !chapter.VideoLink && chapter.Group > 0)
                .GroupBy(chapter => chapter.Group)
                .OrderBy(group => group.Key)
                .ToList();
            var albums = new List<DvdAudioAlbum>(built.Count);
            for (var index = 0; index < built.Count; index++)
            {
                var (titleset, chain) = built[index];
                var groupNumber = index < groups.Count ? groups[index].Key : titleset;
                var tracks = new List<DvdAudioTrack>(chain.Programs.Count);
                for (var trackIndex = 0; trackIndex < chain.Programs.Count; trackIndex++)
                {
                    var program = chain.Programs[trackIndex];
                    var number = program.Id is >= 1 and <= 99 ? program.Id : trackIndex + 1;
                    tracks.Add(new DvdAudioTrack
                    {
                        Number = number,
                        Duration = FromPts(program.LengthPts),
                        StartSector = program.StartSector,
                        EndSectorInclusive = program.EndSector,
                        TitlesetNumber = titleset
                    });
                }

                albums.Add(new DvdAudioAlbum
                {
                    GroupNumber = groupNumber,
                    TitlesetNumber = titleset,
                    ProgramChain = chain.Id,
                    Tracks = tracks
                });
            }

            var video = storage.Find("/VIDEO_TS/VIDEO_TS.IFO") is { Length: >= 1024 };
            var cppm = storage.Exists("/AUDIO_TS/DVDAUDIO.MKB");
            var disc = new DvdAudioDisc(full, storage, video, cppm, albums);
            storage = null!;
            try
            {
                if (!cppm)
                {
                    disc.DescribeTracks();
                }

                return disc;
            }
            catch
            {
                disc.Dispose();
                throw;
            }
        }
        finally
        {
            storage?.Dispose();
        }
    }

    public AobProbe ProbeTrack(DvdAudioTrack track)
    {
        var available = track.EndSectorInclusive - track.StartSector + 1;
        if (available <= 0)
        {
            return new AobProbe { Detail = "The track has an empty sector range." };
        }

        var count = (int)Math.Min(32, available);
        var buffer = new byte[count * SectorImage.UserDataSize];
        for (var i = 0; i < count; i++)
        {
            ReadRelativeSector(track.TitlesetNumber, track.StartSector + i, buffer.AsSpan(i * SectorImage.UserDataSize));
        }

        return AobDemuxer.Inspect(buffer);
    }

    public void DemuxTrack(DvdAudioTrack track, Stream output, AobProbe probe)
    {
        var count = track.EndSectorInclusive - track.StartSector + 1;
        if (count <= 0)
        {
            throw new DvdAudioException("The track has an empty sector range.");
        }

        using var sectors = new TrackSectorStream(this, track.TitlesetNumber, track.StartSector, count);
        AobDemuxer.Demux(sectors, count, output, probe);
    }

    public void Dispose()
    {
        _storage.Dispose();
    }

    private void DescribeTracks()
    {
        foreach (var album in Albums)
        {
            foreach (var track in album.Tracks)
            {
                AobProbe probe;
                try
                {
                    probe = ProbeTrack(track);
                }
                catch (DvdAudioException)
                {
                    continue;
                }

                if (probe.Codec == AobCodec.Mlp)
                {
                    track.Coding = "MLP";
                }
                else if (probe.Lpcm is { } lpcm)
                {
                    track.Coding = "LPCM";
                    track.SampleRate = lpcm.SampleRate;
                    track.BitsPerSample = lpcm.BitsPerSample;
                    track.Channels = lpcm.OutputChannels;
                    track.ChannelLayout = lpcm.ChannelLayout;
                }
            }
        }
    }

    private void ReadRelativeSector(int titleset, long relativeSector, Span<byte> destination)
    {
        if (!_aobs.TryGetValue(titleset, out var files))
        {
            files = _storage.Aobs(titleset);
            _aobs[titleset] = files;
        }

        long cursor = relativeSector;
        foreach (var file in files)
        {
            var fileSectors = (file.Length + SectorImage.UserDataSize - 1) / SectorImage.UserDataSize;
            if (cursor >= fileSectors)
            {
                cursor -= fileSectors;
                continue;
            }

            _storage.ReadFileSector(file, cursor, destination);
            return;
        }

        destination[..SectorImage.UserDataSize].Clear();
    }

    private static TimeSpan FromPts(uint pts)
    {
        return TimeSpan.FromTicks((long)pts * TimeSpan.TicksPerSecond / 90_000);
    }

    private static (long Length, long Ticks) Stamp(string path)
    {
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            return (info.Length, info.LastWriteTimeUtc.Ticks);
        }

        if (!Directory.Exists(path))
        {
            return (0, 0);
        }

        var ifo = Path.Combine(path, "AUDIO_TS", "AUDIO_TS.IFO");
        var audioDirectory = Path.Combine(path, "AUDIO_TS");
        if (!File.Exists(ifo) && Directory.Exists(audioDirectory))
        {
            ifo = Directory.EnumerateFiles(audioDirectory, "ATS_*_0.IFO").FirstOrDefault() ?? path;
        }

        if (!File.Exists(ifo))
        {
            return (0, Directory.GetLastWriteTimeUtc(path).Ticks);
        }

        var file = new FileInfo(ifo);
        return (file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private sealed class TrackSectorStream : Stream
    {
        private readonly DvdAudioDisc _disc;
        private readonly int _titleset;
        private readonly byte[] _sector = new byte[SectorImage.UserDataSize];
        private int _offset = SectorImage.UserDataSize;
        private long _relative;
        private readonly long _end;

        public TrackSectorStream(DvdAudioDisc disc, int titleset, long start, long count)
        {
            _disc = disc;
            _titleset = titleset;
            _relative = start;
            _end = start + count;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (_offset >= _sector.Length)
                {
                    if (_relative >= _end)
                    {
                        break;
                    }

                    _disc.ReadRelativeSector(_titleset, _relative, _sector);
                    _relative++;
                    _offset = 0;
                }

                var take = Math.Min(count - written, _sector.Length - _offset);
                Buffer.BlockCopy(_sector, _offset, buffer, offset + written, take);
                _offset += take;
                written += take;
            }

            return written;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
