using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DvdAudio.Configuration;

/// <summary>
/// Where extracted audio is written.
/// </summary>
public enum AudioDelivery
{
    /// <summary>
    /// Write FLAC files directly into the music library.
    /// </summary>
    ExtractFlac = 0,

    /// <summary>
    /// Write FLAC files under the server data directory and put .strm shortcuts in the music library.
    /// </summary>
    CacheStrm = 1
}

/// <summary>
/// Saved dashboard settings for DVD-Audio import.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the music library root that receives albums.
    /// </summary>
    public string MusicLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the movie library root that receives hybrid video links.
    /// </summary>
    public string MovieLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets extra disc locations, separated by newlines or semicolons.
    /// </summary>
    public string SourcePaths { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how music files are published.
    /// </summary>
    public AudioDelivery AudioDelivery { get; set; } = AudioDelivery.ExtractFlac;

    /// <summary>
    /// Gets or sets a value indicating whether albums are written to the music library.
    /// </summary>
    public bool PublishMusic { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether hybrid video zones are linked into the movie library.
    /// </summary>
    public bool PublishMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether existing extracted tracks are written again.
    /// </summary>
    public bool RebuildExisting { get; set; }
}
