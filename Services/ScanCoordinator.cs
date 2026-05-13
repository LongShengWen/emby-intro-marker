using System.Collections.Concurrent;
using IntroMarkerPlugin.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace IntroMarkerPlugin.Services;

public sealed class ScanCoordinator
{
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly SeasonDetectionService _seasonDetectionService;
    private readonly Plugin _plugin;
    private readonly RuntimeState _runtimeState;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScanCoordinator(ILogger logger, ILibraryManager libraryManager, SeasonDetectionService seasonDetectionService, Plugin plugin, RuntimeState runtimeState)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _seasonDetectionService = seasonDetectionService;
        _plugin = plugin;
        _runtimeState = runtimeState;
    }

    public async Task<object> ScanConfiguredLibrariesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var libraries = LoadTargetLibraries(_plugin.Configuration.LibraryIds);
            _logger.Info($"开始片头片尾扫描：媒体库={FormatLibraryNames(libraries)}，并发={_plugin.Configuration.MaxParallelTasks}，策略={_plugin.Configuration.DetectionStrategy}，采样上限={_plugin.Configuration.MaxSampleEpisodes}，TheIntroDB={_plugin.Configuration.EnableTheIntroDb}");

            var seasons = LoadConfiguredSeasons();
            _logger.Info($"本次共加载 {seasons.Count} 个季度");
            _runtimeState.Start(seasons.Count, $"准备扫描 {seasons.Count} 个季度");

            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _plugin.Configuration.MaxParallelTasks
            };

            var processed = 0;
            await Parallel.ForEachAsync(seasons, parallelOptions, async (season, ct) =>
            {
                var episodes = season.Value;
                try
                {
                    _logger.Info($"开始处理季度 {season.Key}，共 {episodes.Count} 集");
                    _runtimeState.SetStage($"处理中 {season.Key}");
                    var changed = await _seasonDetectionService.ProcessSeasonAsync(episodes, ct).ConfigureAwait(false);
                    Interlocked.Add(ref processed, changed);
                    _logger.Info($"季度 {season.Key} 处理完成，共写入/更新 {changed} 个标记");
                    _runtimeState.ReportSeason($"已处理 {season.Key}", episodes.Count);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"扫描 {season.Key} 失败", ex);
                    _runtimeState.ReportSeason($"{season.Key} 处理失败：{ex.Message}", episodes.Count);
                }
            }).ConfigureAwait(false);

            Plugin.CacheService?.Save();
            _runtimeState.Finish($"扫描完成，共写入/更新 {processed} 个标记");
            return new { Success = true, Message = $"扫描完成，共写入/更新 {processed} 个标记", Seasons = seasons.Count };
        }
        catch (OperationCanceledException)
        {
            _runtimeState.Finish("扫描已取消");
            return new { Success = false, Message = "扫描已取消" };
        }
        catch (Exception ex)
        {
            _runtimeState.Finish($"扫描失败：{ex.Message}");
            _logger.ErrorException("执行片头片尾扫描失败", ex);
            return new { Success = false, Message = ex.Message };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> ScanSeasonAsync(SeasonKey seasonKey, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var seasons = LoadConfiguredSeasons();
            if (!seasons.TryGetValue(seasonKey, out var episodes))
            {
                return new { Success = false, Message = $"未找到季度 {seasonKey}" };
            }

            _runtimeState.Start(1, $"准备处理 {seasonKey}");
            _logger.Info($"开始单季度扫描：{seasonKey}，共 {episodes.Count} 集，策略={_plugin.Configuration.DetectionStrategy}，采样上限={_plugin.Configuration.MaxSampleEpisodes}");
            var processed = await _seasonDetectionService.ProcessSeasonAsync(episodes, cancellationToken).ConfigureAwait(false);
            Plugin.CacheService?.Save();
            _runtimeState.Finish($"{seasonKey} 处理完成，共写入/更新 {processed} 个标记");
            _logger.Info($"单季度扫描完成：{seasonKey}，共写入/更新 {processed} 个标记");
            return new { Success = true, Message = $"{seasonKey} 处理完成，共写入/更新 {processed} 个标记" };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> ScanSeasonBySeriesIdAsync(string rawSeriesId, int seasonNumber, CancellationToken cancellationToken)
    {
        var seasons = LoadConfiguredSeasons();
        long? targetInternalId = null;

        if (long.TryParse(rawSeriesId, out var internalId))
        {
            targetInternalId = internalId;
        }
        else if (Guid.TryParse(rawSeriesId, out var guid))
        {
            var item = _libraryManager.GetItemById(guid);
            if (item != null)
            {
                targetInternalId = item.InternalId;
            }
        }

        if (!targetInternalId.HasValue)
        {
            return new { Success = false, Message = $"无法解析 SeriesId: {rawSeriesId}" };
        }

        var seasonKey = seasons.Keys.FirstOrDefault(k => k.SeriesInternalId == targetInternalId.Value && k.SeasonNumber == seasonNumber);
        if (seasonKey == null)
        {
            return new { Success = false, Message = $"未找到季度：SeriesId={rawSeriesId}, Season={seasonNumber}" };
        }

        return await ScanSeasonAsync(seasonKey, cancellationToken).ConfigureAwait(false);
    }

    public Dictionary<SeasonKey, List<Episode>> LoadConfiguredSeasons()
    {
        var libraryIds = _plugin.Configuration.LibraryIds;
        var libraries = LoadTargetLibraries(libraryIds);
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { "Episode" },
            IsVirtualItem = false,
            HasPath = true,
            Recursive = true
        };

        var episodes = libraries.SelectMany(library =>
        {
            query.AncestorIds = new[] { library.InternalId };
            return _libraryManager.GetItemList(query).OfType<Episode>();
        }).Where(e => e.ParentIndexNumber.HasValue && e.ParentIndexNumber.Value > 0 && e.RunTimeTicks.HasValue && e.RunTimeTicks.Value > 0)
        .GroupBy(e => new SeasonKey
        {
            SeriesInternalId = e.SeriesId,
            SeriesName = e.SeriesName ?? e.Name,
            SeasonNumber = e.ParentIndexNumber ?? 0
        })
        .ToDictionary(g => g.Key, g => g.OrderBy(e => e.IndexNumber ?? 0).ToList());

        return episodes;
    }

    private List<Folder> LoadTargetLibraries(List<string> configuredIds)
    {
        if (configuredIds.Count == 0)
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "CollectionFolder" }
            }).Where(IsTvLibrary).OfType<Folder>().ToList();
        }

        var result = new List<Folder>();
        foreach (var rawId in configuredIds)
        {
            BaseItem? library = null;
            if (Guid.TryParse(rawId, out var guid))
            {
                library = _libraryManager.GetItemById(guid);
            }
            else if (long.TryParse(rawId, out var internalId))
            {
                library = _libraryManager.GetItemById(internalId);
            }

            if (library is Folder folder && IsTvLibrary(folder))
            {
                result.Add(folder);
            }
        }
        return result;
    }

    private static string FormatLibraryNames(IReadOnlyList<Folder> libraries)
    {
        if (libraries.Count == 0)
        {
            return "无";
        }

        return string.Join("、", libraries.Select(l => l.Name));
    }

    private static bool IsTvLibrary(BaseItem item)
    {
        var collectionType = item.GetType().GetProperty("CollectionType")?.GetValue(item) as string;
        return collectionType == "tvshows" || collectionType == "mixed" || string.IsNullOrEmpty(collectionType);
    }
}
