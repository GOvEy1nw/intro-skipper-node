using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SegmentScanner.Contracts;
using SegmentScanner.Engine;

namespace SegmentScanner.Engine.Media;

public sealed class MediaProbe(WorkerConfiguration configuration)
{
    public async Task<ResolvedMedia> ProbeAsync(JobItem item, CancellationToken cancellationToken, QueueActivityCollector? activity = null, RemoteAnalysisMode? mode = null)
    {
        using var scope = activity is null || mode is null ? null : activity.Begin(item.ItemId, mode.Value, "Probe");
        if (item.Multipart) throw new NotSupportedException("Multipart/stacked sources are unsupported.");
        var path = new PathMapper(configuration.Paths).Resolve(item.SourcePath, item.PathMapId);
        var before = await FileIdentityAsync(path, cancellationToken).ConfigureAwait(false);
        var output = await ProcessRunner.RunAsync(configuration.Paths.Ffprobe,
            ["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", path], configuration.Resources.ProcessTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(output.StandardOutput, new JsonDocumentOptions { MaxDepth = 32 });
        var format = json.RootElement.GetProperty("format");
        var origin = Seconds(format, "start_time", 0);
        var duration = Seconds(format, "duration", double.NaN);
        if (!double.IsFinite(duration) || duration <= 0 || duration > TimeSpan.MaxValue.TotalSeconds) throw new NotSupportedException("Media has no valid duration.");
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().Select(s => new MediaStream(
            s.GetProperty("index").GetInt32(), Text(s, "codec_type"), Text(s, "codec_name"),
            s.TryGetProperty("tags", out var tags) ? Text(tags, "language") : null,
            s.TryGetProperty("channels", out var channels) ? channels.GetInt32() : 0,
            s.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1,
            Text(s, "color_transfer") is "smpte2084" or "arib-std-b67")).ToList();
        if (streams.Count > 100 || streams.Count == 0) throw new NotSupportedException("Invalid media stream layout.");
        var video = json.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault(s => Text(s, "codec_type") == "video"
            && (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var p) || p.GetInt32() == 0));
        if (video.ValueKind != JsonValueKind.Undefined)
        {
            var visualDuration = Seconds(video, "duration", double.NaN);
            if (double.IsFinite(visualDuration) && visualDuration > 0) duration = Seconds(video, "start_time", origin) - origin + visualDuration;
            else if (Text(format, "format_name").Split(',').Contains("matroska", StringComparer.Ordinal))
            {
                // Matroska's end timestamp includes a nonzero origin; playback and copyts/start_at_zero do not.
                duration -= origin;
                if (video.TryGetProperty("tags", out var videoTags) && videoTags.TryGetProperty("DURATION", out var durationTag))
                {
                    var components = durationTag.GetString()?.Split(':');
                    if (components?.Length == 3 && decimal.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                        && decimal.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                        && decimal.TryParse(components[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
                        duration = (double)(hours * 3600 + minutes * 60 + seconds) - origin;
                }
            }
            else if (origin != 0) throw new NotSupportedException("Shifted video without a validated stream duration is unsupported for this container.");
        }
        if (!double.IsFinite(duration) || duration <= 0 || duration > TimeSpan.MaxValue.TotalSeconds) throw new NotSupportedException("Invalid normalized visual timeline.");
        var end = checked((long)Math.Round(duration * TimeSpan.TicksPerSecond));
        var after = await FileIdentityAsync(path, cancellationToken).ConfigureAwait(false);
        if (before != after) throw new IOException("Media changed during probe.");
        var signature = new MediaSignature { Length = after.Length, LastWriteUtcTicks = after.Modified, SampleHash = after.Hash,
            TimelineEndTicks = end, StreamLayoutHash = WireJson.Hash(streams) };
        if (item.ExpectedSignature is { } expected && expected != signature) throw new IOException("Media signature differs from the work snapshot.");
        var chapters = item.Chapters.ToList();
        if (!item.ChaptersAreAuthoritative && chapters.Count == 0 && json.RootElement.TryGetProperty("chapters", out var containerChapters))
        {
            chapters = containerChapters.EnumerateArray().Select(c => new ChapterSnapshot
            {
                Name = c.TryGetProperty("tags", out var tags) ? Text(tags, "title") : "",
                StartTicks = checked((long)Math.Round((Seconds(c, "start_time", 0) - origin) * TimeSpan.TicksPerSecond)),
                Origin = ChapterOrigin.Authored,
                OriginId = "original-container",
            }).Where(c => c.StartTicks >= 0 && c.StartTicks < end).ToList();
        }
        return new ResolvedMedia(item with { Chapters = chapters }, path, signature, streams, checked((long)Math.Round(origin * TimeSpan.TicksPerSecond)))
        {
            VideoPixelFormat = video.ValueKind == JsonValueKind.Undefined ? "" : Text(video, "pix_fmt"),
        };
    }

    public static async Task<bool> UnchangedAsync(ResolvedMedia media, CancellationToken cancellationToken)
    {
        var identity = await FileIdentityAsync(media.Path, cancellationToken).ConfigureAwait(false);
        return identity.Length == media.Signature.Length && identity.Modified == media.Signature.LastWriteUtcTicks && identity.Hash == media.Signature.SampleHash;
    }

    public static ResolvedMedia ApplyGeneratedChapterGuard(ResolvedMedia media, bool hasRecordedChapterExport)
        => !hasRecordedChapterExport || media.Item.ChaptersAreAuthoritative ? media : media with
        {
            // Imported chapter tools can strip provenance; recorded generated exports prevent self-confirmation.
            Item = media.Item with { Chapters = media.Item.Chapters.Select(chapter => chapter.OriginId == "original-container"
                ? chapter with { Origin = ChapterOrigin.Unknown, OriginId = "generated-export-provenance-unavailable" } : chapter).ToList() },
        };

    private static async Task<(long Length, long Modified, string Hash)> FileIdentityAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new IOException("Media is missing or empty.");
        var length = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        foreach (var offset in new[] { 0L, Math.Max(0, length / 2 - buffer.Length / 2), Math.Max(0, length - buffer.Length) }.Distinct())
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.AsSpan(0, count));
        }
        info.Refresh();
        if (length != info.Length || modified != info.LastWriteTimeUtc.Ticks) throw new IOException("Media is still growing or changed while reading.");
        return (length, modified, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static double Seconds(JsonElement element, string key, double fallback) => double.TryParse(Text(element, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
}
