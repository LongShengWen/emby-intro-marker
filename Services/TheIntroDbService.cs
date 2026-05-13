using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using IntroMarkerPlugin.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace IntroMarkerPlugin.Services;

public sealed class TheIntroDbService
{
    private const string Endpoint = "https://api.theintrodb.org/v2/media";
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ConcurrentDictionary<string, ExternalMarkerResult?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _disabledUntil;

    public TheIntroDbService(ILogger logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public async Task<ExternalMarkerResult?> TryGetMarkersAsync(Episode episode, string? apiKey, double minConfidence, int minSubmissions, CancellationToken cancellationToken)
    {
        if (!episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue || episode.SeriesId <= 0)
        {
            _logger.Info($"跳过 TheIntroDB 查询：{episode.Name} 缺少季度/集号/SeriesId");
            return null;
        }

        if (_disabledUntil.HasValue && _disabledUntil.Value > DateTimeOffset.Now)
        {
            return null;
        }

        var series = _libraryManager.GetItemById(episode.SeriesId) as Series;
        if (series == null)
        {
            _logger.Info($"跳过 TheIntroDB 查询：未找到剧集所属剧集信息，Episode={episode.Name}");
            return null;
        }

        var tmdbIdRaw = series.GetProviderId(MetadataProviders.Tmdb);
        if (!int.TryParse(tmdbIdRaw, out var tmdbId) || tmdbId <= 0)
        {
            _logger.Info($"跳过 TheIntroDB 查询：{series.Name} 缺少有效 TMDB ID");
            return null;
        }

        var url = $"{Endpoint}?tmdb_id={tmdbId}&season={episode.ParentIndexNumber.Value}&episode={episode.IndexNumber.Value}";
        if (_cache.TryGetValue(url, out var cached))
        {
            _logger.Info(cached == null
                ? $"TheIntroDB 缓存未命中可用结果：{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                : $"TheIntroDB 缓存命中：{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}");
            return cached;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("IntroMarkerPlugin/1.0");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", apiKey.Trim());
        }

        try
        {
            _logger.Info($"开始查询 TheIntroDB：{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}，tmdb={tmdbId}");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 403)
                {
                    _disabledUntil = DateTimeOffset.Now.AddMinutes(30);
                    _logger.Info("TheIntroDB 当前拒绝服务器请求，30 分钟内暂停远程查询");
                }
                else
                {
                    _logger.Info($"TheIntroDB 查询失败：{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}，HTTP {(int)response.StatusCode}");
                }

                _cache[url] = null;
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<TheIntroDbResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (payload == null)
            {
                _cache[url] = null;
                return null;
            }

            var intro = SelectBestIntroSegment(payload.Intro, minConfidence, minSubmissions);
            var credits = SelectBestCreditsSegment(payload.Credits, episode.RunTimeTicks, minConfidence, minSubmissions);

            if (intro == null && credits == null)
            {
                _logger.Info($"TheIntroDB 未返回可用片头片尾：{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}，阈值={minConfidence:F2}，最少提交={minSubmissions}");
                _cache[url] = null;
                return null;
            }

            var result = new ExternalMarkerResult
            {
                IntroStartSeconds = intro?.StartMs / 1000d,
                IntroEndSeconds = intro?.EndMs / 1000d,
                CreditsStartSeconds = credits?.StartMs / 1000d
            };
            _logger.Info($"TheIntroDB 命中：{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}，片头={(result.IntroStartSeconds.HasValue ? $"{result.IntroStartSeconds.Value:F1}-{result.IntroEndSeconds!.Value:F1}s" : "无")}，片尾={(result.CreditsStartSeconds.HasValue ? $"{result.CreditsStartSeconds.Value:F1}s" : "无")}");
            _cache[url] = result;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"查询 TheIntroDB 失败：{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}", ex);
            _cache[url] = null;
            return null;
        }
    }

    private static SegmentPayload? SelectBestIntroSegment(List<SegmentPayload>? segments, double minConfidence, int minSubmissions)
    {
        return segments?
            .Where(s => s.StartMs.HasValue && s.EndMs.HasValue && s.EndMs > s.StartMs)
            .Where(s => s.Confidence >= minConfidence && s.SubmissionCount >= minSubmissions)
            .OrderByDescending(s => s.Confidence)
            .ThenByDescending(s => s.SubmissionCount)
            .FirstOrDefault();
    }

    private static SegmentPayload? SelectBestCreditsSegment(List<SegmentPayload>? segments, long? runtimeTicks, double minConfidence, int minSubmissions)
    {
        if (segments == null || segments.Count == 0)
        {
            return null;
        }

        var runtimeMs = runtimeTicks.HasValue ? runtimeTicks.Value / TimeSpan.TicksPerMillisecond : 0;
        var filtered = segments
            .Where(s => s.StartMs.HasValue)
            .Where(s => s.Confidence >= minConfidence && s.SubmissionCount >= minSubmissions)
            .ToList();

        var inBackHalf = filtered
            .Where(s => runtimeMs <= 0 || s.StartMs!.Value >= runtimeMs * 0.5)
            .OrderByDescending(s => s.Confidence)
            .ThenByDescending(s => s.SubmissionCount)
            .ThenByDescending(s => s.StartMs)
            .FirstOrDefault();

        return inBackHalf ??
            filtered
                .OrderByDescending(s => s.Confidence)
                .ThenByDescending(s => s.SubmissionCount)
                .ThenByDescending(s => s.StartMs)
                .FirstOrDefault();
    }

    private sealed class TheIntroDbResponse
    {
        [JsonPropertyName("tmdb_id")]
        public int TmdbId { get; set; }
        public string Type { get; set; } = string.Empty;
        public List<SegmentPayload>? Intro { get; set; }
        public List<SegmentPayload>? Credits { get; set; }
    }

    private sealed class SegmentPayload
    {
        [JsonPropertyName("start_ms")]
        public long? StartMs { get; set; }
        [JsonPropertyName("end_ms")]
        public long? EndMs { get; set; }
        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
        [JsonPropertyName("submission_count")]
        public int SubmissionCount { get; set; }
    }
}
