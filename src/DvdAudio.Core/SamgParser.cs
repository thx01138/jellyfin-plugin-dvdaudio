namespace DvdAudio.Core;

public sealed class SamgChapter
{
    public int Group { get; init; }

    public int Chapter { get; init; }

    public uint StartPts { get; init; }

    public uint LengthPts { get; init; }

    public bool VideoLink { get; init; }

    public int BitsPerSample { get; init; }

    public int SampleRate { get; init; }

    public int ChannelAssignment { get; init; }

    public uint StartSector { get; init; }

    public uint EndSector { get; init; }
}

/// <summary>
/// Parses AUDIO_PP.IFO, the Simple Audio Manager. Each chapter is a 52-byte record.
/// </summary>
public static class SamgParser
{
    public const int RecordSize = 52;

    public static IReadOnlyList<SamgChapter> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data.StartsWith("DVDAUDIOSAPP"u8))
        {
            return [];
        }

        var count = Math.Min((int)Binary.Be16(data, 12), 314);
        var chapters = new List<SamgChapter>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = 16 + (i * RecordSize);
            if (!Binary.Fits(data, offset, RecordSize))
            {
                break;
            }

            var depthByte = data[offset + 17];
            var rateByte = data[offset + 18];
            var channelByte = data[offset + 19];
            var videoLink = depthByte == 0 && rateByte == 0 && channelByte == 0;
            chapters.Add(new SamgChapter
            {
                Group = data[offset + 2],
                Chapter = data[offset + 3],
                StartPts = Binary.Be32(data, offset + 4),
                LengthPts = Binary.Be32(data, offset + 8),
                VideoLink = videoLink,
                BitsPerSample = videoLink ? 0 : Bits(depthByte),
                SampleRate = videoLink ? 0 : Rate(rateByte),
                ChannelAssignment = channelByte,
                StartSector = Binary.Be32(data, offset + 40),
                EndSector = Binary.Be32(data, offset + 48)
            });
        }

        return chapters;
    }

    private static int Bits(byte value) => (value >> 4) switch
    {
        0 => 16,
        1 => 20,
        2 => 24,
        _ => 0
    };

    private static int Rate(byte value) => (value >> 4) switch
    {
        0 => 48000,
        1 => 96000,
        2 => 192000,
        8 => 44100,
        9 => 88200,
        0xA => 176400,
        _ => 0
    };
}
