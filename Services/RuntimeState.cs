namespace IntroMarkerPlugin.Services;

public sealed class RuntimeState
{
    private readonly object _sync = new();

    public bool IsRunning { get; private set; }
    public string CurrentStage { get; private set; } = "空闲";
    public int SeasonsTotal { get; private set; }
    public int SeasonsCompleted { get; private set; }
    public int EpisodesScanned { get; private set; }
    public string LastMessage { get; private set; } = "尚未开始";
    public DateTimeOffset? LastRunAt { get; private set; }

    public void Start(int seasonsTotal, string message)
    {
        lock (_sync)
        {
            IsRunning = true;
            SeasonsTotal = seasonsTotal;
            SeasonsCompleted = 0;
            EpisodesScanned = 0;
            CurrentStage = "扫描中";
            LastMessage = message;
            LastRunAt = DateTimeOffset.Now;
        }
    }

    public void ReportSeason(string message, int episodesScannedDelta)
    {
        lock (_sync)
        {
            SeasonsCompleted++;
            EpisodesScanned += episodesScannedDelta;
            LastMessage = message;
        }
    }

    public void SetStage(string message)
    {
        lock (_sync)
        {
            LastMessage = message;
            CurrentStage = message;
        }
    }

    public void Finish(string message)
    {
        lock (_sync)
        {
            IsRunning = false;
            CurrentStage = "空闲";
            LastMessage = message;
            LastRunAt = DateTimeOffset.Now;
        }
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            return new
            {
                IsRunning,
                CurrentStage,
                SeasonsTotal,
                SeasonsCompleted,
                EpisodesScanned,
                LastMessage,
                LastRunAt = LastRunAt?.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }
    }
}
