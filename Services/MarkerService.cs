using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace IntroMarkerPlugin.Services;

public sealed class MarkerService
{
    private static readonly PropertyInfo? MarkerTypeProperty = typeof(ChapterInfo).GetProperty("MarkerType");
    private readonly IItemRepository _itemRepository;
    private readonly ILogger _logger;

    public MarkerService(IItemRepository itemRepository, ILogger logger)
    {
        _itemRepository = itemRepository;
        _logger = logger;
    }

    public void ApplyIntroMarkers(Episode episode, double startSeconds, double endSeconds, bool replaceExisting)
    {
        var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
        var existingIntroMarkers = chapters.Where(c => IsMarker(c, "IntroStart") || IsMarker(c, "IntroEnd")).ToList();

        if (!replaceExisting && existingIntroMarkers.Count > 0)
        {
            _logger.Info($"跳过 {episode.Name} 的片头写入：已存在原生片头标记");
            return;
        }

        foreach (var marker in existingIntroMarkers)
        {
            chapters.Remove(marker);
        }

        var introStart = new ChapterInfo
        {
            Name = "Intro Start",
            StartPositionTicks = SecondsToTicks(startSeconds)
        };
        var introEnd = new ChapterInfo
        {
            Name = "Intro End",
            StartPositionTicks = SecondsToTicks(endSeconds)
        };

        SetMarkerType(introStart, MarkerType.IntroStart);
        SetMarkerType(introEnd, MarkerType.IntroEnd);

        chapters.Add(introStart);
        chapters.Add(introEnd);
        chapters = chapters.OrderBy(c => c.StartPositionTicks).ToList();
        _itemRepository.SaveChapters(episode.InternalId, chapters);
    }

    public void ApplyCreditsMarker(Episode episode, double startSeconds, bool replaceExisting)
    {
        var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
        var existingCreditsMarkers = chapters.Where(c => IsMarker(c, "CreditsStart") || IsMarker(c, "Credits")).ToList();

        if (!replaceExisting && existingCreditsMarkers.Count > 0)
        {
            _logger.Info($"跳过 {episode.Name} 的片尾写入：已存在片尾标记");
            return;
        }

        foreach (var marker in existingCreditsMarkers)
        {
            chapters.Remove(marker);
        }

        var creditsStart = new ChapterInfo
        {
            Name = "Credits",
            StartPositionTicks = SecondsToTicks(startSeconds)
        };
        SetMarkerType(creditsStart, MarkerType.CreditsStart);
        chapters.Add(creditsStart);
        chapters = chapters.OrderBy(c => c.StartPositionTicks).ToList();
        _itemRepository.SaveChapters(episode.InternalId, chapters);
    }

    public (double StartSeconds, double EndSeconds)? GetIntroWindow(Episode episode)
    {
        var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
        var introStart = chapters
            .Where(c => IsMarker(c, "IntroStart"))
            .OrderBy(c => c.StartPositionTicks)
            .FirstOrDefault();
        var introEnd = chapters
            .Where(c => IsMarker(c, "IntroEnd"))
            .OrderBy(c => c.StartPositionTicks)
            .FirstOrDefault();

        if (introStart == null || introEnd == null)
        {
            return null;
        }

        var startSeconds = introStart.StartPositionTicks / (double)TimeSpan.TicksPerSecond;
        var endSeconds = introEnd.StartPositionTicks / (double)TimeSpan.TicksPerSecond;
        return endSeconds > startSeconds ? (startSeconds, endSeconds) : null;
    }

    public double? GetCreditsStart(Episode episode)
    {
        var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
        var credits = chapters
            .Where(c => IsMarker(c, "CreditsStart") || IsMarker(c, "Credits"))
            .OrderBy(c => c.StartPositionTicks)
            .FirstOrDefault();

        return credits == null
            ? null
            : credits.StartPositionTicks / (double)TimeSpan.TicksPerSecond;
    }

    private static long SecondsToTicks(double seconds) => (long)(Math.Max(0, seconds) * TimeSpan.TicksPerSecond);

    private static bool IsMarker(ChapterInfo chapter, string markerType)
    {
        return string.Equals(GetMarkerType(chapter), markerType, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetMarkerType(ChapterInfo chapter)
    {
        try
        {
            return MarkerTypeProperty?.GetValue(chapter)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static void SetMarkerType(ChapterInfo chapter, MarkerType markerType)
    {
        if (MarkerTypeProperty?.CanWrite == true)
        {
            MarkerTypeProperty.SetValue(chapter, markerType);
        }
    }
}
