using System.Text.Json;
using System.Text.Json.Serialization;
using IntroSkipper.Configuration;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Credits;
using SegmentScanner.Engine.Introductions;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Engine;

public sealed class ScanService(WorkerConfiguration configuration, WorkerStore store)
{
    private static readonly JsonSerializerOptions ScanSettingsOptions = new(WireJson.Options)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<ResultBundle> ScanAsync(JobSnapshot job, CancellationToken cancellationToken)
    {
        ContractValidation.Job(job);
        var effectiveConfiguration = ConfigurationFor(job);
        effectiveConfiguration.Validate();
        if (job.ServerId is null && job.Modes.Contains(RemoteAnalysisMode.Credits))
            job = job with { CreditsMethod = effectiveConfiguration.Credits.Method };
        if (job.Modes.Contains(RemoteAnalysisMode.Credits) && job.ServerId is not null
            && (job.CreditsMethod != effectiveConfiguration.Credits.Method || job.CreditsProfileHash != new CreditsDetector(effectiveConfiguration).ProfileHash()))
            throw new InvalidDataException("Credits profile differs from the immutable job snapshot. Run doctor and match the server's RemoteCreditsProfileHash before retrying.");
        Directory.CreateDirectory(effectiveConfiguration.Paths.CacheDirectory);
        FrameExtractor.CleanupOwnedTemporary(effectiveConfiguration);
        if (job.IntroductionSettings.ValueKind != System.Text.Json.JsonValueKind.Object || !job.IntroductionSettings.EnumerateObject().Any())
        {
            if (job.ServerId is not null && job.Modes.Contains(RemoteAnalysisMode.Introduction)) throw new InvalidDataException("Connected jobs require their server's effective introduction settings.");
            job = job with { IntroductionSettings = IntroductionDetector.CreateDefaultSettings() };
        }
        var introHash = IntroductionDetector.ProfileHash(job.IntroductionSettings);
        if (job.IntroductionProfileHash.Length == 0) job = job with { IntroductionProfileHash = introHash };
        if (job.ServerId is null)
        {
            job = job with { Items = job.Items.Select(item => item with { ExpectedModeRevisions = job.Modes.ToDictionary(mode => mode, mode =>
                (store.GetCanonical(ResultFactory.Create(job, item, mode, AnalysisStatus.Interrupted, new() { Id = "snapshot", ProfileHash = "" }))?.ResultRevision ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)) }).ToList() };
        }
        var media = new List<ResolvedMedia>();
        var results = new List<AnalysisResult>();
        foreach (var item in job.Items)
        {
            try
            {
                var resolved = await new MediaProbe(effectiveConfiguration).ProbeAsync(item, cancellationToken).ConfigureAwait(false);
                media.Add(MediaProbe.ApplyGeneratedChapterGuard(resolved, store.HasChapterExport(job.ServerId, item)));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                if (!item.WriteTarget) continue;
                foreach (var mode in job.Modes)
                {
                    results.Add(ResultFactory.Create(job, item, mode, exception is NotSupportedException ? AnalysisStatus.SkippedUnsupported : AnalysisStatus.Failed,
                        new() { Id = mode == RemoteAnalysisMode.Introduction ? "introskipper-baseline" : effectiveConfiguration.Credits.ProfileId, ProfileHash = mode == RemoteAnalysisMode.Introduction ? introHash : "unavailable" })
                        with { Warnings = [exception.Message] });
                }
            }
        }
        if (job.Modes.Contains(RemoteAnalysisMode.Introduction) && media.Count > 0)
        {
            var availableJob = job with { Items = media.Select(m => m.Item).ToList() };
            var introductions = await new IntroductionDetector(effectiveConfiguration).AnalyzeAsync(availableJob, media, cancellationToken).ConfigureAwait(false);
            var unavailableSeasons = job.Items.Where(i => !media.Any(m => m.Item.ItemId == i.ItemId)).Select(i => i.SeasonKey).ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < introductions.Count; index++)
                if (introductions[index].Status == AnalysisStatus.CompletedNoSegments && unavailableSeasons.Contains(job.Items.Single(i => i.ItemId == introductions[index].ItemId).SeasonKey))
                    introductions[index] = introductions[index] with { Status = AnalysisStatus.NeedsReview, Warnings = ["ComparisonMediaUnavailable"] };
            results.AddRange(introductions);
            store.SaveProposals(introductions);
        }
        foreach (var mode in job.Modes.Where(mode => mode is RemoteAnalysisMode.Recap or RemoteAnalysisMode.Preview or RemoteAnalysisMode.Commercial))
        {
            var pluginResults = await new PluginModeDetector(effectiveConfiguration).AnalyzeAsync(job, media, mode, cancellationToken).ConfigureAwait(false);
            results.AddRange(pluginResults);
            store.SaveProposals(pluginResults);
        }
        if (job.Modes.Contains(RemoteAnalysisMode.Credits) && effectiveConfiguration.Credits.Method == CreditsDetectionMethod.IntroSkipper)
        {
            var original = await new IntroSkipperCreditsDetector(effectiveConfiguration).AnalyzeAsync(job, media, cancellationToken).ConfigureAwait(false);
            results.AddRange(original);
            store.SaveProposals(original);
        }
        else if (job.Modes.Contains(RemoteAnalysisMode.Credits))
        {
            var targets = media.Where(m => m.Item.WriteTarget).ToList();
            // Identical media share an evidence key, so keep those accesses on one worker.
            var groups = targets.Select((item, index) => (item, index)).GroupBy(entry => WireJson.Hash(entry.item.Signature))
                .OrderByDescending(group => group.First().item.TimelineEndTicks).Select(group => group.ToList()).ToList();
            var workerCount = Math.Min(effectiveConfiguration.Resources.ConcurrentFiles, groups.Count);
            var completed = new AnalysisResult[targets.Count];
            var persistence = new object();
            var nextGroup = -1;
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var limited = effectiveConfiguration with { Resources = effectiveConfiguration.Resources with
                { MaximumTemporaryGigabytes = effectiveConfiguration.Resources.MaximumTemporaryGigabytes / Math.Max(1, workerCount) } };
            var detector = new CreditsDetector(limited);
            try
            {
                await Task.WhenAll(Enumerable.Range(0, workerCount).Select(async _ =>
                {
                    InferenceHelper? helper = null;
                    string? dependencyFailure = null;
                    try
                    {
                        try { helper = await InferenceHelper.StartAsync(limited, stopped.Token).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is InvalidDataException or IOException or System.ComponentModel.Win32Exception)
                        { dependencyFailure = exception.Message; }
                        int groupIndex;
                        while ((groupIndex = Interlocked.Increment(ref nextGroup)) < groups.Count)
                        {
                            foreach (var (item, index) in groups[groupIndex])
                            {
                                stopped.Token.ThrowIfCancellationRequested();
                                AnalysisResult result;
                                try
                                {
                                    result = helper is null ? Failure(item, dependencyFailure ?? "Inference unavailable.")
                                        : await detector.AnalyzeAsync(job, item, helper, stopped.Token, trimCache: false).ConfigureAwait(false);
                                }
                                catch (Exception exception) when (exception is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
                                { result = Failure(item, exception.Message); }
                                lock (persistence)
                                {
                                    stopped.Token.ThrowIfCancellationRequested();
                                    store.SaveProposals([result]);
                                    completed[index] = result;
                                }
                            }
                        }
                    }
                    catch
                    {
                        await stopped.CancelAsync().ConfigureAwait(false);
                        throw;
                    }
                    finally { if (helper is not null) await helper.DisposeAsync().ConfigureAwait(false); }
                })).ConfigureAwait(false);
                results.AddRange(completed);
            }
            finally { detector.TrimCache(); }

            AnalysisResult Failure(ResolvedMedia item, string message) => ResultFactory.Create(job, item.Item, RemoteAnalysisMode.Credits, AnalysisStatus.Failed,
                new() { Id = effectiveConfiguration.Credits.ProfileId, ProfileHash = "unavailable" }, item) with { Warnings = [message] };
        }
        foreach (var item in media)
        {
            if (await MediaProbe.UnchangedAsync(item, cancellationToken).ConfigureAwait(false)) continue;
            for (var index = 0; index < results.Count; index++)
                if (results[index].ItemId == item.Item.ItemId) results[index] = results[index] with { Status = AnalysisStatus.Failed, Segments = [], Warnings = ["Media changed during analysis; proposals invalidated."] };
        }
        foreach (var result in results) ContractValidation.Result(result);
        store.SaveProposals(results);
        return new ResultBundle { Results = results };
    }

    private WorkerConfiguration ConfigurationFor(JobSnapshot job)
    {
        if (job.ServerId is null) return configuration;
        var scansCredits = job.Modes.Contains(RemoteAnalysisMode.Credits);
        if (job.NodePolicy is null || job.ScanSettings is null || (scansCredits && job.CreditsRecipe is null))
            throw new InvalidDataException("Connected jobs require immutable node policy and scan settings; credits jobs also require their recipe.");

        var policy = job.NodePolicy!;
        var recipe = job.CreditsRecipe;
        var scanSettings = job.ScanSettings!;
        if (job.Modes.Any(mode => !policy.Modes.Contains(mode)) || (scansCredits && (job.CreditsMethod != policy.CreditsMethod
            || recipe!.Method != policy.CreditsMethod || recipe.UseGpu != policy.UseGpu || job.CreditsProfileHash != recipe.ProfileHash)))
            throw new InvalidDataException("Connected job policy does not match its immutable credits recipe.");

        var sharedSettings = JsonSerializer.Deserialize<PluginConfiguration>(scanSettings.Configuration.GetRawText(), ScanSettingsOptions)
            ?? throw new InvalidDataException("Connected job scan settings are empty.");
        var mappings = policy.PathMappings.Select(mapping => mapping with { }).ToList();
        var credits = recipe is null ? configuration.Credits : configuration.Credits with
        {
            Method = recipe.Method,
            UseGpu = recipe.UseGpu,
            InferenceBackend = recipe.UseGpu ? recipe.InferenceBackend : CreditInferenceBackend.Cpu,
            VideoDecoder = recipe.UseGpu ? recipe.VideoDecoder : CreditVideoDecoder.Cpu,
        };
        return configuration with
        {
            Paths = configuration.Paths with
            {
                Mappings = mappings,
                AllowedMediaRoots = mappings.Select(mapping => mapping.WorkerPrefix).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            },
            Resources = configuration.Resources with
            {
                ConcurrentFiles = policy.ConcurrentFiles,
                FfmpegThreads = Math.Clamp(sharedSettings.ProcessThreads > 0 ? sharedSettings.ProcessThreads : configuration.Resources.FfmpegThreads, 1, 32),
                ProcessTimeoutSeconds = sharedSettings.ScanTimeoutSeconds,
                InferenceTimeoutSeconds = sharedSettings.ScanTimeoutSeconds,
            },
            Credits = credits,
        };
    }
}
