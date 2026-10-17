using System.Net;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Export;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Engine.Jellyfin;

public sealed class ConnectedWorker(WorkerConfiguration configuration, WorkerStore store, string? credential = null,
    Action<WorkerConnectionStatus>? statusChanged = null)
{
    private readonly object statusLock = new();
    private WorkerConnectionStatus? lastStatus;

    public async Task WorkAsync(CancellationToken cancellationToken)
    {
        using var client = new RemoteClient(configuration, credential);
        ReportStatus(connected: false, scanning: false, nodeEnabled: null, error: null);
        NodeRuntimeInformation? runtime = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var capabilities = await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
                    var nodeRuntime = runtime;
                    if (nodeRuntime is null)
                    {
                        nodeRuntime = await NodeCapabilities.DiscoverAsync(configuration, cancellationToken).ConfigureAwait(false);
                        runtime = nodeRuntime;
                    }

                    var hello = NodeRegistration.CreateHello(configuration, nodeRuntime);
                    await client.HelloAsync(hello, cancellationToken).ConfigureAwait(false);
                    var nodeConfiguration = await client.ConfigurationAsync(hello.NodeId, cancellationToken).ConfigureAwait(false);
                    ValidateConfiguration(nodeConfiguration, hello);
                    ReportStatus(connected: true, scanning: false, nodeEnabled: nodeConfiguration.Node.Enabled, error: null);
                    using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var settingsEvents = client.WatchSettingsAsync(hello.NodeId, connectionCancellation.Token).GetAsyncEnumerator(connectionCancellation.Token);
                    var settingsNotice = settingsEvents.MoveNextAsync().AsTask();
                    var nextPresenceUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            if (settingsNotice.IsCompleted)
                            {
                                if (!await settingsNotice.ConfigureAwait(false)) throw new HttpRequestException("Remote settings event stream closed.");
                                var notice = settingsEvents.Current;
                                if (notice.NodeId != hello.NodeId) throw new InvalidDataException("Remote settings event has the wrong node identity.");
                                nodeConfiguration = await client.ConfigurationAsync(hello.NodeId, cancellationToken).ConfigureAwait(false);
                                ValidateConfiguration(nodeConfiguration, hello);
                                ReportStatus(connected: true, scanning: false, nodeEnabled: nodeConfiguration.Node.Enabled, error: null);
                                settingsNotice = settingsEvents.MoveNextAsync().AsTask();
                                continue;
                            }

                            if (DateTimeOffset.UtcNow >= nextPresenceUtc)
                            {
                                await client.HelloAsync(hello, cancellationToken).ConfigureAwait(false);
                                nextPresenceUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                            }

                            foreach (var pending in store.PendingSubmissions()) await PublishAsync(client, pending, cancellationToken).ConfigureAwait(false);
                            if (!nodeConfiguration.Node.Enabled)
                            {
                                await WaitForPollOrSettingsAsync(settingsNotice, nextPresenceUtc, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            var job = await client.ClaimAsync(cancellationToken).ConfigureAwait(false);
                            if (job is null)
                            {
                                await SyncAsync(client, capabilities.ServerId, cancellationToken).ConfigureAwait(false);
                                await WaitForPollOrSettingsAsync(settingsNotice, nextPresenceUtc, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            ValidateJob(job, nodeConfiguration, capabilities.ServerId);
                            using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var heartbeat = HeartbeatLoopAsync(client, job, nodeConfiguration.Node.Enabled, heartbeatCancellation.Token);
                            ReportStatus(connected: true, scanning: true, nodeEnabled: nodeConfiguration.Node.Enabled, error: null);
                            try
                            {
                                var result = await new ScanService(configuration, store).ScanAsync(job, cancellationToken).ConfigureAwait(false);
                                var output = Path.Combine(configuration.Paths.StateDirectory, "results", $"{job.JobId:D}-{job.AttemptId:D}.json");
                                AtomicFile.Write(output, WireJson.Write(result));
                                await Console.Error.WriteLineAsync(WireJson.Write(new { eventName = "ScanResult", jobId = job.JobId, path = output })).ConfigureAwait(false);
                                var submission = new PendingSubmission(job, result.Results);
                                store.SaveSubmission(submission);
                                await PublishAsync(client, submission, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
                                try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
                                ReportStatus(ScanningFinished(nodeConfiguration.Node.Enabled));
                                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                try { await client.ReleaseAsync(job, releaseTimeout.Token).ConfigureAwait(false); } catch (HttpRequestException) { }
                                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                            }
                        }
                    }
                    finally
                    {
                        await connectionCancellation.CancelAsync().ConfigureAwait(false);
                        try { await settingsNotice.ConfigureAwait(false); } catch (OperationCanceledException) { }
                        await settingsEvents.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException exception)
                {
                    ReportStatus(connected: false, scanning: false, nodeEnabled: null, error: "Jellyfin is unavailable.");
                    await Console.Error.WriteLineAsync(WireJson.Write(new { eventName = "RemoteRetry", httpStatus = (int?)exception.StatusCode })).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(configuration.Server!.PollSeconds), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally { ReportStatus(connected: false, scanning: false, nodeEnabled: null, error: null); }
    }

    private async Task WaitForPollOrSettingsAsync(Task<bool> settingsNotice, DateTimeOffset nextPresenceUtc, CancellationToken token)
    {
        var server = configuration.Server ?? throw new InvalidDataException("No Jellyfin server configured.");
        var untilPresence = nextPresenceUtc - DateTimeOffset.UtcNow;
        var pollInterval = TimeSpan.FromSeconds(server.PollSeconds);
        var delay = untilPresence <= TimeSpan.Zero ? TimeSpan.Zero : pollInterval < untilPresence ? pollInterval : untilPresence;
        var poll = Task.Delay(delay, token);
        await Task.WhenAny(settingsNotice, poll).ConfigureAwait(false);
    }

    private static void ValidateConfiguration(NodeConfiguration configuration, NodeHello hello)
    {
        if (configuration.Node.NodeId != hello.NodeId || configuration.Settings.Version != 1 || string.IsNullOrWhiteSpace(configuration.SettingsHash))
            throw new InvalidDataException("Remote node configuration is incomplete or belongs to another worker.");
    }

    private static void ValidateJob(JobSnapshot job, NodeConfiguration nodeConfiguration, string serverId)
    {
        ContractValidation.Job(job);
        var policy = job.NodePolicy ?? throw new InvalidDataException("Claimed job has no immutable node policy.");
        if ((job.Modes.Contains(RemoteAnalysisMode.Credits) && job.CreditsRecipe is null) || job.ScanSettings is null || job.NodeId != nodeConfiguration.Node.NodeId || job.NodeRevision != nodeConfiguration.Node.Revision
            || job.Modes.Any(mode => !policy.Modes.Contains(mode))
            || job.Items.Any(item => !policy.PathMappings.Any(mapping => NodeScope.MatchesPath(mapping.ServerPrefix, mapping.CaseSensitive, item.SourcePath))))
            throw new InvalidDataException("Claimed job does not match the server's immutable node configuration. No media was accessed.");
        if (job.ServerId != serverId || string.IsNullOrEmpty(job.LeaseToken) || job.LeaseExpiresUtc is null || job.LeaseExpiresUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Claim returned an invalid namespace/lease.");
    }

    public async Task<List<ExportOutcome>> SyncAsync(CancellationToken token)
    {
        using var client = new RemoteClient(configuration, credential);
        var capabilities = await client.HandshakeAsync(token).ConfigureAwait(false);
        return await SyncAsync(client, capabilities.ServerId, token).ConfigureAwait(false);
    }

    public async Task<List<ImportReceipt>> ReviewAsync(ResultBundle bundle, ReviewDocument review, CancellationToken token)
    {
        var requested = review.Decisions.Select(d => d.AnalysisId).Concat(review.AcceptNoSegments).ToHashSet();
        ContractValidation.Require(requested.Count > 0 && requested.All(id => bundle.Results.Count(r => r.AnalysisId == id) == 1),
            "Review references unknown or duplicate analyses.");
        var selected = bundle.Results.Where(r => requested.Contains(r.AnalysisId)).ToList();
        using var client = new RemoteClient(configuration, credential);
        var capabilities = await client.HandshakeAsync(token).ConfigureAwait(false);
        ContractValidation.Require(capabilities.SupportsHeldResultReview, "The installed bridge lacks held-result review. Install the matching development bridge package.");
        ContractValidation.Require(selected.All(r => r.ServerId == capabilities.ServerId), "Review results belong to another server. Run connected work to obtain that server's own proposals.");
        foreach (var result in selected)
        {
            ContractValidation.Result(result);
            if (result.MediaValidation is null) throw new InvalidDataException("Unverified media cannot be reviewed.");
            await new MediaProbe(configuration).ProbeAsync(new JobItem { ItemId = result.ItemId, MediaSourceKey = result.MediaSourceKey,
                SourcePath = result.SourcePath, PathMapId = result.PathMapId, FileVersionToken = result.FileVersionToken,
                ExpectedSignature = result.MediaValidation }, token).ConfigureAwait(false);
        }
        var receipts = new List<ImportReceipt>();
        foreach (var job in selected.GroupBy(r => r.JobId))
        {
            var ids = job.Select(r => r.AnalysisId).ToHashSet();
            var jobReview = review with { Decisions = review.Decisions.Where(d => ids.Contains(d.AnalysisId)).ToList(),
                AcceptNoSegments = review.AcceptNoSegments.Where(ids.Contains).ToList() };
            var current = await client.ReviewAsync(job.Key, new RemoteReviewRequest(job.ToList(), jobReview), token).ConfigureAwait(false);
            SaveReceipts(capabilities.ServerId, job.ToList(), current);
            receipts.AddRange(current);
        }
        await Console.Error.WriteLineAsync(WireJson.Write(new { eventName = "ReviewPublication",
            outcomes = receipts.Select(r => new { r.ItemId, r.Mode, r.Outcome, r.Reason }) })).ConfigureAwait(false);
        return receipts;
    }

    private async Task<List<ExportOutcome>> SyncAsync(RemoteClient client, string serverId, CancellationToken token)
    {
        var tracked = store.AllCanonical().Where(c => c.Result.ServerId == serverId).ToList();
        foreach (var batch in tracked.Select(c => c.Result.ItemId).Distinct().Chunk(100))
        {
            var revisions = await client.RevisionsAsync(batch.ToList(), token).ConfigureAwait(false);
            foreach (var id in batch.Where(id => revisions.Any(r => r.ItemId == id && !tracked.Any(c => c.Authority == "Server" && c.Result.ItemId == id && c.Result.Mode == r.Mode && c.ResultRevision == r.Revision))))
                foreach (var canonical in await client.CanonicalAsync(id, token).ConfigureAwait(false)) store.SaveServerCanonical(canonical);
        }
        return configuration.Exports.Enabled ? await new ExportService(configuration, store).ExportAsync(store.AllCanonical().Where(c => c.Result.ServerId == serverId && c.Authority == "Server").ToList(), true, true, true, token).ConfigureAwait(false) : [];
    }

    private async Task PublishAsync(RemoteClient client, PendingSubmission submission, CancellationToken token)
    {
        try
        {
            var receipts = await client.SubmitAsync(submission, token).ConfigureAwait(false);
            SaveReceipts(submission.Job.ServerId!, submission.Results, receipts);
            await Console.Error.WriteLineAsync(WireJson.Write(new { eventName = "Publication", jobId = submission.Job.JobId, outcomes = receipts.Select(r => new { r.ItemId, r.Mode, r.Outcome, r.Reason }) })).ConfigureAwait(false);
            store.RemoveSubmission(submission.Job.JobId);
            if (configuration.Exports.Enabled)
            {
                foreach (var itemId in receipts.Select(r => r.ItemId).Distinct())
                    foreach (var current in await client.CanonicalAsync(itemId, token).ConfigureAwait(false)) store.SaveServerCanonical(current);
                await SyncAsync(client, submission.Job.ServerId!, token).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Gone)
        {
            // Retain proposals/evidence, but expired publication permission must not block fresh work.
            store.RemoveSubmission(submission.Job.JobId);
            await Console.Error.WriteLineAsync(WireJson.Write(new { eventName = "ImportConflict", jobId = submission.Job.JobId, httpStatus = (int)exception.StatusCode })).ConfigureAwait(false);
        }
    }

    private void SaveReceipts(string serverId, IReadOnlyList<AnalysisResult> results, List<ImportReceipt> receipts)
    {
        if (receipts.Count != results.Count || receipts.Select(r => (r.AnalysisId, r.ItemId, r.Mode)).Distinct().Count() != receipts.Count
            || receipts.Any(r => !results.Any(v => v.AnalysisId == r.AnalysisId && v.ItemId == r.ItemId && v.Mode == r.Mode)))
            throw new InvalidDataException("Remote receipt batch does not match the submitted analyses.");
        foreach (var receipt in receipts)
            if (receipt.Current is { } current)
            {
                if (current.Result.ServerId != serverId || current.Result.ItemId != receipt.ItemId || current.Result.Mode != receipt.Mode)
                    throw new InvalidDataException("Remote canonical receipt has the wrong authority/item/mode.");
                store.SaveServerCanonical(current);
            }
    }

    private async Task HeartbeatLoopAsync(RemoteClient client, JobSnapshot job, bool nodeEnabled, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(configuration.Server!.HeartbeatSeconds), token).ConfigureAwait(false);
            try
            {
                await client.HeartbeatAsync(job, token).ConfigureAwait(false);
                ReportStatus(connected: true, scanning: true, nodeEnabled, error: null);
            }
            catch (HttpRequestException)
            {
                ReportStatus(connected: false, scanning: true, nodeEnabled, error: "Jellyfin is unavailable.");
                // Computation can finish offline; publication still rechecks the lease.
            }
        }
    }

    private WorkerConnectionStatus ScanningFinished(bool nodeEnabled)
    {
        lock (statusLock)
        {
            return (lastStatus ?? new WorkerConnectionStatus(true, false, nodeEnabled, null)) with { Scanning = false, NodeEnabled = nodeEnabled };
        }
    }

    private void ReportStatus(bool connected, bool scanning, bool? nodeEnabled, string? error) =>
        ReportStatus(new WorkerConnectionStatus(connected, scanning, nodeEnabled, error));

    private void ReportStatus(WorkerConnectionStatus status)
    {
        lock (statusLock)
        {
            if (status == lastStatus) return;
            lastStatus = status;
        }
        statusChanged?.Invoke(status);
    }
}

public sealed record WorkerConnectionStatus(bool Connected, bool Scanning, bool? NodeEnabled, string? Error);
