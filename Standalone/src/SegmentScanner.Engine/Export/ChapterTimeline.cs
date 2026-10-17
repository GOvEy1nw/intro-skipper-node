using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SegmentScanner.Contracts;

namespace SegmentScanner.Engine.Export;

public static class ChapterTimeline
{
    public static string Xml(string mediaKey, long timelineEnd, IReadOnlyList<SegmentCandidate> segments)
    {
        var chapters = new List<(long Start, long End, string Title)>();
        long cursor = 0;
        var afterCredits = false;
        foreach (var segment in segments.OrderBy(s => s.StartTicks))
        {
            if (segment.StartTicks < cursor || segment.EndTicks > timelineEnd) throw new InvalidDataException("Canonical segment modes overlap or exceed the media timeline.");
            if (segment.StartTicks > cursor) chapters.Add((cursor, segment.StartTicks, afterCredits ? "Post-credits content" : cursor == 0 && segment.Type == RemoteAnalysisMode.Introduction ? "Opening scene" : "Main content"));
            chapters.Add((segment.StartTicks, segment.EndTicks, segment.Type switch
            {
                RemoteAnalysisMode.Introduction => "Intro",
                RemoteAnalysisMode.Credits => "Credits",
                RemoteAnalysisMode.Recap => "Recap",
                RemoteAnalysisMode.Preview => "Preview",
                RemoteAnalysisMode.Commercial => "Commercial",
                _ => throw new InvalidDataException("Unsupported chapter segment type."),
            }));
            cursor = segment.EndTicks;
            afterCredits |= segment.Type == RemoteAnalysisMode.Credits;
        }
        if (cursor < timelineEnd) chapters.Add((cursor, timelineEnd, afterCredits ? "Post-credits content" : "Main content"));
        var identifiers = new HashSet<ulong>();
        ulong Identifier(string name)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(mediaKey + "/" + name)));
            while (value == 0 || !identifiers.Add(value)) value = checked(value + 1);
            return value;
        }
        var edition = new XElement("EditionEntry", new XElement("EditionUID", Identifier("edition")), new XElement("EditionFlagDefault", 1), new XElement("EditionFlagHidden", 0));
        foreach (var chapter in chapters)
        {
            edition.Add(new XElement("ChapterAtom", new XElement("ChapterUID", Identifier($"{chapter.Start}/{chapter.End}/{chapter.Title}")),
                new XElement("ChapterTimeStart", FormatTicks(chapter.Start)), new XElement("ChapterTimeEnd", FormatTicks(chapter.End)),
                new XElement("ChapterFlagEnabled", 1), new XElement("ChapterFlagHidden", 0),
                new XElement("ChapterDisplay", new XElement("ChapterString", chapter.Title), new XElement("ChapterLanguage", "und"))));
        }
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + new XDocument(new XElement("Chapters", edition)).ToString() + "\n";
    }

    public static string FormatTicks(long ticks) => string.Create(CultureInfo.InvariantCulture,
        $"{ticks / TimeSpan.TicksPerHour:D2}:{ticks / TimeSpan.TicksPerMinute % 60:D2}:{ticks / TimeSpan.TicksPerSecond % 60:D2}.{ticks % TimeSpan.TicksPerSecond:D7}");
}
