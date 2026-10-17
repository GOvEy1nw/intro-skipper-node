using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Jellyfin;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Engine.Settings;

public sealed class SettingsServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string configurationPath;
    private readonly string session = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly WorkerConfiguration? runtimeConfiguration;
    private WorkerConfiguration configuration;
    private WorkerConfiguration fileConfiguration;
    private string? originalFile;
    private string? credential;
    private string? credentialOrigin;
    private ServerConfiguration? connectedServer;
    private List<RemoteLibrary> libraries = [];
    private RemoteNodeInfo? node;
    private CancellationTokenSource? workerCancellation;
    private Task? worker;
    private WorkerRuntimeStatus workerStatus = new(false, false, false, null, null, false);
    private bool disposing;

    private SettingsServer(WebApplication app, string path, WorkerConfiguration? runtimeConfiguration)
    {
        this.app = app;
        configurationPath = Path.GetFullPath(path);
        PathMapper.RejectLinks(configurationPath);
        originalFile = File.Exists(configurationPath) ? File.ReadAllText(configurationPath) : null;
        fileConfiguration = originalFile is null ? new WorkerConfiguration() : ConfigurationLoader.Load(configurationPath);
        this.runtimeConfiguration = runtimeConfiguration;
        runtimeConfiguration?.Validate();
        configuration = ApplyRuntimeBindings(fileConfiguration);
        RefreshCanStart();
    }

    public Uri Address { get; private set; } = null!;
    public string BrowserUrl => Address.AbsoluteUri + "#" + session;

    public static async Task<SettingsServer> StartAsync(string path, CancellationToken token, WorkerConfiguration? runtimeConfiguration = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 64 * 1024;
        });
        var app = builder.Build();
        var server = new SettingsServer(app, path, runtimeConfiguration);
        server.MapEndpoints();
        await app.StartAsync(token).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server.Address = new Uri(address);
        return server;
    }

    public Task WaitForShutdownAsync(CancellationToken token) => app.WaitForShutdownAsync(token);

    public async Task StartWorkerAsync(CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { StartWorkerCore(); }
        finally { changes.Release(); }
    }

    public async Task StopWorkerAsync(CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopWorkerCoreAsync().ConfigureAwait(false); }
        finally { changes.Release(); }
    }

    public WorkerRuntimeStatus GetWorkerStatus()
    {
        return Volatile.Read(ref workerStatus);
    }

    private void MapEndpoints()
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        using var stream = typeof(SettingsServer).Assembly.GetManifestResourceStream("SegmentScanner.Engine.Settings.index.html")!;
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd().Replace("__NONCE__", nonce, StringComparison.Ordinal);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
                || context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != Address.Port
                || (context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != Address.GetLeftPart(UriPartial.Authority)))
            {
                context.Response.StatusCode = 403;
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["X-Scanner-Session"].ToString()), Encoding.UTF8.GetBytes(session)))
            {
                context.Response.StatusCode = 401;
                return;
            }
            try { await next(context).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or IOException or UnauthorizedAccessException or HttpRequestException)
            {
                var message = exception is HttpRequestException
                    ? "Jellyfin could not be reached or rejected the connection. Check its address, API key and matching bridge version."
                    : exception.Message;
                context.Response.StatusCode = exception is IOException ? 409 : 400;
                await context.Response.WriteAsJsonAsync(new { error = message }, WireJson.Options, context.RequestAborted).ConfigureAwait(false);
            }
        });
        app.MapGet("/", () => Results.Content(html, "text/html; charset=utf-8"));
        app.MapGet("/api/settings", () => Results.Json(View(), WireJson.Options));
        app.MapPost("/api/connect", async (HttpContext context) =>
        {
            var request = await ReadAsync<ConnectionSettings>(context).ConfigureAwait(false);
            await changes.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                ThrowIfDisposing();
                RequireStopped();
                var existing = configuration.Server ?? new ServerConfiguration { BaseUrl = request.ServerUrl };
                var name = string.IsNullOrWhiteSpace(request.Name) ? existing.Node.Name : request.Name.Trim();
                var server = existing with
                {
                    BaseUrl = request.ServerUrl.TrimEnd('/'),
                    AllowInsecureHttp = request.AllowInsecureHttp,
                    Node = existing.Node with { Configured = true, Name = name },
                };
                var candidate = configuration with { Mode = "ConnectedWorker", Server = server };
                candidate.Validate();
                var key = ConnectionCredential(candidate, server, request.ApiKey)
                    ?? throw new InvalidDataException("Enter an API key for this Jellyfin server.");
                using var client = new RemoteClient(candidate, key);
                await client.HandshakeAsync(context.RequestAborted).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(request.ApiKey)) JellyfinCredentialStore.Save(candidate, request.ApiKey);
                await SaveConfigurationAsync(candidate, context.RequestAborted).ConfigureAwait(false);
                credential = key;
                credentialOrigin = JellyfinCredentialStore.Origin(server);
                connectedServer = configuration.Server;
                node = null;
                RefreshCanStart();
                return Results.Json(View(), WireJson.Options);
            }
            finally { changes.Release(); }
        });
        app.MapPost("/api/save", async (HttpContext context) =>
        {
            var request = await ReadAsync<LibrarySettings>(context).ConfigureAwait(false);
            await changes.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                ThrowIfDisposing();
                RequireStopped();
                var server = connectedServer ?? configuration.Server ?? throw new InvalidDataException("Connect to Jellyfin before saving.");
                var updated = configuration with
                {
                    Mode = "ConnectedWorker",
                    Server = server with { Node = server.Node with { Configured = true, Name = request.Name.Trim() } },
                };
                updated.Validate();
                await SaveConfigurationAsync(updated, context.RequestAborted).ConfigureAwait(false);
                connectedServer = configuration.Server;
                node = null;
                RefreshCanStart();
                return Results.Json(View(), WireJson.Options);
            }
            finally { changes.Release(); }
        });
        app.MapPost("/api/start", async (HttpContext context) =>
        {
            await changes.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                StartWorkerCore();
                return Results.Json(View(), WireJson.Options);
            }
            finally { changes.Release(); }
        });
        app.MapPost("/api/stop", async (HttpContext context) =>
        {
            await changes.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try { await StopWorkerCoreAsync().ConfigureAwait(false); return Results.Json(View(), WireJson.Options); }
            finally { changes.Release(); }
        });
        app.MapPost("/api/remove-credential", async (HttpContext context) =>
        {
            await changes.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                ThrowIfDisposing();
                var server = connectedServer ?? configuration.Server ?? throw new InvalidDataException("Connect to Jellyfin before removing its credential.");
                await StopWorkerCoreAsync().ConfigureAwait(false);
                JellyfinCredentialStore.Remove(configuration with { Server = server });
                credential = null;
                credentialOrigin = null;
                connectedServer = null;
                libraries = [];
                node = null;
                RefreshCanStart();
                return Results.Json(View(), WireJson.Options);
            }
            finally { changes.Release(); }
        });
    }

    private object View()
    {
        var status = GetWorkerStatus();
        return new
        {
            serverUrl = connectedServer?.BaseUrl ?? configuration.Server?.BaseUrl ?? "",
            allowInsecureHttp = connectedServer?.AllowInsecureHttp ?? configuration.Server?.AllowInsecureHttp ?? false,
            credentialAvailable = CredentialAvailable(),
            credentialPersistenceSupported = JellyfinCredentialStore.IsSupported,
            name = configuration.Server?.Node.Name ?? Environment.MachineName,
            libraryIds = Array.Empty<Guid>(),
            modes = Array.Empty<RemoteAnalysisMode>(),
            mappings = Array.Empty<PathMapping>(),
            creditsMethod = default(CreditsDetectionMethod),
            inferenceBackend = default(CreditInferenceBackend),
            videoDecoder = default(CreditVideoDecoder),
            concurrentFiles = 0,
            automaticImports = false,
            libraries,
            connected = connectedServer is not null,
            configured = configuration.Server?.Node.Configured ?? false,
            nodeEnabled = node?.Enabled,
            running = status.Running,
            workerError = status.Error,
            runtimeStatus = status,
        };
    }

    private void RequireStopped()
    {
        if (worker is { IsCompleted: false }) throw new IOException("Stop the scanner before changing its settings.");
    }

    private string? CredentialFor(ServerConfiguration server) => credentialOrigin == JellyfinCredentialStore.Origin(server) ? credential : null;

    private string? ConnectionCredential(WorkerConfiguration candidate, ServerConfiguration server, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        var remembered = CredentialFor(server);
        if (!string.IsNullOrWhiteSpace(remembered)) return remembered;
        if (configuration.Server is { } configured && JellyfinCredentialStore.Origin(configured) == JellyfinCredentialStore.Origin(server))
        {
            var environment = Environment.GetEnvironmentVariable(configured.CredentialEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environment)) return environment;
        }
        return JellyfinCredentialStore.Load(candidate);
    }

    private bool CredentialAvailable()
    {
        var server = connectedServer ?? configuration.Server;
        if (server is null) return false;
        return !string.IsNullOrWhiteSpace(CredentialFor(server)
            ?? Environment.GetEnvironmentVariable(server.CredentialEnvironmentVariable))
            || JellyfinCredentialStore.Exists(configuration with { Server = server });
    }

    private bool WorkerStartupCanStart()
    {
        try { return configuration.Server is not null && CredentialAvailable(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    private void RefreshCanStart()
    {
        var status = Volatile.Read(ref workerStatus);
        Volatile.Write(ref workerStatus, status with { CanStart = WorkerStartupCanStart() });
    }

    private WorkerConfiguration ApplyRuntimeBindings(WorkerConfiguration file) => runtimeConfiguration is null ? file : file with
    {
        Paths = file.Paths with
        {
            Ffmpeg = runtimeConfiguration.Paths.Ffmpeg,
            Ffprobe = runtimeConfiguration.Paths.Ffprobe,
            Python = runtimeConfiguration.Paths.Python,
            InferenceScript = runtimeConfiguration.Paths.InferenceScript,
        },
        Credits = file.Credits with { ModelManifest = runtimeConfiguration.Credits.ModelManifest },
    };

    private WorkerConfiguration RestoreFileBindings(WorkerConfiguration updated) => updated with
    {
        Paths = updated.Paths with
        {
            Ffmpeg = fileConfiguration.Paths.Ffmpeg,
            Ffprobe = fileConfiguration.Paths.Ffprobe,
            Python = fileConfiguration.Paths.Python,
            InferenceScript = fileConfiguration.Paths.InferenceScript,
        },
        Credits = updated.Credits with { ModelManifest = fileConfiguration.Credits.ModelManifest },
    };

    private async Task RequireUnchangedFileAsync(CancellationToken token)
    {
        PathMapper.RejectLinks(configurationPath);
        if ((File.Exists(configurationPath) ? await File.ReadAllTextAsync(configurationPath, token).ConfigureAwait(false) : null) != originalFile)
            throw new IOException("The settings file changed outside this window. Reopen settings before saving.");
    }

    private async Task SaveConfigurationAsync(WorkerConfiguration updated, CancellationToken token)
    {
        await RequireUnchangedFileAsync(token).ConfigureAwait(false);
        var json = WireJson.Write(RestoreFileBindings(updated));
        AtomicFile.Write(configurationPath, json);
        originalFile = json;
        fileConfiguration = ConfigurationLoader.Load(configurationPath);
        configuration = ApplyRuntimeBindings(fileConfiguration);
    }

    private async Task RunWorkerAsync(WorkerConfiguration saved, string? key, CancellationToken token)
    {
        try
        {
            using var store = new WorkerStore(saved.Paths.StateDirectory);
            await new ConnectedWorker(saved, store, key, UpdateWorkerStatus).WorkAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(WireJson.Write(new { eventName = "WorkerFailure", error = exception.ToString() })).ConfigureAwait(false);
            var status = Volatile.Read(ref workerStatus);
            Volatile.Write(ref workerStatus, status with { Error = WorkerError(exception) });
        }
        finally
        {
            var status = Volatile.Read(ref workerStatus);
            Volatile.Write(ref workerStatus, status with { Running = false, Connected = false, Scanning = false, NodeEnabled = null });
        }
    }

    private void StartWorkerCore()
    {
        ThrowIfDisposing();
        RequireStopped();
        workerCancellation?.Dispose();
        workerCancellation = new CancellationTokenSource();
        RefreshCanStart();
        Volatile.Write(ref workerStatus, new WorkerRuntimeStatus(true, false, false, null, null, GetWorkerStatus().CanStart));
        worker = RunWorkerAsync(configuration, CredentialFor(configuration.Server!), workerCancellation.Token);
    }

    private async Task StopWorkerCoreAsync()
    {
        var cancellation = workerCancellation;
        var runningWorker = worker;
        if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false);
        if (runningWorker is not null) await runningWorker.ConfigureAwait(false);
        if (ReferenceEquals(worker, runningWorker)) worker = null;
        if (ReferenceEquals(workerCancellation, cancellation)) workerCancellation = null;
        cancellation?.Dispose();
        var status = Volatile.Read(ref workerStatus);
        Volatile.Write(ref workerStatus, status with { Running = false, Connected = false, Scanning = false, NodeEnabled = null });
    }

    private void UpdateWorkerStatus(WorkerConnectionStatus status)
    {
        var current = Volatile.Read(ref workerStatus);
        Volatile.Write(ref workerStatus, new WorkerRuntimeStatus(true, status.Connected, status.Scanning, status.NodeEnabled, status.Error, current.CanStart));
    }

    private static string WorkerError(Exception exception) => exception switch
    {
        HttpRequestException => "Jellyfin is unavailable.",
        InvalidDataException => "The scanner configuration or Jellyfin response is invalid.",
        IOException or UnauthorizedAccessException => "The scanner cannot access its local state.",
        _ => "The scanner stopped unexpectedly.",
    };

    private void ThrowIfDisposing()
    {
        if (disposing) throw new IOException("The settings server is shutting down.");
    }

    private static async Task<T> ReadAsync<T>(HttpContext context) where T : class =>
        await context.Request.ReadFromJsonAsync<T>(WireJson.Options, context.RequestAborted).ConfigureAwait(false)
            ?? throw new InvalidDataException("Empty settings request.");

    public async ValueTask DisposeAsync()
    {
        await changes.WaitAsync().ConfigureAwait(false);
        try
        {
            disposing = true;
            await StopWorkerCoreAsync().ConfigureAwait(false);
        }
        finally { changes.Release(); }
        await app.DisposeAsync().ConfigureAwait(false);
        changes.Dispose();
    }
}

public sealed record ConnectionSettings(string ServerUrl, string? ApiKey, bool AllowInsecureHttp, string? Name = null);
public sealed record LibrarySettings(string Name, List<Guid> LibraryIds, List<RemoteAnalysisMode> Modes, List<PathMapping> Mappings,
    CreditInferenceBackend InferenceBackend = CreditInferenceBackend.Cpu, CreditVideoDecoder VideoDecoder = CreditVideoDecoder.Cpu, int ConcurrentFiles = 2, bool AutomaticImports = false,
    CreditsDetectionMethod CreditsMethod = CreditsDetectionMethod.TextAware);
