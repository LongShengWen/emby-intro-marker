using System.Collections.Concurrent;
using System.Timers;
using IntroMarkerPlugin.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace IntroMarkerPlugin.Services;

public sealed class ImportScanWatcher : IDisposable
{
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ScanCoordinator _scanCoordinator;
    private readonly Plugin _plugin;
    private readonly ConcurrentDictionary<SeasonKey, DateTimeOffset> _pending = new();
    private readonly System.Timers.Timer _timer;

    public ImportScanWatcher(ILogger logger, ILibraryManager libraryManager, ScanCoordinator scanCoordinator, Plugin plugin)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _scanCoordinator = scanCoordinator;
        _plugin = plugin;
        _timer = new System.Timers.Timer(30000);
        _timer.Elapsed += OnTimerElapsed;
    }

    public void Start()
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _timer.Start();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (!_plugin.Configuration.EnableLibraryScanOnImport)
        {
            return;
        }

        if (e.Item is not Episode episode || !episode.ParentIndexNumber.HasValue || episode.ParentIndexNumber <= 0 || episode.SeriesId <= 0)
        {
            return;
        }

        var key = new SeasonKey
        {
            SeriesInternalId = episode.SeriesId,
            SeriesName = episode.SeriesName ?? episode.Name,
            SeasonNumber = episode.ParentIndexNumber.Value
        };
        _pending[key] = DateTimeOffset.Now;
        _logger.Info($"已加入入库扫描队列：{key}");
    }

    private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            if (!_plugin.Configuration.EnableLibraryScanOnImport)
            {
                return;
            }

            var threshold = DateTimeOffset.Now.AddSeconds(-_plugin.Configuration.ImportDebounceSeconds);
            var due = _pending.Where(kv => kv.Value <= threshold).Select(kv => kv.Key).ToList();
            foreach (var key in due)
            {
                if (_pending.TryRemove(key, out _))
                {
                    await _scanCoordinator.ScanSeasonAsync(key, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.ErrorException("处理入库扫描队列失败", ex);
        }
    }

    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _timer.Stop();
        _timer.Dispose();
    }
}
