using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvdAudio.ScheduledTasks;

/// <summary>
/// Imports DVD-Audio discs on a daily interval, then queues one library scan when files changed.
/// </summary>
public sealed class DvdAudioImportTask : IScheduledTask
{
    private readonly ILogger<DvdAudioImportTask> _logger;
    private readonly IServerApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="DvdAudioImportTask"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="paths">Server paths. Cached audio is stored under the data directory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaEncoder">Supplies the server ffmpeg binary.</param>
    public DvdAudioImportTask(
        ILogger<DvdAudioImportTask> logger,
        IServerApplicationPaths paths,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _paths = paths;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
    }

    /// <inheritdoc />
    public string Name => "Import DVD-Audio";

    /// <inheritdoc />
    public string Key => "DvdAudioImport";

    /// <inheritdoc />
    public string Description => "Extracts DVD-Audio tracks into the music library and links hybrid video zones into the movie library.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("The DVD-Audio plugin is not initialized.");
            progress.Report(100);
            return;
        }

        var changed = await DvdAudioImporter.RunAsync(
            plugin.Configuration,
            _paths.DataPath,
            string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath,
            _logger,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (changed)
        {
            _libraryManager.QueueLibraryScan();
        }
    }
}
