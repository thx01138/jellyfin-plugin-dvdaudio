using System.Buffers.Binary;
using System.Text;

namespace DvdAudio.Core.Tests;

public class ParserTests
{
    [Fact]
    public void SamgParser_ReadsAudioAndVideoChapters()
    {
        var data = new byte[16 + (52 * 2)];
        "DVDAUDIOSAPP"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 2);
        WriteChapter(data, 0, group: 1, chapter: 1, lengthPts: 90_000, depth: 0x2F, rate: 0x1F, channels: 1, start: 100, end: 200);
        WriteChapter(data, 1, group: 2, chapter: 1, lengthPts: 0, depth: 0, rate: 0, channels: 0, start: 0, end: 0);

        var chapters = SamgParser.Parse(data);

        Assert.Equal(2, chapters.Count);
        Assert.False(chapters[0].VideoLink);
        Assert.Equal(24, chapters[0].BitsPerSample);
        Assert.Equal(96000, chapters[0].SampleRate);
        Assert.Equal(1, chapters[0].ChannelAssignment);
        Assert.Equal(100u, chapters[0].StartSector);
        Assert.True(chapters[1].VideoLink);
    }

    [Fact]
    public void AtsParser_ReadsOneProgram()
    {
        var ifo = AtsIfo(lengthPts: 90_000, endSector: 4);
        var chains = AtsParser.Parse(ifo);
        var program = Assert.Single(Assert.Single(chains).Programs);
        Assert.Equal(1, program.Id);
        Assert.Equal(0u, program.StartSector);
        Assert.Equal(4u, program.EndSector);
        Assert.Equal(90_000u, program.LengthPts);
    }

    private static void WriteChapter(byte[] data, int index, byte group, byte chapter, uint lengthPts, byte depth, byte rate, byte channels, uint start, uint end)
    {
        var offset = 16 + (index * 52);
        data[offset + 2] = group;
        data[offset + 3] = chapter;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 8), lengthPts);
        data[offset + 17] = depth;
        data[offset + 18] = rate;
        data[offset + 19] = channels;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 40), start);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 48), end);
    }

    internal static byte[] AtsIfo(uint lengthPts, uint endSector)
    {
        var ifo = new byte[4096];
        "DVDAUDIO-ATS"u8.CopyTo(ifo);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xCC), 1);
        var pgci = 2048;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(pgci), 1);
        ifo[pgci + 8] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(pgci + 12), 16);
        var chain = pgci + 16;
        ifo[chain + 2] = 1;
        ifo[chain + 3] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(chain + 4), lengthPts);
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(chain + 0x0A), 16);
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(chain + 0x0C), 36);
        var time = chain + 16;
        ifo[time + 4] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(time + 10), lengthPts);
        var sector = chain + 36;
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(sector + 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(sector + 8), endSector);
        return ifo;
    }
}

public class DemuxTests
{
    [Fact]
    public void Demuxes16BitStereoToLittleEndian()
    {
        byte[] pcm = [0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04];
        var sector = AobSector.Lpcm(pcm, depth: 0x0F, rate: 0x0F, assignment: 1);
        var probe = AobDemuxer.Inspect(sector);
        using var output = new MemoryStream();
        AobDemuxer.Demux(sector, output, probe);

        Assert.Equal(AobCodec.Lpcm, probe.Codec);
        Assert.Equal(48000, probe.Lpcm!.SampleRate);
        Assert.Equal(2, probe.Lpcm.OutputChannels);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00 }, output.ToArray());
    }

    [Fact]
    public void ReordersAssignment20ToWaveOrder()
    {
        var pcm = new byte[12];
        for (var sample = 1; sample <= 6; sample++)
        {
            pcm[(sample - 1) * 2] = 0;
            pcm[(sample - 1) * 2 + 1] = (byte)sample;
        }

        var sector = AobSector.Lpcm(pcm, depth: 0x0F, rate: 0x1F, assignment: 20);
        var probe = AobDemuxer.Inspect(sector);
        using var output = new MemoryStream();
        AobDemuxer.Demux(sector, output, probe);

        Assert.Equal("FL+FR+FC+LFE+BL+BR", probe.Lpcm!.ChannelLayout);
        Assert.Equal(96000, probe.Lpcm.SampleRate);
        Assert.Equal(new byte[] { 1, 0, 2, 0, 5, 0, 6, 0, 3, 0, 4, 0 }, output.ToArray());
    }

    [Fact]
    public void Unpacks20BitPairs()
    {
        byte[] packed = [0x12, 0x34, 0x5F, 0xFF, 0xFE];
        var sector = AobSector.Lpcm(packed, depth: 0x1F, rate: 0x8F, assignment: 1);
        var probe = AobDemuxer.Inspect(sector);
        using var output = new MemoryStream();
        AobDemuxer.Demux(sector, output, probe);

        Assert.Equal(20, probe.Lpcm!.BitsPerSample);
        Assert.Equal(44100, probe.Lpcm.SampleRate);
        var bytes = output.ToArray();
        Assert.Equal(0x00012345, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(-2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
    }
}

public class ImageTests
{
    [Fact]
    public void Iso9660RoundTripsAFile()
    {
        var payload = Encoding.ASCII.GetBytes("audio-ts");
        var image = IsoImageBuilder.Build(("/AUDIO_TS/NOTE.TXT", payload));
        using var sectors = SectorImage.FromSectors(image);
        var file = Assert.Single(ImageFileSystem.Read(sectors), item => item.FullPath.EndsWith("NOTE.TXT", StringComparison.OrdinalIgnoreCase));
        using var output = new MemoryStream();
        sectors.CopyUserBytes(file.Extents[0].Sector, 0, file.Length, output);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public void UdfRoundTripsAFile()
    {
        var payload = "hello"u8.ToArray();
        var image = UdfImageBuilder.Build("/AUDIO_TS/HELLO.TXT", payload);
        using var sectors = SectorImage.FromSectors(image);
        var files = ImageFileSystem.Read(sectors);
        var file = Assert.Single(files, item => item.FullPath.Contains("HELLO.TXT", StringComparison.OrdinalIgnoreCase));
        using var output = new MemoryStream();
        sectors.CopyUserBytes(file.Extents[0].Sector, 0, file.Extents[0].Length, output);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public void DiscReaderDemuxesTrackFromIso()
    {
        byte[] samples = [0x00, 0x10, 0x00, 0x20];
        var image = IsoImageBuilder.Build(
            ("/AUDIO_TS/ATS_01_0.IFO", ParserTests.AtsIfo(90_000, 0)),
            ("/AUDIO_TS/ATS_01_1.AOB", AobSector.Lpcm(samples, 0x0F, 0x0F, 1)),
            ("/VIDEO_TS/VIDEO_TS.IFO", new byte[2048]));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            File.WriteAllBytes(path, image);
            using var disc = DvdAudioDisc.Open(path);
            Assert.True(disc.HasVideoZone);
            var album = Assert.Single(disc.Albums);
            var track = Assert.Single(album.Tracks);
            Assert.Equal(TimeSpan.FromSeconds(1), track.Duration);
            Assert.Equal("LPCM", track.Coding);
            Assert.Equal(48000, track.SampleRate);
            Assert.Equal(2, track.Channels);
            var probe = disc.ProbeTrack(track);
            using var output = new MemoryStream();
            disc.DemuxTrack(track, output, probe);
            Assert.Equal(new byte[] { 0x10, 0x00, 0x20, 0x00 }, output.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DiscReaderReadsAFolderRip()
    {
        var root = Path.Combine(Path.GetTempPath(), "dvda-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "AUDIO_TS"));
            File.WriteAllBytes(Path.Combine(root, "AUDIO_TS", "ATS_01_0.IFO"), ParserTests.AtsIfo(180_000, 0));
            File.WriteAllBytes(Path.Combine(root, "AUDIO_TS", "ATS_01_1.AOB"), AobSector.Lpcm([0x00, 0x01, 0x00, 0x02], 0x0F, 0x0F, 1));
            File.WriteAllBytes(Path.Combine(root, "AUDIO_TS", "DVDAUDIO.MKB"), [1, 2, 3]);
            using var disc = DvdAudioDisc.Open(root);
            Assert.True(disc.IsCppmProtected);
            Assert.False(disc.HasVideoZone);
            Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(Assert.Single(disc.Albums).Tracks).Duration);
            Assert.Null(Assert.Single(disc.Albums).Tracks[0].Coding);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal static class AobSector
{
    public static byte[] Lpcm(byte[] audio, byte depth, byte rate, byte assignment)
    {
        var header = new byte[]
        {
            0xA0, 0x00, 0x00, 0x08,
            0x00, 0x00, 0x10, depth, rate, 0x00, assignment, 0x80
        };
        var payload = header.Concat(audio).ToArray();
        var pesHeader = new byte[] { 0x80, 0x80, 0x05, 0x21, 0x00, 0x01, 0x00, 0x01 };
        var packetLength = pesHeader.Length + payload.Length;
        var sector = new byte[2048];
        sector[2] = 1;
        sector[3] = 0xBD;
        sector[4] = (byte)(packetLength >> 8);
        sector[5] = (byte)packetLength;
        pesHeader.CopyTo(sector, 6);
        payload.CopyTo(sector, 6 + pesHeader.Length);
        return sector;
    }
}
