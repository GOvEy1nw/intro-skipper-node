using System.Globalization;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;

namespace SegmentScanner.Engine.Credits;

public sealed partial class FrameExtractor(WorkerConfiguration configuration, ResolvedMedia media) : IDisposable
{
    private readonly string directory = Path.Combine(configuration.Paths.CacheDirectory, "frames", Guid.NewGuid().ToString("N"));

    public async Task<List<ExtractedFrame>> ExtractAsync(long startTicks, long endTicks, double intervalSeconds, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        using var owner = Process.GetCurrentProcess();
        await File.WriteAllTextAsync(Path.Combine(directory, ".segmentscanner-owner"),
            $"SegmentScannerFrames/v1\n{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}\n{owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
        var batchDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(batchDirectory);
        var maximumFrames = checked((int)Math.Ceiling((endTicks - startTicks) / (double)TimeSpan.TicksPerSecond / intervalSeconds)) + 5;
        if (maximumFrames > 50000 || maximumFrames * 200000d > configuration.Resources.MaximumTemporaryGigabytes * 1024 * 1024 * 1024)
            throw new IOException("Frame extraction exceeds the configured temporary storage budget.");
        var start = (startTicks / (double)TimeSpan.TicksPerSecond).ToString("R", CultureInfo.InvariantCulture);
        var end = (endTicks / (double)TimeSpan.TicksPerSecond).ToString("R", CultureInfo.InvariantCulture);
        var interval = intervalSeconds.ToString("R", CultureInfo.InvariantCulture);
        var duration = ((endTicks - startTicks) / (double)TimeSpan.TicksPerSecond + intervalSeconds).ToString("R", CultureInfo.InvariantCulture);
        var av1Options = media.Streams.Any(stream => stream.Index == media.VideoStreamIndex && stream.Codec == "av1")
            ? new[] { "-export_side_data", "film_grain" } : [];
        var filter = $"trim=start={start}:end={end},select='isnan(prev_selected_t)+gt(floor(t/{interval}),floor(prev_selected_t/{interval}))',scale=w='trunc(iw*sar/2)*2':h=ih,setsar=1,scale=320:320:force_original_aspect_ratio=increase,showinfo";
        var output = await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg,
            configuration.Credits.VideoDecoder == CreditVideoDecoder.Cpu
                ? ["-hide_banner", "-nostdin", "-copyts", "-start_at_zero", "-ss", start, "-t", duration, ..av1Options,
                "-threads", configuration.Resources.FfmpegThreads.ToString(CultureInfo.InvariantCulture), "-i", media.Path, "-map", $"0:{media.VideoStreamIndex}",
                "-filter_threads", configuration.Resources.FfmpegThreads.ToString(CultureInfo.InvariantCulture),
                "-an", "-sn", "-dn", "-vf", filter, "-fps_mode", "vfr", "-frames:v", maximumFrames.ToString(CultureInfo.InvariantCulture),
                "-threads", configuration.Resources.FfmpegThreads.ToString(CultureInfo.InvariantCulture), "-q:v", "2", Path.Combine(batchDirectory, "%06d.jpg")]
                : HardwareArguments(start, end, duration, interval, maximumFrames, batchDirectory),
            configuration.Resources.ProcessTimeoutSeconds, cancellationToken, 16 * 1024 * 1024).ConfigureAwait(false);
        var frames = new List<ExtractedFrame>();
        var timeBase = TimeBasePattern().Match(output.StandardError);
        if (!timeBase.Success) throw new InvalidDataException("Extractor did not report the presentation time base.");
        var numerator = int.Parse(timeBase.Groups[1].Value, CultureInfo.InvariantCulture);
        var denominator = int.Parse(timeBase.Groups[2].Value, CultureInfo.InvariantCulture);
        if (numerator <= 0 || denominator <= 0) throw new InvalidDataException("Invalid presentation time base.");
        foreach (Match match in TimestampPattern().Matches(output.StandardError))
        {
            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var pts = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var ticks = checked((long)decimal.Round((decimal)pts * numerator * TimeSpan.TicksPerSecond / denominator));
            var path = Path.Combine(batchDirectory, (index + 1).ToString("D6", CultureInfo.InvariantCulture) + ".jpg");
            if (ticks < startTicks || ticks >= endTicks || !File.Exists(path)) throw new InvalidDataException("Extractor timestamp/image contract failed.");
            frames.Add(new ExtractedFrame(ticks.ToString(CultureInfo.InvariantCulture), ticks, path, pts, numerator, denominator));
        }
        if (frames.Count != Directory.EnumerateFiles(batchDirectory, "*.jpg").Count() || frames.Count == 0)
            throw new InvalidDataException("Incomplete frame decode; a successful negative cannot be recorded.");
        if (Directory.EnumerateFiles(directory, "*.jpg", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) > configuration.Resources.MaximumTemporaryGigabytes * 1024 * 1024 * 1024)
            throw new IOException("Temporary frame budget exhausted.");
        return frames;
    }

    private List<string> HardwareArguments(string start, string end, string duration, string interval, int maximumFrames, string batchDirectory)
    {
        var stream = media.Streams.FirstOrDefault(candidate => candidate.Index == media.VideoStreamIndex)
            ?? throw new InvalidDataException("Hardware credits decoding requires a primary video stream.");
        if (stream.Codec is not ("h264" or "hevc" or "av1"))
            throw new InvalidDataException("Hardware credits decoding supports only h264, hevc, or av1 video.");
        var downloadFormat = media.VideoPixelFormat switch
        {
            "yuv420p" => "nv12",
            "yuv420p10le" => "p010le",
            _ => throw new InvalidDataException("Hardware credits decoding supports only yuv420p or yuv420p10le video."),
        };
        var hardwareArguments = configuration.Credits.VideoDecoder switch
        {
            CreditVideoDecoder.NvidiaNvdec => new List<string> { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-c:v", stream.Codec == "av1" ? "av1" : stream.Codec + "_cuvid" },
            CreditVideoDecoder.IntelQuickSync => ["-init_hw_device", "qsv=intel,child_device_type=d3d11va", "-hwaccel", "qsv", "-hwaccel_output_format", "qsv", "-c:v", stream.Codec + "_qsv"],
            _ => throw new InvalidDataException("Unsupported credits video decoder."),
        };
        // AV1 analysis omits grain; the cuvid AV1 wrapper ignores this flag, so it uses the native hardware decoder above.
        if (stream.Codec == "av1")
            hardwareArguments.AddRange(["-export_side_data", "film_grain"]);
        var filter = $"trim=start={start}:end={end},select='isnan(prev_selected_t)+gt(floor(t/{interval}),floor(prev_selected_t/{interval}))',hwdownload,format={downloadFormat},scale=w='trunc(iw*sar/2)*2':h=ih,setsar=1,scale=320:320:force_original_aspect_ratio=increase,showinfo";
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-copyts", "-start_at_zero", "-ss", start, "-t", duration,
            "-threads", configuration.Resources.FfmpegThreads.ToString(CultureInfo.InvariantCulture) };
        arguments.AddRange(hardwareArguments);
        arguments.AddRange(["-i", media.Path, "-map", $"0:{media.VideoStreamIndex}",
            "-filter_threads", configuration.Resources.FfmpegThreads.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn", "-vf", filter, "-fps_mode", "vfr", "-frames:v", maximumFrames.ToString(CultureInfo.InvariantCulture),
            "-threads", configuration.Resources.FfmpegThreads.ToString(CultureInfo.InvariantCulture), "-q:v", "2", Path.Combine(batchDirectory, "%06d.jpg")]);
        return arguments;
    }

    public void Dispose()
    {
        var root = Path.Combine(configuration.Paths.CacheDirectory, "frames");
        PathMapper.EnsureWithin(directory, root);
        PathMapper.RejectLinks(directory);
        if (File.Exists(Path.Combine(directory, ".segmentscanner-owner"))) Directory.Delete(directory, recursive: true);
    }

    public static void CleanupOwnedTemporary(WorkerConfiguration configuration)
    {
        var root = Path.Combine(configuration.Paths.CacheDirectory, "frames");
        PathMapper.RejectLinks(root);
        if (!Directory.Exists(root)) return;
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(candidate), "N", out _)) continue;
            PathMapper.EnsureWithin(candidate, root);
            PathMapper.RejectLinks(candidate);
            var marker = Path.Combine(candidate, ".segmentscanner-owner");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 256) continue;
            PathMapper.RejectLinks(marker);
            var lines = File.ReadAllLines(marker);
            if (lines.Length != 3 || lines[0] != "SegmentScannerFrames/v1" || !int.TryParse(lines[1], out var id) || !long.TryParse(lines[2], out var start)) continue;
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == start) continue;
            }
            catch (ArgumentException) { /* The recorded process has exited. */ }
            catch (System.ComponentModel.Win32Exception) { continue; }
            var children = Directory.EnumerateDirectories(candidate).ToList();
            foreach (var child in children) PathMapper.RejectLinks(child);
            if (children.Any(child => Directory.EnumerateDirectories(child).Any())) continue;
            foreach (var file in children.SelectMany(Directory.EnumerateFiles).Concat(Directory.EnumerateFiles(candidate))) PathMapper.RejectLinks(file);
            Directory.Delete(candidate, recursive: true);
        }
    }

    [GeneratedRegex(@"\bn:\s*(\d+)\s+pts:\s*(-?\d+)\s+pts_time:")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"config in time_base:\s*(\d+)/(\d+)")]
    private static partial Regex TimeBasePattern();
}
