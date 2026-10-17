using System.Text.Json;
using System.Text.Json.Serialization;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Remote;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;
using RemoteAnalysisMode = SegmentScanner.Contracts.RemoteAnalysisMode;
using UpstreamAnalysisMode = IntroSkipper.Data.AnalysisMode;

namespace SegmentScanner.Engine.Introductions;

/// <summary>
/// Runs the upstream introduction matcher against a portable job snapshot without hosting Jellyfin.
/// </summary>
public sealed class IntroductionDetector
{
    private static readonly JsonSerializerOptions IntroductionSettingsOptions = new(WireJson.Options)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly WorkerConfiguration _configuration;

    public IntroductionDetector(WorkerConfiguration configuration)
    {
        configuration.Validate();
        _configuration = configuration;
    }

    /// <summary>
    /// Creates the effective upstream-compatible introduction settings for an offline job.
    /// </summary>
    /// <returns>A portable JSON snapshot of the upstream defaults.</returns>
    public static JsonElement CreateDefaultSettings() => RemoteAnalysisDispatcher.GetIntroductionSettings(new PluginConfiguration());

    /// <summary>
    /// Computes a deterministic identity for an effective introduction-settings snapshot.
    /// </summary>
    /// <param name="settings">Effective settings snapshot.</param>
    /// <returns>Lowercase SHA-256 hash of recursively ordinal-sorted JSON properties.</returns>
    public static string ProfileHash(JsonElement settings) => WireJson.Hash(settings);

    /// <summary>
    /// Analyzes introduction write targets while retaining comparison-only episodes within their season.
    /// </summary>
    /// <param name="job">Immutable job inputs.</param>
    /// <param name="media">Resolved local media for the job items.</param>
    /// <param name="cancellationToken">Token used to cancel the job.</param>
    /// <returns>One final result per introduction write target.</returns>
    public async Task<List<AnalysisResult>> AnalyzeAsync(
        JobSnapshot job,
        IReadOnlyList<ResolvedMedia> media,
        CancellationToken cancellationToken)
    {
        ContractValidation.Job(job);
        if (!job.Modes.Contains(RemoteAnalysisMode.Introduction))
        {
            return [];
        }

        var settings = job.IntroductionSettings.ValueKind == JsonValueKind.Object && job.IntroductionSettings.EnumerateObject().Any()
            ? job.IntroductionSettings
            : CreateDefaultSettings();
        var pluginConfiguration = JsonSerializer.Deserialize<PluginConfiguration>(settings.GetRawText(), IntroductionSettingsOptions)
            ?? throw new InvalidDataException("Introduction settings are empty.");
        var profileHash = ProfileHash(settings);
        if (!string.IsNullOrWhiteSpace(job.IntroductionProfileHash)
            && !string.Equals(job.IntroductionProfileHash, profileHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Introduction settings do not match the supplied profile hash.");
        }
        pluginConfiguration.ProcessThreads = _configuration.Resources.FfmpegThreads;
        pluginConfiguration.ScanTimeoutSeconds = _configuration.Resources.ProcessTimeoutSeconds;
        var mediaByItem = media.ToDictionary(item => item.Item.ItemId);
        var results = new List<AnalysisResult>();

        foreach (var season in job.Items.GroupBy(item => item.SeasonKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orderedItems = season.OrderBy(item => item.Order).ToList();
            var writeTargets = orderedItems.Where(item => item.WriteTarget).ToList();
            if (writeTargets.Count == 0)
            {
                continue;
            }

            var unresolved = writeTargets.Where(item => !mediaByItem.ContainsKey(item.ItemId)).ToList();
            results.AddRange(unresolved.Select(item => FailedResult(job, item, profileHash, "Resolved media is missing for this job item.")));
            var available = orderedItems.Where(item => mediaByItem.ContainsKey(item.ItemId)).ToList();
            var availableWriteTargets = available.Where(item => item.WriteTarget).ToList();
            if (availableWriteTargets.Count == 0)
            {
                continue;
            }

            try
            {
                var outcome = await AnalyzeSeasonAsync(job, available, availableWriteTargets, mediaByItem, pluginConfiguration, profileHash, cancellationToken).ConfigureAwait(false);
                results.AddRange(outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.AddRange(availableWriteTargets.Select(item => FailedResult(job, item, profileHash, exception.Message)));
            }
        }

        return results;
    }

    private async Task<List<AnalysisResult>> AnalyzeSeasonAsync(
        JobSnapshot job,
        IReadOnlyList<JobItem> orderedItems,
        IReadOnlyList<JobItem> writeTargets,
        IReadOnlyDictionary<Guid, ResolvedMedia> media,
        PluginConfiguration configuration,
        string profileHash,
        CancellationToken cancellationToken)
    {
        var chapters = orderedItems.ToDictionary(
            item => item.ItemId,
            item => (IReadOnlyList<ChapterInfo>)media[item.ItemId].Item.Chapters
                .Where(chapter => chapter.Origin is ChapterOrigin.Authored or ChapterOrigin.UserReviewed)
                .Select(chapter => new ChapterInfo { Name = chapter.Name, StartPositionTicks = chapter.StartTicks })
                .ToList());
        var cacheIdentities = new Dictionary<Guid, string>();
        var environment = new ExplicitAnalysisEnvironment(
            configuration,
            _configuration.Paths.Ffmpeg,
            itemId => chapters.TryGetValue(itemId, out var itemChapters) ? itemChapters : [],
            _configuration.Paths.Ffprobe,
            itemId => cacheIdentities.GetValueOrDefault(itemId));
        var queued = orderedItems.Select(item => ToQueuedEpisode(item, media[item.ItemId], configuration)).ToList();
        var targets = queued.Where(episode => writeTargets.Any(item => item.ItemId == episode.EpisodeId)).ToList();
        var cache = CreateCacheService(environment);
        var ffmpeg = new FFmpegService(NullLogger<FFmpegService>.Instance, cache, null, null, environment);
        var analyzer = new ChromaprintAnalyzer(NullLogger<ChromaprintAnalyzer>.Instance, ffmpeg, cache, null, configuration, environment);
        var chapterAnalyzer = new ChapterAnalyzer(NullLogger<ChapterAnalyzer>.Instance, ffmpeg, null!, configuration, environment: environment);
        var adjustment = new TimeAdjustmentHelper(NullLogger.Instance, configuration, UpstreamAnalysisMode.Introduction, ffmpeg, environment);
        var results = new Dictionary<Guid, AnalysisResult>();
        string? decoderIdentity = null;

        if (!configuration.PreferChromaprint) await AnalyzeChaptersAsync().ConfigureAwait(false);
        var pending = targets.Where(episode => !Settled(episode.EpisodeId)).ToList();
        if (pending.Count > 0)
        {
            var ffmpegVersion = await ProcessRunner.RunAsync(_configuration.Paths.Ffmpeg, ["-version"], 15, cancellationToken, 65536).ConfigureAwait(false);
            var ffprobeVersion = await ProcessRunner.RunAsync(_configuration.Paths.Ffprobe, ["-version"], 15, cancellationToken, 65536).ConfigureAwait(false);
            decoderIdentity = WireJson.Hash(new { ffmpegVersion.StandardOutput, Probe = ffprobeVersion.StandardOutput });
            foreach (var item in orderedItems)
                cacheIdentities[item.ItemId] = WireJson.Hash(new { media[item.ItemId].Signature, media[item.ItemId].PlaybackOriginTicks,
                    Decoder = decoderIdentity, profileHash, configuration.ProcessThreads });
            var references = queued.Where(episode => pending.All(target => target.EpisodeId != episode.EpisodeId)).ToList();
            var candidates = await analyzer.ComputeCandidatesAsync(
                pending,
                references,
                UpstreamAnalysisMode.Introduction,
                cancellationToken,
                _configuration.Resources.ConcurrentFiles).ConfigureAwait(false);
            foreach (var episode in pending)
            {
                var item = writeTargets.Single(item => item.ItemId == episode.EpisodeId);
                var resolved = media[item.ItemId];
                if (candidates.FingerprintFailures.Contains(item.ItemId))
                {
                    results[item.ItemId] = FailedResult(job, item, profileHash, "Chromaprint extraction failed.", resolved.Signature);
                    continue;
                }
                if (!candidates.Candidates.TryGetValue(item.ItemId, out var raw))
                {
                    results[item.ItemId] = candidates.FingerprintFailures.Count > 0
                        ? FailedResult(job, item, profileHash, "A comparison fingerprint extraction failed.", resolved.Signature)
                        : NoMatchResult(job, item, resolved, profileHash, episode.IntroFingerprintEnd);
                    continue;
                }
                var adjusted = await adjustment.AdjustIntroTimesAsync(episode, raw, cancellationToken: cancellationToken).ConfigureAwait(false);
                results[item.ItemId] = adjusted.Valid
                    ? CompletedResult(job, item, resolved, [(raw, adjusted)], profileHash, episode.IntroFingerprintEnd, "upstream-chromaprint")
                    : NoMatchResult(job, item, resolved, profileHash, episode.IntroFingerprintEnd);
            }
        }
        if (configuration.PreferChromaprint) await AnalyzeChaptersAsync().ConfigureAwait(false);
        return writeTargets.Select(item =>
        {
            var result = results[item.ItemId] with { Detector = results[item.ItemId].Detector with { InferenceEnvironment = decoderIdentity } };
            if (result.Status is AnalysisStatus.CompletedWithSegments or AnalysisStatus.CompletedNoSegments or AnalysisStatus.NeedsReview
                && RemoteAnalysisDispatcher.HasUnknownChapterDependency(result, media[item.ItemId].Item, job.IntroductionSettings))
                result = result with { Status = AnalysisStatus.NeedsReview, Detector = result.Detector with { ProductionApproved = false },
                    Segments = result.Segments.Select(segment => segment with { Decision = CandidateDecision.Review, ConfidenceTier = "Review" }).ToList(),
                    Warnings = [.. result.Warnings, "ChapterProvenanceUnknown"] };
            return result;
        }).ToList();

        bool Settled(Guid itemId) => results.TryGetValue(itemId, out var result) && result.Segments.Count > 0;

        async Task AnalyzeChaptersAsync()
        {
            foreach (var episode in targets.Where(episode => !Settled(episode.EpisodeId)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = chapterAnalyzer.FindChapterCandidates(episode, UpstreamAnalysisMode.Introduction);
                if (raw.Count == 0) continue;
                var adjusted = new List<(Segment Raw, Segment Adjusted)>();
                foreach (var match in raw)
                {
                    var final = await adjustment.AdjustIntroTimesAsync(episode, match, false, cancellationToken).ConfigureAwait(false);
                    if (final.Valid) adjusted.Add((match, final));
                }
                var item = writeTargets.Single(item => item.ItemId == episode.EpisodeId);
                results[item.ItemId] = adjusted.Count > 0
                    ? CompletedResult(job, item, media[item.ItemId], adjusted, profileHash, episode.Duration, "upstream-chapters")
                    : NoMatchResult(job, item, media[item.ItemId], profileHash, episode.Duration);
            }
        }
    }

    private DetectionCacheService CreateCacheService(IAnalysisEnvironment environment)
    {
        Directory.CreateDirectory(_configuration.Paths.CacheDirectory);
        var options = new DbContextOptionsBuilder<DetectionCacheDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_configuration.Paths.CacheDirectory, "introskipper-cache.db")}")
            .Options;
        var database = new DetectionCacheDatabase(
            new PooledDbContextFactory<DetectionCacheDbContext>(options),
            NullLogger<DetectionCacheDatabase>.Instance);
        return new DetectionCacheService(NullLogger<DetectionCacheService>.Instance, database, environment);
    }

    private static QueuedEpisode ToQueuedEpisode(JobItem item, ResolvedMedia media, PluginConfiguration configuration)
    {
        var duration = TimeSpan.FromTicks(media.TimelineEndTicks).TotalSeconds;
        var analysisPercent = configuration.AnalysisPercent / 100.0;
        return new QueuedEpisode
        {
            EpisodeId = item.ItemId,
            Path = media.Path,
            Name = item.MediaSourceKey,
            EpisodeNumber = item.Order,
            Duration = duration,
            IntroFingerprintEnd = Math.Min(duration >= 5 * 60 ? duration * analysisPercent : duration, 60 * configuration.AnalysisLengthLimit),
        };
    }

    private static AnalysisResult CompletedResult(JobSnapshot job, JobItem item, ResolvedMedia media, IReadOnlyList<(Segment Raw, Segment Adjusted)> segments, string profileHash, double searchedEnd, string detectorId)
        => BaseResult(
            job,
            item,
            media,
            profileHash,
            IsParityApproved(job, profileHash) ? AnalysisStatus.CompletedWithSegments : AnalysisStatus.NeedsReview,
            searchedEnd) with
        {
            Detector = new DetectorIdentity { Id = detectorId, ProfileHash = profileHash, ProductionApproved = IsParityApproved(job, profileHash) },
            Segments = segments.Select((segment, index) => new SegmentCandidate
            {
                CandidateId = detectorId + "-" + index,
                Type = RemoteAnalysisMode.Introduction,
                RawStartTicks = TimeSpan.FromSeconds(segment.Raw.Start).Ticks,
                RawEndTicks = TimeSpan.FromSeconds(segment.Raw.End).Ticks,
                StartTicks = TimeSpan.FromSeconds(segment.Adjusted.Start).Ticks,
                EndTicks = TimeSpan.FromSeconds(segment.Adjusted.End).Ticks,
                Decision = IsParityApproved(job, profileHash) ? CandidateDecision.Accept : CandidateDecision.Review,
                ConfidenceTier = IsParityApproved(job, profileHash) ? "High" : "Review",
                Reasons = [detectorId == "upstream-chapters" ? "AuthoredChapters" : "Chromaprint", "UpstreamAdjusted"],
            }).ToList(),
        };

    private static bool IsParityApproved(JobSnapshot job, string profileHash)
        => job.ServerId is not null
            && string.Equals(profileHash, job.IntroductionProfileHash, StringComparison.Ordinal);

    private static AnalysisResult NoMatchResult(JobSnapshot job, JobItem item, ResolvedMedia media, string profileHash, double searchedEnd)
        => BaseResult(job, item, media, profileHash, AnalysisStatus.CompletedNoSegments, searchedEnd);

    private static AnalysisResult FailedResult(JobSnapshot job, JobItem item, string profileHash, string warning, MediaSignature? signature = null)
        => new()
        {
            AnalysisId = Guid.NewGuid(), JobId = job.JobId, AttemptId = job.AttemptId, ServerId = job.ServerId,
            ItemId = item.ItemId, MediaSourceKey = item.MediaSourceKey, SourcePath = item.SourcePath, PathMapId = item.PathMapId, FileVersionToken = item.FileVersionToken,
            ExpectedItemModeRevision = item.ExpectedModeRevisions.GetValueOrDefault(RemoteAnalysisMode.Introduction, "0"),
            Mode = RemoteAnalysisMode.Introduction, Status = AnalysisStatus.Failed,
            TimelineEndTicks = signature?.TimelineEndTicks ?? item.RuntimeTicks,
            MediaValidation = signature, Detector = new DetectorIdentity { Id = "upstream-chromaprint", ProfileHash = profileHash }, Warnings = [warning],
        };

    private static AnalysisResult BaseResult(JobSnapshot job, JobItem item, ResolvedMedia media, string profileHash, AnalysisStatus status, double searchedEnd)
        => new()
        {
            AnalysisId = Guid.NewGuid(), JobId = job.JobId, AttemptId = job.AttemptId, ServerId = job.ServerId,
            ItemId = item.ItemId, MediaSourceKey = item.MediaSourceKey, SourcePath = item.SourcePath, PathMapId = item.PathMapId, FileVersionToken = item.FileVersionToken,
            ExpectedItemModeRevision = item.ExpectedModeRevisions.GetValueOrDefault(RemoteAnalysisMode.Introduction, "0"),
            Mode = RemoteAnalysisMode.Introduction, Status = status, TimelineEndTicks = media.TimelineEndTicks,
            MediaValidation = media.Signature, Detector = new DetectorIdentity { Id = "upstream-chromaprint", ProfileHash = profileHash, ProductionApproved = IsParityApproved(job, profileHash) },
            SearchedRanges = [new SegmentScanner.Contracts.TimeRange(0, TimeSpan.FromSeconds(searchedEnd).Ticks)],
        };
}
