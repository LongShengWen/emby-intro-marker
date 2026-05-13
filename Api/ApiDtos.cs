using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace IntroMarkerPlugin.Api;

[Authenticated]
[Route("/IntroMarker/ScanNow", "POST", Summary = "立即执行片头片尾扫描")]
public sealed class ScanNowRequest : IReturn<object>
{
}

[Authenticated]
[Route("/IntroMarker/Status", "GET", Summary = "获取片头片尾扫描状态")]
public sealed class StatusRequest : IReturn<object>
{
}

[Authenticated]
[Route("/IntroMarker/ScanSeason", "POST", Summary = "扫描指定季度")]
public sealed class ScanSeasonRequest : IReturn<object>
{
    public string SeriesId { get; set; } = string.Empty;
    public int SeasonNumber { get; set; }
}

[Authenticated]
[Route("/IntroMarker/ClearCache", "POST", Summary = "清空片头片尾缓存")]
public sealed class ClearCacheRequest : IReturn<object>
{
}
