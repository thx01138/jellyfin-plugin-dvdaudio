using DvdAudio.Core;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvdAudio.Resolvers;

/// <summary>
/// Keeps DVD-Audio images from becoming broken video items.
/// Hybrid discs stay visible in movie and mixed libraries. Audio-only images are ignored.
/// Every DVD-Audio image in a music library is ignored; the import task publishes the audio.
/// </summary>
public sealed class DvdAudioIgnoreRule : IResolverIgnoreRule
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DvdAudioIgnoreRule> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DvdAudioIgnoreRule"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager used to find the owning collection.</param>
    /// <param name="logger">Logger.</param>
    public DvdAudioIgnoreRule(ILibraryManager libraryManager, ILogger<DvdAudioIgnoreRule> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem? parent)
    {
        if (fileInfo.IsDirectory || !IsIso(fileInfo))
        {
            return false;
        }

        var path = fileInfo.FullName;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        DiscProbe probe;
        try
        {
            probe = DvdAudioDisc.Probe(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DVD-Audio probe failed for {Path}", path);
            return false;
        }

        if (!probe.IsDvdAudio)
        {
            return false;
        }

        if (BelongsToMusicLibrary(parent, path))
        {
            return true;
        }

        return !probe.HasVideoZone;
    }

    private bool BelongsToMusicLibrary(BaseItem? parent, string path)
    {
        try
        {
            if (parent is not null)
            {
                foreach (var folder in _libraryManager.GetCollectionFolders(parent))
                {
                    if (folder is CollectionFolder collection && collection.CollectionType == CollectionType.music)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the library for {Path}", path);
        }

        var configured = Plugin.Instance?.Configuration.MusicLibraryPath;
        return DvdAudioPaths.IsUnder(path, configured);
    }

    private static bool IsIso(FileSystemMetadata fileInfo)
    {
        var extension = fileInfo.Extension;
        if (string.IsNullOrEmpty(extension))
        {
            extension = Path.GetExtension(fileInfo.Name ?? string.Empty);
        }

        return extension.Equals(".iso", StringComparison.OrdinalIgnoreCase)
            || extension.Equals("iso", StringComparison.OrdinalIgnoreCase);
    }
}
