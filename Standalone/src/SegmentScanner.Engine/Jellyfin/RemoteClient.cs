using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Engine.Jellyfin;

public sealed class RemoteClient : IDisposable
{
    private const string LegacyPrefix = "IntroSkipper/Remote/v2/";
    private const string NodePrefix = "IntroSkipper/Remote/v3/";
    private readonly HttpClient client;
    private readonly ServerConfiguration configuration;

    public RemoteClient(WorkerConfiguration configuration, string? credential = null)
        : this(configuration.Server ?? throw new InvalidDataException("No server configured."), ResolveCredential(configuration, credential), true)
    {
    }

    public RemoteClient(ServerConfiguration configuration, string? credential = null)
        : this(configuration, credential ?? Environment.GetEnvironmentVariable(configuration.CredentialEnvironmentVariable), true)
    {
    }

    private RemoteClient(ServerConfiguration configuration, string? credential, bool _)
    {
        this.configuration = configuration;
        var token = credential;
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("The configured server credential environment variable is empty.");
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(configuration.BaseUrl.TrimEnd('/') + '/'), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{Uri.EscapeDataString(token)}\"");
    }

    private static string? ResolveCredential(WorkerConfiguration configuration, string? credential)
    {
        if (!string.IsNullOrWhiteSpace(credential)) return credential;
        var environment = Environment.GetEnvironmentVariable(configuration.Server!.CredentialEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(environment) ? environment : JellyfinCredentialStore.Load(configuration);
    }

    public async Task<RemoteCapabilities> HandshakeAsync(CancellationToken cancellationToken)
    {
        var capabilities = await SendAsync<RemoteCapabilities>(HttpMethod.Get, "capabilities", null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Missing remote bridge capabilities.");
        if (capabilities.ProtocolVersion != 3 || !capabilities.SupportsNodeConfiguration || capabilities.AnalysisContract != "segment-scanner-v1" || !capabilities.ServerVersion.StartsWith("12.1", StringComparison.Ordinal))
            throw new InvalidDataException("Install the matching node-configuration bridge and worker; this version requires remote protocol 3 on Jellyfin 12.1.");
        return capabilities;
    }

    public async Task<RemoteNodeInfo> HelloAsync(NodeHello hello, CancellationToken token) =>
        await SendV3Async<RemoteNodeInfo>(HttpMethod.Post, "nodes/hello", hello, token).ConfigureAwait(false)
        ?? throw new InvalidDataException("The bridge did not return the linked node.");

    public async Task<NodeConfiguration> ConfigurationAsync(string nodeId, CancellationToken token) =>
        await SendV3Async<NodeConfiguration>(HttpMethod.Get, $"nodes/{Uri.EscapeDataString(nodeId)}/configuration", null, token).ConfigureAwait(false)
        ?? throw new InvalidDataException("The bridge did not return node configuration.");

    public async IAsyncEnumerable<NodeSettingsNotice> WatchSettingsAsync(
        string nodeId,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, NodePrefix + $"nodes/{Uri.EscapeDataString(nodeId)}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Remote bridge returned HTTP {(int)response.StatusCode}. Check compatibility, scope, snapshot and lease.", null, response.StatusCode);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Remote settings event stream has an invalid content type.");

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                yield return ParseSettingsNotice(nodeId, data.ToString());
                data.Clear();
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
                if (data.Length > 64 * 1024) throw new InvalidDataException("Remote settings event exceeds the protocol limit.");
            }
        }
        throw new HttpRequestException("Remote settings event stream closed.");
    }

    private static NodeSettingsNotice ParseSettingsNotice(string nodeId, string data)
        => string.Equals(data.Trim(), "changed", StringComparison.Ordinal)
            ? new NodeSettingsNotice(nodeId, 0, string.Empty)
            : WireJson.Read<NodeSettingsNotice>(data);

    public async Task<List<RemoteLibrary>> LibrariesAsync(CancellationToken token) =>
        await SendAsync<List<RemoteLibrary>>(HttpMethod.Get, "libraries", null, token).ConfigureAwait(false) ?? [];
    public async Task<List<RemoteNodeInfo>> NodesAsync(CancellationToken token) =>
        await SendAsync<List<RemoteNodeInfo>>(HttpMethod.Get, "nodes", null, token).ConfigureAwait(false) ?? [];
    public async Task<RemoteNodeInfo> RegisterAsync(RemoteNodeRegistration registration, CancellationToken token)
    {
        RemoteNodeInfo registered;
        try
        {
            registered = await SendLegacyAsync<RemoteNodeInfo>(HttpMethod.Post, "nodes/register", registration, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The bridge did not return the registered node.");
        }
        catch (HttpRequestException exception) when (registration.AutomaticImports && exception.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidDataException("The bridge rejected automatic imports. Install the matching bridge before enabling this setting.", exception);
        }
        if (registration.AutomaticImports != registered.AutomaticImports)
            throw new InvalidDataException("The bridge did not retain this node's automatic-import setting. Install the matching bridge.");
        if (registration.CreditsMethod != registered.CreditsMethod)
            throw new InvalidDataException("The bridge did not retain this node's credits method. Install the matching bridge.");
        return registered;
    }

    public Task<JobSnapshot?> ClaimAsync(CancellationToken token) => SendAsync<JobSnapshot>(HttpMethod.Post, "jobs/claim", new ClaimRequest(configuration.WorkerId), token);
    public Task HeartbeatAsync(JobSnapshot job, CancellationToken token) => SendAsync<object>(HttpMethod.Post, $"jobs/{job.JobId}/heartbeat", new LeaseRequest(job.LeaseToken!, job.LeaseGeneration), token);
    public Task ReleaseAsync(JobSnapshot job, CancellationToken token) => SendAsync<object>(HttpMethod.Post, $"jobs/{job.JobId}/release", new LeaseRequest(job.LeaseToken!, job.LeaseGeneration), token);
    public async Task<List<ImportReceipt>> SubmitAsync(PendingSubmission submission, CancellationToken token) =>
        await SendAsync<List<ImportReceipt>>(HttpMethod.Post, $"jobs/{submission.Job.JobId}/results", new SubmitRequest(submission.Job.LeaseToken!, submission.Job.LeaseGeneration, submission.Results), token).ConfigureAwait(false) ?? [];
    public async Task<List<ImportReceipt>> ReviewAsync(Guid jobId, RemoteReviewRequest request, CancellationToken token) =>
        await SendLegacyAsync<List<ImportReceipt>>(HttpMethod.Post, $"jobs/{jobId}/review", request, token).ConfigureAwait(false) ?? [];
    public async Task<List<CanonicalResult>> CanonicalAsync(Guid itemId, CancellationToken token) =>
        await SendAsync<List<CanonicalResult>>(HttpMethod.Get, $"results/{itemId}", null, token).ConfigureAwait(false) ?? [];
    public async Task<List<RevisionResponse>> RevisionsAsync(List<Guid> itemIds, CancellationToken token) =>
        await SendAsync<List<RevisionResponse>>(HttpMethod.Post, "results/revisions", new RevisionRequest(itemIds), token).ConfigureAwait(false) ?? [];
    public void Dispose() => client.Dispose();

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken token) where T : class
        => await SendV3Async<T>(method, path, body, token).ConfigureAwait(false);

    private async Task<T?> SendV3Async<T>(HttpMethod method, string path, object? body, CancellationToken token) where T : class
        => await SendAsync<T>(NodePrefix, method, path, body, token).ConfigureAwait(false);

    private async Task<T?> SendLegacyAsync<T>(HttpMethod method, string path, object? body, CancellationToken token) where T : class
        => await SendAsync<T>(LegacyPrefix, method, path, body, token).ConfigureAwait(false);

    private async Task<T?> SendAsync<T>(string prefix, HttpMethod method, string path, object? body, CancellationToken token) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try { return await SendCoreAsync<T>(prefix, method, path, body, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("Remote bridge timed out; publication remains pending.", exception); }
    }

    private async Task<T?> SendCoreAsync<T>(string prefix, HttpMethod method, string path, object? body, CancellationToken token) where T : class
    {
        using var request = new HttpRequestMessage(method, prefix + path);
        if (body is not null) request.Content = new StringContent(WireJson.Write(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Remote bridge returned HTTP {(int)response.StatusCode}. Check compatibility, scope, snapshot and lease.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Remote response exceeds the protocol limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Remote response exceeds the protocol limit.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        return bytes.Length == 0 ? null : WireJson.Read<T>(Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
