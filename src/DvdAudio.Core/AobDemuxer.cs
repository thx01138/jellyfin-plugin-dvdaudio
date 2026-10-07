namespace DvdAudio.Core;

public enum AobCodec
{
    Unknown = 0,
    Lpcm = 1,
    Mlp = 2
}

public sealed class LpcmStreamInfo
{
    public int SampleRate { get; init; }

    public int BitsPerSample { get; init; }

    public int ChannelAssignment { get; init; }

    public int SourceChannels { get; init; }

    public int OutputChannels { get; init; }

    /// <summary>Source channel index for each output channel, in ffmpeg layout order.</summary>
    public int[] OutputOrder { get; init; } = [];

    public string ChannelLayout { get; init; } = string.Empty;
}

public sealed class AobProbe
{
    public AobCodec Codec { get; init; }

    public bool Scrambled { get; init; }

    public LpcmStreamInfo? Lpcm { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// Demuxes DVD-Audio AOB packs (2048-byte MPEG program stream sectors) into
/// little-endian PCM or a raw MLP bitstream ffmpeg can decode.
/// </summary>
public static class AobDemuxer
{
    public static AobProbe Inspect(ReadOnlySpan<byte> sectors)
    {
        if (sectors.Length < SectorImage.UserDataSize)
        {
            return new AobProbe { Detail = "The audio object is empty." };
        }

        AobProbe? last = null;
        var count = sectors.Length / SectorImage.UserDataSize;
        for (var i = 0; i < count && i < 32; i++)
        {
            var probe = InspectSector(sectors.Slice(i * SectorImage.UserDataSize, SectorImage.UserDataSize));
            if (probe.Scrambled)
            {
                return probe;
            }

            if (probe.Codec != AobCodec.Unknown)
            {
                return probe;
            }

            last = probe;
        }

        return last ?? new AobProbe { Detail = "No DVD-Audio packet was found in the first sectors of the track." };
    }

    public static void Demux(ReadOnlySpan<byte> sectors, Stream output, AobProbe probe)
    {
        if (probe.Codec == AobCodec.Unknown || probe.Scrambled)
        {
            throw new DvdAudioException(probe.Detail ?? "The track cannot be demuxed.");
        }

        var startedMlp = false;
        var count = sectors.Length / SectorImage.UserDataSize;
        for (var i = 0; i < count; i++)
        {
            DemuxSector(sectors.Slice(i * SectorImage.UserDataSize, SectorImage.UserDataSize), output, probe, ref startedMlp);
        }
    }

    public static void Demux(Stream sectors, long sectorCount, Stream output, AobProbe probe)
    {
        if (probe.Codec == AobCodec.Unknown || probe.Scrambled)
        {
            throw new DvdAudioException(probe.Detail ?? "The track cannot be demuxed.");
        }

        var startedMlp = false;
        var sector = new byte[SectorImage.UserDataSize];
        for (long i = 0; i < sectorCount; i++)
        {
            var read = 0;
            while (read < sector.Length)
            {
                var n = sectors.Read(sector, read, sector.Length - read);
                if (n == 0)
                {
                    return;
                }

                read += n;
            }

            DemuxSector(sector, output, probe, ref startedMlp);
        }
    }

    private static AobProbe InspectSector(ReadOnlySpan<byte> sector)
    {
        AobProbe? detail = null;
        foreach (var payload in Payloads(sector, out var scrambled))
        {
            if (scrambled)
            {
                return new AobProbe
                {
                    Scrambled = true,
                    Detail = "The audio pack is scrambled. This plugin does not decrypt CPPM-protected DVD-Audio."
                };
            }

            if (payload.IsEmpty)
            {
                continue;
            }

            if (payload.Span[0] == 0xA1)
            {
                return new AobProbe { Codec = AobCodec.Mlp };
            }

            if (payload.Span[0] == 0xA0)
            {
                if (!TryDescribeLpcm(payload.Span, out var info, out var error))
                {
                    detail = new AobProbe { Detail = error };
                    continue;
                }

                return new AobProbe { Codec = AobCodec.Lpcm, Lpcm = info };
            }
        }

        return detail ?? new AobProbe { Detail = "The sector does not contain a DVD-Audio private stream." };
    }

    private static void DemuxSector(ReadOnlySpan<byte> sector, Stream output, AobProbe probe, ref bool startedMlp)
    {
        foreach (var payload in Payloads(sector, out var scrambled))
        {
            if (scrambled || payload.IsEmpty)
            {
                continue;
            }

            if (probe.Codec == AobCodec.Mlp && payload.Span[0] == 0xA1)
            {
                WriteMlp(payload.Span, output, ref startedMlp);
            }
            else if (probe.Codec == AobCodec.Lpcm && payload.Span[0] == 0xA0 && probe.Lpcm is not null)
            {
                WriteLpcm(payload.Span, output, probe.Lpcm);
            }
        }
    }

    private static List<ReadOnlyMemory<byte>> Payloads(ReadOnlySpan<byte> sector, out bool scrambled)
    {
        // The caller needs the payload to outlive this method, so copy each one.
        var list = new List<ReadOnlyMemory<byte>>();
        scrambled = false;
        for (var i = 0; i + 6 < sector.Length; i++)
        {
            if (sector[i] != 0 || sector[i + 1] != 0 || sector[i + 2] != 1 || sector[i + 3] != 0xBD)
            {
                continue;
            }

            var packetLength = (sector[i + 4] << 8) | sector[i + 5];
            var available = sector.Length - (i + 6);
            if (packetLength == 0 || packetLength > available)
            {
                packetLength = available;
            }

            var pes = sector.Slice(i + 6, packetLength);
            if (pes.Length < 4 || (pes[0] & 0xC0) != 0x80)
            {
                continue;
            }

            if ((pes[0] & 0x30) != 0)
            {
                scrambled = true;
                return list;
            }

            var headerLength = pes[2];
            var payloadAt = 3 + headerLength;
            if (payloadAt >= pes.Length)
            {
                continue;
            }

            list.Add(pes[payloadAt..].ToArray());
            i += 5 + packetLength;
        }

        return list;
    }

    private static bool TryDescribeLpcm(ReadOnlySpan<byte> payload, out LpcmStreamInfo info, out string error)
    {
        info = new LpcmStreamInfo();
        error = string.Empty;
        if (payload.Length < 12)
        {
            error = "The LPCM header is truncated.";
            return false;
        }

        var headerLength = (payload[2] << 8) | payload[3];
        if (headerLength < 8 || 4 + headerLength > payload.Length)
        {
            error = "The LPCM header length is not valid.";
            return false;
        }

        var depth = payload[7];
        var rate = payload[8];
        if ((depth & 0x0F) != 0x0F || (rate & 0x0F) != 0x0F)
        {
            error = "Dual channel-group LPCM (different sample rate or depth per group) is not supported.";
            return false;
        }

        var bits = (depth >> 4) switch
        {
            0 => 16,
            1 => 20,
            2 => 24,
            _ => 0
        };
        var sampleRate = (rate >> 4) switch
        {
            0 => 48000,
            1 => 96000,
            2 => 192000,
            8 => 44100,
            9 => 88200,
            0xA => 176400,
            _ => 0
        };
        var assignment = payload[10];
        if (bits == 0 || sampleRate == 0 || !ChannelMaps.TryDescribe(assignment, bits, sampleRate, out info))
        {
            error = $"Unsupported LPCM format (assignment {assignment}, {sampleRate} Hz, {bits}-bit).";
            return false;
        }

        return true;
    }

    private static void WriteLpcm(ReadOnlySpan<byte> payload, Stream output, LpcmStreamInfo info)
    {
        var headerLength = (payload[2] << 8) | payload[3];
        var audioAt = 4 + headerLength;
        if (audioAt > payload.Length)
        {
            return;
        }

        var audio = payload[audioAt..];
        if (info.BitsPerSample == 20)
        {
            Write20Bit(audio, output, info);
            return;
        }

        var bytesPerSample = info.BitsPerSample / 8;
        var frameBytes = info.SourceChannels * bytesPerSample;
        if (frameBytes <= 0)
        {
            return;
        }

        var frames = audio.Length / frameBytes;
        var packed = new byte[info.OutputChannels * bytesPerSample];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < info.OutputOrder.Length; channel++)
            {
                var source = info.OutputOrder[channel];
                var from = (frame * frameBytes) + (source * bytesPerSample);
                var to = channel * bytesPerSample;
                for (var b = 0; b < bytesPerSample; b++)
                {
                    packed[to + b] = audio[from + bytesPerSample - 1 - b];
                }
            }

            output.Write(packed);
        }
    }

    private static void Write20Bit(ReadOnlySpan<byte> audio, Stream output, LpcmStreamInfo info)
    {
        // Two 20-bit samples are packed into 5 bytes, in source channel order.
        var pairBytes = audio.Length / 5 * 5;
        var sampleCount = (pairBytes / 5) * 2;
        var frames = sampleCount / info.SourceChannels;
        Span<byte> packed = stackalloc byte[4];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < info.OutputOrder.Length; channel++)
            {
                var sampleIndex = (frame * info.SourceChannels) + info.OutputOrder[channel];
                var value = Read20(audio, sampleIndex);
                packed[0] = (byte)value;
                packed[1] = (byte)(value >> 8);
                packed[2] = (byte)(value >> 16);
                packed[3] = (byte)(value >> 24);
                output.Write(packed);
            }
        }
    }

    private static int Read20(ReadOnlySpan<byte> audio, int sampleIndex)
    {
        var pair = sampleIndex / 2;
        var offset = pair * 5;
        int value = (sampleIndex & 1) == 0
            ? (audio[offset] << 12) | (audio[offset + 1] << 4) | (audio[offset + 2] >> 4)
            : ((audio[offset + 2] & 0x0F) << 16) | (audio[offset + 3] << 8) | audio[offset + 4];
        if ((value & 0x80000) != 0)
        {
            value |= unchecked((int)0xFFF00000);
        }

        return value;
    }

    private static void WriteMlp(ReadOnlySpan<byte> payload, Stream output, ref bool started)
    {
        if (payload.Length <= 4)
        {
            return;
        }

        var body = payload[4..];
        if (!started)
        {
            var sync = body.IndexOf("\xF8\x72\x6F"u8);
            if (sync < 0)
            {
                return;
            }

            body = body[sync..];
            started = true;
        }

        output.Write(body);
    }
}

internal static class ChannelMaps
{
    // Channel order inside a DVD-Audio LPCM frame for each assignment id.
    private static readonly string[][] Assignments =
    [
        ["C"],
        ["L", "R"],
        ["L", "R", "S"],
        ["L", "R", "Ls", "Rs"],
        ["L", "R", "Lfe"],
        ["L", "R", "Lfe", "S"],
        ["L", "R", "Lfe", "Ls", "Rs"],
        ["L", "R", "C"],
        ["L", "R", "C", "S"],
        ["L", "R", "C", "Ls", "Rs"],
        ["L", "R", "C", "Lfe"],
        ["L", "R", "C", "Lfe", "S"],
        ["L", "R", "C", "Lfe", "Ls", "Rs"],
        ["L", "R", "C", "S"],
        ["L", "R", "C", "Ls", "Rs"],
        ["L", "R", "C", "Lfe"],
        ["L", "R", "C", "Lfe", "S"],
        ["L", "R", "C", "Lfe", "Ls", "Rs"],
        ["L", "R", "Ls", "Rs", "Lfe"],
        ["L", "R", "Ls", "Rs", "C"],
        ["L", "R", "Ls", "Rs", "C", "Lfe"]
    ];

    public static bool TryDescribe(int assignment, int bits, int sampleRate, out LpcmStreamInfo info)
    {
        info = new LpcmStreamInfo();
        if (assignment < 0 || assignment >= Assignments.Length)
        {
            return false;
        }

        var source = Assignments[assignment];
        var ranked = source
            .Select((name, index) => (Name: name, Index: index))
            .OrderBy(item => Rank(item.Name))
            .ThenBy(item => item.Index)
            .ToArray();
        info = new LpcmStreamInfo
        {
            SampleRate = sampleRate,
            BitsPerSample = bits,
            ChannelAssignment = assignment,
            SourceChannels = source.Length,
            OutputChannels = source.Length,
            OutputOrder = ranked.Select(item => item.Index).ToArray(),
            ChannelLayout = string.Join('+', ranked.Select(item => FfmpegName(item.Name)))
        };
        return true;
    }

    private static int Rank(string channel) => channel switch
    {
        "L" => 0,
        "R" => 1,
        "C" => 2,
        "Lfe" => 3,
        "Ls" => 4,
        "Rs" => 5,
        "S" => 6,
        _ => 9
    };

    private static string FfmpegName(string channel) => channel switch
    {
        "L" => "FL",
        "R" => "FR",
        "C" => "FC",
        "Lfe" => "LFE",
        "Ls" => "BL",
        "Rs" => "BR",
        "S" => "BC",
        _ => "FC"
    };
}
