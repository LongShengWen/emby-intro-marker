using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using IntroMarkerPlugin.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;

namespace IntroMarkerPlugin.Services;

public sealed class FrameHashExtractor
{
    private const int VideoExtractionTimeoutSeconds = 20;
    private readonly ILogger _logger;
    private readonly DetectionCacheService _cacheService;
    private readonly Plugin _plugin;
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    public FrameHashExtractor(ILogger logger, DetectionCacheService cacheService, Plugin plugin)
    {
        _logger = logger;
        _cacheService = cacheService;
        _plugin = plugin;
    }

    public EpisodeFeatureSequence Extract(Episode episode, SegmentKind kind)
    {
        var context = BuildExtractionContext(episode, kind);
        var fileInfo = context.FileInfo;
        var analysisSeconds = context.AnalysisSeconds;
        var sampleFps = context.SampleFps;
        var offsetSeconds = context.OffsetSeconds;
        var actualDuration = context.ActualDuration;

        var cached = _cacheService.TryGet(fileInfo, context.DurationTicks, sampleFps, analysisSeconds, kind);
        if (cached != null)
        {
            var cachedHashes = DetectionCacheService.ParseHashes(cached).ToList();
            if (cachedHashes.Count > 0)
            {
            _logger.Debug($"命中本地缓存：{Path.GetFileName(episode.Path)}，类型={kind}，模式=画面哈希");
            return new EpisodeFeatureSequence
            {
                EpisodeInternalId = episode.InternalId,
                EpisodeName = episode.Name,
                EpisodePath = episode.Path,
                DurationTicks = context.DurationTicks,
                OffsetSeconds = offsetSeconds,
                FrameHashes = cachedHashes,
                AudioSignatures = DetectionCacheService.ParseAudioSignatures(cached).ToList(),
                AudioLevels = DetectionCacheService.ParseAudioLevels(cached).ToList(),
                SubtitleSignatures = DetectionCacheService.ParseSubtitleSignatures(cached).ToList()
            };
            }
        }

        _logger.Debug($"开始提取画面哈希：{Path.GetFileName(episode.Path)}，类型={kind}，offset={offsetSeconds:F1}s，duration={actualDuration:F1}s，fps={sampleFps}");
        var hashes = ReadHashesWithFfmpeg(episode.Path, offsetSeconds, actualDuration, sampleFps);
        var subtitleSignatures = ReadSubtitleSignatures(episode.Path, offsetSeconds, actualDuration);
        _cacheService.Set(fileInfo, context.DurationTicks, sampleFps, analysisSeconds, kind, hashes, Array.Empty<uint>(), Array.Empty<double>(), subtitleSignatures);

        return new EpisodeFeatureSequence
        {
            EpisodeInternalId = episode.InternalId,
            EpisodeName = episode.Name,
            EpisodePath = episode.Path,
            DurationTicks = context.DurationTicks,
            OffsetSeconds = offsetSeconds,
            FrameHashes = hashes.ToList(),
            AudioSignatures = new List<uint>(),
            AudioLevels = new List<double>(),
            SubtitleSignatures = subtitleSignatures.ToList()
        };
    }

    public EpisodeFeatureSequence ExtractAudioOnly(Episode episode, SegmentKind kind)
    {
        var context = BuildExtractionContext(episode, kind);
        var fileInfo = context.FileInfo;
        var analysisSeconds = context.AnalysisSeconds;
        var sampleFps = context.SampleFps;
        var offsetSeconds = context.OffsetSeconds;
        var actualDuration = context.ActualDuration;

        var cached = _cacheService.TryGet(fileInfo, context.DurationTicks, sampleFps, analysisSeconds, kind);
        if (cached != null)
        {
            var cachedAudio = DetectionCacheService.ParseAudioSignatures(cached).ToList();
            if (cachedAudio.Count > 0)
            {
                _logger.Debug($"命中本地缓存：{Path.GetFileName(episode.Path)}，类型={kind}，模式=音频指纹");
                return new EpisodeFeatureSequence
                {
                    EpisodeInternalId = episode.InternalId,
                    EpisodeName = episode.Name,
                    EpisodePath = episode.Path,
                    DurationTicks = context.DurationTicks,
                    OffsetSeconds = offsetSeconds,
                    FrameHashes = new List<ulong>(),
                    AudioSignatures = cachedAudio,
                    AudioLevels = DetectionCacheService.ParseAudioLevels(cached).ToList(),
                    SubtitleSignatures = DetectionCacheService.ParseSubtitleSignatures(cached).ToList()
                };
            }
        }

        _logger.Debug($"开始提取音频指纹：{Path.GetFileName(episode.Path)}，类型={kind}，offset={offsetSeconds:F1}s，duration={actualDuration:F1}s");
        var audioBatch = ReadAudioFeaturesWithFfmpeg(episode.Path!, offsetSeconds, actualDuration);
        var subtitleSignatures = ReadSubtitleSignatures(episode.Path!, offsetSeconds, actualDuration);
        _cacheService.Set(fileInfo, context.DurationTicks, sampleFps, analysisSeconds, kind, Array.Empty<ulong>(), audioBatch.Signatures, audioBatch.Levels, subtitleSignatures);

        return new EpisodeFeatureSequence
        {
            EpisodeInternalId = episode.InternalId,
            EpisodeName = episode.Name,
            EpisodePath = episode.Path!,
            DurationTicks = context.DurationTicks,
            OffsetSeconds = offsetSeconds,
            FrameHashes = new List<ulong>(),
            AudioSignatures = audioBatch.Signatures.ToList(),
            AudioLevels = audioBatch.Levels.ToList(),
            SubtitleSignatures = subtitleSignatures.ToList()
        };
    }

    public IReadOnlyList<uint> EnsureAudioSignatures(EpisodeFeatureSequence sequence, SegmentKind kind)
    {
        if (sequence.AudioSignatures.Count > 0)
        {
            return sequence.AudioSignatures;
        }

        var fileInfo = new FileInfo(sequence.EpisodePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("剧集文件不存在", sequence.EpisodePath);
        }

        var analysisSeconds = kind == SegmentKind.Intro ? _plugin.Configuration.IntroAnalysisSeconds : _plugin.Configuration.CreditsAnalysisSeconds;
        var sampleFps = _plugin.Configuration.SampleFps;
        var durationSeconds = sequence.DurationTicks / (double)TimeSpan.TicksPerSecond;
        var actualDuration = Math.Min(durationSeconds, analysisSeconds);

        var cached = _cacheService.TryGet(fileInfo, sequence.DurationTicks, sampleFps, analysisSeconds, kind);
        var cachedAudio = cached == null ? Array.Empty<uint>() : DetectionCacheService.ParseAudioSignatures(cached).ToArray();
        if (cachedAudio.Length > 0)
        {
            _logger.Debug($"命中本地缓存音频指纹：{Path.GetFileName(sequence.EpisodePath)}，类型={kind}");
            sequence.AudioSignatures = cachedAudio.ToList();
            sequence.AudioLevels = cached == null ? new List<double>() : DetectionCacheService.ParseAudioLevels(cached).ToList();
            sequence.SubtitleSignatures = cached == null ? sequence.SubtitleSignatures : DetectionCacheService.ParseSubtitleSignatures(cached).ToList();
            return sequence.AudioSignatures;
        }

        _logger.Debug($"开始补提音频指纹：{Path.GetFileName(sequence.EpisodePath)}，类型={kind}，offset={sequence.OffsetSeconds:F1}s，duration={actualDuration:F1}s");
        var audioBatch = ReadAudioFeaturesWithFfmpeg(sequence.EpisodePath, sequence.OffsetSeconds, actualDuration);
        sequence.AudioSignatures = audioBatch.Signatures.ToList();
        sequence.AudioLevels = audioBatch.Levels.ToList();
        if (sequence.SubtitleSignatures.Count == 0)
        {
            sequence.SubtitleSignatures = ReadSubtitleSignatures(sequence.EpisodePath, sequence.OffsetSeconds, actualDuration).ToList();
        }
        _cacheService.Set(fileInfo, sequence.DurationTicks, sampleFps, analysisSeconds, kind, sequence.FrameHashes, audioBatch.Signatures, audioBatch.Levels, sequence.SubtitleSignatures);
        return sequence.AudioSignatures;
    }

    private IReadOnlyList<ulong> ReadHashesWithFfmpeg(string path, double offsetSeconds, double durationSeconds, int sampleFps)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-v", "error",
                "-threads", "1",
                "-filter_threads", "1",
                "-ss", offsetSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-t", durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", path,
                "-vf", $"fps={sampleFps},scale=9:8,format=gray",
                "-f", "rawvideo",
                "-pix_fmt", "gray",
                "pipe:1"
            }
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg");
        using var ms = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(ms);
        if (!process.WaitForExit(VideoExtractionTimeoutSeconds * 1000))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
            }

            _logger.Info($"视频抽帧超时，已跳过：{Path.GetFileName(path)}，类型=视频哈希，限制={VideoExtractionTimeoutSeconds}s");
            return Array.Empty<ulong>();
        }

        copyTask.GetAwaiter().GetResult();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg 提取帧失败: {stderr}");
        }

        var bytes = ms.ToArray();
        const int frameSize = 72;
        var result = new List<ulong>(bytes.Length / frameSize);
        for (var offset = 0; offset + frameSize <= bytes.Length; offset += frameSize)
        {
            result.Add(ComputeDHash(bytes.AsSpan(offset, frameSize)));
        }

        _logger.Debug($"已提取 {result.Count} 帧哈希，offset={offsetSeconds:F1}s, duration={durationSeconds:F1}s, path={path}");
        return result;
    }

    private AudioFeatureBatch ReadAudioFeaturesWithFfmpeg(string path, double offsetSeconds, double durationSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-v", "error",
                "-threads", "1",
                "-filter_threads", "1",
                "-ss", offsetSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-t", durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", path,
                "-vn",
                "-ac", "1",
                "-ar", "8000",
                "-f", "s16le",
                "pipe:1"
            }
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg 音频提取");
        using var ms = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg 提取音频失败: {stderr}");
        }

        var bytes = ms.ToArray();
        const int sampleRate = 8000;
        const int bytesPerSample = 2;
        var bytesPerSecond = sampleRate * bytesPerSample;
        var signatures = new List<uint>(Math.Max(1, bytes.Length / bytesPerSecond));
        var levels = new List<double>(Math.Max(1, bytes.Length / bytesPerSecond));

        for (var offset = 0; offset + bytesPerSecond <= bytes.Length; offset += bytesPerSecond)
        {
            var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(offset, bytesPerSecond));
            signatures.Add(ComputeAudioSignature(samples));
            levels.Add(ComputeAudioLevel(samples));
        }

        _logger.Debug($"已提取 {signatures.Count} 个音频签名，offset={offsetSeconds:F1}s, duration={durationSeconds:F1}s, path={path}");
        return new AudioFeatureBatch(signatures, levels);
    }

    private IReadOnlyList<uint> ReadSubtitleSignatures(string videoPath, double offsetSeconds, double durationSeconds)
    {
        var subtitlePath = FindSubtitlePath(videoPath);
        var seconds = Math.Max(1, (int)Math.Ceiling(durationSeconds));
        if (subtitlePath == null)
        {
            return Enumerable.Repeat(0u, seconds).ToList();
        }

        try
        {
            var cues = ParseSubtitleCues(subtitlePath)
                .Where(c => c.EndSeconds > offsetSeconds && c.StartSeconds < offsetSeconds + durationSeconds)
                .ToList();

            var result = new List<uint>(seconds);
            for (var i = 0; i < seconds; i++)
            {
                var currentSecond = offsetSeconds + i;
                var activeText = string.Join(" ",
                    cues.Where(c => c.StartSeconds <= currentSecond + 0.5 && c.EndSeconds >= currentSecond)
                        .Select(c => c.Text));
                result.Add(string.IsNullOrWhiteSpace(activeText) ? 0u : ComputeSubtitleSignature(activeText));
            }

            _logger.Debug($"已提取 {result.Count} 个字幕签名，offset={offsetSeconds:F1}s, duration={durationSeconds:F1}s, subtitle={Path.GetFileName(subtitlePath)}");
            return result;
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"读取字幕特征失败：{subtitlePath}", ex);
            return Enumerable.Repeat(0u, seconds).ToList();
        }
    }

    private ExtractionContext BuildExtractionContext(Episode episode, SegmentKind kind)
    {
        if (string.IsNullOrWhiteSpace(episode.Path))
        {
            throw new InvalidOperationException($"剧集 {episode.Name} 没有文件路径");
        }

        if (!episode.RunTimeTicks.HasValue || episode.RunTimeTicks.Value <= 0)
        {
            throw new InvalidOperationException($"剧集 {episode.Name} 没有可用时长");
        }

        var fileInfo = new FileInfo(episode.Path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("剧集文件不存在", episode.Path);
        }

        var analysisSeconds = kind == SegmentKind.Intro ? _plugin.Configuration.IntroAnalysisSeconds : _plugin.Configuration.CreditsAnalysisSeconds;
        var sampleFps = _plugin.Configuration.SampleFps;
        var durationTicks = episode.RunTimeTicks.Value;
        var durationSeconds = durationTicks / (double)TimeSpan.TicksPerSecond;
        var offsetSeconds = kind == SegmentKind.Intro ? 0 : Math.Max(0, durationSeconds - analysisSeconds);
        var actualDuration = Math.Min(durationSeconds, analysisSeconds);

        return new ExtractionContext(fileInfo, durationTicks, analysisSeconds, sampleFps, offsetSeconds, actualDuration);
    }

    private static ulong ComputeDHash(ReadOnlySpan<byte> pixels)
    {
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            var row = y * 9;
            for (var x = 0; x < 8; x++)
            {
                if (pixels[row + x] > pixels[row + x + 1])
                {
                    hash |= 1UL << bit;
                }
                bit++;
            }
        }
        return hash;
    }

    private static uint ComputeAudioSignature(ReadOnlySpan<short> samples)
    {
        const int buckets = 16;
        if (samples.Length < buckets)
        {
            return 0;
        }

        Span<double> energies = stackalloc double[buckets];
        Span<int> zeroCrossings = stackalloc int[buckets];
        var bucketSize = samples.Length / buckets;

        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = bucket * bucketSize;
            var end = bucket == buckets - 1 ? samples.Length : start + bucketSize;
            long sumAbs = 0;
            var zc = 0;

            for (var i = start; i < end; i++)
            {
                var current = samples[i];
                sumAbs += Math.Abs((int)current);

                if (i > start)
                {
                    var previous = samples[i - 1];
                    if ((previous < 0 && current >= 0) || (previous >= 0 && current < 0))
                    {
                        zc++;
                    }
                }
            }

            energies[bucket] = sumAbs / (double)Math.Max(1, end - start);
            zeroCrossings[bucket] = zc;
        }

        uint signature = 0;
        var bit = 0;

        for (var i = 0; i < buckets - 1; i++)
        {
            if (energies[i] > energies[i + 1])
            {
                signature |= 1u << bit;
            }
            bit++;
        }

        for (var i = 0; i < buckets - 1; i++)
        {
            if (zeroCrossings[i] > zeroCrossings[i + 1])
            {
                signature |= 1u << bit;
            }
            bit++;
        }

        return signature;
    }

    private static double ComputeAudioLevel(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sumSquares = 0;
        foreach (var sample in samples)
        {
            var normalized = sample / 32768d;
            sumSquares += normalized * normalized;
        }

        return Math.Sqrt(sumSquares / samples.Length);
    }

    private static uint ComputeSubtitleSignature(string text)
    {
        var normalized = NormalizeSubtitleText(text);
        if (normalized.Length == 0)
        {
            return 0;
        }

        Span<int> bits = stackalloc int[32];
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hash = Fnv1a32(token);
            for (var bit = 0; bit < 32; bit++)
            {
                bits[bit] += ((hash >> bit) & 1) == 1 ? 1 : -1;
            }
        }

        uint signature = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            if (bits[bit] >= 0)
            {
                signature |= 1u << bit;
            }
        }
        return signature;
    }

    private static string NormalizeSubtitleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(text, @"<[^>]+>", " ");
        cleaned = Regex.Replace(cleaned, @"\{\\.*?\}", " ");
        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N}\s]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim().ToLowerInvariant();
        return cleaned;
    }

    private static uint Fnv1a32(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        foreach (var ch in Encoding.UTF8.GetBytes(value))
        {
            hash ^= ch;
            hash *= prime;
        }
        return hash;
    }

    private static string? FindSubtitlePath(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var extension in SubtitleExtensions)
        {
            var exact = Path.Combine(directory, baseName + extension);
            if (File.Exists(exact))
            {
                return exact;
            }
        }

        return Directory.EnumerateFiles(directory)
            .Where(path => SubtitleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<SubtitleCue> ParseSubtitleCues(string subtitlePath)
    {
        var extension = Path.GetExtension(subtitlePath).ToLowerInvariant();
        var content = File.ReadAllText(subtitlePath);
        return extension switch
        {
            ".srt" => ParseSrt(content),
            ".vtt" => ParseVtt(content),
            ".ass" or ".ssa" => ParseAss(content),
            _ => []
        };
    }

    private static IReadOnlyList<SubtitleCue> ParseSrt(string content)
    {
        var blocks = Regex.Split(content.Replace("\r\n", "\n"), @"\n\s*\n");
        var result = new List<SubtitleCue>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
            {
                continue;
            }

            var timeLineIndex = lines[0].Contains("-->") ? 0 : 1;
            if (timeLineIndex >= lines.Length || !TryParseTimeRange(lines[timeLineIndex], ',', out var start, out var end))
            {
                continue;
            }

            var text = string.Join(" ", lines.Skip(timeLineIndex + 1));
            result.Add(new SubtitleCue(start, end, NormalizeSubtitleText(text)));
        }

        return result;
    }

    private static IReadOnlyList<SubtitleCue> ParseVtt(string content)
    {
        var blocks = Regex.Split(content.Replace("\r\n", "\n"), @"\n\s*\n");
        var result = new List<SubtitleCue>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var timeLineIndex = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timeLineIndex < 0 || !TryParseTimeRange(lines[timeLineIndex], '.', out var start, out var end))
            {
                continue;
            }

            var text = string.Join(" ", lines.Skip(timeLineIndex + 1));
            result.Add(new SubtitleCue(start, end, NormalizeSubtitleText(text)));
        }

        return result;
    }

    private static IReadOnlyList<SubtitleCue> ParseAss(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<SubtitleCue>();
        foreach (var line in lines)
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(',', 10);
            if (parts.Length < 10)
            {
                continue;
            }

            if (!TryParseAssTime(parts[1], out var start) || !TryParseAssTime(parts[2], out var end))
            {
                continue;
            }

            result.Add(new SubtitleCue(start, end, NormalizeSubtitleText(parts[9])));
        }

        return result;
    }

    private static bool TryParseTimeRange(string line, char millisecondSeparator, out double start, out double end)
    {
        start = 0;
        end = 0;
        var parts = line.Split("-->", StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               TryParseSubtitleTime(parts[0], millisecondSeparator, out start) &&
               TryParseSubtitleTime(parts[1], millisecondSeparator, out end);
    }

    private static bool TryParseSubtitleTime(string text, char millisecondSeparator, out double seconds)
    {
        seconds = 0;
        text = text.Trim();
        var normalized = millisecondSeparator == ',' ? text.Replace(',', '.') : text;
        return TimeSpan.TryParse(normalized, out var span) && (seconds = span.TotalSeconds) >= 0;
    }

    private static bool TryParseAssTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var hours) ||
            !int.TryParse(parts[1], out var minutes))
        {
            return false;
        }

        var secParts = parts[2].Split('.');
        if (secParts.Length != 2 ||
            !int.TryParse(secParts[0], out var secs) ||
            !int.TryParse(secParts[1], out var centiseconds))
        {
            return false;
        }

        seconds = hours * 3600 + minutes * 60 + secs + centiseconds / 100d;
        return true;
    }

    private sealed record ExtractionContext(
        FileInfo FileInfo,
        long DurationTicks,
        int AnalysisSeconds,
        int SampleFps,
        double OffsetSeconds,
        double ActualDuration);

    private sealed record AudioFeatureBatch(IReadOnlyList<uint> Signatures, IReadOnlyList<double> Levels);
    private sealed record SubtitleCue(double StartSeconds, double EndSeconds, string Text);
}
