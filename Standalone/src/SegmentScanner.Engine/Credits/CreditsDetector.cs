using System.Diagnostics;
using System.Text.Json;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Engine.Credits;

public sealed class CreditsDetector(WorkerConfiguration configuration)
{
    public string ProfileHash() => configuration.Credits.Method == CreditsDetectionMethod.IntroSkipper
        ? IntroSkipperCreditsDetector.ProfileHash(configuration)
        : WireJson.Hash(new { Credits = configuration.Credits with { ModelManifest = null }, Model = ModelIdentity(),
        CreditPolicy.ShortGapThresholdTicks, CreditPolicy.ImageFeatureVersion, GroupPolicy = "ordered-coarse-groups-v2-sparse-scene-text",
        IndexPolicy = "one-based-window-array-source-pts-v1", DecoderPolicy });

    private const string DecoderPolicy = "source-pts-display-aspect-sdr-320-increase-v3-av1-no-grain";
    private const string NonAv1DecoderPolicy = "source-pts-display-aspect-sdr-320-increase-v2";

    public async Task<AnalysisResult> AnalyzeAsync(JobSnapshot job, ResolvedMedia media, InferenceHelper helper, CancellationToken cancellationToken, bool trimCache = true)
    {
        var identity = new DetectorIdentity { Id = configuration.Credits.ProfileId, ProfileHash = ProfileHash(), ModelSha256 = helper.ModelSha256,
            InferenceEnvironment = helper.EnvironmentIdentity, ProductionApproved = false };
        var result = ResultFactory.Create(job, media.Item, RemoteAnalysisMode.Credits, AnalysisStatus.NeedsReview, identity, media);
        if ((job.ServerId is not null && !ContractValidation.IsSha256(job.CreditsProfileHash))
            || (job.CreditsProfileHash.Length > 0 && job.CreditsProfileHash != identity.ProfileHash))
            return result with { Status = AnalysisStatus.Failed, Warnings = ["Credits profile differs from the immutable job snapshot."] };
        if (media.VideoStreamIndex is null || media.HasHdr)
            return result with { Status = AnalysisStatus.SkippedUnsupported, Warnings = [media.HasHdr ? "HDR requires an independently verified tone-mapping profile." : "No primary video stream."] };
        var clock = Stopwatch.StartNew();
        var version = await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg, ["-version"], 20, cancellationToken).ConfigureAwait(false);
        var stream = media.Streams.First(candidate => candidate.Index == media.VideoStreamIndex);
        var evidencePolicy = stream.Codec == "av1" ? DecoderPolicy : NonAv1DecoderPolicy;
        string cacheKey;
        if (configuration.Credits.VideoDecoder == CreditVideoDecoder.Cpu)
        {
            cacheKey = WireJson.Hash(new { media.Signature, Model = ModelIdentity(), InferenceEnvironment = helper.EnvironmentIdentity,
                CreditPolicy.ImageFeatureVersion, configuration.Credits.SampleIntervalSeconds,
                Decoder = new { version = version.StandardOutput.Split('\n')[0], policy = evidencePolicy, media.PlaybackOriginTicks, media.VideoStreamIndex } });
        }
        else
        {
            cacheKey = WireJson.Hash(new { media.Signature, Model = ModelIdentity(), InferenceEnvironment = helper.EnvironmentIdentity,
                CreditPolicy.ImageFeatureVersion, configuration.Credits.SampleIntervalSeconds,
                Decoder = new { version = version.StandardOutput.Split('\n')[0], policy = evidencePolicy, media.PlaybackOriginTicks, media.VideoStreamIndex,
                    backend = configuration.Credits.VideoDecoder.ToString(), codec = stream.Codec, pixelFormat = media.VideoPixelFormat } });
        }
        var cacheDirectory = Path.Combine(configuration.Paths.CacheDirectory, "evidence");
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = Path.Combine(cacheDirectory, cacheKey + ".json");
        var intervalTicks = checked((long)Math.Round(configuration.Credits.SampleIntervalSeconds * TimeSpan.TicksPerSecond));
        if (intervalTicks < 1) throw new InvalidDataException("Sampling interval is below tick precision.");
        var evidence = new SortedDictionary<long, FrameEvidence>();
        if (File.Exists(cachePath))
        {
            if (new FileInfo(cachePath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Oversized evidence cache.");
            foreach (var frame in WireJson.Read<List<FrameEvidence>>(await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false))) evidence[frame.TimestampTicks / intervalTicks] = frame;
        }
        using var extractor = new FrameExtractor(configuration, media);
        long searchedStart = media.TimelineEndTicks;
        var uniqueFrames = 0;
        var incomplete = false;
        var windowCount = 0;
        List<SegmentCandidate> candidates = [];
        List<string> rejections = [];
        foreach (var fraction in configuration.Credits.LookbackFractions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            windowCount++;
            var start = Math.Max(0, (long)(media.TimelineEndTicks * (1 - fraction)) / intervalTicks * intervalTicks);
            var missingEnd = Math.Min(media.TimelineEndTicks, searchedStart + intervalTicks);
            var missing = await extractor.ExtractAsync(start, missingEnd, configuration.Credits.SampleIntervalSeconds, cancellationToken).ConfigureAwait(false);
            var newFrames = missing.Where(f => !evidence.ContainsKey(f.TimestampTicks / intervalTicks)).ToList();
            for (var index = 0; index < newFrames.Count; index += 32)
            {
                var inferred = await helper.InferAsync(newFrames.Skip(index).Take(32).ToList(), cancellationToken, resetTemporal: index == 0).ConfigureAwait(false);
                foreach (var frame in inferred) evidence[frame.TimestampTicks / intervalTicks] = frame;
                uniqueFrames += inferred.Count;
                AtomicFile.Write(cachePath, WireJson.Write(evidence.Values.ToList()));
            }
            searchedStart = start;
            var selected = evidence.Values.Where(f => f.TimestampTicks >= start && f.TimestampTicks < media.TimelineEndTicks).ToList();
            incomplete = selected.Count == 0 || selected.Any(f => f.Error is not null || f.Features is null || !CreditPolicy.HasSpanMetrics(f.Features))
                || selected.Zip(selected.Skip(1)).Any(p => p.Second.TimestampTicks - p.First.TimestampTicks > intervalTicks * 1.6)
                || selected[0].TimestampTicks - start > intervalTicks * 1.6 || media.TimelineEndTicks - selected[^1].TimestampTicks > intervalTicks * 1.6;
            candidates = CreditPolicy.Candidates(selected, configuration.Credits.DecisionProfile, start, media.TimelineEndTicks,
                configuration.Credits.SampleIntervalSeconds, out rejections);
            if (!incomplete && candidates.Count > 0) break;
        }
        AtomicFile.Write(cachePath, WireJson.Write(evidence.Values.ToList()));
        if (trimCache) TrimCache();
        var final = new List<SegmentCandidate>();
        foreach (var candidate in candidates)
        {
            var reasons = candidate.Reasons.Append("ProfileNotCalibrated").ToList();
            var rawEnd = candidate.RawEndTicks!.Value;
            var observedEnd = incomplete ? rawEnd : candidate.EndTicks;
            if (incomplete) reasons.Add("DecodeIncomplete");
            var adjustedStart = checked(candidate.StartTicks + (long)Math.Round(configuration.Credits.StartOffsetSeconds * TimeSpan.TicksPerSecond));
            var adjustedEnd = checked(observedEnd + (long)Math.Round(configuration.Credits.EndOffsetSeconds * TimeSpan.TicksPerSecond));
            if (adjustedStart < 0 || adjustedStart >= adjustedEnd || adjustedEnd > media.TimelineEndTicks)
            {
                reasons.Add("InvalidPlaybackOffset");
                adjustedStart = candidate.StartTicks;
                adjustedEnd = observedEnd;
            }
            final.Add(candidate with { StartTicks = adjustedStart, EndTicks = adjustedEnd,
                ReachesFileEnd = !incomplete && candidate.ReachesFileEnd && adjustedEnd == media.TimelineEndTicks,
                Reasons = reasons.Distinct().ToList() });
        }
        var warnings = rejections.Append("ExperimentalCreditsGroupPolicy").Distinct().ToList();
        if (final.Zip(final.Skip(1)).Any(p => p.First.EndTicks > p.Second.StartTicks))
        {
            warnings.Add("PlaybackOffsetConflict");
            final = final.Select((c, i) => c with { StartTicks = candidates[i].StartTicks,
                EndTicks = incomplete ? candidates[i].RawEndTicks!.Value : candidates[i].EndTicks,
                ReachesFileEnd = !incomplete && candidates[i].ReachesFileEnd,
                Reasons = c.Reasons.Append("PlaybackOffsetConflict").ToList() }).ToList();
        }
        if (incomplete) warnings.Add("DecodeIncomplete");
        return result with { Segments = final, SearchedRanges = [new(searchedStart, media.TimelineEndTicks)], Warnings = warnings,
            Metrics = new() { ["uniqueFramesInferred"] = uniqueFrames, ["analysisMilliseconds"] = clock.Elapsed.TotalMilliseconds,
                ["cachedCoarseFrames"] = evidence.Count, ["groupsRejected"] = rejections.Count, ["endingWindowsSearched"] = windowCount } };
    }

    internal void TrimCache()
    {
        var directory = Path.Combine(configuration.Paths.CacheDirectory, "evidence");
        if (!Directory.Exists(directory)) return;
        var files = Directory.EnumerateFiles(directory, "*.json").Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToList();
        var bytes = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (bytes <= configuration.Resources.MaximumCacheMegabytes * 1024L * 1024L) break;
            File.Delete(file.FullName);
            bytes -= file.Length;
        }
    }

    private JsonElement ModelIdentity()
    {
        if (configuration.Credits.ModelManifest is null) return JsonSerializer.SerializeToElement(new { missing = true });
        using var manifest = JsonDocument.Parse(File.ReadAllText(configuration.Credits.ModelManifest));
        return JsonSerializer.SerializeToElement(manifest.RootElement.EnumerateObject().Where(p => p.Name != "modelPath")
            .ToDictionary(p => p.Name, p => p.Value.Clone()));
    }
}
