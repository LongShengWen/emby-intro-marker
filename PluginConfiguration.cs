using MediaBrowser.Model.Plugins;

namespace IntroMarkerPlugin;

public class PluginConfiguration : BasePluginConfiguration
{
    public const bool DefaultReplaceNativeIntroMarkers = false;
    public const bool DefaultReplaceExistingCreditsMarkers = true;
    public const bool DefaultEnableIntroDetection = true;
    public const bool DefaultEnableCreditsDetection = true;
    public const int DefaultMaxParallelTasks = 1;
    public const bool DefaultEnableLibraryScanOnImport = true;
    public const bool DefaultEnableCache = true;
    public const bool DefaultEnableTheIntroDb = true;
    public const double DefaultTheIntroDbMinConfidence = 0.80;
    public const int DefaultTheIntroDbMinSubmissions = 2;
    public const string DefaultDetectionStrategy = "Auto";
    public const int DefaultMaxSampleEpisodes = 6;
    public const int DefaultIntroAnalysisSeconds = 240;
    public const int DefaultCreditsAnalysisSeconds = 300;
    public const int DefaultMinIntroDurationSeconds = 15;
    public const int DefaultMaxIntroDurationSeconds = 150;
    public const int DefaultMinCreditsDurationSeconds = 20;
    public const int DefaultMaxCreditsDurationSeconds = 240;
    public const int DefaultSampleFps = 1;
    public const double DefaultMatchThreshold = 0.88;
    public const int DefaultMatchWindowSeconds = 12;
    public const int DefaultMinEpisodesPerSeason = 2;
    public const int DefaultImportDebounceSeconds = 90;

    public List<string> LibraryIds { get; set; } = new();
    public bool ReplaceNativeIntroMarkers { get; set; } = DefaultReplaceNativeIntroMarkers;
    public bool ReplaceExistingCreditsMarkers { get; set; } = DefaultReplaceExistingCreditsMarkers;
    public bool EnableIntroDetection { get; set; } = DefaultEnableIntroDetection;
    public bool EnableCreditsDetection { get; set; } = DefaultEnableCreditsDetection;
    public int MaxParallelTasks { get; set; } = DefaultMaxParallelTasks;
    public bool EnableLibraryScanOnImport { get; set; } = DefaultEnableLibraryScanOnImport;
    public bool EnableCache { get; set; } = DefaultEnableCache;
    public bool EnableTheIntroDb { get; set; } = DefaultEnableTheIntroDb;
    public string TheIntroDbApiKey { get; set; } = string.Empty;
    public double TheIntroDbMinConfidence { get; set; } = DefaultTheIntroDbMinConfidence;
    public int TheIntroDbMinSubmissions { get; set; } = DefaultTheIntroDbMinSubmissions;
    public string DetectionStrategy { get; set; } = DefaultDetectionStrategy;
    public int MaxSampleEpisodes { get; set; } = DefaultMaxSampleEpisodes;
    public int IntroAnalysisSeconds { get; set; } = DefaultIntroAnalysisSeconds;
    public int CreditsAnalysisSeconds { get; set; } = DefaultCreditsAnalysisSeconds;
    public int MinIntroDurationSeconds { get; set; } = DefaultMinIntroDurationSeconds;
    public int MaxIntroDurationSeconds { get; set; } = DefaultMaxIntroDurationSeconds;
    public int MinCreditsDurationSeconds { get; set; } = DefaultMinCreditsDurationSeconds;
    public int MaxCreditsDurationSeconds { get; set; } = DefaultMaxCreditsDurationSeconds;
    public int SampleFps { get; set; } = DefaultSampleFps;
    public double MatchThreshold { get; set; } = DefaultMatchThreshold;
    public int MatchWindowSeconds { get; set; } = DefaultMatchWindowSeconds;
    public int MinEpisodesPerSeason { get; set; } = DefaultMinEpisodesPerSeason;
    public int ImportDebounceSeconds { get; set; } = DefaultImportDebounceSeconds;

    public void EnsureDefaults()
    {
        LibraryIds ??= new List<string>();
        LibraryIds = LibraryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        TheIntroDbApiKey = (TheIntroDbApiKey ?? string.Empty).Trim();
        DetectionStrategy = NormalizeStrategy(DetectionStrategy);

        MaxParallelTasks = NormalizeInt(MaxParallelTasks, DefaultMaxParallelTasks, 1, 16);
        TheIntroDbMinConfidence = NormalizeDouble(TheIntroDbMinConfidence, DefaultTheIntroDbMinConfidence, 0.10, 1.0);
        TheIntroDbMinSubmissions = NormalizeInt(TheIntroDbMinSubmissions, DefaultTheIntroDbMinSubmissions, 1, 20);
        IntroAnalysisSeconds = NormalizeInt(IntroAnalysisSeconds, DefaultIntroAnalysisSeconds, 30, 600);
        CreditsAnalysisSeconds = NormalizeInt(CreditsAnalysisSeconds, DefaultCreditsAnalysisSeconds, 30, 900);
        MinIntroDurationSeconds = NormalizeInt(MinIntroDurationSeconds, DefaultMinIntroDurationSeconds, 5, 180);
        MaxIntroDurationSeconds = NormalizeInt(MaxIntroDurationSeconds, DefaultMaxIntroDurationSeconds, MinIntroDurationSeconds, 300);
        MinCreditsDurationSeconds = NormalizeInt(MinCreditsDurationSeconds, DefaultMinCreditsDurationSeconds, 5, 300);
        MaxCreditsDurationSeconds = NormalizeInt(MaxCreditsDurationSeconds, DefaultMaxCreditsDurationSeconds, MinCreditsDurationSeconds, 600);
        SampleFps = NormalizeInt(SampleFps, DefaultSampleFps, 1, 2);
        MatchThreshold = NormalizeDouble(MatchThreshold, DefaultMatchThreshold, 0.70, 0.99);
        MatchWindowSeconds = NormalizeInt(MatchWindowSeconds, DefaultMatchWindowSeconds, 6, 30);
        MinEpisodesPerSeason = NormalizeInt(MinEpisodesPerSeason, DefaultMinEpisodesPerSeason, 2, 10);
        MaxSampleEpisodes = NormalizeInt(MaxSampleEpisodes, DefaultMaxSampleEpisodes, MinEpisodesPerSeason, 12);
        ImportDebounceSeconds = NormalizeInt(ImportDebounceSeconds, DefaultImportDebounceSeconds, 10, 600);
    }

    private static int NormalizeInt(int value, int defaultValue, int min, int max)
    {
        var effectiveValue = value <= 0 ? defaultValue : value;
        return Math.Clamp(effectiveValue, min, max);
    }

    private static double NormalizeDouble(double value, double defaultValue, double min, double max)
    {
        var effectiveValue = double.IsFinite(value) && value > 0 ? value : defaultValue;
        return Math.Clamp(effectiveValue, min, max);
    }

    private static string NormalizeStrategy(string? value)
    {
        return value switch
        {
            "HashOnly" => "HashOnly",
            "AudioOnly" => "AudioOnly",
            _ => "Auto"
        };
    }
}
