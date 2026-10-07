namespace DvdAudio.Core;

public sealed class AtsProgram
{
    public int Id { get; init; }

    public uint StartPts { get; init; }

    public uint LengthPts { get; init; }

    public uint StartSector { get; init; }

    public uint EndSector { get; init; }
}

public sealed class AtsProgramChain
{
    public int Id { get; init; }

    public uint DurationPts { get; init; }

    public IReadOnlyList<AtsProgram> Programs { get; init; } = [];
}

/// <summary>
/// Parses an ATS_xx_0.IFO program chain index. Sector numbers are relative to ATS_xx_1.AOB.
/// </summary>
public static class AtsParser
{
    public static IReadOnlyList<AtsProgramChain> Parse(ReadOnlySpan<byte> ifo)
    {
        if (ifo.Length < 0xD0 || !ifo.StartsWith("DVDAUDIO-ATS"u8))
        {
            return [];
        }

        var pgciSector = Binary.Be32(ifo, 0xCC);
        var pgci = (long)pgciSector * SectorImage.UserDataSize;
        if (pgci < 0 || pgci + 8 > ifo.Length)
        {
            return [];
        }

        var chainCount = Math.Min((int)Binary.Be16(ifo, (int)pgci), 99);
        var chains = new List<AtsProgramChain>(chainCount);
        for (var index = 0; index < chainCount; index++)
        {
            var element = (int)pgci + 8 + (index * 8);
            if (!Binary.Fits(ifo, element, 8))
            {
                break;
            }

            var id = ifo[element] & 0x7F;
            var chainOffset = Binary.Be32(ifo, element + 4);
            var chain = (int)pgci + (int)chainOffset;
            if (!Binary.Fits(ifo, chain, 16))
            {
                continue;
            }

            var programCount = Math.Min((int)ifo[chain + 2], 99);
            var duration = Binary.Be32(ifo, chain + 4);
            var timeMap = Binary.Be16(ifo, chain + 0x0A);
            var sectorMap = Binary.Be16(ifo, chain + 0x0C);
            var programs = new List<AtsProgram>(programCount);
            for (var program = 0; program < programCount; program++)
            {
                var time = chain + timeMap + (program * 20);
                var sector = chain + sectorMap + (program * 12);
                if (!Binary.Fits(ifo, time, 20) || !Binary.Fits(ifo, sector, 12))
                {
                    break;
                }

                var startSector = Binary.Be32(ifo, sector + 4);
                var endSector = Binary.Be32(ifo, sector + 8);
                if (endSector < startSector)
                {
                    continue;
                }

                programs.Add(new AtsProgram
                {
                    Id = ifo[time + 4],
                    StartPts = Binary.Be32(ifo, time + 6),
                    LengthPts = Binary.Be32(ifo, time + 10),
                    StartSector = startSector,
                    EndSector = endSector
                });
            }

            if (programs.Count > 0)
            {
                chains.Add(new AtsProgramChain
                {
                    Id = id,
                    DurationPts = duration,
                    Programs = programs
                });
            }
        }

        return chains;
    }
}
