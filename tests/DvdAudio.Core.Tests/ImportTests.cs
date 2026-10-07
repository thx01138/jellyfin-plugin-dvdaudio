using System.Text;
using Jellyfin.Plugin.DvdAudio;
using Jellyfin.Plugin.DvdAudio.Configuration;
using Microsoft.Extensions.Logging;

namespace DvdAudio.Core.Tests;

public class ImportTests
{
    [Fact]
    public void ConfigPageIsEmbedded()
    {
        var resource = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.DvdAudio.Configuration.configPage.html");
        Assert.NotNull(resource);
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var html = reader.ReadToEnd();
        Assert.Contains("c3a91e47-6d2b-4f58-9a17-8e4b0d6c2f91", html, StringComparison.Ordinal);
        Assert.Contains("Library/VirtualFolders", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishesFlacAlbumAndMovieShortcut()
    {
        var ffmpeg = RequireFfmpeg();
        byte[] samples = [0x00, 0x10, 0x00, 0x20];
        var image = IsoImageBuilder.Build(
            ("/AUDIO_TS/ATS_01_0.IFO", ParserTests.AtsIfo(90_000, 0)),
            ("/AUDIO_TS/ATS_01_1.AOB", AobSector.Lpcm(samples, 0x0F, 0x0F, 1)),
            ("/VIDEO_TS/VIDEO_TS.IFO", new byte[2048]));

        var root = Path.Combine(Path.GetTempPath(), "dvda-import-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var music = Path.Combine(root, "music");
        var movies = Path.Combine(root, "movies");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(movies);
        var iso = Path.Combine(source, "Artist - Album (2001).iso");
        await File.WriteAllBytesAsync(iso, image);

        var configuration = new PluginConfiguration
        {
            MusicLibraryPath = music,
            MovieLibraryPath = movies,
            SourcePaths = source,
            AudioDelivery = AudioDelivery.ExtractFlac,
            PublishMusic = true,
            PublishMovies = true
        };

        try
        {
            var probed = DvdAudioDisc.Probe(iso);
            Assert.True(probed.IsDvdAudio, probed.ToString());
            using (var opened = DvdAudioDisc.Open(iso))
            {
                Assert.NotEmpty(opened.Albums);
                Assert.NotEmpty(opened.Albums[0].Tracks);
            }

            var log = new ListLogger();
            var changed = await DvdAudioImporter.RunAsync(configuration, data, ffmpeg, log, new Progress<double>(), CancellationToken.None);
            Assert.True(changed, string.Join(Environment.NewLine, log.Lines));

            var album = Path.Combine(music, "Artist", "Album (2001)");
            var flac = Path.Combine(album, "01 - Track 01.flac");
            Assert.True(File.Exists(flac));
            var header = new byte[4];
            await using (var stream = File.OpenRead(flac))
            {
                Assert.Equal(4, await stream.ReadAsync(header));
            }

            Assert.Equal("fLaC"u8.ToArray(), header);
            var nfo = await File.ReadAllTextAsync(Path.Combine(album, "album.nfo"));
            Assert.Contains("<title>Album (2001)</title>", nfo, StringComparison.Ordinal);
            Assert.Contains("<artist>Artist</artist>", nfo, StringComparison.Ordinal);
            Assert.Contains("<year>2001</year>", nfo, StringComparison.Ordinal);
            Assert.Contains("\"TrackCount\": 1", await File.ReadAllTextAsync(Path.Combine(album, "dvd-audio.json")), StringComparison.Ordinal);

            var movieIso = Path.Combine(movies, "DVD-Audio", "Album (2001)", "Album (2001).iso");
            var movieStrm = Path.ChangeExtension(movieIso, ".strm");
            Assert.True(File.Exists(movieIso) || File.Exists(movieStrm));
            if (File.Exists(movieStrm))
            {
                Assert.Equal(Path.GetFullPath(iso), (await File.ReadAllTextAsync(movieStrm)).Trim());
            }

            await File.WriteAllTextAsync(Path.Combine(album, "album.nfo"), "custom-nfo");
            var again = await DvdAudioImporter.RunAsync(configuration, data, ffmpeg, log, new Progress<double>(), CancellationToken.None);
            Assert.False(again);
            Assert.Equal("custom-nfo", await File.ReadAllTextAsync(Path.Combine(album, "album.nfo")));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task ProtectedDiscSkipsAudioAndDoesNotReplaceAnExistingMovie()
    {
        var root = Path.Combine(Path.GetTempPath(), "dvda-cppm-" + Guid.NewGuid().ToString("N"));
        var disc = Path.Combine(root, "Hybrid Disc");
        var music = Path.Combine(root, "music");
        var movies = Path.Combine(root, "movies");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(disc, "AUDIO_TS"));
        Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS"));
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(movies);
        await File.WriteAllBytesAsync(Path.Combine(disc, "AUDIO_TS", "ATS_01_0.IFO"), ParserTests.AtsIfo(90_000, 0));
        await File.WriteAllBytesAsync(Path.Combine(disc, "AUDIO_TS", "DVDAUDIO.MKB"), [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(Path.Combine(disc, "VIDEO_TS", "VIDEO_TS.IFO"), new byte[2048]);

        var configuration = new PluginConfiguration
        {
            MusicLibraryPath = music,
            MovieLibraryPath = movies,
            SourcePaths = disc,
            PublishMusic = true,
            PublishMovies = true
        };

        try
        {
            var log = new ListLogger();
            await DvdAudioImporter.RunAsync(configuration, data, "ffmpeg", log, new Progress<double>(), CancellationToken.None);
            Assert.Empty(Directory.EnumerateFiles(music, "*.flac", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(music, "*.strm", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.EnumerateFiles(data, "cppm.json", SearchOption.AllDirectories));

            var movieFolder = Path.Combine(movies, "DVD-Audio", "Hybrid Disc");
            if (Directory.Exists(movieFolder))
            {
                await File.WriteAllTextAsync(Path.Combine(movieFolder, "keep.txt"), "keep");
                await DvdAudioImporter.RunAsync(configuration, data, "ffmpeg", log, new Progress<double>(), CancellationToken.None);
                Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(movieFolder, "keep.txt")));
            }
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        Directory.Delete(root, recursive: true);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => new Scope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(logLevel + ": " + formatter(state, exception) + (exception is null ? string.Empty : Environment.NewLine + exception));
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static string RequireFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("ffmpeg was not found on PATH.");
    }
}
