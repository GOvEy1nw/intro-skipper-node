using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SegmentScanner.Contracts;
using SegmentScanner.Engine;
using SegmentScanner.Engine.Jellyfin;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;
using SegmentScanner.Engine.Settings;
using Xunit;

namespace SegmentScanner.Tests;

public sealed class NodeSettingsTests
{
    [Fact]
    public async Task QueueControlHeaderEnablesLeaseProgressAndSurfacesCancellation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var bridge = builder.Build();
        var progress = new TaskCompletionSource<ScannerQueueProgressRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = 0;
        bridge.MapGet("/IntroSkipper/Remote/v3/capabilities", (HttpContext context) =>
        {
            context.Response.Headers.Append("X-IntroSkipper-Queue-Control", "1");
            return Results.Json(new RemoteCapabilities
            {
                ServerId = "queue-test", ServerVersion = "12.2.0", PluginVersion = "12.0.5.0", ProtocolVersion = 3,
                AnalysisContract = "segment-scanner-v1", SupportsNodeConfiguration = true,
            }, WireJson.Options);
        });
        bridge.MapPost("/IntroSkipper/Remote/v3/jobs/{jobId:guid}/progress", async (HttpRequest request) =>
        {
            if (Interlocked.Increment(ref reports) > 1) return Results.StatusCode((int)HttpStatusCode.Conflict);
            progress.TrySetResult((await request.ReadFromJsonAsync<ScannerQueueProgressRequest>(WireJson.Options, timeout.Token).ConfigureAwait(false))!);
            return Results.NoContent();
        });
        await bridge.StartAsync(timeout.Token);
        var bridgeAddress = bridge.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var variable = "SEGMENTSCANNER_QUEUE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "queue-test-key");
        try
        {
            using var client = new RemoteClient(new ServerConfiguration { BaseUrl = bridgeAddress, AllowInsecureHttp = true, CredentialEnvironmentVariable = variable });
            await client.HandshakeAsync(timeout.Token);
            Assert.True(client.SupportsQueueControl);
            var job = new JobSnapshot { JobId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), Items = [], LeaseToken = "lease", LeaseGeneration = 3 };
            var itemId = Guid.NewGuid();
            await client.ProgressAsync(job, [new ScannerQueueActivity(itemId, RemoteAnalysisMode.Credits, "TextCredits")], [], timeout.Token);
            var reported = await progress.Task.WaitAsync(timeout.Token);
            Assert.Equal(job.LeaseToken, reported.Lease.LeaseToken);
            Assert.Equal(job.LeaseGeneration, reported.Lease.LeaseGeneration);
            Assert.Equal(itemId, Assert.Single(reported.Active).ItemId);
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ProgressAsync(job, [], [itemId], timeout.Token));
            Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            await bridge.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task QueueControlConflictCancelsOnlyTheClaimAndKeepsPolling()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var workerCancellation = new CancellationTokenSource();
        var directory = Path.Combine(Path.GetTempPath(), "segmentscanner-queue-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var bridge = builder.Build();
        var nodeId = Environment.MachineName;
        var itemId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var mapping = new PathMapping { ServerPrefix = @"C:\queue-worker", WorkerPrefix = @"C:\queue-worker" };
        var node = new RemoteNodeInfo { NodeId = nodeId, Name = "Queue worker", Modes = [RemoteAnalysisMode.Introduction],
            ServerPaths = [new() { Prefix = mapping.ServerPrefix }], Enabled = true, Revision = 1, UpdatedUtc = DateTimeOffset.UtcNow };
        var settings = new JobScanSettings { Version = 1, Configuration = JsonSerializer.SerializeToElement(new { }), Action = "Default" };
        var job = new JobSnapshot
        {
            JobId = jobId,
            AttemptId = Guid.NewGuid(),
            ServerId = "queue-worker-test",
            NodeId = nodeId,
            NodeRevision = node.Revision,
            LeaseToken = "lease",
            LeaseGeneration = 1,
            LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            Modes = [RemoteAnalysisMode.Introduction],
            IntroductionSettings = JsonSerializer.SerializeToElement(new { }),
            ScanSettings = settings,
            NodePolicy = new RemoteNodePolicy { Modes = [RemoteAnalysisMode.Introduction], ConcurrentFiles = 1, PathMappings = [mapping] },
            Items = [new JobItem { ItemId = itemId, MediaSourceKey = "queue-worker-item", SourcePath = @"C:\queue-worker\episode.mkv",
                FileVersionToken = "1", RuntimeTicks = TimeSpan.TicksPerSecond, ExpectedModeRevisions = new() { [RemoteAnalysisMode.Introduction] = "0" } }],
        };
        var progress = new TaskCompletionSource<ScannerQueueProgressRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextClaim = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = 0;
        var releases = 0;
        var submissions = 0;
        Task? work = null;
        bridge.MapGet("/IntroSkipper/Remote/v3/capabilities", (HttpContext context) =>
        {
            context.Response.Headers.Append("X-IntroSkipper-Queue-Control", "1");
            return Results.Json(new RemoteCapabilities
            {
                ServerId = "queue-worker-test", ServerVersion = "12.2.0", PluginVersion = "12.0.5.0", ProtocolVersion = 3,
                AnalysisContract = "segment-scanner-v1", SupportsNodeConfiguration = true,
            }, WireJson.Options);
        });
        bridge.MapPost("/IntroSkipper/Remote/v3/nodes/hello", () => Results.Json(node, WireJson.Options));
        bridge.MapGet("/IntroSkipper/Remote/v3/nodes/{nodeId}/configuration", () => Results.Json(new NodeConfiguration(node, settings, "queue-worker-settings"), WireJson.Options));
        bridge.MapGet("/IntroSkipper/Remote/v3/nodes/{nodeId}/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
        });
        bridge.MapPost("/IntroSkipper/Remote/v3/jobs/claim", () =>
        {
            if (Interlocked.Increment(ref claims) == 1) return Results.Json(job, WireJson.Options);
            nextClaim.TrySetResult(true);
            return Results.NoContent();
        });
        bridge.MapPost("/IntroSkipper/Remote/v3/jobs/{jobId:guid}/progress", async (HttpRequest request) =>
        {
            progress.TrySetResult((await request.ReadFromJsonAsync<ScannerQueueProgressRequest>(WireJson.Options, timeout.Token).ConfigureAwait(false))!);
            return Results.StatusCode((int)HttpStatusCode.Conflict);
        });
        bridge.MapPost("/IntroSkipper/Remote/v3/jobs/{jobId:guid}/release", () =>
        {
            Interlocked.Increment(ref releases);
            return Results.NoContent();
        });
        bridge.MapPost("/IntroSkipper/Remote/v3/jobs/{jobId:guid}/results", () =>
        {
            Interlocked.Increment(ref submissions);
            return Results.NoContent();
        });
        bridge.MapPost("/IntroSkipper/Remote/v3/results/revisions", () => Results.Json(new List<RevisionResponse>(), WireJson.Options));
        await bridge.StartAsync(timeout.Token);
        var bridgeAddress = bridge.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var configuration = new WorkerConfiguration
        {
            Mode = "ConnectedWorker",
            Paths = new WorkerPaths { StateDirectory = Path.Combine(directory, "state"), CacheDirectory = Path.Combine(directory, "cache"), Mappings = [mapping] },
            Server = new ServerConfiguration
            {
                BaseUrl = bridgeAddress,
                AllowInsecureHttp = true,
                PollSeconds = 1,
                CredentialEnvironmentVariable = "unused-by-injected-credential",
                Node = new NodeSelectionConfiguration { Configured = true, Name = node.Name, Modes = [RemoteAnalysisMode.Introduction] },
            },
        };
        try
        {
            using var store = new WorkerStore(configuration.Paths.StateDirectory);
            var worker = new ConnectedWorker(configuration, store, "queue-worker-key", scan: async (_, activity, token) =>
            {
                using var scope = activity.Begin(itemId, RemoteAnalysisMode.Introduction, "Fixture");
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    throw new InvalidOperationException("The queue conflict should cancel the claimed scan.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    scanCancelled.TrySetResult(true);
                    throw;
                }
            });
            work = worker.WorkAsync(workerCancellation.Token);
            var reported = await progress.Task.WaitAsync(timeout.Token);
            Assert.Equal(itemId, Assert.Single(reported.Active).ItemId);
            await scanCancelled.Task.WaitAsync(timeout.Token);
            await nextClaim.Task.WaitAsync(timeout.Token);
            Assert.Empty(store.PendingSubmissions());
            Assert.Equal(0, Volatile.Read(ref releases));
            Assert.Equal(0, Volatile.Read(ref submissions));
            await workerCancellation.CancelAsync();
            try { await work.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested) { }
        }
        finally
        {
            await workerCancellation.CancelAsync();
            if (work is not null)
            {
                try { await work.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested) { }
            }
            await bridge.StopAsync(timeout.Token);
            PathMapper.EnsureWithin(directory, Path.GetTempPath());
            PathMapper.RejectLinks(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublicAndApiWorkerControlsShareConnectionStatus()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var directory = Path.Combine(Path.GetTempPath(), "segmentscanner-worker-status-" + Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(directory, "worker.json");
        var environmentVariable = "SEGMENTSCANNER_WORKER_STATUS_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var bridge = builder.Build();
        var firstRegistration = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsReloaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = 0;
        var configurationReads = 0;
        var eventStreams = 0;
        var node = new RemoteNodeInfo { NodeId = Environment.MachineName, Name = "Status test", Enabled = false, Revision = 1,
            UpdatedUtc = DateTimeOffset.UtcNow };
        var scanSettings = new JobScanSettings { Version = 1, Configuration = JsonSerializer.SerializeToElement(new { }), Action = "Default" };
        bridge.MapGet("/IntroSkipper/Remote/v3/capabilities", () => Results.Json(new RemoteCapabilities
        {
            ServerId = "worker-status-test", ServerVersion = "12.1.0", PluginVersion = "12.0.5.0", ProtocolVersion = 3,
            AnalysisContract = "segment-scanner-v1", SupportsNodeConfiguration = true,
        }, WireJson.Options));
        bridge.MapPost("/IntroSkipper/Remote/v3/nodes/hello", (HttpContext context) =>
        {
            var attempt = Interlocked.Increment(ref registrations);
            if (attempt == 1) firstRegistration.TrySetResult(true);
            if (attempt == 2) reconnected.TrySetResult(true);
            if (attempt == 3) restarted.TrySetResult(true);
            return Results.Json(node, WireJson.Options);
        });
        bridge.MapGet("/IntroSkipper/Remote/v3/nodes/{nodeId}/configuration", () =>
        {
            if (Interlocked.Increment(ref configurationReads) == 2) settingsReloaded.TrySetResult(true);
            return Results.Json(new NodeConfiguration(node, scanSettings, "status-test-settings"), WireJson.Options);
        });
        bridge.MapGet("/IntroSkipper/Remote/v3/nodes/{nodeId}/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            if (Interlocked.Increment(ref eventStreams) == 1)
            {
                await context.Response.WriteAsync("event: settings\ndata: changed\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                retryRequested.TrySetResult(true);
                await allowDisconnect.Task.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
        });
        await bridge.StartAsync(timeout.Token);
        var bridgeAddress = bridge.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        AtomicFile.Write(configurationPath, WireJson.Write(new WorkerConfiguration
        {
            Mode = "ConnectedWorker",
            Paths = new WorkerPaths
            {
                StateDirectory = Path.Combine(directory, "state"),
                Ffmpeg = Path.Combine(directory, "missing-ffmpeg"),
            },
            Server = new ServerConfiguration
            {
                BaseUrl = bridgeAddress,
                AllowInsecureHttp = true,
                CredentialEnvironmentVariable = environmentVariable,
                PollSeconds = 1,
                Node = new NodeSelectionConfiguration { Configured = true, Modes = [], LibraryIds = [] },
            },
        }));
        Environment.SetEnvironmentVariable(environmentVariable, "status-test-key");
        try
        {
            await using var settings = await SettingsServer.StartAsync(configurationPath, timeout.Token);
            using var client = new HttpClient { BaseAddress = settings.Address };
            client.DefaultRequestHeaders.Add("X-Scanner-Session", new Uri(settings.BrowserUrl).Fragment[1..]);

            Assert.True(settings.GetWorkerStatus().CanStart);
            await settings.StartWorkerAsync(timeout.Token);
            while (!firstRegistration.Task.IsCompleted && !timeout.IsCancellationRequested)
            {
                var starting = settings.GetWorkerStatus();
                Assert.True(starting.Running, starting.Error ?? "Worker stopped before hello.");
                await Task.Delay(25, timeout.Token);
            }
            await firstRegistration.Task.WaitAsync(timeout.Token);
            var connected = await WaitForWorkerStatusAsync(settings, status => status.Running && status.Connected && !status.Scanning && status.NodeEnabled == false, timeout.Token);
            Assert.Null(connected.Error);
            await settingsReloaded.Task.WaitAsync(timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            Assert.Equal(2, Volatile.Read(ref configurationReads));
            Assert.Equal(1, Volatile.Read(ref eventStreams));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("api/start", null, timeout.Token)).StatusCode);

            await retryRequested.Task.WaitAsync(timeout.Token);
            allowDisconnect.TrySetResult(true);
            var disconnected = await WaitForWorkerStatusAsync(settings, status => status.Running && !status.Connected && status.NodeEnabled is null, timeout.Token);
            Assert.Equal("Jellyfin is unavailable.", disconnected.Error);

            await reconnected.Task.WaitAsync(timeout.Token);
            var reconnectedStatus = await WaitForWorkerStatusAsync(settings, status => status.Running && status.Connected && status.NodeEnabled == false, timeout.Token);
            Assert.Null(reconnectedStatus.Error);
            (await client.PostAsync("api/stop", null, timeout.Token)).EnsureSuccessStatusCode();
            Assert.False(settings.GetWorkerStatus().Running);

            (await client.PostAsync("api/start", null, timeout.Token)).EnsureSuccessStatusCode();
            await restarted.Task.WaitAsync(timeout.Token);
            await settings.StopWorkerAsync(timeout.Token);
            Assert.False(settings.GetWorkerStatus().Running);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, null);
            await bridge.StopAsync(timeout.Token);
            PathMapper.EnsureWithin(directory, Path.GetTempPath());
            PathMapper.RejectLinks(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalSettingsKeepCredentialsPrivateAndPersistConnectionName()
    {
        const string key = "node-settings-private-test-key";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var directory = Path.Combine(Path.GetTempPath(), "segmentscanner-settings-" + Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(directory, "worker.json");
        var environmentVariable = "SEGMENTSCANNER_SETTINGS_TEST_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var bridge = builder.Build();
        var remoteRequests = 0;
        bridge.Use(async (context, next) =>
        {
            remoteRequests++;
            Assert.Contains(key, context.Request.Headers.Authorization.ToString(), StringComparison.Ordinal);
            await next(context);
        });
        bridge.MapGet("/IntroSkipper/Remote/v3/capabilities", () => Results.Json(new RemoteCapabilities
        {
            ServerId = "local-test", ServerVersion = "12.1.0", PluginVersion = "12.0.5.0", ProtocolVersion = 3,
            AnalysisContract = "segment-scanner-v1", SupportsNodeConfiguration = true,
        }, WireJson.Options));
        await bridge.StartAsync(timeout.Token);
        var bridgeAddress = bridge.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        AtomicFile.Write(configurationPath, WireJson.Write(new WorkerConfiguration
        {
            Paths = new WorkerPaths { StateDirectory = Path.Combine(directory, "state") },
            Server = new ServerConfiguration
            {
                BaseUrl = bridgeAddress,
                AllowInsecureHttp = true,
                CredentialEnvironmentVariable = environmentVariable,
            },
        }));
        Environment.SetEnvironmentVariable(environmentVariable, "environment-sentinel");
        try
        {
            await using var settings = await SettingsServer.StartAsync(configurationPath, timeout.Token);
            using var client = new HttpClient { BaseAddress = settings.Address };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/settings", timeout.Token)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Scanner-Session", new Uri(settings.BrowserUrl).Fragment[1..]);
            var connection = await client.PostAsJsonAsync("api/connect", new ConnectionSettings(bridgeAddress, key, true, "Lounge PC"), WireJson.Options, timeout.Token);
            connection.EnsureSuccessStatusCode();
            var body = await connection.Content.ReadAsStringAsync(timeout.Token);
            Assert.DoesNotContain(key, body, StringComparison.Ordinal);
            Assert.DoesNotContain(key, await File.ReadAllTextAsync(configurationPath, timeout.Token), StringComparison.Ordinal);
            Assert.Equal("Lounge PC", ConfigurationLoader.Load(configurationPath).Server!.Node.Name);

            var differentOrigin = bridgeAddress.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
            var requestsBeforeDifferentOrigin = remoteRequests;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("api/connect", new ConnectionSettings(differentOrigin, null, true), WireJson.Options, timeout.Token)).StatusCode);
            Assert.Equal(requestsBeforeDifferentOrigin, remoteRequests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, null);
            await bridge.StopAsync(timeout.Token);
            PathMapper.EnsureWithin(directory, Path.GetTempPath());
            PathMapper.RejectLinks(directory);
            Directory.Delete(directory, recursive: true);
        }
    }
    private static async Task<WorkerRuntimeStatus> WaitForWorkerStatusAsync(SettingsServer settings, Func<WorkerRuntimeStatus, bool> condition, CancellationToken token)
    {
        while (true)
        {
            var status = settings.GetWorkerStatus();
            if (condition(status)) return status;
            await Task.Delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
        }
    }
}
