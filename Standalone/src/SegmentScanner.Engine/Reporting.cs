using System.Net;
using System.Text;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Engine;

public static class Reporting
{
    public static void Report(ResultBundle bundle, string path, string inputPath)
    {
        path = Path.GetFullPath(path);
        var decisionsPath = Path.ChangeExtension(path, ".decisions.json");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(decisionsPath, Path.GetFullPath(inputPath), comparison) || string.Equals(decisionsPath, path, comparison))
            throw new InvalidDataException("The companion decisions file would overwrite the report input/output.");
        PathMapper.RejectLinks(decisionsPath);
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Segment review</title><style>body{font:16px system-ui;margin:2rem;max-width:1000px}table{border-collapse:collapse;width:100%}td,th{padding:.6rem;text-align:left;border-bottom:1px solid #ccc}code{overflow-wrap:anywhere}.review{color:#854d0e}</style><h1>Segment review</h1><p>Proposals require explicit acceptance. This report does not change playback or sidecars.</p>");
        foreach (var result in bundle.Results)
        {
            string Escape(string text) => WebUtility.HtmlEncode(text);
            html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<h2>{Escape(Path.GetFileName(result.SourcePath))} · {result.Mode}</h2><p>{result.Status} · {Escape(result.Detector.Id)}</p><p><code>{result.AnalysisId}</code></p><table><tr><th>Candidate</th><th>Start–end (seconds)</th><th>Decision</th><th>Reasons</th></tr>");
            foreach (var segment in result.Segments)
                html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<tr><td>{Escape(segment.CandidateId)}</td><td>{segment.StartTicks / (double)TimeSpan.TicksPerSecond:F3}–{segment.EndTicks / (double)TimeSpan.TicksPerSecond:F3}</td><td>{segment.Decision}</td><td>{Escape(string.Join(", ", segment.Reasons))}</td></tr>");
            html.Append(System.Globalization.CultureInfo.InvariantCulture, $"</table><p>{Escape(string.Join("; ", result.Warnings))}</p>");
        }
        html.Append("<p>Edit the companion decisions JSON to Accept or Reject every candidate in a mode. Successful negatives require their analysis ID in acceptNoSegments.</p></html>");
        AtomicFile.Write(path, html.ToString());
        var decisions = new ReviewDocument { Decisions = bundle.Results.SelectMany(r => r.Segments.Select(s => new ReviewDecision
            { AnalysisId = r.AnalysisId, CandidateId = s.CandidateId, Decision = CandidateDecision.Review })).ToList() };
        if (!File.Exists(decisionsPath))
        {
            using var stream = new FileStream(decisionsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.UTF8.GetBytes(WireJson.Write(decisions)));
        }
    }
}
