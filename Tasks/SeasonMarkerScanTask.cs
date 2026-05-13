using MediaBrowser.Model.Tasks;

namespace IntroMarkerPlugin.Tasks;

public sealed class SeasonMarkerScanTask : IScheduledTask
{
    public string Key => "IntroMarkerSeasonScan";
    public string Name => "片头片尾识别扫描";
    public string Description => "扫描所选媒体库，识别季度共用的片头片尾并写入 Emby 标记。";
    public string Category => "插件";

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        progress.Report(1);
        if (Plugin.Coordinator == null)
        {
            return;
        }

        await Plugin.Coordinator.ScanConfiguredLibrariesAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = "WeeklyTrigger",
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
