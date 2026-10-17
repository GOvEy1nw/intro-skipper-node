using System.Text.Json;
using System.Text.Json.Serialization;
using IntroSkipper.Analyzers;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Remote;
using MediaBrowser.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Jellyfin;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;
using ContractTimeRange = SegmentScanner.Contracts.TimeRange;
using RemoteAnalysisMode = SegmentScanner.Contracts.RemoteAnalysisMode;
using UpstreamAnalysisMode = IntroSkipper.Data.AnalysisMode;

namespace SegmentScanner.Engine.Credits;

/// <summary>
/// Runs Intro Skipper's credits pass over an isolated segment database.
/// </summary>
public sealed class IntroSkipperCreditsDetector(WorkerConfiguration configuration)
{
    private const string CpuDetectorId = "introskipper-credits-cpu-v1";
    private const string GpuDetectorId = "introskipper-credits-gpu-v1";
    private const string CpuSemanticVersion = "introskipper-credits-cpu-v1";
    private const string GpuSemanticVersion = "introskipper-credits-gpu-v1";
    private const string GpuDecoderPolicy = "keyframe-hardware-v1-av1-native-no-grain";
    private static readonly JsonSerializerOptions CreditsSettingsOptions = new(WireJson.Options)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Computes the stable identity of the selected original-credits profile.
    /// </summary>
    /// <param name="configuration">Worker configuration.</param>
    /// <returns>A deterministic profile hash independent of text inference resources.</returns>
    public static string ProfileHash(WorkerConfiguration configuration)
    {
        if (!UsesGpu(configuration))
        {
            return WireJson.Hash(new
            {
                SemanticVersion = CpuSemanticVersion,
                configuration.Credits.StartOffsetSeconds,
                configuration.Credits.EndOffsetSeconds,
            });
        }

        return WireJson.Hash(new
        {
            SemanticVersion = GpuSemanticVersion,
            configuration.Credits.VideoDecoder,
            DecoderPolicy = GpuDecoderPolicy,
            configuration.Credits.StartOffsetSeconds,
            configuration.Credits.EndOffsetSeconds,
        });
    }

    /// <summary>
    /// Analyzes every credits write target with Intro Skipper's existing combined credits pass.
    /// </summary>
    /// <param name="job">Immutable job inputs.</param>
    /// <param name="media">Resolved local media.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One held result for every credits write target.</returns>
    public async Task<List<AnalysisResult>> AnalyzeAsync(
        JobSnapshot job,
        IReadOnlyList<ResolvedMedia> media,
        CancellationToken cancellationToken,
        QueueActivityCollector? activity = null)
    {
        configuration.Validate();
        ContractValidation.Job(job);
        if (!job.Modes.Contains(RemoteAnalysisMode.Credits))
        {
            return [];
        }

        var profileHash = ProfileHash(configuration);
        var detectorId = UsesGpu(configuration) ? GpuDetectorId : CpuDetectorId;
        var videoDecoder = UsesGpu(configuration) ? configuration.Credits.VideoDecoder : CreditVideoDecoder.Cpu;
        if (!string.IsNullOrWhiteSpace(job.CreditsProfileHash)
            && !string.Equals(job.CreditsProfileHash, profileHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Credits profile differs from the immutable job snapshot.");
        }

        var settings = EffectiveSettings(job);
        var (action, pluginConfiguration) = DeserializeSettings(settings);
        pluginConfiguration.UseLegacyBlackFrameAnalyzer = false;
        pluginConfiguration.ProcessThreads = configuration.Resources.FfmpegThreads;
        pluginConfiguration.ScanTimeoutSeconds = configuration.Resources.ProcessTimeoutSeconds;

        var mediaByItem = media.ToDictionary(value => value.Item.ItemId);
        string inferenceEnvironment;
        try
        {
            var ffmpegVersion = await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg, ["-version"], configuration.Resources.ProcessTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            var ffprobeVersion = await ProcessRunner.RunAsync(configuration.Paths.Ffprobe, ["-version"], configuration.Resources.ProcessTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            var gpuInventory = videoDecoder == CreditVideoDecoder.Cpu
                ? []
                : (await NodeCapabilities.DiscoverHardwareAsync(cancellationToken).ConfigureAwait(false)).Gpus
                    .Where(gpu => videoDecoder == CreditVideoDecoder.NvidiaNvdec
                        ? gpu.Name.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
                        : gpu.Name.Contains("intel", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            inferenceEnvironment = WireJson.Hash(new
            {
                Ffmpeg = ffmpegVersion.StandardOutput,
                Ffprobe = ffprobeVersion.StandardOutput,
                Settings = WireJson.Hash(settings),
                Profile = profileHash,
                Gpu = gpuInventory,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return job.Items.Where(item => item.WriteTarget)
                .Select(item => FailedResult(job, item, detectorId, profileHash, exception.Message, mediaByItem.GetValueOrDefault(item.ItemId)?.Signature))
                .ToList();
        }

        var results = new List<AnalysisResult>();
        var scratchRoot = Path.Combine(Path.GetTempPath(), "segment-scanner-original-credits");
        Directory.CreateDirectory(scratchRoot);
        PathMapper.RejectLinks(scratchRoot);
        var scratch = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        PathMapper.EnsureWithin(scratch, scratchRoot);
        Directory.CreateDirectory(scratch);
        PathMapper.RejectLinks(scratch);

        try
        {
            var cacheIdentities = new Dictionary<Guid, string>();
            var environment = new ExplicitAnalysisEnvironment(
                pluginConfiguration,
                configuration.Paths.Ffmpeg,
                itemId => Chapters(mediaByItem, itemId),
                configuration.Paths.Ffprobe,
                itemId => cacheIdentities.GetValueOrDefault(itemId),
                videoDecoder,
                itemId => VideoInfo(mediaByItem, itemId),
                trackItem: activity is null ? null : activity.TrackItem);
            var cache = CreateCache(environment);
            var ffmpeg = new FFmpegService(NullLogger<FFmpegService>.Instance, cache, null, null, environment);
            var database = new IntroSkipperDatabase(new SegmentFactory(Path.Combine(scratch, "segments.db")), NullLogger<IntroSkipperDatabase>.Instance);
            await database.InitializeAsync().ConfigureAwait(false);
            var ffmpegValid = await ffmpeg.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var season in job.Items.GroupBy(item => item.SeasonKey, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ordered = season.OrderBy(item => item.Order).ToList();
                var targets = ordered.Where(item => item.WriteTarget).ToList();
                if (targets.Count == 0)
                {
                    continue;
                }

                var available = ordered.Where(item => mediaByItem.ContainsKey(item.ItemId)).ToList();
                var availableTargets = available.Where(item => item.WriteTarget).ToList();
                if (availableTargets.Count == 0)
                {
                    continue;
                }

                foreach (var item in available)
                {
                    cacheIdentities[item.ItemId] = CacheIdentity(mediaByItem[item.ItemId], inferenceEnvironment);
                }

                var episodes = available.ToDictionary(item => item.ItemId, item => ToQueuedEpisode(item, mediaByItem[item.ItemId], pluginConfiguration));
                var queue = available.Select(item => episodes[item.ItemId]).ToArray();
                foreach (var item in available.Where(item => !item.WriteTarget))
                {
                    episodes[item.ItemId].SetAnalyzed(UpstreamAnalysisMode.Credits, EpisodeState.UserProvided);
                }
                var pass = new CreditsPass(NullLoggerFactory.Instance, ffmpeg, cache, database, pluginConfiguration, environment);
                var seasonHasUnresolvedReference = !availableTargets.All(item => item.IsMovie) && ordered.Any(item => !mediaByItem.ContainsKey(item.ItemId));

                try
                {
                    var chromaprintAvailable = ffmpegValid && !availableTargets.Any(item => item.IsMovie);
                    var handledChapters = await pass.SettleChaptersAsync(queue, action, chromaprintAvailable, cancellationToken).ConfigureAwait(false);
                    if (availableTargets.Any(item => !handledChapters.Contains(item.ItemId)))
                    {
                        await ProbeAudioWindowsAsync(available, episodes, ffmpeg, pluginConfiguration, cancellationToken, activity).ConfigureAwait(false);
                    }
                    try
                    {
                        await PrefetchAsync(queue, availableTargets.Select(item => item.ItemId).ToHashSet(), handledChapters, action, chromaprintAvailable, ffmpeg, pluginConfiguration, configuration.Resources.ConcurrentFiles, cancellationToken, activity).ConfigureAwait(false);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // CreditsPass retries the failed extraction and records its per-item failure state.
                    }
                    await pass.RunAsync(queue, action, chromaprintAvailable, cancellationToken, handledChapters).ConfigureAwait(false);

                    foreach (var item in availableTargets)
                    {
                        var resolved = mediaByItem[item.ItemId];
                        var episode = episodes[item.ItemId];
                        var range = new ContractTimeRange(
                            TimeSpan.FromSeconds(episode.CreditsFingerprintStart).Ticks,
                            TimeSpan.FromSeconds(episode.CreditsFingerprintEnd).Ticks);
                        if (episode.GetAnalyzed(UpstreamAnalysisMode.Credits) == EpisodeState.AnalysisFailed)
                        {
                            results.Add(FailedResult(job, item, detectorId, profileHash, "Original credits analysis failed.", resolved.Signature, range));
                            continue;
                        }

                        var stored = (await database.GetSegmentsAsync(item.ItemId, cancellationToken: cancellationToken).ConfigureAwait(false))
                            .Where(segment => segment.Type == UpstreamAnalysisMode.Credits && segment.State == SegmentState.Active)
                            .OrderBy(segment => segment.StartTicks)
                            .ToList();
                        if (stored.Any(segment => segment.Source == SegmentSource.Chapter))
                        {
                            range = new ContractTimeRange(0, resolved.TimelineEndTicks);
                        }
                        var translated = Translate(resolved, stored, out var invalidOffsets);
                        var warning = HasUnknownChapterDependency(item, pluginConfiguration) ? "UnknownChapterDependency"
                            : seasonHasUnresolvedReference ? "UnresolvedSeasonComparisonMedia"
                            : invalidOffsets ? "InvalidPlaybackOffset"
                            : null;
                        results.Add(ResultFactory.Create(job, item, RemoteAnalysisMode.Credits, AnalysisStatus.NeedsReview,
                            new DetectorIdentity { Id = detectorId, ProfileHash = profileHash, InferenceEnvironment = inferenceEnvironment, ProductionApproved = false }, resolved) with
                        {
                            SearchedRanges = [range],
                            Segments = translated,
                            Warnings = warning is null ? ["OriginalIntroSkipperCredits"] : [warning],
                        });
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    results.AddRange(availableTargets.Select(item => FailedResult(job, item, detectorId, profileHash, exception.Message, mediaByItem[item.ItemId].Signature,
                        new ContractTimeRange(TimeSpan.FromSeconds(episodes[item.ItemId].CreditsFingerprintStart).Ticks, TimeSpan.FromSeconds(episodes[item.ItemId].CreditsFingerprintEnd).Ticks))));
                }
            }
        }
        finally
        {
            PathMapper.EnsureWithin(scratch, scratchRoot);
            PathMapper.RejectLinks(scratch);
            Directory.Delete(scratch, true);
        }

        return results;
    }

    private static JsonElement EffectiveSettings(JobSnapshot job)
    {
        if (job.CreditsSettings is { ValueKind: JsonValueKind.Object } settings && settings.EnumerateObject().Any())
        {
            return settings;
        }

        if (job.ServerId is not null)
        {
            throw new InvalidDataException("Connected original credits analysis requires immutable credits settings.");
        }

        return RemoteAnalysisDispatcher.GetCreditsSettings(new PluginConfiguration(), AnalyzerAction.Default);
    }

    private static (AnalyzerAction Action, PluginConfiguration Configuration) DeserializeSettings(JsonElement settings)
    {
        if (!settings.TryGetProperty("action", out var actionElement)
            || !Enum.TryParse<AnalyzerAction>(actionElement.GetString(), true, out var action)
            || !Enum.IsDefined(action)
            || !settings.TryGetProperty("configuration", out var configurationElement))
        {
            throw new InvalidDataException("Credits settings are invalid.");
        }

        var pluginConfiguration = JsonSerializer.Deserialize<PluginConfiguration>(configurationElement.GetRawText(), CreditsSettingsOptions)
            ?? throw new InvalidDataException("Credits settings are empty.");
        return (action, pluginConfiguration);
    }

    private DetectionCacheService CreateCache(IAnalysisEnvironment environment)
    {
        Directory.CreateDirectory(configuration.Paths.CacheDirectory);
        return new DetectionCacheService(NullLogger<DetectionCacheService>.Instance,
            new DetectionCacheDatabase(new CacheFactory(Path.Combine(configuration.Paths.CacheDirectory, "introskipper-original-credits-cache.db")), NullLogger<DetectionCacheDatabase>.Instance), environment);
    }

    private static IReadOnlyList<ChapterInfo> Chapters(IReadOnlyDictionary<Guid, ResolvedMedia> media, Guid itemId)
        => media.TryGetValue(itemId, out var resolved)
            ? resolved.Item.Chapters
                .Where(chapter => chapter.Origin is ChapterOrigin.Authored or ChapterOrigin.UserReviewed)
                .Select(chapter => new ChapterInfo { Name = chapter.Name, StartPositionTicks = chapter.StartTicks })
                .ToList()
            : [];

    private static CreditVideoInfo? VideoInfo(IReadOnlyDictionary<Guid, ResolvedMedia> media, Guid itemId)
    {
        if (!media.TryGetValue(itemId, out var resolved) || resolved.VideoStreamIndex is not { } videoStreamIndex)
        {
            return null;
        }

        var stream = resolved.Streams.FirstOrDefault(candidate => candidate.Index == videoStreamIndex);
        return stream is null ? null : new CreditVideoInfo(stream.Codec, resolved.VideoPixelFormat);
    }

    private static bool UsesGpu(WorkerConfiguration configuration)
        => configuration.Credits.UseGpu && configuration.Credits.VideoDecoder != CreditVideoDecoder.Cpu;

    private static string CacheIdentity(ResolvedMedia media, string inferenceEnvironment)
        => WireJson.Hash(new
        {
            media.Signature,
            media.PlaybackOriginTicks,
            media.Streams,
            media.VideoPixelFormat,
            InferenceEnvironment = inferenceEnvironment,
        });

    private static QueuedEpisode ToQueuedEpisode(JobItem item, ResolvedMedia media, PluginConfiguration configuration)
    {
        var duration = TimeSpan.FromTicks(media.TimelineEndTicks).TotalSeconds;
        var window = item.IsMovie ? configuration.MaximumMovieCreditsDuration : configuration.MaximumCreditsDuration;
        return new QueuedEpisode
        {
            EpisodeId = item.ItemId,
            Path = media.Path,
            Name = item.MediaSourceKey,
            Chapters = media.Item.Chapters
                .Where(chapter => chapter.Origin is ChapterOrigin.Authored or ChapterOrigin.UserReviewed)
                .Select(chapter => new ChapterInfo { Name = chapter.Name, StartPositionTicks = chapter.StartTicks }).ToList(),
            SeriesName = item.SeasonKey,
            EpisodeNumber = item.Order,
            Category = item.IsMovie ? QueuedMediaCategory.Movie : QueuedMediaCategory.Episode,
            Duration = duration,
            CreditsFingerprintStart = Math.Max(0, duration - window),
            CreditsFingerprintEnd = duration,
        };
    }

    private static async Task ProbeAudioWindowsAsync(
        IReadOnlyList<JobItem> targets,
        IReadOnlyDictionary<Guid, QueuedEpisode> episodes,
        IFFmpegService ffmpeg,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        QueueActivityCollector? activity)
    {
        if (!configuration.ProbeAudioDuration)
        {
            return;
        }

        foreach (var item in targets)
        {
            var episode = episodes[item.ItemId];
            using var scope = activity?.Begin(item.ItemId, RemoteAnalysisMode.Credits, "AudioProbe");
            var audioDuration = await ffmpeg.ProbeAudioDurationAsync(episode.Path, cancellationToken).ConfigureAwait(false);
            if (audioDuration is not { } duration || duration <= 0)
            {
                continue;
            }

            var window = item.IsMovie ? configuration.MaximumMovieCreditsDuration : configuration.MaximumCreditsDuration;
            episode.CreditsFingerprintEnd = Math.Min(episode.Duration, duration);
            episode.CreditsFingerprintStart = Math.Max(0, episode.CreditsFingerprintEnd - window);
        }
    }

    private static async Task PrefetchAsync(
        IReadOnlyList<QueuedEpisode> episodes,
        IReadOnlySet<Guid> targetIds,
        IReadOnlySet<Guid> handledChapters,
        AnalyzerAction action,
        bool chromaprintAvailable,
        IFFmpegService ffmpeg,
        PluginConfiguration configuration,
        int concurrentFiles,
        CancellationToken cancellationToken,
        QueueActivityCollector? activity)
    {
        var unhandledTargets = episodes.Where(episode => targetIds.Contains(episode.EpisodeId) && !handledChapters.Contains(episode.EpisodeId)).ToList();
        if (unhandledTargets.Count == 0)
        {
            return;
        }

        var fingerprintIds = chromaprintAvailable && action != AnalyzerAction.BlackFrame && episodes.Count > 1
            ? episodes.Select(episode => episode.EpisodeId).ToHashSet()
            : [];
        var keyframeIds = action != AnalyzerAction.Chromaprint
            ? unhandledTargets.Select(episode => episode.EpisodeId).ToHashSet()
            : [];
        var prefetch = episodes.Where(episode => fingerprintIds.Contains(episode.EpisodeId) || keyframeIds.Contains(episode.EpisodeId)).ToList();
        await Parallel.ForEachAsync(prefetch, new ParallelOptions { MaxDegreeOfParallelism = concurrentFiles, CancellationToken = cancellationToken }, async (episode, token) =>
        {
            if (fingerprintIds.Contains(episode.EpisodeId))
            {
                using var scope = activity?.Begin(episode.EpisodeId, RemoteAnalysisMode.Credits, "CreditsFingerprintPrefetch");
                _ = await ffmpeg.FingerprintAsync(episode, UpstreamAnalysisMode.Credits, token).ConfigureAwait(false);
            }

            if (keyframeIds.Contains(episode.EpisodeId))
            {
                using var scope = activity?.Begin(episode.EpisodeId, RemoteAnalysisMode.Credits, "CreditsKeyframePrefetch");
                _ = await ffmpeg.ScanKeyframesAsync(episode, configuration.BlackFrameThreshold, token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static bool HasUnknownChapterDependency(JobItem item, PluginConfiguration configuration)
        => item.Chapters.Any(chapter => chapter.Origin == ChapterOrigin.Unknown)
            && (configuration.AdjustIntroBasedOnChapters
                || !string.IsNullOrWhiteSpace(configuration.ChapterAnalyzerEndCreditsPattern));

    private List<SegmentCandidate> Translate(ResolvedMedia media, IReadOnlyList<DbSegment> segments, out bool invalidOffsets)
    {
        invalidOffsets = false;
        var result = new List<SegmentCandidate>();
        var startOffset = checked((long)Math.Round(configuration.Credits.StartOffsetSeconds * TimeSpan.TicksPerSecond));
        var endOffset = checked((long)Math.Round(configuration.Credits.EndOffsetSeconds * TimeSpan.TicksPerSecond));
        foreach (var (segment, index) in segments.Select((segment, index) => (segment, index)))
        {
            var start = checked(segment.StartTicks + startOffset);
            var end = checked(segment.EndTicks + endOffset);
            if (start < 0 || start >= end || end > media.TimelineEndTicks)
            {
                invalidOffsets = true;
                continue;
            }

            result.Add(new SegmentCandidate
            {
                CandidateId = (UsesGpu(configuration) ? GpuDetectorId : CpuDetectorId) + "-" + index,
                Type = RemoteAnalysisMode.Credits,
                RawStartTicks = segment.StartTicks,
                RawEndTicks = segment.EndTicks,
                StartTicks = start,
                EndTicks = end,
                Decision = CandidateDecision.Review,
                ConfidenceTier = "Review",
                Reasons = ["OriginalIntroSkipperCredits", "UpstreamAdjusted"],
                ReachesFileEnd = end == media.TimelineEndTicks,
            });
        }

        return result;
    }

    private static AnalysisResult FailedResult(JobSnapshot job, JobItem item, string detectorId, string profileHash, string warning, MediaSignature? signature = null, ContractTimeRange? range = null)
        => new()
        {
            AnalysisId = Guid.NewGuid(),
            JobId = job.JobId,
            AttemptId = job.AttemptId,
            ServerId = job.ServerId,
            ItemId = item.ItemId,
            MediaSourceKey = item.MediaSourceKey,
            SourcePath = item.SourcePath,
            PathMapId = item.PathMapId,
            FileVersionToken = item.FileVersionToken,
            ExpectedItemModeRevision = item.ExpectedModeRevisions.GetValueOrDefault(RemoteAnalysisMode.Credits, "0"),
            Mode = RemoteAnalysisMode.Credits,
            Status = AnalysisStatus.Failed,
            TimelineEndTicks = signature?.TimelineEndTicks ?? item.RuntimeTicks,
            MediaValidation = signature,
            Detector = new DetectorIdentity { Id = detectorId, ProfileHash = profileHash },
            SearchedRanges = range is { } value ? [value] : [],
            Warnings = [warning],
        };

    private sealed class CacheFactory(string path) : IDbContextFactory<DetectionCacheDbContext>
    {
        public DetectionCacheDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<DetectionCacheDbContext>();
            SqlitePragmas.Configure(options, path);
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            return new DetectionCacheDbContext(options.Options);
        }
    }

    private sealed class SegmentFactory(string path) : IDbContextFactory<IntroSkipperDbContext>
    {
        public IntroSkipperDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<IntroSkipperDbContext>();
            SqlitePragmas.Configure(options, path);
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            return new IntroSkipperDbContext(options.Options);
        }
    }
}
