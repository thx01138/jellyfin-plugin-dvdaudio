namespace DvdAudio.Core;

public static class ImageFileSystem
{
    public static IReadOnlyList<ImageFile> Read(SectorImage image)
    {
        var iso = Iso9660Reader.TryRead(image);
        if (iso is { Count: > 0 } && HasAudioZone(iso))
        {
            return iso;
        }

        var udf = UdfReader.TryRead(image);
        if (udf is { Count: > 0 })
        {
            return udf;
        }

        return iso ?? udf ?? [];
    }

    private static bool HasAudioZone(IReadOnlyList<ImageFile> files)
    {
        foreach (var file in files)
        {
            if (file.FullPath.Contains("/AUDIO_TS/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
