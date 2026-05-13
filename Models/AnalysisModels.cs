namespace IntroMarkerPlugin.Models;

public enum SegmentKind
{
    Intro,
    Credits
}

public sealed class EpisodeFeatureSequence
{
    public required long EpisodeInternalId { get; init; }
    public required string EpisodeName { get; init; }
    public required string EpisodePath { get; init; }
    public required long DurationTicks { get; init; }
    public required double OffsetSeconds { get; init; }
    public required List<ulong> FrameHashes { get; init; }
    public List<uint> AudioSignatures { get; set; } = new();
    public List<double> AudioLevels { get; set; } = new();
    public List<uint> SubtitleSignatures { get; set; } = new();
}

public sealed record EpisodeMarkerDecision
{
    public required long EpisodeInternalId { get; init; }
    public required string EpisodeName { get; init; }
    public required double StartSeconds { get; init; }
    public required double EndSeconds { get; init; }
    public required SegmentKind Kind { get; init; }
}

public sealed class CommonSegmentResult
{
    public required SegmentKind Kind { get; init; }
    public required double AverageScore { get; init; }
    public required int DurationFrames { get; init; }
    public required string DetectionMethod { get; init; }
    public required int MatchedEpisodes { get; init; }
    public required int RequiredEpisodes { get; init; }
    public required double PositionSpreadSeconds { get; init; }
    public required List<EpisodeMarkerDecision> Decisions { get; init; }
}

public sealed class ExternalMarkerResult
{
    public double? IntroStartSeconds { get; init; }
    public double? IntroEndSeconds { get; init; }
    public double? CreditsStartSeconds { get; init; }
    public string Source { get; init; } = "TheIntroDB";
}

public sealed class SeasonKey : IEquatable<SeasonKey>
{
    public required long SeriesInternalId { get; init; }
    public required string SeriesName { get; init; }
    public required int SeasonNumber { get; init; }

    public bool Equals(SeasonKey? other)
    {
        if (other is null) return false;
        return SeriesInternalId == other.SeriesInternalId && SeasonNumber == other.SeasonNumber;
    }

    public override bool Equals(object? obj) => Equals(obj as SeasonKey);
    public override int GetHashCode() => HashCode.Combine(SeriesInternalId, SeasonNumber);
    public override string ToString() => $"{SeriesName} S{SeasonNumber:00}";
}

public sealed class CacheEnvelope
{
    public int Version { get; set; } = 1;
    public List<CachedEpisodeAnalysis> Episodes { get; set; } = new();
}

public sealed class CachedEpisodeAnalysis
{
    public required string CacheKey { get; init; }
    public required string FilePath { get; init; }
    public required long FileSize { get; init; }
    public required long LastWriteUtcTicks { get; init; }
    public required long DurationTicks { get; init; }
    public required int SampleFps { get; init; }
    public required int AnalysisSeconds { get; init; }
    public required string AlgorithmVersion { get; init; }
    public required SegmentKind Kind { get; init; }
    public required List<string> FrameHashes { get; init; }
    public List<string> AudioSignatures { get; init; } = new();
    public List<double> AudioLevels { get; init; } = new();
    public List<string> SubtitleSignatures { get; init; } = new();
}
