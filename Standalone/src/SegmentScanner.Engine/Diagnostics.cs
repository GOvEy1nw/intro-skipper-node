using SegmentScanner.Contracts;
using SegmentScanner.Engine.Credits;
using SegmentScanner.Engine.Jellyfin;
using SegmentScanner.Engine.Media;

namespace SegmentScanner.Engine;

public sealed record DiagnosticCheck(string Name, bool Passed, string Detail);

public static class Diagnostics
{
    public static async Task<List<DiagnosticCheck>> DoctorAsync(WorkerConfiguration configuration, CancellationToken token)
    {
        var checks = new List<DiagnosticCheck>();
        async Task Check(string name, Func<Task<string>> action)
        {
            try { checks.Add(new(name, true, await action().ConfigureAwait(false))); }
            catch (Exception exception) when (exception is not OperationCanceledException) { checks.Add(new(name, false, exception.Message)); }
        }
        var originalCredits = configuration.Credits.Method == CreditsDetectionMethod.IntroSkipper;
        await Check("Configuration", () => { configuration.Validate(); return Task.FromResult($"schema v1; one job; {configuration.Resources.ConcurrentFiles} file workers; credits {(originalCredits ? $"Original Intro Skipper; video {(configuration.Credits.UseGpu ? configuration.Credits.VideoDecoder : CreditVideoDecoder.Cpu)}" : $"text {configuration.Credits.InferenceBackend}; video {configuration.Credits.VideoDecoder}")}; black-only fallback off"); }).ConfigureAwait(false);
        foreach (var binary in new[] { ("FFmpeg", configuration.Paths.Ffmpeg), ("FFprobe", configuration.Paths.Ffprobe), ("Python", configuration.Paths.Python) }.Where(binary => !originalCredits || binary.Item1 != "Python"))
            await Check(binary.Item1, async () =>
            {
                var output = await ProcessRunner.RunAsync(binary.Item2, [binary.Item1 == "Python" ? "--version" : "-version"], 20, token).ConfigureAwait(false);
                return (output.StandardOutput + output.StandardError).Split('\n')[0].Trim();
            }).ConfigureAwait(false);
        await Check("Chromaprint", async () =>
        {
            var directory = Path.Combine(configuration.Paths.CacheDirectory, "doctor");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".fingerprint");
            try
            {
                await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-f", "chromaprint", "-fp_format", "raw", path], 30, token).ConfigureAwait(false);
                var size = new FileInfo(path).Length;
                if (size == 0 || size % 4 != 0) throw new InvalidDataException("FFmpeg did not produce raw 32-bit Chromaprint values.");
                return $"{size / 4} raw fingerprint points";
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }).ConfigureAwait(false);
        await Check("Video filters", async () =>
        {
            var output = await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg, ["-hide_banner", "-filters"], 20, token).ConfigureAwait(false);
            foreach (var filter in originalCredits ? new[] { "blackframe", "blackdetect", "signalstats", "metadata", "scale", "showinfo" } : ["select", "trim", "scale", "setsar", "showinfo"])
                if (!output.StandardOutput.Contains(filter, StringComparison.Ordinal)) throw new InvalidDataException("Required filter unavailable: " + filter);
            return originalCredits ? "CPU keyframe, black-frame and image-statistics filters available" : "sample selection, timestamp reporting, aspect handling and scale available";
        }).ConfigureAwait(false);
        if (!originalCredits)
        {
        await Check("Model / actual backend", async () =>
        {
            Directory.CreateDirectory(Path.Combine(configuration.Paths.CacheDirectory, "frames"));
            await using var helper = await InferenceHelper.StartAsync(configuration, token).ConfigureAwait(false);
            return helper.EnvironmentIdentity + "; SHA-256 " + helper.ModelSha256 + "; OpenCV threads actual " + helper.EffectiveInferenceThreads + "; configured limit " + configuration.Resources.InferenceThreads + "; production approval requires held-out evaluation";
        }).ConfigureAwait(false);
        }
        if (!originalCredits || configuration.Credits.UseGpu)
        {
        await Check("Credits video decoder", async () =>
        {
            if (configuration.Credits.VideoDecoder == CreditVideoDecoder.Cpu) return "CPU video decoding; AV1 analysis omits film grain";
            var intel = configuration.Credits.VideoDecoder == CreditVideoDecoder.IntelQuickSync;
            var decoders = await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg, ["-hide_banner", "-decoders"], 20, token).ConfigureAwait(false);
            var available = decoders.StandardOutput.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(fields => fields.Length > 1).Select(fields => fields[1]).ToHashSet(StringComparer.Ordinal);
            var required = intel ? new[] { "h264_qsv", "hevc_qsv", "av1_qsv" } : ["h264_cuvid", "hevc_cuvid", "av1"];
            foreach (var codec in required)
                if (!available.Contains(codec)) throw new InvalidDataException("FFmpeg decoder unavailable: " + codec);
            var device = intel ? "qsv=segmentscanner,child_device_type=d3d11va" : "cuda=segmentscanner";
            await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg,
                ["-hide_banner", "-nostdin", "-init_hw_device", device, "-f", "lavfi", "-i", "color=size=32x32:duration=0.04", "-frames:v", "1", "-f", "null", "-"], 30, token).ConfigureAwait(false);
            return (intel ? "Intel device initialized; " : "NVIDIA device initialized; ") + "AV1 analysis omits film grain; "
                + "H.264/HEVC/AV1 decoders present; per-file codec support is checked during scanning";
        }).ConfigureAwait(false);
        }
        else
        {
            checks.Add(new("Credits backend", true, "Original Intro Skipper: CPU analysis and video decoding; Python, the text model and saved GPU selections are unused"));
        }
        await Check("Credits profile", () => Task.FromResult(new CreditsDetector(configuration).ProfileHash())).ConfigureAwait(false);
        foreach (var root in configuration.Paths.AllowedMediaRoots.Concat(configuration.Paths.Mappings.Select(m => m.WorkerPrefix)).Distinct())
            await Check("Media root", () =>
            {
                PathMapper.RejectLinks(root);
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Configured media root is unavailable.");
                _ = Directory.EnumerateFileSystemEntries(root).Take(1).ToList();
                return Task.FromResult(root);
            }).ConfigureAwait(false);
        foreach (var root in new[] { configuration.Paths.StateDirectory, configuration.Paths.CacheDirectory }.Concat(configuration.Exports.Enabled && configuration.Exports.MirroredRoot is { } exportRoot ? new[] { exportRoot } : []))
            await Check("Local/output write permission", () =>
            {
                PathMapper.RejectLinks(root);
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, ".segmentscanner-doctor-" + Guid.NewGuid().ToString("N"));
                using (File.Create(path)) { }
                File.Delete(path);
                return Task.FromResult(root);
            }).ConfigureAwait(false);
        if (configuration.Server is { } server)
            await Check("Remote bridge", async () =>
            {
                using var client = new RemoteClient(configuration);
                var capabilities = await client.HandshakeAsync(token).ConfigureAwait(false);
                if (!capabilities.SupportsHeldResultReview)
                    throw new InvalidDataException("The installed bridge lacks held-result review. Install the matching development bridge package.");
                var node = (await client.NodesAsync(token).ConfigureAwait(false)).FirstOrDefault(n => n.NodeId == server.WorkerId);
                var state = node is null ? "not connected; open IntroSkipper-Node settings" : node.Enabled ? "enabled; policy managed in Intro Skipper → Nodes" : "disabled; configure and enable it in Intro Skipper → Nodes";
                return $"server {capabilities.ServerVersion}; plugin {capabilities.PluginVersion}; protocol {capabilities.ProtocolVersion}; node {state}; held-result review available" + (server.AllowInsecureHttp ? "; explicit insecure HTTP transport" : "");
            }).ConfigureAwait(false);
        return checks;
    }
}
