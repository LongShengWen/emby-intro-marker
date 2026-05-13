using System.Text.Json;
using IntroMarkerPlugin.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;

namespace IntroMarkerPlugin.Services;

public sealed class DetectionCacheService
{
    private const string AlgorithmVersion = "multimodal-cpu-v4";
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger _logger;
    private readonly Plugin _plugin;
    private readonly Dictionary<string, CachedEpisodeAnalysis> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public DetectionCacheService(IApplicationPaths applicationPaths, ILogger logger, Plugin plugin)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        _plugin = plugin;
    }

    private string CachePath => Path.Combine(_applicationPaths.PluginConfigurationsPath, "IntroMarker.cache.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            var envelope = JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(CachePath));
            if (envelope?.Episodes == null)
            {
                return;
            }

            lock (_sync)
            {
                _items.Clear();
                foreach (var item in envelope.Episodes)
                {
                    _items[item.CacheKey] = item;
                }
            }

            _logger.Info($"片头片尾识别缓存已加载，共 {_items.Count} 条");
        }
        catch (Exception ex)
        {
            _logger.ErrorException("加载片头片尾识别缓存失败", ex);
        }
    }

    public void Save()
    {
        try
        {
            CacheEnvelope envelope;
            lock (_sync)
            {
                envelope = new CacheEnvelope
                {
                    Episodes = _items.Values.OrderBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase).ToList()
                };
            }

            var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            _logger.ErrorException("保存片头片尾识别缓存失败", ex);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
        }
        Save();
    }

    public CachedEpisodeAnalysis? TryGet(FileInfo fileInfo, long durationTicks, int sampleFps, int analysisSeconds, SegmentKind kind)
    {
        if (!_plugin.Configuration.EnableCache)
        {
            return null;
        }

        var key = BuildKey(fileInfo.FullName, kind);
        lock (_sync)
        {
            if (!_items.TryGetValue(key, out var cached))
            {
                return null;
            }

            if (cached.FileSize != fileInfo.Length ||
                cached.LastWriteUtcTicks != fileInfo.LastWriteTimeUtc.Ticks ||
                cached.DurationTicks != durationTicks ||
                cached.SampleFps != sampleFps ||
                cached.AnalysisSeconds != analysisSeconds ||
                !string.Equals(cached.AlgorithmVersion, AlgorithmVersion, StringComparison.Ordinal))
            {
                _items.Remove(key);
                return null;
            }

            return cached;
        }
    }

    public void Set(
        FileInfo fileInfo,
        long durationTicks,
        int sampleFps,
        int analysisSeconds,
        SegmentKind kind,
        IReadOnlyList<ulong> hashes,
        IReadOnlyList<uint> audioSignatures,
        IReadOnlyList<double> audioLevels,
        IReadOnlyList<uint> subtitleSignatures)
    {
        if (!_plugin.Configuration.EnableCache)
        {
            return;
        }

        var key = BuildKey(fileInfo.FullName, kind);
        List<string> mergedFrameHashes;
        List<string> mergedAudioSignatures;
        List<double> mergedAudioLevels;
        List<string> mergedSubtitleSignatures;

        lock (_sync)
        {
            _items.TryGetValue(key, out var existing);

            mergedFrameHashes = hashes.Count > 0
                ? hashes.Select(h => h.ToString("X16")).ToList()
                : existing?.FrameHashes?.ToList() ?? new List<string>();

            mergedAudioSignatures = audioSignatures.Count > 0
                ? audioSignatures.Select(h => h.ToString("X8")).ToList()
                : existing?.AudioSignatures?.ToList() ?? new List<string>();

            mergedAudioLevels = audioLevels.Count > 0
                ? audioLevels.ToList()
                : existing?.AudioLevels?.ToList() ?? new List<double>();

            mergedSubtitleSignatures = subtitleSignatures.Count > 0
                ? subtitleSignatures.Select(h => h.ToString("X8")).ToList()
                : existing?.SubtitleSignatures?.ToList() ?? new List<string>();
        }

        var item = new CachedEpisodeAnalysis
        {
            CacheKey = key,
            FilePath = fileInfo.FullName,
            FileSize = fileInfo.Length,
            LastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks,
            DurationTicks = durationTicks,
            SampleFps = sampleFps,
            AnalysisSeconds = analysisSeconds,
            AlgorithmVersion = AlgorithmVersion,
            Kind = kind,
            FrameHashes = mergedFrameHashes,
            AudioSignatures = mergedAudioSignatures,
            AudioLevels = mergedAudioLevels,
            SubtitleSignatures = mergedSubtitleSignatures
        };

        lock (_sync)
        {
            _items[key] = item;
        }
    }

    public static string BuildKey(string path, SegmentKind kind) => $"{kind}:{path}";
    public static IReadOnlyList<ulong> ParseHashes(CachedEpisodeAnalysis cached) => cached.FrameHashes.Select(h => Convert.ToUInt64(h, 16)).ToList();
    public static IReadOnlyList<uint> ParseAudioSignatures(CachedEpisodeAnalysis cached) => (cached.AudioSignatures ?? []).Select(h => Convert.ToUInt32(h, 16)).ToList();
    public static IReadOnlyList<double> ParseAudioLevels(CachedEpisodeAnalysis cached) => cached.AudioLevels ?? [];
    public static IReadOnlyList<uint> ParseSubtitleSignatures(CachedEpisodeAnalysis cached) => (cached.SubtitleSignatures ?? []).Select(h => Convert.ToUInt32(h, 16)).ToList();
}
