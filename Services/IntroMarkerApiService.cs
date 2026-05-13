using IntroMarkerPlugin.Api;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace IntroMarkerPlugin.Services;

public sealed class IntroMarkerApiService : IService
{
    private readonly ILogger _logger;

    public IntroMarkerApiService(ILogManager logManager)
    {
        _logger = logManager.GetLogger(GetType().Name);
    }

    public async Task<object> Post(ScanNowRequest request)
    {
        if (Plugin.Coordinator == null)
        {
            return new { Success = false, Message = "插件尚未初始化" };
        }

        _logger.Info("收到手动片头片尾扫描请求");
        return await Plugin.Coordinator.ScanConfiguredLibrariesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public object Get(StatusRequest request)
    {
        return Plugin.Runtime?.Snapshot() ?? new { IsRunning = false, CurrentStage = "未初始化" };
    }

    public async Task<object> Post(ScanSeasonRequest request)
    {
        if (Plugin.Coordinator == null)
        {
            return new { Success = false, Message = "插件尚未初始化" };
        }

        if (string.IsNullOrWhiteSpace(request.SeriesId) || request.SeasonNumber <= 0)
        {
            return new { Success = false, Message = "SeriesId 和 SeasonNumber 必填" };
        }

        _logger.Info($"收到季度扫描请求：SeriesId={request.SeriesId}, Season={request.SeasonNumber}");
        return await Plugin.Coordinator.ScanSeasonBySeriesIdAsync(request.SeriesId, request.SeasonNumber, CancellationToken.None).ConfigureAwait(false);
    }

    public object Post(ClearCacheRequest request)
    {
        _logger.Info("收到清空片头片尾缓存请求");
        Plugin.CacheService?.Clear();
        return new { Success = true, Message = "缓存已清空" };
    }
}
