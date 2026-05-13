using IntroMarkerPlugin.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System.Numerics;

namespace IntroMarkerPlugin.Services;

public sealed class SeasonDetectionService
{
    private const double AudioMatchThreshold = 0.72;
    private const double AudioExtendThreshold = 0.66;
    private const double MinimumSampleSupportRatio = 0.75;
    private const double MaxIntroPositionSpreadSeconds = 30;
    private const double MaxCreditsPositionSpreadSeconds = 45;
    private const double SilenceThreshold = 0.015;
    private const int HardMinimumIntroSeconds = 15;
    private const int HardMinimumCreditsSeconds = 15;
    private readonly ILogger _logger;
    private readonly FrameHashExtractor _extractor;
    private readonly MarkerService _markerService;
    private readonly TheIntroDbService _theIntroDbService;
    private readonly Plugin _plugin;
    private readonly RuntimeState _runtimeState;

    public SeasonDetectionService(ILogger logger, FrameHashExtractor extractor, MarkerService markerService, TheIntroDbService theIntroDbService, Plugin plugin, RuntimeState runtimeState)
    {
        _logger = logger;
        _extractor = extractor;
        _markerService = markerService;
        _theIntroDbService = theIntroDbService;
        _plugin = plugin;
        _runtimeState = runtimeState;
    }

    public async Task<int> ProcessSeasonAsync(IReadOnlyList<Episode> episodes, CancellationToken cancellationToken)
    {
        if (episodes.Count < _plugin.Configuration.MinEpisodesPerSeason)
        {
            if (episodes.Count > 0)
            {
                _logger.Info($"跳过季度 {episodes[0].SeriesName} S{episodes[0].ParentIndexNumber:00}：集数 {episodes.Count} 小于最少样本集数 {_plugin.Configuration.MinEpisodesPerSeason}");
            }
            return 0;
        }

        _logger.Info($"季度 {episodes[0].SeriesName} S{episodes[0].ParentIndexNumber:00} 开始识别：总集数={episodes.Count}，策略={_plugin.Configuration.DetectionStrategy}，采样上限={_plugin.Configuration.MaxSampleEpisodes}");

        var introHandled = new HashSet<long>();
        var creditsHandled = new HashSet<long>();
        var nativeProcessed = ApplyNativeMarkers(episodes, introHandled, creditsHandled);
        var externalProcessed = await ApplyExternalMarkersAsync(episodes, introHandled, creditsHandled, cancellationToken).ConfigureAwait(false);
        _logger.Info($"季度 {episodes[0].SeriesName} S{episodes[0].ParentIndexNumber:00} 已有结果：原生命中={nativeProcessed}，远端后片头命中={introHandled.Count}，片尾命中={creditsHandled.Count}");

        return nativeProcessed + externalProcessed + await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processed = 0;

            if (_plugin.Configuration.EnableIntroDetection)
            {
                var introEpisodes = episodes.Where(e => !introHandled.Contains(e.InternalId)).ToList();
                processed += ProcessLocalDetections(introEpisodes, SegmentKind.Intro, cancellationToken);
            }

            if (_plugin.Configuration.EnableCreditsDetection)
            {
                var creditsEpisodes = episodes.Where(e => !creditsHandled.Contains(e.InternalId)).ToList();
                processed += ProcessLocalDetections(creditsEpisodes, SegmentKind.Credits, cancellationToken);
            }

            return processed;
        }, cancellationToken).ConfigureAwait(false);
    }

    private int ApplyNativeMarkers(
        IReadOnlyList<Episode> episodes,
        HashSet<long> introHandled,
        HashSet<long> creditsHandled)
    {
        var processed = 0;

        foreach (var episode in episodes)
        {
            if (_plugin.Configuration.EnableIntroDetection)
            {
                var intro = _markerService.GetIntroWindow(episode);
                if (intro.HasValue)
                {
                    introHandled.Add(episode.InternalId);
                    processed++;
                }
            }

            if (_plugin.Configuration.EnableCreditsDetection)
            {
                var credits = _markerService.GetCreditsStart(episode);
                if (credits.HasValue)
                {
                    creditsHandled.Add(episode.InternalId);
                    processed++;
                }
            }
        }

        if (processed > 0 && episodes.Count > 0)
        {
            _logger.Info($"季度 {episodes[0].SeriesName} S{episodes[0].ParentIndexNumber:00} 复用 Emby 原生结果 {processed} 个标记");
        }

        return processed;
    }

    private int ProcessLocalDetections(IReadOnlyList<Episode> episodes, SegmentKind kind, CancellationToken cancellationToken)
    {
        if (episodes.Count < _plugin.Configuration.MinEpisodesPerSeason)
        {
            if (episodes.Count > 0)
            {
                _logger.Info($"{FormatSeason(episodes)} 跳过本地{GetKindName(kind)}识别：剩余集数 {episodes.Count} 小于最少样本集数 {_plugin.Configuration.MinEpisodesPerSeason}");
            }
            return 0;
        }

        var sampleEpisodes = SelectSampleEpisodes(episodes);
        if (sampleEpisodes.Count < _plugin.Configuration.MinEpisodesPerSeason)
        {
            _logger.Info($"{FormatSeason(episodes)} 跳过本地{GetKindName(kind)}识别：有效采样集数不足");
            return 0;
        }

        _logger.Info($"{FormatSeason(episodes)} 开始本地{GetKindName(kind)}识别：策略={_plugin.Configuration.DetectionStrategy}，采样 {sampleEpisodes.Count}/{episodes.Count} 集，样本={string.Join(", ", sampleEpisodes.Select(FormatEpisode))}，通过要求={GetRequiredMatchCount(sampleEpisodes.Count)}/{sampleEpisodes.Count}");

        var result = DetectCommonSegment(sampleEpisodes, kind, cancellationToken);
        if (result == null)
        {
            _logger.Info($"{FormatSeason(episodes)} 本地{GetKindName(kind)}未识别到有效公共片段");
            return 0;
        }

        var decisionMap = result.Decisions.ToDictionary(d => d.EpisodeInternalId);
        var representative = BuildRepresentativeWindow(episodes, result);
        if (representative == null)
        {
            return 0;
        }

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (decisionMap.TryGetValue(episode.InternalId, out var decision))
            {
                ApplyMarker(episode, kind, decision.StartSeconds, decision.EndSeconds);
                continue;
            }

            var window = representative.GetWindow(episode);
            ApplyMarker(episode, kind, window.StartSeconds, window.EndSeconds);
        }

        var segmentName = kind == SegmentKind.Intro ? "片头" : "片尾";
        _logger.Info($"季 {episodes[0].SeriesName} S{episodes[0].ParentIndexNumber:00} 识别到{segmentName}，方式={result.DetectionMethod}，采样 {sampleEpisodes.Count}/{episodes.Count} 集，命中样本={result.MatchedEpisodes}/{sampleEpisodes.Count}，平均得分 {result.AverageScore:F3}，位置离散={result.PositionSpreadSeconds:F1}s，代表区间={FormatRepresentativeWindow(result.Kind, episodes, representative)}");
        return episodes.Count;
    }

    private async Task<int> ApplyExternalMarkersAsync(
        IReadOnlyList<Episode> episodes,
        HashSet<long> introHandled,
        HashSet<long> creditsHandled,
        CancellationToken cancellationToken)
    {
        if (!_plugin.Configuration.EnableTheIntroDb)
        {
            return 0;
        }

        var processed = 0;

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var external = await _theIntroDbService.TryGetMarkersAsync(
                episode,
                _plugin.Configuration.TheIntroDbApiKey,
                _plugin.Configuration.TheIntroDbMinConfidence,
                _plugin.Configuration.TheIntroDbMinSubmissions,
                cancellationToken).ConfigureAwait(false);
            if (external == null)
            {
                continue;
            }

            if (_plugin.Configuration.EnableIntroDetection &&
                external.IntroStartSeconds.HasValue &&
                external.IntroEndSeconds.HasValue &&
                external.IntroEndSeconds.Value > external.IntroStartSeconds.Value)
            {
                _markerService.ApplyIntroMarkers(episode, external.IntroStartSeconds.Value, external.IntroEndSeconds.Value, _plugin.Configuration.ReplaceNativeIntroMarkers);
                introHandled.Add(episode.InternalId);
                processed++;
            }

            if (_plugin.Configuration.EnableCreditsDetection &&
                external.CreditsStartSeconds.HasValue &&
                external.CreditsStartSeconds.Value >= 0)
            {
                _markerService.ApplyCreditsMarker(episode, external.CreditsStartSeconds.Value, _plugin.Configuration.ReplaceExistingCreditsMarkers);
                creditsHandled.Add(episode.InternalId);
                processed++;
            }
        }

        if (processed > 0)
        {
            _logger.Info($"TheIntroDB 已命中 {processed} 个标记");
        }

        return processed;
    }

    private CommonSegmentResult? DetectCommonSegment(IReadOnlyList<Episode> episodes, SegmentKind kind, CancellationToken cancellationToken)
    {
        var strategy = _plugin.Configuration.DetectionStrategy;
        var featureSets = strategy switch
        {
            "HashOnly" => episodes.Select(e => _extractor.Extract(e, kind)).ToList(),
            _ => episodes.Select(e => _extractor.ExtractAudioOnly(e, kind)).ToList()
        };

        if (featureSets.Count < _plugin.Configuration.MinEpisodesPerSeason)
        {
            return null;
        }

        if (strategy == "HashOnly")
        {
            var hashOnlyResult = DetectCommonSegmentByFrames(featureSets, kind, cancellationToken);
            if (hashOnlyResult == null)
            {
                _logger.Info($"{FormatSeason(episodes)} {GetKindName(kind)}哈希识别未命中");
            }
            return hashOnlyResult;
        }

        if (strategy == "AudioOnly")
        {
            var audioOnlyResult = DetectCommonSegmentByAudio(featureSets, kind, cancellationToken);
            if (audioOnlyResult == null)
            {
                _logger.Info($"{FormatSeason(episodes)} {GetKindName(kind)}声纹识别未命中");
            }
            return audioOnlyResult;
        }

        var audioResult = DetectCommonSegmentByAudio(featureSets, kind, cancellationToken);
        var subtitleResult = DetectCommonSegmentBySubtitles(featureSets, kind, cancellationToken);

        var fastMerged = MergeModalResults(kind, audioResult, subtitleResult);
        if (IsConfidentCheapResult(fastMerged, featureSets.Count))
        {
            _logger.Info($"{FormatSeason(episodes)} {GetKindName(kind)}快速路径命中：{fastMerged!.DetectionMethod}");
            return fastMerged;
        }

        if (fastMerged != null)
        {
            _logger.Info($"{FormatSeason(episodes)} {GetKindName(kind)}音频/字幕已命中，但置信不足，开始补充视频校验");
        }
        else
        {
            _logger.Info($"{FormatSeason(episodes)} {GetKindName(kind)}音频/字幕未命中，开始视频回退");
        }

        var visualFeatures = ExtractVisualFallbackFeatures(episodes, kind, cancellationToken);
        var visualResult = DetectCommonSegmentByFrames(visualFeatures, kind, cancellationToken);
        var merged = MergeModalResults(kind, visualResult, audioResult, subtitleResult);
        if (merged != null)
        {
            _logger.Info($"{FormatSeason(episodes)} {GetKindName(kind)}多模态融合命中：{merged.DetectionMethod}");
            return merged;
        }

        _logger.Info($"{FormatSeason(episodes)} {GetKindName(kind)}多模态未命中：画面={(visualResult != null ? "命中" : "未命中")}，音频={(audioResult != null ? "命中" : "未命中")}，字幕={(subtitleResult != null ? "命中" : "未命中")}");
        return null;
    }

    private CommonSegmentResult? DetectCommonSegmentByFrames(IReadOnlyList<EpisodeFeatureSequence> featureSets, SegmentKind kind, CancellationToken cancellationToken)
    {
        var window = _plugin.Configuration.MatchWindowSeconds * _plugin.Configuration.SampleFps;
        var usable = featureSets.Where(f => f.FrameHashes.Count >= window).ToList();
        if (usable.Count < _plugin.Configuration.MinEpisodesPerSeason)
        {
            _logger.Info($"{FormatSeason(featureSets)} {GetKindName(kind)}哈希识别跳过：可用样本 {usable.Count} 小于 {_plugin.Configuration.MinEpisodesPerSeason}");
            return null;
        }

        var requiredMatches = GetRequiredMatchCount(usable.Count);
        var minDuration = GetEffectiveMinimumDurationSeconds(kind) * _plugin.Configuration.SampleFps;
        var maxDuration = (kind == SegmentKind.Intro ? _plugin.Configuration.MaxIntroDurationSeconds : _plugin.Configuration.MaxCreditsDurationSeconds) * _plugin.Configuration.SampleFps;
        var threshold = _plugin.Configuration.MatchThreshold;

        CommonSegmentResult? best = null;

        foreach (var anchor in usable)
        {
            for (var anchorStart = 0; anchorStart <= anchor.FrameHashes.Count - window; anchorStart++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decisions = new List<EpisodeMarkerDecision>();
                var scores = new List<double>();
                var durations = new List<int>();

                foreach (var current in usable)
                {
                    var bestMatch = FindBestAlignment(anchor.FrameHashes, anchorStart, current.FrameHashes, window, threshold, maxDuration);
                    if (bestMatch == null)
                    {
                        continue;
                    }

                    scores.Add(bestMatch.Value.Score);
                    durations.Add(bestMatch.Value.DurationFrames);
                    var fps = _plugin.Configuration.SampleFps;
                    var startSeconds = current.OffsetSeconds + (bestMatch.Value.OtherStart / (double)fps);
                    var endSeconds = startSeconds + (bestMatch.Value.DurationFrames / (double)fps);
                    decisions.Add(new EpisodeMarkerDecision
                    {
                        EpisodeInternalId = current.EpisodeInternalId,
                        EpisodeName = current.EpisodeName,
                        StartSeconds = startSeconds,
                        EndSeconds = endSeconds,
                        Kind = kind
                    });
                }

                if (decisions.Count < requiredMatches)
                {
                    continue;
                }

                var durationFrames = durations.Min();
                if (durationFrames < minDuration)
                {
                    continue;
                }

                var averageScore = scores.Average();
                if (averageScore < threshold)
                {
                    continue;
                }

                var candidateDecisions = decisions.Select(d => d with { EndSeconds = d.StartSeconds + (durationFrames / (double)_plugin.Configuration.SampleFps) }).ToList();
                var positionSpread = ComputePositionSpreadSeconds(candidateDecisions, kind, usable);
                if (positionSpread > GetMaxAllowedPositionSpread(kind))
                {
                    continue;
                }

                var candidate = new CommonSegmentResult
                {
                    Kind = kind,
                    AverageScore = averageScore,
                    DurationFrames = durationFrames,
                    DetectionMethod = "画面哈希",
                    MatchedEpisodes = candidateDecisions.Count,
                    RequiredEpisodes = requiredMatches,
                    PositionSpreadSeconds = positionSpread,
                    Decisions = candidateDecisions
                };

                if (IsBetterCandidate(best, candidate))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private CommonSegmentResult? DetectCommonSegmentByAudio(IReadOnlyList<EpisodeFeatureSequence> featureSets, SegmentKind kind, CancellationToken cancellationToken)
    {
        foreach (var featureSet in featureSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _extractor.EnsureAudioSignatures(featureSet, kind);
        }

        var usable = featureSets.Where(f => f.AudioSignatures.Count >= _plugin.Configuration.MatchWindowSeconds).ToList();
        if (usable.Count < _plugin.Configuration.MinEpisodesPerSeason)
        {
            _logger.Info($"{FormatSeason(featureSets)} {GetKindName(kind)}声纹识别跳过：可用样本 {usable.Count} 小于 {_plugin.Configuration.MinEpisodesPerSeason}");
            return null;
        }

        var window = _plugin.Configuration.MatchWindowSeconds;
        var requiredMatches = GetRequiredMatchCount(usable.Count);
        var minDuration = GetEffectiveMinimumDurationSeconds(kind);
        var maxDuration = kind == SegmentKind.Intro ? _plugin.Configuration.MaxIntroDurationSeconds : _plugin.Configuration.MaxCreditsDurationSeconds;
        CommonSegmentResult? best = null;

        foreach (var anchor in usable)
        {
            for (var anchorStart = 0; anchorStart <= anchor.AudioSignatures.Count - window; anchorStart++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsMostlySilent(anchor.AudioLevels, anchorStart, window))
                {
                    continue;
                }
                var decisions = new List<EpisodeMarkerDecision>();
                var scores = new List<double>();
                var durations = new List<int>();

                foreach (var current in usable)
                {
                    var bestMatch = FindBestAudioAlignment(anchor, anchorStart, current, window, AudioMatchThreshold, maxDuration);
                    if (bestMatch == null)
                    {
                        continue;
                    }

                    scores.Add(bestMatch.Value.Score);
                    durations.Add(bestMatch.Value.DurationSeconds);
                    var startSeconds = current.OffsetSeconds + bestMatch.Value.OtherStart;
                    decisions.Add(new EpisodeMarkerDecision
                    {
                        EpisodeInternalId = current.EpisodeInternalId,
                        EpisodeName = current.EpisodeName,
                        StartSeconds = startSeconds,
                        EndSeconds = startSeconds + bestMatch.Value.DurationSeconds,
                        Kind = kind
                    });
                }

                if (decisions.Count < requiredMatches)
                {
                    continue;
                }

                var durationSeconds = durations.Min();
                if (durationSeconds < minDuration)
                {
                    continue;
                }

                var averageScore = scores.Average();
                if (averageScore < AudioMatchThreshold)
                {
                    continue;
                }

                var candidateDecisions = decisions.Select(d => d with { EndSeconds = d.StartSeconds + durationSeconds }).ToList();
                var positionSpread = ComputePositionSpreadSeconds(candidateDecisions, kind, usable);
                if (positionSpread > GetMaxAllowedPositionSpread(kind))
                {
                    continue;
                }

                var candidate = new CommonSegmentResult
                {
                    Kind = kind,
                    AverageScore = averageScore,
                    DurationFrames = durationSeconds * _plugin.Configuration.SampleFps,
                    DetectionMethod = "音频指纹",
                    MatchedEpisodes = candidateDecisions.Count,
                    RequiredEpisodes = requiredMatches,
                    PositionSpreadSeconds = positionSpread,
                    Decisions = candidateDecisions
                };

                if (IsBetterCandidate(best, candidate))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private CommonSegmentResult? DetectCommonSegmentBySubtitles(IReadOnlyList<EpisodeFeatureSequence> featureSets, SegmentKind kind, CancellationToken cancellationToken)
    {
        var window = _plugin.Configuration.MatchWindowSeconds;
        var usable = featureSets.Where(f => f.SubtitleSignatures.Count >= window && f.SubtitleSignatures.Any(v => v != 0)).ToList();
        if (usable.Count < _plugin.Configuration.MinEpisodesPerSeason)
        {
            return null;
        }

        var requiredMatches = GetRequiredMatchCount(usable.Count);
        var minDuration = GetEffectiveMinimumDurationSeconds(kind);
        var maxDuration = kind == SegmentKind.Intro ? _plugin.Configuration.MaxIntroDurationSeconds : _plugin.Configuration.MaxCreditsDurationSeconds;
        CommonSegmentResult? best = null;

        foreach (var anchor in usable)
        {
            for (var anchorStart = 0; anchorStart <= anchor.SubtitleSignatures.Count - window; anchorStart++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsEmptySubtitleWindow(anchor.SubtitleSignatures, anchorStart, window))
                {
                    continue;
                }

                var decisions = new List<EpisodeMarkerDecision>();
                var scores = new List<double>();
                var durations = new List<int>();

                foreach (var current in usable)
                {
                    var bestMatch = FindBestSubtitleAlignment(anchor.SubtitleSignatures, anchorStart, current.SubtitleSignatures, window, 0.84, maxDuration);
                    if (bestMatch == null)
                    {
                        continue;
                    }

                    scores.Add(bestMatch.Value.Score);
                    durations.Add(bestMatch.Value.DurationSeconds);
                    var startSeconds = current.OffsetSeconds + bestMatch.Value.OtherStart;
                    decisions.Add(new EpisodeMarkerDecision
                    {
                        EpisodeInternalId = current.EpisodeInternalId,
                        EpisodeName = current.EpisodeName,
                        StartSeconds = startSeconds,
                        EndSeconds = startSeconds + bestMatch.Value.DurationSeconds,
                        Kind = kind
                    });
                }

                if (decisions.Count < requiredMatches)
                {
                    continue;
                }

                var durationSeconds = durations.Min();
                if (durationSeconds < minDuration)
                {
                    continue;
                }

                var averageScore = scores.Average();
                if (averageScore < 0.84)
                {
                    continue;
                }

                var candidateDecisions = decisions.Select(d => d with { EndSeconds = d.StartSeconds + durationSeconds }).ToList();
                var positionSpread = ComputePositionSpreadSeconds(candidateDecisions, kind, usable);
                if (positionSpread > GetMaxAllowedPositionSpread(kind))
                {
                    continue;
                }

                var candidate = new CommonSegmentResult
                {
                    Kind = kind,
                    AverageScore = averageScore,
                    DurationFrames = durationSeconds * _plugin.Configuration.SampleFps,
                    DetectionMethod = "字幕特征",
                    MatchedEpisodes = candidateDecisions.Count,
                    RequiredEpisodes = requiredMatches,
                    PositionSpreadSeconds = positionSpread,
                    Decisions = candidateDecisions
                };

                if (IsBetterCandidate(best, candidate))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private (int OtherStart, int DurationFrames, double Score)? FindBestAlignment(
        IReadOnlyList<ulong> anchor,
        int anchorStart,
        IReadOnlyList<ulong> other,
        int window,
        double threshold,
        int maxDuration)
    {
        (int OtherStart, int DurationFrames, double Score)? best = null;

        for (var otherStart = 0; otherStart <= other.Count - window; otherStart++)
        {
            var score = AverageSimilarity(anchor, anchorStart, other, otherStart, window);
            if (score < threshold)
            {
                continue;
            }

            var duration = ExtendMatch(anchor, anchorStart, other, otherStart, maxDuration);
            if (best == null || duration > best.Value.DurationFrames || (duration == best.Value.DurationFrames && score > best.Value.Score))
            {
                best = (otherStart, duration, score);
            }
        }

        return best;
    }

    private (int OtherStart, int DurationSeconds, double Score)? FindBestAudioAlignment(
        EpisodeFeatureSequence anchor,
        int anchorStart,
        EpisodeFeatureSequence other,
        int window,
        double threshold,
        int maxDuration)
    {
        (int OtherStart, int DurationSeconds, double Score)? best = null;

        for (var otherStart = 0; otherStart <= other.AudioSignatures.Count - window; otherStart++)
        {
            if (IsMostlySilent(other.AudioLevels, otherStart, window))
            {
                continue;
            }

            var score = AverageAudioSimilarity(anchor.AudioSignatures, anchorStart, other.AudioSignatures, otherStart, window);
            if (score < threshold)
            {
                continue;
            }

            var duration = ExtendAudioMatch(anchor.AudioSignatures, anchor.AudioLevels, anchorStart, other.AudioSignatures, other.AudioLevels, otherStart, maxDuration);
            if (best == null || duration > best.Value.DurationSeconds || (duration == best.Value.DurationSeconds && score > best.Value.Score))
            {
                best = (otherStart, duration, score);
            }
        }

        return best;
    }

    private (int OtherStart, int DurationSeconds, double Score)? FindBestSubtitleAlignment(
        IReadOnlyList<uint> anchor,
        int anchorStart,
        IReadOnlyList<uint> other,
        int window,
        double threshold,
        int maxDuration)
    {
        (int OtherStart, int DurationSeconds, double Score)? best = null;

        for (var otherStart = 0; otherStart <= other.Count - window; otherStart++)
        {
            if (IsEmptySubtitleWindow(other, otherStart, window))
            {
                continue;
            }

            var score = AverageSubtitleSimilarity(anchor, anchorStart, other, otherStart, window);
            if (score < threshold)
            {
                continue;
            }

            var duration = ExtendSubtitleMatch(anchor, anchorStart, other, otherStart, maxDuration);
            if (best == null || duration > best.Value.DurationSeconds || (duration == best.Value.DurationSeconds && score > best.Value.Score))
            {
                best = (otherStart, duration, score);
            }
        }

        return best;
    }

    private int ExtendMatch(IReadOnlyList<ulong> anchor, int anchorStart, IReadOnlyList<ulong> other, int otherStart, int maxDuration)
    {
        var max = Math.Min(maxDuration, Math.Min(anchor.Count - anchorStart, other.Count - otherStart));
        var duration = 0;
        for (var i = 0; i < max; i++)
        {
            var sim = Similarity(anchor[anchorStart + i], other[otherStart + i]);
            if (sim < 0.82)
            {
                break;
            }
            duration++;
        }
        return duration;
    }

    private int ExtendAudioMatch(IReadOnlyList<uint> anchor, IReadOnlyList<double> anchorLevels, int anchorStart, IReadOnlyList<uint> other, IReadOnlyList<double> otherLevels, int otherStart, int maxDuration)
    {
        var max = Math.Min(maxDuration, Math.Min(anchor.Count - anchorStart, other.Count - otherStart));
        var duration = 0;
        for (var i = 0; i < max; i++)
        {
            if (IsSilent(anchorLevels, anchorStart + i) || IsSilent(otherLevels, otherStart + i))
            {
                break;
            }

            var sim = AudioSimilarity(anchor[anchorStart + i], other[otherStart + i]);
            if (sim < AudioExtendThreshold)
            {
                break;
            }
            duration++;
        }
        return duration;
    }

    private int ExtendSubtitleMatch(IReadOnlyList<uint> anchor, int anchorStart, IReadOnlyList<uint> other, int otherStart, int maxDuration)
    {
        var max = Math.Min(maxDuration, Math.Min(anchor.Count - anchorStart, other.Count - otherStart));
        var duration = 0;
        for (var i = 0; i < max; i++)
        {
            var sim = SubtitleSimilarity(anchor[anchorStart + i], other[otherStart + i]);
            if (sim < 0.84)
            {
                break;
            }
            duration++;
        }
        return duration;
    }

    private static double AverageSimilarity(IReadOnlyList<ulong> left, int leftStart, IReadOnlyList<ulong> right, int rightStart, int length)
    {
        double total = 0;
        for (var i = 0; i < length; i++)
        {
            total += Similarity(left[leftStart + i], right[rightStart + i]);
        }
        return total / length;
    }

    private static double AverageAudioSimilarity(IReadOnlyList<uint> left, int leftStart, IReadOnlyList<uint> right, int rightStart, int length)
    {
        double total = 0;
        for (var i = 0; i < length; i++)
        {
            total += AudioSimilarity(left[leftStart + i], right[rightStart + i]);
        }
        return total / length;
    }

    private static double Similarity(ulong left, ulong right)
    {
        var distance = BitOperations.PopCount(left ^ right);
        return 1d - (distance / 64d);
    }

    private static double AudioSimilarity(uint left, uint right)
    {
        var distance = BitOperations.PopCount(left ^ right);
        return 1d - (distance / 32d);
    }

    private static double SubtitleSimilarity(uint left, uint right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        var distance = BitOperations.PopCount(left ^ right);
        return 1d - (distance / 32d);
    }

    private static double AverageSubtitleSimilarity(IReadOnlyList<uint> left, int leftStart, IReadOnlyList<uint> right, int rightStart, int length)
    {
        double total = 0;
        var compared = 0;
        for (var i = 0; i < length; i++)
        {
            var sim = SubtitleSimilarity(left[leftStart + i], right[rightStart + i]);
            if (sim <= 0)
            {
                continue;
            }

            total += sim;
            compared++;
        }

        return compared == 0 ? 0 : total / compared;
    }

    private int GetRequiredMatchCount(int sampleCount)
    {
        if (sampleCount <= 3)
        {
            return sampleCount;
        }

        return Math.Max(_plugin.Configuration.MinEpisodesPerSeason, (int)Math.Ceiling(sampleCount * MinimumSampleSupportRatio));
    }

    private static bool IsBetterCandidate(CommonSegmentResult? currentBest, CommonSegmentResult candidate)
    {
        if (currentBest == null)
        {
            return true;
        }

        if (candidate.MatchedEpisodes != currentBest.MatchedEpisodes)
        {
            return candidate.MatchedEpisodes > currentBest.MatchedEpisodes;
        }

        if (candidate.DurationFrames != currentBest.DurationFrames)
        {
            return candidate.DurationFrames > currentBest.DurationFrames;
        }

        if (Math.Abs(candidate.AverageScore - currentBest.AverageScore) > 0.0001)
        {
            return candidate.AverageScore > currentBest.AverageScore;
        }

        return candidate.PositionSpreadSeconds < currentBest.PositionSpreadSeconds;
    }

    private static double ComputePositionSpreadSeconds(
        IReadOnlyList<EpisodeMarkerDecision> decisions,
        SegmentKind kind,
        IReadOnlyList<EpisodeFeatureSequence> featureSets)
    {
        if (decisions.Count <= 1)
        {
            return 0;
        }

        var durationMap = featureSets.ToDictionary(f => f.EpisodeInternalId, f => f.DurationTicks / (double)TimeSpan.TicksPerSecond);
        var positions = decisions
            .Select(d =>
            {
                if (kind == SegmentKind.Credits && durationMap.TryGetValue(d.EpisodeInternalId, out var durationSeconds))
                {
                    return Math.Max(0, durationSeconds - d.StartSeconds);
                }

                return d.StartSeconds;
            })
            .OrderBy(v => v)
            .ToList();

        return positions[^1] - positions[0];
    }

    private static double GetMaxAllowedPositionSpread(SegmentKind kind)
    {
        return kind == SegmentKind.Intro ? MaxIntroPositionSpreadSeconds : MaxCreditsPositionSpreadSeconds;
    }

    private List<EpisodeFeatureSequence> ExtractVisualFallbackFeatures(IReadOnlyList<Episode> episodes, SegmentKind kind, CancellationToken cancellationToken)
    {
        var result = new List<EpisodeFeatureSequence>(episodes.Count);
        var requiredMatches = GetRequiredMatchCount(episodes.Count);
        var failures = 0;
        var maxFailures = Math.Max(0, episodes.Count - requiredMatches);

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = _extractor.Extract(episode, kind);
            result.Add(feature);

            if (feature.FrameHashes.Count == 0)
            {
                failures++;
                if (failures > maxFailures)
                {
                    _logger.Info($"{FormatSeason(episodes)} {GetKindName(kind)}视频回退提前终止：视频样本超时/失败过多，已无可能达到 {requiredMatches}/{episodes.Count}");
                    break;
                }
            }
        }

        return result;
    }

    private bool IsConfidentCheapResult(CommonSegmentResult? result, int sampleCount)
    {
        if (result == null)
        {
            return false;
        }

        var requiredMatches = GetRequiredMatchCount(sampleCount);
        var minDuration = GetEffectiveMinimumDurationSeconds(result.Kind);

        return result.MatchedEpisodes >= Math.Max(requiredMatches, sampleCount - 1) &&
               result.PositionSpreadSeconds <= Math.Max(6, GetMaxAllowedPositionSpread(result.Kind) / 3d) &&
               result.DurationFrames >= minDuration * _plugin.Configuration.SampleFps;
    }

    private CommonSegmentResult? MergeModalResults(SegmentKind kind, params CommonSegmentResult?[] candidates)
    {
        var available = candidates.Where(c => c != null).Cast<CommonSegmentResult>().ToList();
        if (available.Count == 0)
        {
            return null;
        }

        if (available.Count == 1)
        {
            return available[0];
        }

        var clusters = new List<List<CommonSegmentResult>>();
        var tolerance = kind == SegmentKind.Intro ? 12 : 20;

        foreach (var candidate in available.OrderByDescending(c => c.MatchedEpisodes))
        {
            var added = false;
            var candidateCenter = Median(candidate.Decisions.Select(d => d.StartSeconds));
            foreach (var cluster in clusters)
            {
                var clusterCenter = Median(cluster.SelectMany(c => c.Decisions).Select(d => d.StartSeconds));
                if (Math.Abs(candidateCenter - clusterCenter) <= tolerance)
                {
                    cluster.Add(candidate);
                    added = true;
                    break;
                }
            }

            if (!added)
            {
                clusters.Add([candidate]);
            }
        }

        var bestCluster = clusters
            .OrderByDescending(cluster => cluster.Sum(GetModalityWeight))
            .ThenByDescending(cluster => cluster.Max(c => c.MatchedEpisodes))
            .ThenByDescending(cluster => cluster.Average(c => c.AverageScore))
            .First();

        var baseCandidate = bestCluster
            .OrderByDescending(GetModalityWeight)
            .ThenByDescending(c => c.MatchedEpisodes)
            .ThenByDescending(c => c.AverageScore)
            .First();

        if (bestCluster.Count == 1)
        {
            return baseCandidate;
        }

        var detectionMethod = $"多模态融合({string.Join("+", bestCluster.Select(c => c.DetectionMethod).Distinct())})";
        return new CommonSegmentResult
        {
            Kind = baseCandidate.Kind,
            DetectionMethod = detectionMethod,
            DurationFrames = baseCandidate.DurationFrames,
            AverageScore = bestCluster.Average(c => c.AverageScore),
            MatchedEpisodes = bestCluster.Max(c => c.MatchedEpisodes),
            RequiredEpisodes = bestCluster.Max(c => c.RequiredEpisodes),
            PositionSpreadSeconds = bestCluster.Min(c => c.PositionSpreadSeconds)
            ,
            Decisions = baseCandidate.Decisions
        };
    }

    private static double GetModalityWeight(CommonSegmentResult result) => result.DetectionMethod switch
    {
        "画面哈希" => 1.00,
        "音频指纹" => 0.85,
        "字幕特征" => 0.70,
        _ when result.DetectionMethod.StartsWith("多模态融合", StringComparison.Ordinal) => 1.20,
        _ => 0.60
    };

    private static bool IsSilent(IReadOnlyList<double> levels, int index)
    {
        return levels.Count == 0 || index >= levels.Count || levels[index] < SilenceThreshold;
    }

    private static bool IsMostlySilent(IReadOnlyList<double> levels, int start, int window)
    {
        if (levels.Count == 0 || start + window > levels.Count)
        {
            return true;
        }

        var active = 0;
        for (var i = 0; i < window; i++)
        {
            if (levels[start + i] >= SilenceThreshold)
            {
                active++;
            }
        }

        return active < Math.Max(2, window / 2);
    }

    private static bool IsEmptySubtitleWindow(IReadOnlyList<uint> subtitles, int start, int window)
    {
        if (subtitles.Count == 0 || start + window > subtitles.Count)
        {
            return true;
        }

        for (var i = 0; i < window; i++)
        {
            if (subtitles[start + i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private int GetEffectiveMinimumDurationSeconds(SegmentKind kind)
    {
        var configured = kind == SegmentKind.Intro ? _plugin.Configuration.MinIntroDurationSeconds : _plugin.Configuration.MinCreditsDurationSeconds;
        return Math.Max(configured, kind == SegmentKind.Intro ? HardMinimumIntroSeconds : HardMinimumCreditsSeconds);
    }

    private IReadOnlyList<Episode> SelectSampleEpisodes(IReadOnlyList<Episode> episodes)
    {
        var sampleCount = Math.Min(episodes.Count, Math.Max(_plugin.Configuration.MinEpisodesPerSeason, _plugin.Configuration.MaxSampleEpisodes));
        if (sampleCount >= episodes.Count)
        {
            return episodes;
        }

        var selected = new List<Episode>(sampleCount);
        var usedIndexes = new HashSet<int>();

        for (var i = 0; i < sampleCount; i++)
        {
            var index = (int)Math.Round(i * (episodes.Count - 1d) / Math.Max(1, sampleCount - 1d));
            if (!usedIndexes.Add(index))
            {
                continue;
            }

            selected.Add(episodes[index]);
        }

        for (var i = 0; selected.Count < sampleCount && i < episodes.Count; i++)
        {
            if (usedIndexes.Add(i))
            {
                selected.Add(episodes[i]);
            }
        }

        return selected;
    }

    private RepresentativeWindow? BuildRepresentativeWindow(IReadOnlyList<Episode> episodes, CommonSegmentResult result)
    {
        if (result.Decisions.Count == 0)
        {
            return null;
        }

        return result.Kind == SegmentKind.Intro
            ? BuildIntroWindow(result)
            : BuildCreditsWindow(episodes, result);
    }

    private RepresentativeWindow BuildIntroWindow(CommonSegmentResult result)
    {
        var startSeconds = Median(result.Decisions.Select(d => d.StartSeconds));
        var durationSeconds = Median(result.Decisions.Select(d => Math.Max(0, d.EndSeconds - d.StartSeconds)));

        return new RepresentativeWindow(episode =>
        {
            var episodeDuration = GetEpisodeDurationSeconds(episode);
            var start = Math.Clamp(startSeconds, 0, Math.Max(0, episodeDuration));
            var end = Math.Clamp(start + durationSeconds, start, Math.Max(start, episodeDuration));
            return (start, end);
        });
    }

    private RepresentativeWindow BuildCreditsWindow(IReadOnlyList<Episode> episodes, CommonSegmentResult result)
    {
        var episodeMap = episodes.ToDictionary(e => e.InternalId);
        var secondsFromEnd = result.Decisions
            .Where(d => episodeMap.ContainsKey(d.EpisodeInternalId))
            .Select(d => Math.Max(0, GetEpisodeDurationSeconds(episodeMap[d.EpisodeInternalId]) - d.StartSeconds))
            .ToList();

        var representativeFromEnd = secondsFromEnd.Count > 0 ? Median(secondsFromEnd) : 0;

        return new RepresentativeWindow(episode =>
        {
            var episodeDuration = GetEpisodeDurationSeconds(episode);
            var start = Math.Clamp(episodeDuration - representativeFromEnd, 0, Math.Max(0, episodeDuration));
            return (start, episodeDuration);
        });
    }

    private void ApplyMarker(Episode episode, SegmentKind kind, double startSeconds, double endSeconds)
    {
        if (kind == SegmentKind.Intro)
        {
            _markerService.ApplyIntroMarkers(episode, startSeconds, endSeconds, _plugin.Configuration.ReplaceNativeIntroMarkers);
            return;
        }

        _markerService.ApplyCreditsMarker(episode, startSeconds, _plugin.Configuration.ReplaceExistingCreditsMarkers);
    }

    private static double GetEpisodeDurationSeconds(Episode episode)
    {
        return Math.Max(0, (episode.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2d;
    }

    private sealed class RepresentativeWindow
    {
        private readonly Func<Episode, (double StartSeconds, double EndSeconds)> _factory;

        public RepresentativeWindow(Func<Episode, (double StartSeconds, double EndSeconds)> factory)
        {
            _factory = factory;
        }

        public (double StartSeconds, double EndSeconds) GetWindow(Episode episode)
        {
            return _factory(episode);
        }
    }

    private static string GetKindName(SegmentKind kind) => kind == SegmentKind.Intro ? "片头" : "片尾";

    private static string FormatEpisode(Episode episode)
    {
        var season = episode.ParentIndexNumber ?? 0;
        var index = episode.IndexNumber ?? 0;
        return $"S{season:00}E{index:00}";
    }

    private static string FormatSeason(IReadOnlyList<Episode> episodes)
    {
        return episodes.Count == 0 ? "未知季度" : $"季 {episodes[0].SeriesName} S{episodes[0].ParentIndexNumber:00}";
    }

    private static string FormatSeason(IReadOnlyList<EpisodeFeatureSequence> episodes)
    {
        return episodes.Count == 0 ? "未知季度" : $"季度样本 {string.Join(", ", episodes.Select(e => e.EpisodeName))}";
    }

    private static string FormatRepresentativeWindow(SegmentKind kind, IReadOnlyList<Episode> episodes, RepresentativeWindow representative)
    {
        var sampleEpisode = episodes.FirstOrDefault();
        if (sampleEpisode == null)
        {
            return "-";
        }

        var window = representative.GetWindow(sampleEpisode);
        return kind == SegmentKind.Intro
            ? $"{window.StartSeconds:F1}s-{window.EndSeconds:F1}s"
            : $"{window.StartSeconds:F1}s";
    }
}
