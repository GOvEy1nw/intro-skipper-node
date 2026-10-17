using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;

namespace SegmentScanner.Engine.Credits;

public sealed class InferenceHelper : IAsyncDisposable
{
    private readonly Process process;
    private readonly WorkerConfiguration configuration;
    private readonly Task stderr;
    private readonly StringBuilder diagnostic = new();
    public string ModelSha256 { get; private set; } = "";
    public string EnvironmentIdentity { get; private set; } = "";
    public int EffectiveInferenceThreads { get; private set; }

    private InferenceHelper(WorkerConfiguration configuration)
    {
        this.configuration = configuration;
        process = new Process { StartInfo = new ProcessStartInfo(configuration.Paths.Python)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        var arguments = new List<string> { configuration.Paths.InferenceScript, "--manifest", configuration.Credits.ModelManifest!, "--image-root", Path.Combine(configuration.Paths.CacheDirectory, "frames"), "--threads", configuration.Resources.InferenceThreads.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (configuration.Credits.InferenceBackend == CreditInferenceBackend.OpenClGpu)
            arguments.AddRange(["--backend", "OpenClGpu"]);
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["OMP_NUM_THREADS"] = configuration.Resources.InferenceThreads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (configuration.Credits.InferenceBackend == CreditInferenceBackend.OpenClGpu)
            process.StartInfo.Environment["OPENCV_OPENCL_CACHE_DIR"] = Path.Combine(configuration.Paths.CacheDirectory, "opencl");
        process.Start();
        stderr = DrainErrorsAsync();
    }

    public static async Task<InferenceHelper> StartAsync(WorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.Credits.ModelManifest is null || !File.Exists(configuration.Credits.ModelManifest))
            throw new InvalidDataException("Credits require an explicit local model manifest. Run doctor after setup.");
        var imageRoot = Path.Combine(configuration.Paths.CacheDirectory, "frames");
        PathMapper.RejectLinks(imageRoot);
        Directory.CreateDirectory(imageRoot);
        if (configuration.Credits.InferenceBackend == CreditInferenceBackend.OpenClGpu)
        {
            var openClCache = Path.Combine(configuration.Paths.CacheDirectory, "opencl");
            PathMapper.RejectLinks(openClCache);
            Directory.CreateDirectory(openClCache);
        }
        var helper = new InferenceHelper(configuration);
        try
        {
            using var hello = JsonDocument.Parse(await helper.ReadAsync(cancellationToken).ConfigureAwait(false));
            var root = hello.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "hello" || root.GetProperty("protocolVersion").GetInt32() != 1)
                throw new InvalidDataException("Inference capability handshake failed.");
            var expectedBackend = configuration.Credits.InferenceBackend.ToString();
            if (root.GetProperty("backend").GetString() != expectedBackend)
                throw new InvalidDataException($"Inference backend handshake expected {expectedBackend}.");
            if (!root.TryGetProperty("imageFeatureVersion", out var imageVersion) || imageVersion.GetString() != CreditPolicy.ImageFeatureVersion)
                throw new InvalidDataException("Inference image features are incompatible. Update the helper together with the worker.");
            if (!root.TryGetProperty("inferenceThreads", out var inferenceThreads) || inferenceThreads.ValueKind != JsonValueKind.Number || !inferenceThreads.TryGetInt32(out var effectiveThreads) || effectiveThreads < 1 || effectiveThreads > configuration.Resources.InferenceThreads)
                throw new InvalidDataException($"Inference thread handshake must report an integer between 1 and configured limit {configuration.Resources.InferenceThreads}.");
            helper.ModelSha256 = root.GetProperty("modelSha256").GetString()!;
            helper.EffectiveInferenceThreads = effectiveThreads;
            if (configuration.Credits.InferenceBackend == CreditInferenceBackend.Cpu)
            {
                helper.EnvironmentIdentity = $"Python {root.GetProperty("pythonVersion").GetString()}; OpenCV {root.GetProperty("opencvVersion").GetString()}; Cpu; features {root.GetProperty("featureVersion").GetString()}; image stats {imageVersion.GetString()}";
            }
            else
            {
                var device = GpuDevice(root);
                helper.EnvironmentIdentity = $"Python {root.GetProperty("pythonVersion").GetString()}; OpenCV {root.GetProperty("opencvVersion").GetString()}; {expectedBackend}; features {root.GetProperty("featureVersion").GetString()}; image stats {imageVersion.GetString()}; actual {JsonSerializer.Serialize(new { Backend = expectedBackend, Device = device })}";
            }
            return helper;
        }
        catch
        {
            await helper.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IReadOnlyDictionary<string, string> GpuDevice(JsonElement root)
    {
        if (!root.TryGetProperty("gpuDevice", out var device) || device.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("OpenCL GPU inference handshake is missing GPU device metadata.");
        var metadata = new Dictionary<string, string>();
        foreach (var name in new[] { "name", "vendor", "driver", "version" })
        {
            if (!device.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException($"OpenCL GPU inference handshake has invalid GPU device {name}.");
            metadata.Add(name, value.GetString()!);
        }

        return metadata;
    }

    public async Task<List<FrameEvidence>> InferAsync(IReadOnlyList<ExtractedFrame> frames, CancellationToken cancellationToken, bool resetTemporal = false)
    {
        if (frames.Count is < 1 or > 32) throw new InvalidDataException("Inference batches contain 1–32 frames.");
        var requestId = Guid.NewGuid().ToString("N");
        var request = WireJson.Write(new { type = "infer", requestId, resetTemporal, frames = frames.Select(f => new { f.Token, f.TimestampTicks, f.Path }) });
        // JSON-lines must not contain the pretty-printer's literal newlines.
        using var document = JsonDocument.Parse(request);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(document.RootElement).AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        using var response = JsonDocument.Parse(await ReadAsync(cancellationToken).ConfigureAwait(false));
        var root = response.RootElement;
        if (root.GetProperty("type").GetString() != "result" || root.GetProperty("requestId").GetString() != requestId)
            throw new InvalidDataException("Mismatched inference response.");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Inference request failed: " + error.ToString());
        var results = root.GetProperty("frames").EnumerateArray().Select(e => WireJson.Read<FrameEvidence>(e.GetRawText())).ToList();
        if (results.Count != frames.Count || !results.Select(f => (f.Token, f.TimestampTicks)).SequenceEqual(frames.Select(f => (f.Token, f.TimestampTicks))))
            throw new InvalidDataException("Inference returned different frame identities.");
        foreach (var result in results)
        {
            if (result.Features is { } features)
            {
                var values = new[] { features.TextFraction, features.MaxScore, features.ScoreP95, features.TextWidth, features.TextHeight,
                    features.SpatialDispersion, features.MeanLuminance, features.Entropy, features.HistogramPeakRatio, features.NearBlackRatio,
                    features.TextCenterX ?? 0, features.TextCenterY ?? 0, features.TemporalDifference ?? 0 };
                if (values.Concat(features.RowOccupancy).Concat(features.ColumnOccupancy).Any(v => !double.IsFinite(v)))
                    throw new InvalidDataException("Non-finite inference evidence.");
                if (!CreditPolicy.HasSpanMetrics(features)) throw new InvalidDataException("Missing or invalid inference span measurements.");
            }
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("{\"type\":\"shutdown\"}".AsMemory(), shutdown.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(shutdown.Token).ConfigureAwait(false);
                await ReadAsync(shutdown.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException) { }
        ProcessRunner.Kill(process);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
        process.Dispose();
    }

    private async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.Resources.InferenceTimeoutSeconds));
        using var terminate = timeout.Token.Register(() => ProcessRunner.Kill(process));
        var text = new StringBuilder();
        var character = new char[1];
        try
        {
            while (await process.StandardOutput.ReadAsync(character.AsMemory(), timeout.Token).ConfigureAwait(false) > 0)
            {
                if (character[0] == '\n') return text.ToString();
                if (text.Length >= 1024 * 1024) throw new InvalidDataException("Inference response exceeds 1 MiB.");
                if (character[0] != '\r') text.Append(character[0]);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("Inference helper exceeded its configured timeout.", exception); }
        throw new InvalidDataException("Inference helper exited: " + diagnostic);
    }

    private async Task DrainErrorsAsync()
    {
        var buffer = new char[1024];
        int count;
        while ((count = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (diagnostic.Length < 4096) diagnostic.Append(buffer, 0, Math.Min(count, 4096 - diagnostic.Length));
        }
    }
}
