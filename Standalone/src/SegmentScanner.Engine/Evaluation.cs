using SegmentScanner.Contracts;

namespace SegmentScanner.Engine;

public sealed record LabelInterval
{
    public required long StartTicks { get; init; }
    public required long EndTicks { get; init; }
    public long? StartMinimumTicks { get; init; }
    public long? StartMaximumTicks { get; init; }
    public long? EndMinimumTicks { get; init; }
    public long? EndMaximumTicks { get; init; }
}

public sealed record LabelItem
{
    public required Guid ItemId { get; init; }
    public required RemoteAnalysisMode Mode { get; init; }
    public string? MediaSourceKey { get; init; }
    public string? FileVersionToken { get; init; }
    public string Category { get; init; } = "Unspecified";
    public string Split { get; init; } = "HeldOut";
    public List<LabelInterval> Segments { get; init; } = [];
    public List<TimeRange> Programme { get; init; } = [];
}

public sealed record LabelDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required List<LabelItem> Items { get; init; }
}

public sealed record ItemScore(Guid ItemId, RemoteAnalysisMode Mode, string Category, string Split, long ProgrammeSkippedTicks,
    long MissedCreditTicks, int MissedBlocks, int FalsePositiveSegments, double MeanStartErrorSeconds, double MeanEndErrorSeconds,
    int BoundariesWithinBands, int AcceptedBlocks, int ProposedBlocks, bool NeedsReview);
public sealed record EvaluationReport(string ResultHash, List<string> ProfileHashes, List<ItemScore> Items, long ProgrammeSkippedTicks,
    long MissedCreditTicks, int FalsePositiveSegments, double ReviewRate, double AcceptedCoverage, double MedianMilliseconds, double P95Milliseconds);

public static class Evaluation
{
    public static EvaluationReport Score(ResultBundle bundle, LabelDocument labels)
    {
        ContractValidation.Require(labels.SchemaVersion == 1 && labels.Items.Count > 0, "Labels require schema v1 and at least one item.");
        var scores = new List<ItemScore>();
        long labelledTicks = 0;
        foreach (var label in labels.Items)
        {
            var result = bundle.Results.SingleOrDefault(r => r.ItemId == label.ItemId && r.Mode == label.Mode && (label.MediaSourceKey is null || r.MediaSourceKey == label.MediaSourceKey))
                ?? throw new InvalidDataException("A labelled item/mode is missing from this result bundle.");
            ContractValidation.Result(result);
            ContractValidation.Require(label.FileVersionToken is null || result.FileVersionToken == label.FileVersionToken, "Labels refer to another media version.");
            var expected = label.Segments.Select(s => new TimeRange(s.StartTicks, s.EndTicks)).ToList();
            foreach (var range in expected.Concat(label.Programme)) ContractValidation.Require(range.StartTicks >= 0 && range.StartTicks < range.EndTicks && range.EndTicks <= result.TimelineEndTicks, "Invalid labelled interval.");
            ContractValidation.Require(!expected.OrderBy(r => r.StartTicks).Zip(expected.OrderBy(r => r.StartTicks).Skip(1)).Any(p => p.First.EndTicks > p.Second.StartTicks), "Labelled segments overlap.");
            var predicted = result.Segments.Where(s => s.Decision == CandidateDecision.Accept).ToList();
            var programme = label.Programme.Count > 0 ? label.Programme : Complement(expected, result.TimelineEndTicks);
            var skipped = predicted.Sum(p => programme.Sum(c => Overlap(p.StartTicks, p.EndTicks, c.StartTicks, c.EndTicks)));
            var missed = expected.Sum(c => c.EndTicks - c.StartTicks - predicted.Sum(p => Overlap(p.StartTicks, p.EndTicks, c.StartTicks, c.EndTicks)));
            var pairs = new List<(SegmentCandidate Prediction, LabelInterval Label)>();
            var usedPredictions = new HashSet<string>();
            var usedLabels = new HashSet<LabelInterval>();
            foreach (var pair in predicted.SelectMany(p => label.Segments.Select(l => new { P = p, L = l, Overlap = Overlap(p.StartTicks, p.EndTicks, l.StartTicks, l.EndTicks) })).Where(p => p.Overlap > 0).OrderByDescending(p => p.Overlap))
            {
                if (usedPredictions.Contains(pair.P.CandidateId) || usedLabels.Contains(pair.L)) continue;
                usedPredictions.Add(pair.P.CandidateId); usedLabels.Add(pair.L); pairs.Add((pair.P, pair.L));
            }
            var withinBands = pairs.Count(p => p.Prediction.StartTicks >= (p.Label.StartMinimumTicks ?? p.Label.StartTicks)
                && p.Prediction.StartTicks <= (p.Label.StartMaximumTicks ?? p.Label.StartTicks) && p.Prediction.EndTicks >= (p.Label.EndMinimumTicks ?? p.Label.EndTicks)
                && p.Prediction.EndTicks <= (p.Label.EndMaximumTicks ?? p.Label.EndTicks));
            scores.Add(new(label.ItemId, label.Mode, label.Category, label.Split, skipped, missed, expected.Count - usedLabels.Count,
                predicted.Count - usedPredictions.Count, pairs.Count == 0 ? 0 : pairs.Average(p => Math.Abs(p.Prediction.StartTicks - p.Label.StartTicks) / (double)TimeSpan.TicksPerSecond),
                pairs.Count == 0 ? 0 : pairs.Average(p => Math.Abs(p.Prediction.EndTicks - p.Label.EndTicks) / (double)TimeSpan.TicksPerSecond), withinBands,
                predicted.Count, result.Segments.Count, result.Status == AnalysisStatus.NeedsReview));
            labelledTicks += expected.Sum(s => s.EndTicks - s.StartTicks);
        }
        var times = bundle.Results.Select(r => r.Metrics.GetValueOrDefault("analysisMilliseconds")).Where(v => v > 0).Order().ToList();
        return new(WireJson.Hash(bundle), bundle.Results.Select(r => r.Detector.ProfileHash).Distinct().ToList(), scores,
            scores.Sum(s => s.ProgrammeSkippedTicks), scores.Sum(s => s.MissedCreditTicks), scores.Sum(s => s.FalsePositiveSegments),
            scores.Count(s => s.NeedsReview) / (double)scores.Count, labelledTicks == 0 ? 0 : 1 - scores.Sum(s => s.MissedCreditTicks) / (double)labelledTicks,
            times.Count == 0 ? 0 : times[times.Count / 2], times.Count == 0 ? 0 : times[Math.Clamp((int)Math.Ceiling(times.Count * 0.95) - 1, 0, times.Count - 1)]);
    }

    private static long Overlap(long a, long b, long c, long d) => Math.Max(0, Math.Min(b, d) - Math.Max(a, c));
    private static List<TimeRange> Complement(List<TimeRange> segments, long end)
    {
        var content = new List<TimeRange>();
        long cursor = 0;
        foreach (var segment in segments.OrderBy(s => s.StartTicks))
        {
            if (cursor < segment.StartTicks) content.Add(new(cursor, segment.StartTicks));
            cursor = segment.EndTicks;
        }
        if (cursor < end) content.Add(new(cursor, end));
        return content;
    }
}
