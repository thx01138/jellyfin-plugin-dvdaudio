using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DvdAudio.Core;
using Jellyfin.Plugin.DvdAudio.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvdAudio;

/// <summary>
/// Publishes one album per DVD-Audio group and, for hybrid discs, one movie-library link.
/// </summary>
public static class DvdAudioImporter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex YearPattern = new(@"(?:19|20)\d{2}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Imports every configured disc. Returns true when a library file was written.
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="dataPath">Server data directory. Cache mode stores FLAC files under it.</param>
    /// <param name="ffmpegPath">ffmpeg executable used to encode FLAC.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="progress">Task progress, reported from 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when music or movie files were created or replaced.</returns>
    public static async Task<bool> RunAsync(
        PluginConfiguration configuration,
        string dataPath,
        string ffmpegPath,
        ILogger logger,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var sources = Discover(configuration, logger);
        if (sources.Count == 0)
        {
            logger.LogInformation("DVD-Audio import found no discs. Set a music library, a movie library, or extra source paths.");
            progress.Report(100);
            return false;
        }

        var changed = false;
        for (var index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await ImportDiscAsync(sources[index], configuration, dataPath, ffmpegPath, logger, cancellationToken).ConfigureAwait(false))
                {
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "DVD-Audio import failed for {Path}", sources[index]);
            }

            progress.Report((index + 1d) / sources.Count * 100d);
        }

        return changed;
    }

    private static async Task<bool> ImportDiscAsync(
        string path,
        PluginConfiguration configuration,
        string dataPath,
        string ffmpegPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        DiscProbe probe;
        try
        {
            probe = DvdAudioDisc.Probe(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not probe {Path}", path);
            return false;
        }

        if (!probe.IsDvdAudio)
        {
            return false;
        }

        if (probe.IsCppmProtected)
        {
            return ImportProtected(path, probe, configuration, dataPath, logger);
        }

        using var disc = DvdAudioDisc.Open(path);
        var changed = false;
        if (configuration.PublishMusic)
        {
            changed |= await PublishMusicAsync(disc, configuration, dataPath, ffmpegPath, logger, cancellationToken).ConfigureAwait(false);
        }

        if (configuration.PublishMovies && disc.HasVideoZone)
        {
            changed |= PublishMovie(path, configuration, logger);
        }

        return changed;
    }

    private static bool ImportProtected(string path, DiscProbe probe, PluginConfiguration configuration, string dataPath, ILogger logger)
    {
        var stamp = Stamp(path);
        var marker = MarkerPath(dataPath, path);
        var seen = marker is not null && File.Exists(marker) && MarkerMatches(marker, path, stamp);
        logger.Log(
            seen ? LogLevel.Information : LogLevel.Error,
            "Skipping audio extraction for {Path}. DVDAUDIO.MKB is present, so the audio zone is CPPM protected. This plugin does not decrypt CPPM.",
            path);
        if (marker is not null && !seen)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, JsonSerializer.Serialize(new AlbumManifest
            {
                SourcePath = path,
                Length = stamp.Length,
                MtimeUtcTicks = stamp.Ticks,
                Group = 0,
                Delivery = "CppmSkipped",
                TrackCount = 0
            }, JsonOptions), Utf8);
        }

        if (!configuration.PublishMovies || !probe.HasVideoZone)
        {
            return false;
        }

        return PublishMovie(path, configuration, logger);
    }

    private static async Task<bool> PublishMusicAsync(
        DvdAudioDisc disc,
        PluginConfiguration configuration,
        string dataPath,
        string ffmpegPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.MusicLibraryPath))
        {
            logger.LogWarning("DVD-Audio music publishing is enabled, but no music library path is set.");
            return false;
        }

        if (configuration.AudioDelivery == AudioDelivery.CacheStrm && string.IsNullOrWhiteSpace(dataPath))
        {
            logger.LogError("DVD-Audio cache delivery needs the Jellyfin data directory, and that path is empty.");
            return false;
        }

        if (!Directory.Exists(configuration.MusicLibraryPath))
        {
            logger.LogWarning("DVD-Audio music library path does not exist: {Path}", configuration.MusicLibraryPath);
            return false;
        }

        var changed = false;
        foreach (var album in disc.Albums)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed |= await PublishAlbumAsync(disc, album, configuration, dataPath, ffmpegPath, logger, cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private static async Task<bool> PublishAlbumAsync(
        DvdAudioDisc disc,
        DvdAudioAlbum album,
        PluginConfiguration configuration,
        string dataPath,
        string ffmpegPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (album.Tracks.Count == 0)
        {
            return false;
        }

        var names = NamesFor(disc.SourcePath, album.GroupNumber);
        var musicDirectory = Path.Combine(configuration.MusicLibraryPath, names.Artist, names.Album);
        Directory.CreateDirectory(musicDirectory);

        var stamp = Stamp(disc.SourcePath);
        var delivery = configuration.AudioDelivery.ToString();
        var manifestPath = Path.Combine(musicDirectory, "dvd-audio.json");
        var existing = ReadManifest(manifestPath);
        if (existing is not null
            && !string.Equals(existing.SourcePath, disc.SourcePath, PathComparison))
        {
            logger.LogWarning(
                "Album folder {Folder} was previously imported from {Previous}. It is being reused for {Path}.",
                musicDirectory,
                existing.SourcePath,
                disc.SourcePath);
        }

        var cacheDirectory = configuration.AudioDelivery == AudioDelivery.CacheStrm
            ? CacheDirectory(dataPath, disc.SourcePath, album.GroupNumber)
            : null;
        if (cacheDirectory is not null)
        {
            Directory.CreateDirectory(cacheDirectory);
        }

        if (existing is not null && !string.Equals(existing.Delivery, delivery, StringComparison.Ordinal))
        {
            RemoveTracks(musicDirectory, existing.TrackCount, existing.Delivery);
        }

        var complete = true;
        var changed = false;
        for (var index = 0; index < album.Tracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = index + 1;
            var track = album.Tracks[index];
            var baseName = sequence.ToString("00", CultureInfo.InvariantCulture)
                + " - Track "
                + sequence.ToString("00", CultureInfo.InvariantCulture);
            var flacPath = configuration.AudioDelivery == AudioDelivery.CacheStrm
                ? Path.Combine(cacheDirectory!, baseName + ".flac")
                : Path.Combine(musicDirectory, baseName + ".flac");
            var libraryPath = configuration.AudioDelivery == AudioDelivery.CacheStrm
                ? Path.Combine(musicDirectory, baseName + ".strm")
                : flacPath;

            if (!configuration.RebuildExisting && LibraryTrackReady(libraryPath, flacPath, configuration.AudioDelivery))
            {
                continue;
            }

            try
            {
                var tags = Tags(names, sequence, album.Tracks.Count, track, disc.SourcePath);
                await FlacExporter.ExportAsync(disc, track, ffmpegPath, flacPath, tags, cancellationToken).ConfigureAwait(false);
                if (configuration.AudioDelivery == AudioDelivery.CacheStrm)
                {
                    File.WriteAllText(libraryPath, flacPath, Utf8);
                }

                changed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                complete = false;
                logger.LogError(ex, "Could not extract track {Track} from {Path}", sequence, disc.SourcePath);
            }
        }

        if (Directory.EnumerateFiles(musicDirectory, "*.flac").Any()
            || Directory.EnumerateFiles(musicDirectory, "*.strm").Any())
        {
            if (WriteAlbumNfo(musicDirectory, names, disc.SourcePath))
            {
                changed = true;
            }
        }

        if (complete)
        {
            var manifest = new AlbumManifest
            {
                SourcePath = disc.SourcePath,
                Length = stamp.Length,
                MtimeUtcTicks = stamp.Ticks,
                Group = album.GroupNumber,
                Delivery = delivery,
                TrackCount = album.Tracks.Count
            };
            var previous = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null;
            var next = JsonSerializer.Serialize(manifest, JsonOptions);
            if (!string.Equals(previous, next, StringComparison.Ordinal))
            {
                File.WriteAllText(manifestPath, next, Utf8);
                changed = true;
            }
        }

        return changed;
    }

    private static bool PublishMovie(string path, PluginConfiguration configuration, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configuration.MovieLibraryPath))
        {
            logger.LogWarning("DVD-Audio movie publishing is enabled, but no movie library path is set. Video zone of {Path} was left in place.", path);
            return false;
        }

        if (DvdAudioPaths.IsUnder(path, configuration.MovieLibraryPath))
        {
            logger.LogInformation("Hybrid disc {Path} is already in the movie library, so no second link was created.", path);
            return false;
        }

        if (!Directory.Exists(configuration.MovieLibraryPath))
        {
            logger.LogWarning("DVD-Audio movie library path does not exist: {Path}", configuration.MovieLibraryPath);
            return false;
        }

        var album = NamesFor(path, groupNumber: 1).Album;
        if (File.Exists(path))
        {
            var destination = Path.Combine(configuration.MovieLibraryPath, "DVD-Audio", album, album + ".iso");
            return LinkFile(path, destination, logger);
        }

        var directory = Path.Combine(configuration.MovieLibraryPath, "DVD-Audio", album);
        return LinkDirectory(path, directory, logger);
    }

    private static bool LinkFile(string source, string destination, ILogger logger)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            logger.LogInformation("Leaving the existing movie file {Path}", destination);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.CreateSymbolicLink(destination, source);
            logger.LogInformation("Linked movie {Destination} to {Source}", destination, source);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var strm = Path.ChangeExtension(destination, ".strm");
            logger.LogWarning(ex, "Could not create a symlink at {Destination}. Writing {Strm} instead.", destination, strm);
            if (File.Exists(strm) || Directory.Exists(strm))
            {
                return false;
            }

            File.WriteAllText(strm, source, Utf8);
            return true;
        }
    }

    private static bool LinkDirectory(string source, string destination, ILogger logger)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            logger.LogInformation("Leaving the existing movie folder {Path}", destination);
            return false;
        }

        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            Directory.CreateSymbolicLink(destination, source);
            logger.LogInformation("Linked movie folder {Destination} to {Source}", destination, source);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(
                ex,
                "Could not create a directory symlink from {Destination} to {Source}. Create that link on the Jellyfin server so the VIDEO_TS folder can be played.",
                destination,
                source);
            return false;
        }
    }

    private static List<string> Discover(PluginConfiguration configuration, ILogger logger)
    {
        var roots = new List<string>();
        AddRoots(roots, configuration.SourcePaths);
        AddRoots(roots, configuration.MusicLibraryPath);
        AddRoots(roots, configuration.MovieLibraryPath);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var found = new HashSet<string>(PathComparer);
        foreach (var root in roots)
        {
            try
            {
                if (File.Exists(root))
                {
                    if (root.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        AddReal(found, root);
                    }

                    continue;
                }

                if (!Directory.Exists(root))
                {
                    logger.LogWarning("DVD-Audio source path does not exist: {Path}", root);
                    continue;
                }

                foreach (var iso in Directory.EnumerateFiles(root, "*.iso", options))
                {
                    AddReal(found, iso);
                }

                foreach (var audioDirectory in Directory.EnumerateDirectories(root, "AUDIO_TS", options))
                {
                    var disc = Directory.GetParent(audioDirectory)?.FullName;
                    if (!string.IsNullOrEmpty(disc))
                    {
                        AddReal(found, disc);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not search {Path} for DVD-Audio discs", root);
            }
        }

        return found.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddRoots(List<string> roots, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var part in value.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            roots.Add(part);
        }
    }

    private static void AddReal(HashSet<string> found, string path)
    {
        try
        {
            FileSystemInfo? target = File.Exists(path)
                ? File.ResolveLinkTarget(path, returnFinalTarget: true)
                : Directory.Exists(path)
                    ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                    : null;
            found.Add(target?.FullName ?? Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found.Add(Path.GetFullPath(path));
        }
    }

    private static DiscNames NamesFor(string sourcePath, int groupNumber)
    {
        var raw = File.Exists(sourcePath)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : new DirectoryInfo(sourcePath).Name;
        raw = raw.Trim();
        var artist = "DVD-Audio";
        var album = raw.Length == 0 ? "DVD-Audio" : raw;
        var split = raw.IndexOf(" - ", StringComparison.Ordinal);
        if (split > 0 && split + 3 < raw.Length)
        {
            artist = raw[..split].Trim();
            album = raw[(split + 3)..].Trim();
        }

        if (groupNumber != 1)
        {
            album += " (Group " + groupNumber.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return new DiscNames(SanitizeFileName(artist), SanitizeFileName(album));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var text = builder.ToString().Trim().TrimEnd('.');
        if (text is "" or "." or "..")
        {
            return "DVD-Audio";
        }

        return text.Length <= 150 ? text : text[..150].TrimEnd();
    }

    private static Dictionary<string, string> Tags(DiscNames names, int sequence, int total, DvdAudioTrack track, string sourcePath)
    {
        var title = "Track " + sequence.ToString("00", CultureInfo.InvariantCulture);
        var comment = Describe(track) + " Imported from " + Path.GetFileName(sourcePath) + ".";
        return new Dictionary<string, string>
        {
            ["TITLE"] = Tag(title),
            ["ARTIST"] = Tag(names.Artist),
            ["ALBUM"] = Tag(names.Album),
            ["ALBUMARTIST"] = Tag(names.Artist),
            ["TRACKNUMBER"] = sequence.ToString(CultureInfo.InvariantCulture),
            ["TRACKTOTAL"] = total.ToString(CultureInfo.InvariantCulture),
            ["GENRE"] = "DVD-Audio",
            ["COMMENT"] = Tag(comment)
        };
    }

    private static string Describe(DvdAudioTrack track)
    {
        if (track.Coding is null)
        {
            return "DVD-Audio.";
        }

        var text = track.Coding;
        if (track.SampleRate is int rate)
        {
            text += " " + rate.ToString(CultureInfo.InvariantCulture) + " Hz";
        }

        if (track.BitsPerSample is int bits)
        {
            text += " " + bits.ToString(CultureInfo.InvariantCulture) + "-bit";
        }

        if (track.Channels is int channels)
        {
            text += " " + channels.ToString(CultureInfo.InvariantCulture) + " ch";
        }

        if (!string.IsNullOrWhiteSpace(track.ChannelLayout))
        {
            text += " " + track.ChannelLayout;
        }

        return text + ".";
    }

    private static string Tag(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character is '\r' or '\n' or '=' ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static bool WriteAlbumNfo(string musicDirectory, DiscNames names, string sourcePath)
    {
        var nfo = Path.Combine(musicDirectory, "album.nfo");
        if (File.Exists(nfo))
        {
            return false;
        }

        var year = YearPattern.Match(names.Album);
        var body = new StringBuilder();
        body.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
        body.AppendLine("<album>");
        body.AppendLine("  <title>" + Xml(names.Album) + "</title>");
        body.AppendLine("  <artist>" + Xml(names.Artist) + "</artist>");
        body.AppendLine("  <albumartist>" + Xml(names.Artist) + "</albumartist>");
        body.AppendLine("  <genre>DVD-Audio</genre>");
        if (year.Success)
        {
            body.AppendLine("  <year>" + year.Value + "</year>");
        }

        body.AppendLine("  <review>" + Xml("Imported from DVD-Audio disc " + Path.GetFileName(sourcePath) + ".") + "</review>");
        body.AppendLine("</album>");
        File.WriteAllText(nfo, body.ToString(), Utf8);
        return true;
    }

    private static string Xml(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }

    private static bool LibraryTrackReady(string libraryPath, string flacPath, AudioDelivery delivery)
    {
        if (!File.Exists(libraryPath) || new FileInfo(libraryPath).Length == 0)
        {
            return false;
        }

        if (delivery == AudioDelivery.ExtractFlac)
        {
            return true;
        }

        var linked = File.ReadAllText(libraryPath).Trim();
        return string.Equals(linked, flacPath, PathComparison) && File.Exists(flacPath) && new FileInfo(flacPath).Length > 0;
    }

    private static void RemoveTracks(string musicDirectory, int trackCount, string previousDelivery)
    {
        var extension = string.Equals(previousDelivery, nameof(AudioDelivery.CacheStrm), StringComparison.Ordinal) ? ".strm" : ".flac";
        for (var sequence = 1; sequence <= trackCount; sequence++)
        {
            var name = sequence.ToString("00", CultureInfo.InvariantCulture)
                + " - Track "
                + sequence.ToString("00", CultureInfo.InvariantCulture)
                + extension;
            var candidate = Path.Combine(musicDirectory, name);
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private static (long Length, long Ticks) Stamp(string path)
    {
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            return (info.Length, info.LastWriteTimeUtc.Ticks);
        }

        var audioDirectory = Path.Combine(path, "AUDIO_TS");
        var ifo = Path.Combine(audioDirectory, "AUDIO_TS.IFO");
        if (!File.Exists(ifo) && Directory.Exists(audioDirectory))
        {
            ifo = Directory.EnumerateFiles(audioDirectory, "ATS_*_0.IFO").FirstOrDefault() ?? string.Empty;
        }

        if (!File.Exists(ifo))
        {
            return (0, Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks : 0);
        }

        var file = new FileInfo(ifo);
        return (file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static string? CacheDirectory(string? dataPath, string sourcePath, int groupNumber)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            throw new DvdAudioException("Cache delivery needs the Jellyfin data directory, and that path is empty.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath))))[..16].ToLowerInvariant();
        return Path.Combine(dataPath, "dvd-audio", hash, "group" + groupNumber.ToString("00", CultureInfo.InvariantCulture));
    }

    private static string? MarkerPath(string? dataPath, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath))))[..16].ToLowerInvariant();
        return Path.Combine(dataPath, "dvd-audio", hash, "cppm.json");
    }

    private static bool MarkerMatches(string marker, string path, (long Length, long Ticks) stamp)
    {
        var manifest = ReadManifest(marker);
        return manifest is not null
            && string.Equals(manifest.SourcePath, path, PathComparison)
            && manifest.Length == stamp.Length
            && manifest.MtimeUtcTicks == stamp.Ticks;
    }

    private static AlbumManifest? ReadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AlbumManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly record struct DiscNames(string Artist, string Album);

    private sealed class AlbumManifest
    {
        public string SourcePath { get; set; } = string.Empty;

        public long Length { get; set; }

        public long MtimeUtcTicks { get; set; }

        public int Group { get; set; }

        public string Delivery { get; set; } = string.Empty;

        public int TrackCount { get; set; }
    }
}
