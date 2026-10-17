using System.Text.Json.Serialization;
using SegmentScanner.Contracts;

namespace SegmentScanner.Engine.Media;

public sealed record MediaStream(int Index, string Kind, string Codec, string? Language, int Channels, bool AttachedPicture, bool Hdr);

public sealed record ResolvedMedia(JobItem Item, string Path, MediaSignature Signature, IReadOnlyList<MediaStream> Streams, long PlaybackOriginTicks)
{
    [JsonIgnore]
    public string VideoPixelFormat { get; init; } = "";
    public long TimelineEndTicks => Signature.TimelineEndTicks;
    public int? VideoStreamIndex => Streams.FirstOrDefault(s => s.Kind == "video" && !s.AttachedPicture)?.Index;
    public bool HasHdr => Streams.Any(s => s.Kind == "video" && !s.AttachedPicture && s.Hdr);
}
