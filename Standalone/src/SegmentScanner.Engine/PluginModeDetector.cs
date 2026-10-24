using System.Text.Json;
using MediaBrowser.Model.Entities;
using System.Text.Json.Serialization;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Remote;
using IntroSkipper.ScheduledTasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;
using ContractTimeRange = SegmentScanner.Contracts.TimeRange;
using RemoteAnalysisMode = SegmentScanner.Contracts.RemoteAnalysisMode;
using UpstreamAnalysisMode = IntroSkipper.Data.AnalysisMode;

namespace SegmentScanner.Engine;

/// <summary>
/// Runs the plugin's recap, preview, or commercial chain against an isolated segment database.
/// </summary>
public sealed class PluginModeDetector(WorkerConfiguration configuration)
{
    private const string DetectorId = "introskipper-plugin-v3";
    private static readonly JsonSerializerOptions ScanSettingsOptions = new(WireJson.Options)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Analyzes one portable plugin mode without writing the worker's canonical store.
    /// </summary>
    /// <param name="job">Immutable server job.</param>
    /// <param name="media">Resolved node-local media.</param>
    /// <param name="mode">Plugin mode to run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result for every resolved write target in the requested mode.</returns>
    public async Task<List<AnalysisResult>> AnalyzeAsync(
        JobSnapshot job,
        IReadOnlyList<ResolvedMedia> media,
        RemoteAnalysisMode mode,
        CancellationToken cancellationToken,
        QueueActivityCollector? activity = null)
    {
        configuration.Validate();
        ContractValidation.Job(job);
        var upstreamMode = ToUpstreamMode(mode);
        if (!job.Modes.Contains(mode))
        {
            return [];
        }

        var settings = EffectiveSettings(job);
        var (action, pluginConfiguration) = DeserializeSettings(settings);
        pluginConfiguration.ProcessThreads = configuration.Resources.FfmpegThreads;
        pluginConfiguration.ScanTimeoutSeconds = configuration.Resources.ProcessTimeoutSeconds;
        var settingsHash = WireJson.Hash(settings);
        var mediaByItem = media.ToDictionary(value => value.Item.ItemId);
        string inferenceEnvironment;
        try
        {
            var ffmpegVersion = await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg, ["-version"], configuration.Resources.ProcessTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            var ffprobeVersion = await ProcessRunner.RunAsync(configuration.Paths.Ffprobe, ["-version"], configuration.Resources.ProcessTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            inferenceEnvironment = WireJson.Hash(new
            {
                Ffmpeg = ffmpegVersion.StandardOutput,
                Ffprobe = ffprobeVersion.StandardOutput,
                Settings = settingsHash,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return job.Items.Where(item => item.WriteTarget && mediaByItem.ContainsKey(item.ItemId))
                .Select(item => FailedResult(job, item, mode, settingsHash, exception.Message, mediaByItem.GetValueOrDefault(item.ItemId)?.Signature))
                .ToList();
        }

        var results = new List<AnalysisResult>();
        var scratchRoot = Path.Combine(Path.GetTempPath(), "segment-scanner-plugin-modes");
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
                var available = ordered.Where(item => mediaByItem.ContainsKey(item.ItemId)).ToList();
                var targets = available.Where(item => item.WriteTarget).ToList();
                if (targets.Count == 0)
                {
                    continue;
                }

                foreach (var item in available)
                {
                    cacheIdentities[item.ItemId] = CacheIdentity(mediaByItem[item.ItemId], inferenceEnvironment, settingsHash);
                }

                var episodes = available.ToDictionary(item => item.ItemId, item => ToQueuedEpisode(item, mediaByItem[item.ItemId], pluginConfiguration));
                await SeedContextAsync(database, available, mediaByItem, settingsHash, cancellationToken).ConfigureAwait(false);
                foreach (var item in available.Where(item => !item.WriteTarget))
                {
                    episodes[item.ItemId].SetAnalyzed(upstreamMode, EpisodeState.UserProvided);
                }

                try
                {
                    var analyzer = new BaseItemAnalyzerTask(NullLoggerFactory.Instance, null!, ffmpeg, cache, null!, database)
                    {
                        AnalysisEnvironment = environment,
                    };
                    await analyzer.AnalyzeItemsAsync(episodes.Values.ToArray(), upstreamMode, action, ffmpegValid, cancellationToken,
                        useLocalConsensus: job.UseLocalChromaprintConsensus, manualScan: job.ManualScan).ConfigureAwait(false);

                    var hasUnresolvedReference = !targets.All(item => item.IsMovie) && ordered.Any(item => !mediaByItem.ContainsKey(item.ItemId));
                    foreach (var item in targets)
                    {
                        var resolved = mediaByItem[item.ItemId];
                        var episode = episodes[item.ItemId];
                        var stored = (await database.GetSegmentsAsync(item.ItemId, cancellationToken: cancellationToken).ConfigureAwait(false))
                            .Where(segment => segment.Type == upstreamMode && segment.State == SegmentState.Active)
                            .OrderBy(segment => segment.StartTicks)
                            .ToList();
                        results.Add(CreateResult(job, item, resolved, mode, settingsHash, inferenceEnvironment, pluginConfiguration, action, episode, stored, hasUnresolvedReference));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    results.AddRange(targets.Select(item => FailedResult(job, item, mode, settingsHash, exception.Message, mediaByItem[item.ItemId].Signature)));
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

    private static JobScanSettings EffectiveSettings(JobSnapshot job)
    {
        if (job.ScanSettings is { Version: 1, Configuration.ValueKind: JsonValueKind.Object } settings
            && settings.Configuration.EnumerateObject().Any())
        {
            return settings;
        }

        if (job.ServerId is not null)
        {
            throw new InvalidDataException("Connected plugin analysis requires immutable scan settings.");
        }

        return RemoteAnalysisDispatcher.GetScanSettings(new PluginConfiguration());
    }

    private static (AnalyzerAction Action, PluginConfiguration Configuration) DeserializeSettings(JobScanSettings settings)
    {
        if (!Enum.TryParse<AnalyzerAction>(settings.Action, true, out var action) || !Enum.IsDefined(action))
        {
            throw new InvalidDataException("Plugin scan settings have an invalid action.");
        }

        var configuration = JsonSerializer.Deserialize<PluginConfiguration>(settings.Configuration.GetRawText(), ScanSettingsOptions)
            ?? throw new InvalidDataException("Plugin scan settings are empty.");
        return (action, configuration);
    }

    private DetectionCacheService CreateCache(IAnalysisEnvironment environment)
    {
        Directory.CreateDirectory(configuration.Paths.CacheDirectory);
        return new DetectionCacheService(
            NullLogger<DetectionCacheService>.Instance,
            new DetectionCacheDatabase(new CacheFactory(Path.Combine(configuration.Paths.CacheDirectory, "introskipper-plugin-mode-cache.db")), NullLogger<DetectionCacheDatabase>.Instance),
            environment);
    }

    private static async Task SeedContextAsync(
        IntroSkipperDatabase database,
        IReadOnlyList<JobItem> items,
        IReadOnlyDictionary<Guid, ResolvedMedia> media,
        string settingsHash,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            foreach (var group in item.ContextSegments?.Where(context => context.Mode is RemoteAnalysisMode.Introduction or RemoteAnalysisMode.Credits)
                .GroupBy(context => context.Mode) ?? [])
            {
                var mode = group.Key == RemoteAnalysisMode.Introduction ? UpstreamAnalysisMode.Introduction : UpstreamAnalysisMode.Credits;
                var segments = group.Select(context => ToSegment(item.ItemId, context, media[item.ItemId].TimelineEndTicks)).ToList();
                await database.ReplaceAutoSegmentsAsync(item.ItemId, mode, segments, SegmentSource.Chapter, settingsHash, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Segment ToSegment(Guid itemId, ContextSegment context, long timelineEndTicks)
    {
        if (context.StartTicks < 0 || context.StartTicks >= context.EndTicks || context.EndTicks > timelineEndTicks)
        {
            throw new InvalidDataException("Context segment is outside the resolved media timeline.");
        }

        return new Segment(itemId, new IntroSkipper.Data.TimeRange(
            TimeSpan.FromTicks(context.StartTicks).TotalSeconds,
            TimeSpan.FromTicks(context.EndTicks).TotalSeconds));
    }

    private static IReadOnlyList<ChapterInfo> Chapters(IReadOnlyDictionary<Guid, ResolvedMedia> media, Guid itemId)
        => media.TryGetValue(itemId, out var resolved)
            ? resolved.Item.Chapters
                .Where(chapter => chapter.Origin is ChapterOrigin.Authored or ChapterOrigin.UserReviewed)
                .Select(chapter => new ChapterInfo { Name = chapter.Name, StartPositionTicks = chapter.StartTicks })
                .ToList()
            : [];

    private static QueuedEpisode ToQueuedEpisode(JobItem item, ResolvedMedia media, PluginConfiguration configuration)
    {
        var duration = TimeSpan.FromTicks(media.TimelineEndTicks).TotalSeconds;
        var analysisPercent = configuration.AnalysisPercent / 100.0;
        return new QueuedEpisode
        {
            EpisodeId = item.ItemId,
            Path = media.Path,
            Name = item.MediaSourceKey,
            Chapters = media.Item.Chapters
                .Where(chapter => chapter.Origin is ChapterOrigin.Authored or ChapterOrigin.UserReviewed)
                .Select(chapter => new ChapterInfo { Name = chapter.Name, StartPositionTicks = chapter.StartTicks }).ToList(),
            Category = item.IsMovie ? QueuedMediaCategory.Movie : item.IsAnime ? QueuedMediaCategory.AnimeEpisode : QueuedMediaCategory.Episode,
            SeasonNumber = item.SeasonNumber ?? (item.IsMovie ? 0 : 1),
            EpisodeNumber = item.EpisodeNumber ?? (item.Order + 1),
            Duration = duration,
            FileVersion = long.TryParse(item.FileVersionToken, out var fileVersion) ? fileVersion : null,
            IntroFingerprintEnd = Math.Min(duration >= 5 * 60 ? duration * analysisPercent : duration, 60 * configuration.AnalysisLengthLimit),
        };
    }

    private static AnalysisResult CreateResult(
        JobSnapshot job,
        JobItem item,
        ResolvedMedia media,
        RemoteAnalysisMode mode,
        string settingsHash,
        string inferenceEnvironment,
        PluginConfiguration configuration,
        AnalyzerAction action,
        QueuedEpisode episode,
        IReadOnlyList<DbSegment> stored,
        bool hasUnresolvedReference)
    {
        var candidates = stored.Select(segment => new SegmentCandidate
        {
            CandidateId = segment.Id.ToString("N"),
            Type = mode,
            StartTicks = segment.StartTicks,
            EndTicks = segment.EndTicks,
            RawStartTicks = segment.StartTicks,
            RawEndTicks = segment.EndTicks,
            Decision = CandidateDecision.Review,
            ConfidenceTier = "Review",
            Reasons = ["SharedPluginSettings", "UpstreamAdjusted"],
            ReachesFileEnd = segment.EndTicks == media.TimelineEndTicks,
        }).ToList();
        var reviewReason = action == AnalyzerAction.None ? "PluginModeDisabled"
            : episode.GetAnalyzed(ToUpstreamMode(mode)) == EpisodeState.AnalysisFailed ? "PluginAnalysisFailed"
            : episode.GetSubtitleOutcome(ToUpstreamMode(mode)) == SubtitleOutcome.Unresolved ? "UnresolvedSubtitleDetection"
            : candidates.Count == 0 && episode.GetSubtitleOutcome(ToUpstreamMode(mode)) == SubtitleOutcome.Rejected ? "RejectedSubtitleDetection"
            : hasUnresolvedReference ? "UnresolvedSeasonComparisonMedia"
            : HasUnknownChapterDependency(item, mode, configuration, action) ? "UnknownChapterDependency"
            : episode.GetAnalyzed(ToUpstreamMode(mode)) == EpisodeState.NotAnalyzed ? "UnresolvedPluginAnalysis"
            : null;
        var status = candidates.Count > 0 || reviewReason is not null ? AnalysisStatus.NeedsReview : AnalysisStatus.CompletedNoSegments;
        var result = ResultFactory.Create(
            job,
            item,
            mode,
            status,
            new DetectorIdentity { Id = DetectorId, ProfileHash = settingsHash, ModelSha256 = null, InferenceEnvironment = inferenceEnvironment },
            media) with
        {
            SearchedRanges = [SearchedRange(mode, media, configuration, stored)],
            Segments = candidates,
            Warnings = reviewReason is null ? ["SharedPluginSettings"] : ["SharedPluginSettings", reviewReason],
        };
        ContractValidation.Result(result);
        return result;
    }

    private static bool HasUnknownChapterDependency(
        JobItem item,
        RemoteAnalysisMode mode,
        PluginConfiguration configuration,
        AnalyzerAction action)
    {
        if (action == AnalyzerAction.None || !item.Chapters.Any(chapter => chapter.Origin == ChapterOrigin.Unknown))
        {
            return false;
        }

        var pattern = mode switch
        {
            RemoteAnalysisMode.Recap => configuration.ChapterAnalyzerRecapPattern,
            RemoteAnalysisMode.Preview => configuration.ChapterAnalyzerPreviewPattern,
            RemoteAnalysisMode.Commercial => configuration.ChapterAnalyzerCommercialPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return !string.IsNullOrWhiteSpace(pattern) || configuration.EnableSponsorBlockChapterDetection;
    }

    private static ContractTimeRange SearchedRange(RemoteAnalysisMode mode, ResolvedMedia media, PluginConfiguration configuration, IReadOnlyList<DbSegment> stored)
    {
        if (stored.Any(segment => segment.Source is SegmentSource.Chapter or SegmentSource.CreditsDerived))
        {
            return new ContractTimeRange(0, media.TimelineEndTicks);
        }

        return mode switch
        {
            RemoteAnalysisMode.Recap => new ContractTimeRange(0, Math.Min(
                media.TimelineEndTicks,
                TimeSpan.FromSeconds(Math.Min(
                    TimeSpan.FromTicks(media.TimelineEndTicks).TotalSeconds >= 5 * 60
                        ? TimeSpan.FromTicks(media.TimelineEndTicks).TotalSeconds * configuration.AnalysisPercent / 100.0
                        : TimeSpan.FromTicks(media.TimelineEndTicks).TotalSeconds,
                    60 * configuration.AnalysisLengthLimit)).Ticks)),
            RemoteAnalysisMode.Preview => new ContractTimeRange(
                Math.Max(0, media.TimelineEndTicks - TimeSpan.FromSeconds(configuration.MaximumPreviewDuration).Ticks),
                media.TimelineEndTicks),
            RemoteAnalysisMode.Commercial => new ContractTimeRange(0, media.TimelineEndTicks),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static AnalysisResult FailedResult(JobSnapshot job, JobItem item, RemoteAnalysisMode mode, string settingsHash, string warning, MediaSignature? signature = null)
    {
        var result = ResultFactory.Create(
            job,
            item,
            mode,
            AnalysisStatus.Failed,
            new DetectorIdentity { Id = DetectorId, ProfileHash = settingsHash, ModelSha256 = null },
            signature is null ? null : new ResolvedMedia(item, item.SourcePath, signature, [], 0));
        ContractValidation.Result(result);
        return result with { Warnings = [warning] };
    }

    private static string CacheIdentity(ResolvedMedia media, string inferenceEnvironment, string settingsHash)
        => WireJson.Hash(new
        {
            media.Item.SourcePath,
            media.Item.FileVersionToken,
            media.Signature,
            media.PlaybackOriginTicks,
            media.Streams,
            media.VideoPixelFormat,
            InferenceEnvironment = inferenceEnvironment,
            SettingsHash = settingsHash,
        });

    private static UpstreamAnalysisMode ToUpstreamMode(RemoteAnalysisMode mode) => mode switch
    {
        RemoteAnalysisMode.Recap => UpstreamAnalysisMode.Recap,
        RemoteAnalysisMode.Preview => UpstreamAnalysisMode.Preview,
        RemoteAnalysisMode.Commercial => UpstreamAnalysisMode.Commercial,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), "Only plugin-only modes are supported."),
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
