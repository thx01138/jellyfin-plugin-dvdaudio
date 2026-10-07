using System.Diagnostics;
using System.Globalization;

namespace DvdAudio.Core;

public static class FlacExporter
{
    public static async Task ExportAsync(
        DvdAudioDisc disc,
        DvdAudioTrack track,
        string ffmpegPath,
        string destinationPath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        var probe = disc.ProbeTrack(track);
        if (probe.Scrambled || probe.Codec == AobCodec.Unknown)
        {
            throw new DvdAudioException(probe.Detail ?? "This DVD-Audio track is not a supported LPCM or MLP stream.");
        }

        // ffmpeg picks the muxer from the extension, so the temporary file has to end in .flac.
        var partial = Path.Combine(
            Path.GetDirectoryName(destinationPath) ?? ".",
            Path.GetFileNameWithoutExtension(destinationPath) + ".partial.flac");
        if (File.Exists(partial))
        {
            File.Delete(partial);
        }

        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (probe.Codec == AobCodec.Lpcm)
        {
            var info = probe.Lpcm ?? throw new DvdAudioException("LPCM parameters were not found.");
            arguments.Add("-f");
            arguments.Add(info.BitsPerSample switch
            {
                16 => "s16le",
                24 => "s24le",
                20 => "s32le",
                _ => throw new DvdAudioException($"Unsupported LPCM width {info.BitsPerSample}.")
            });
            arguments.Add("-ar");
            arguments.Add(info.SampleRate.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-ac");
            arguments.Add(info.OutputChannels.ToString(CultureInfo.InvariantCulture));
            if (info.ChannelLayout.Length > 0)
            {
                arguments.Add("-channel_layout");
                arguments.Add(info.ChannelLayout);
            }
        }
        else
        {
            arguments.Add("-f");
            arguments.Add("mlp");
        }

        arguments.Add("-i");
        arguments.Add("pipe:0");
        foreach (var tag in tags)
        {
            var value = tag.Value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (value.Length == 0)
            {
                continue;
            }

            arguments.Add("-metadata");
            arguments.Add(tag.Key + "=" + value);
        }

        arguments.Add("-c:a");
        arguments.Add("flac");
        arguments.Add("-compression_level");
        arguments.Add("8");
        arguments.Add(partial);

        var start = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new DvdAudioException("ffmpeg did not start.");
        }

        using var cancel = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        try
        {
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdin = process.StandardInput;
            disc.DemuxTrack(track, stdin.BaseStream, probe);
            stdin.Close();

            var error = await stderr.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new DvdAudioException("ffmpeg exited " + process.ExitCode + ": " + Trim(error));
            }

            File.Move(partial, destinationPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            throw;
        }
    }

    private static string Trim(string text)
    {
        text = text.Trim();
        return text.Length <= 800 ? text : text[^800..];
    }
}
