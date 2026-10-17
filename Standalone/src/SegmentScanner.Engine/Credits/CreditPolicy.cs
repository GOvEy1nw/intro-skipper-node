using SegmentScanner.Contracts;

namespace SegmentScanner.Engine.Credits;

public static class CreditPolicy
{
    public const long ShortGapThresholdTicks = 10 * TimeSpan.TicksPerSecond;
    public const string ImageFeatureVersion = "bgr-byte-square-v3";

    public static bool HasSpanMetrics(FrameFeatures features) => features.NormalizedByteEntropy is >= 0 and <= 1
        && features.FlatnessScore is >= 0 and <= 1 && features.ScoreMapTextCellCount is >= 0 and <= 65536
        && (features.ScoreMapTextCellCount == 0 ? features.ScoreMapTextSourceCenterX is null && features.ScoreMapTextSourceCenterY is null
            : features.ScoreMapTextSourceCenterX is >= 0 and <= 1
                && features.ScoreMapTextSourceCenterY is >= 0 and <= 1);

    public static bool? GroupPositionMatches(IReadOnlyList<FrameEvidence> frames)
    {
        if (frames.Any(f => f.Error is not null || f.Features is not { } features || !HasSpanMetrics(features))) return null;
        var eligible = frames.Where(f => f.Features!.ScoreMapTextCellCount >= 9).ToList();
        var wellPlaced = eligible.Count(f => f.Features!.ScoreMapTextSourceCenterX is > 0.10 and < 0.90
            && f.Features.ScoreMapTextSourceCenterY > 0.10
            && f.Features.ScoreMapTextSourceCenterY < (f.Features.NormalizedByteEntropy >= 0.75 ? 0.65 : 0.70));
        return eligible.Count == 0 || wellPlaced * 5 >= eligible.Count * 3;
    }

    public static List<SegmentCandidate> ApplySpanRules(IReadOnlyList<SegmentCandidate> proposals, IReadOnlyList<FrameEvidence> evidence,
        CreditDecisionProfile profile, long timelineEnd, double intervalSeconds, long endOffsetTicks = 0)
    {
        var samples = evidence.OrderBy(f => f.TimestampTicks).ToList();
        var tolerance = intervalSeconds * TimeSpan.TicksPerSecond * 1.6;
        List<FrameEvidence> Window(long start, long end) => samples.Where(f => f.TimestampTicks > start && f.TimestampTicks < end).ToList();
        bool Complete(List<FrameEvidence> frames, long start, long end) => frames.Count > 0
            && frames.All(f => f.Error is null && f.Features is { } features && HasSpanMetrics(features))
            && frames[0].TimestampTicks - start <= tolerance && end - frames[^1].TimestampTicks <= tolerance
            && !frames.Zip(frames.Skip(1)).Any(p => p.Second.TimestampTicks - p.First.TimestampTicks > tolerance);
        var spans = new List<SegmentCandidate>();
        foreach (var next in proposals.OrderBy(c => c.StartTicks))
        {
            if (spans.Count == 0) { spans.Add(next); continue; }
            var previous = spans[^1];
            if (next.StartTicks - previous.EndTicks <= ShortGapThresholdTicks
                || previous.Decision == CandidateDecision.Reject || next.Decision == CandidateDecision.Reject)
            {
                spans.Add(next);
                continue;
            }
            var gap = Window(previous.EndTicks, next.StartTicks);
            if (!Complete(gap, previous.EndTicks, next.StartTicks))
            {
                spans[^1] = Hold(previous, "GapEvidenceIncomplete");
                spans.Add(Hold(next, "GapEvidenceIncomplete"));
                continue;
            }
            var flat = gap.Average(f => f.Features!.FlatnessScore!.Value) <= 0.30;
            var entropy = gap.Average(f => f.Features!.NormalizedByteEntropy!.Value);
            var count = gap.Average(f => f.Features!.ScoreMapTextCellCount!.Value);
            var text = entropy >= 0.62 ? count >= 60 : count >= 3.5;
            if (flat || text) spans[^1] = Join(previous, next, flat ? "FlatGapJoined" : "TextGapJoined");
            else spans.Add(next);
        }
        if (spans.Count == 0 || spans[^1].Decision == CandidateDecision.Reject || spans[^1].ReachesFileEnd) return spans;
        var last = spans[^1];
        var rawStart = last.RawStartTicks ?? last.StartTicks;
        var rawEnd = last.RawEndTicks ?? last.EndTicks;
        var groupIndex = samples.FindLastIndex(f => f.TimestampTicks >= rawStart - (last.StartUncertaintyTicks ?? 0)
            && f.TimestampTicks <= rawEnd + (last.EndUncertaintyTicks ?? 0) && Label(f, profile).Label == EvidenceLabel.CreditLike);
        var tail = Window(rawEnd, timelineEnd);
        var tailComplete = Complete(tail, rawEnd, timelineEnd)
            || tail.Count == 0 && groupIndex == samples.Count - 1 && groupIndex >= 0
                && samples[groupIndex].Error is null && samples[groupIndex].Features is { } features && HasSpanMetrics(features)
                && timelineEnd - samples[groupIndex].TimestampTicks <= tolerance;
        if (!tailComplete || groupIndex < 0)
        {
            spans[^1] = Hold(last, "TailEvidenceIncomplete");
            return spans;
        }
        var finalSamples = groupIndex >= samples.Count - 2;
        if (!finalSamples && tail.Average(f => f.Features!.FlatnessScore!.Value) > 0.30) return spans;
        var adjustedEnd = checked(timelineEnd + endOffsetTicks);
        if (adjustedEnd <= last.StartTicks || adjustedEnd > timelineEnd)
        {
            spans[^1] = Hold(last, "InvalidEofPlaybackOffset");
            return spans;
        }
        if (adjustedEnd <= last.EndTicks) return spans;
        spans[^1] = Hold(last, finalSamples ? "FinalSamplesReachEOF" : "FlatTailAbsorbed") with
            { EndTicks = adjustedEnd, ReachesFileEnd = adjustedEnd == timelineEnd };
        return spans;
    }

    private static SegmentCandidate Hold(SegmentCandidate proposal, string reason) => proposal with
    {
        Decision = CandidateDecision.Review, ConfidenceTier = "Review", Reasons = proposal.Reasons.Append(reason).Distinct().ToList()
    };

    private static SegmentCandidate Join(SegmentCandidate first, SegmentCandidate last, string reason) => first with
    {
        EndTicks = last.EndTicks, RawEndTicks = last.RawEndTicks, EndUncertaintyTicks = last.EndUncertaintyTicks,
        ReachesFileEnd = last.ReachesFileEnd, Decision = CandidateDecision.Review, ConfidenceTier = "Review",
        Reasons = first.Reasons.Concat(last.Reasons).Append(reason).Distinct().ToList()
    };

    public static List<SegmentCandidate> JoinShortGaps(IReadOnlyList<SegmentCandidate> proposals)
    {
        var joined = new List<SegmentCandidate>();
        foreach (var next in proposals.OrderBy(c => c.StartTicks))
        {
            if (joined.Count == 0) { joined.Add(next); continue; }
            var previous = joined[^1];
            var gap = next.StartTicks - previous.EndTicks;
            if (gap < 0 || gap > ShortGapThresholdTicks
                || previous.Decision == CandidateDecision.Reject || next.Decision == CandidateDecision.Reject)
            {
                joined.Add(next);
                continue;
            }
            // The gap may contain story footage, so the combined proposal always needs review.
            joined[^1] = Join(previous, next, "ShortGapJoined");
        }
        return joined;
    }

    public static long ConnectedBoundary(IReadOnlyList<LabelledFrame> frames, long anchor, bool start, double stepSeconds)
    {
        var index = Enumerable.Range(0, frames.Count).OrderBy(i => Math.Abs(frames[i].Frame.TimestampTicks - anchor)).FirstOrDefault(-1);
        if (index < 0 || frames[index].Label != EvidenceLabel.CreditLike) return anchor;
        var direction = start ? -1 : 1;
        while (index + direction >= 0 && index + direction < frames.Count)
        {
            var next = index + direction;
            if (frames[next].Label != EvidenceLabel.CreditLike
                || Math.Abs(frames[next].Frame.TimestampTicks - frames[index].Frame.TimestampTicks) > stepSeconds * 1.6 * TimeSpan.TicksPerSecond) break;
            index = next;
        }
        return frames[index].Frame.TimestampTicks;
    }

    public static LabelledFrame Label(FrameEvidence frame, CreditDecisionProfile profile)
    {
        var f = frame.Features;
        if (f is null || frame.Error is not null) return new(frame, EvidenceLabel.Unknown, "InferenceUnavailable");
        if (f.TextFraction < profile.MinimumTextFraction)
            return new(frame, f.NearBlackRatio >= profile.NearBlackRatio ? EvidenceLabel.BlankTransition : EvidenceLabel.OrdinaryContent, "NoText");
        if (f.TextCenterY >= profile.SubtitleBandStart && f.TextHeight < 0.2) return new(frame, EvidenceLabel.Unknown, "SubtitleLikeOnly");
        if (f.TextWidth < profile.MinimumTextWidth || f.TextHeight < profile.MinimumTextHeight) return new(frame, EvidenceLabel.Unknown, "SparseOrCornerText");
        return new(frame, EvidenceLabel.CreditLike, "TextLayout");
    }

    public static List<SegmentCandidate> Candidates(IReadOnlyList<FrameEvidence> evidence, CreditDecisionProfile profile, long searchedStart, long timelineEnd, double intervalSeconds)
        => Candidates(evidence, profile, searchedStart, timelineEnd, intervalSeconds, out _);

    public static List<SegmentCandidate> Candidates(IReadOnlyList<FrameEvidence> evidence, CreditDecisionProfile profile,
        long searchedStart, long timelineEnd, double intervalSeconds, out List<string> rejections)
    {
        var samples = evidence.OrderBy(f => f.TimestampTicks).ToList();
        var rejected = new List<string>();
        rejections = rejected;
        var tolerance = intervalSeconds * TimeSpan.TicksPerSecond * 1.6;
        bool Valid(int index) => samples[index].Error is null && samples[index].Features is { } features && HasSpanMetrics(features);
        FrameFeatures Features(int index) => samples[index].Features!;
        var groups = new List<CreditGroup>();
        var run = new List<int>();
        void FinishGroup()
        {
            if (run.Count == 0) return;
            groups.Add(new CreditGroup(run));
            run = [];
        }
        for (var i = 0; i < samples.Count; i++)
        {
            if (run.Count > 0 && samples[i].TimestampTicks - samples[run[^1]].TimestampTicks > tolerance) FinishGroup();
            if (Valid(i) && (Features(i).NormalizedByteEntropy <= 0.2 || Features(i).ScoreMapTextCellCount >= 9)) run.Add(i);
            else FinishGroup();
        }
        FinishGroup();
        if (samples.Any(f => f.Error is not null || f.Features is not { } features || !HasSpanMetrics(features))) rejected.Add("GroupEvidenceIncomplete");
        bool StaticText(CreditGroup group)
        {
            for (var start = 0; start + 5 <= group.Indexes.Count; start++)
            {
                var first = Features(group.Indexes[start]);
                if (group.Indexes.Skip(start).Take(5).All(index =>
                {
                    var f = Features(index);
                    return f.NormalizedByteEntropy >= 0.7 && f.ScoreMapTextCellCount > 0
                        && f.ScoreMapTextSourceCenterX > 0 && f.ScoreMapTextSourceCenterY > 0
                        && first.ScoreMapTextCellCount > 0 && first.ScoreMapTextSourceCenterX > 0 && first.ScoreMapTextSourceCenterY > 0
                        && Math.Abs(f.ScoreMapTextCellCount!.Value - first.ScoreMapTextCellCount!.Value) <= 2
                        && Math.Abs(f.ScoreMapTextSourceCenterX!.Value - first.ScoreMapTextSourceCenterX!.Value) <= 0.01
                        && Math.Abs(f.ScoreMapTextSourceCenterY!.Value - first.ScoreMapTextSourceCenterY!.Value) <= 0.01;
                })) return true;
            }
            return false;
        }
        void Remove(Func<CreditGroup, bool> predicate, string reason)
        {
            groups.RemoveAll(g => { if (!predicate(g)) return false; rejected.Add(reason); return true; });
        }
        bool CompleteBetween(int first, int last) => Enumerable.Range(first, Math.Max(0, last - first + 1)).All(Valid)
            && !samples.Skip(first).Take(last - first + 1).Zip(samples.Skip(first + 1).Take(last - first))
                .Any(p => p.Second.TimestampTicks - p.First.TimestampTicks > tolerance);
        void Merge(int index, string reason)
        {
            groups[index].Indexes.AddRange(groups[index + 1].Indexes);
            groups[index].Reasons.UnionWith(groups[index + 1].Reasons);
            groups[index].Reasons.Add(reason);
            groups.RemoveAt(index + 1);
        }
        Remove(StaticText, "StaticTextGroupRejected");
        // Brief scene signs must not gain credibility by chaining across short gaps into real credits.
        Remove(g => g.Indexes.Count <= 4 && g.Indexes.All(index => Features(index).NormalizedByteEntropy >= 0.7
            && Label(samples[index], profile).Label != EvidenceLabel.CreditLike), "SparseSceneTextGroupRejected");
        for (var i = 0; i + 1 < groups.Count;)
        {
            var first = groups[i]; var next = groups[i + 1];
            var gap = next.First - first.Last;
            var joins = gap <= 4 || gap == 5 && Features(first.Last).NormalizedByteEntropy - Features(next.First).NormalizedByteEntropy < 0.7
                && first.Indexes.Average(n => Features(n).NormalizedByteEntropy!.Value) < 0.6
                && next.Indexes.Average(n => Features(n).NormalizedByteEntropy!.Value) < 0.6;
            if (joins && CompleteBetween(first.Last, next.First)) Merge(i, "SampleIndexGapJoined");
            else i++;
        }
        // Stored one-based frame indexes cannot exceed the sample count, so the first length gate has no EOF exception.
        Remove(g => g.Indexes.Count <= 5, "FiveSampleGroupRejected");
        for (var i = 0; i + 1 < groups.Count;)
        {
            var first = groups[i]; var next = groups[i + 1];
            if (!CompleteBetween(first.Last, next.First)) { i++; continue; }
            var gapTicks = samples[next.First].TimestampTicks - samples[first.Last].TimestampTicks;
            var gap = Enumerable.Range(first.Last + 1, next.First - first.Last - 1).ToList();
            var shortGap = gapTicks <= ShortGapThresholdTicks;
            var flat = gap.Count > 0 && gap.Average(n => Features(n).FlatnessScore!.Value) <= 0.30;
            var entropy = gap.Count > 0 ? gap.Average(n => Features(n).NormalizedByteEntropy!.Value) : 1;
            var text = gap.Count > 0 && gap.Average(n => Features(n).ScoreMapTextCellCount!.Value) >= (entropy >= 0.62 ? 60 : 3.5);
            if (shortGap || flat || text) Merge(i, shortGap ? "ShortGapJoined" : flat ? "FlatGapJoined" : "TextGapJoined");
            else i++;
        }
        if (groups.Count > 0)
        {
            var last = groups[^1];
            var completeTail = CompleteBetween(last.Last, samples.Count - 1)
                && timelineEnd - samples[^1].TimestampTicks <= tolerance;
            var tail = Enumerable.Range(last.Last + 1, samples.Count - last.Last - 1).ToList();
            if (completeTail && (last.Last >= samples.Count - 2 || tail.Count > 0 && tail.Average(n => Features(n).FlatnessScore!.Value) <= 0.30))
            {
                var finalSamples = last.Last >= samples.Count - 2;
                last.ReachesFileEnd = true;
                if (!finalSamples) last.Indexes.AddRange(tail);
                last.Reasons.Add(finalSamples ? "FinalSamplesReachEOF" : "FlatTailAbsorbed");
            }
        }
        Remove(g => g.Indexes.Count <= 10 && g.Last < samples.Count - 3, "TenSampleGroupRejected");
        Remove(g => GroupPositionMatches(g.Indexes.Select(i => samples[i]).ToList()) == false, "TextPositionRejected");
        for (var i = 0; i + 1 < groups.Count;)
        {
            if (groups[i].Indexes.Count <= 30 && groups[i + 1].First - groups[i].Last >= 100)
            { rejected.Add("IsolatedEarlyGroupRejected"); groups.RemoveAt(i); }
            else i++;
        }
        var candidates = new List<SegmentCandidate>();
        foreach (var group in groups)
        {
            var opening = group.Indexes.TakeWhile(i => Features(i).ScoreMapTextCellCount > 9 && Features(i).NormalizedByteEntropy > 0.5).Count();
            var startIndex = opening > 4 ? group.First : group.Indexes.Skip(opening).FirstOrDefault(i => Features(i).ScoreMapTextCellCount > 0, -1);
            if (startIndex < 0 || startIndex >= group.Last) { rejected.Add("NoTextSpanBoundary"); continue; }
            var start = samples[startIndex].TimestampTicks;
            var rawEnd = samples[group.Last].TimestampTicks;
            var end = group.ReachesFileEnd ? timelineEnd : rawEnd;
            var reasons = group.Reasons.Append("OrderedCreditsGroupPolicy").ToList();
            if (samples[group.First].TimestampTicks - searchedStart < intervalSeconds * TimeSpan.TicksPerSecond) reasons.Add("LeftBoundaryCensored");
            candidates.Add(new SegmentCandidate { CandidateId = "credits-" + samples[group.First].TimestampTicks, Type = RemoteAnalysisMode.Credits,
                StartTicks = start, EndTicks = end, RawStartTicks = start, RawEndTicks = rawEnd,
                StartUncertaintyTicks = (long)(intervalSeconds * TimeSpan.TicksPerSecond), EndUncertaintyTicks = (long)(intervalSeconds * TimeSpan.TicksPerSecond),
                ReachesFileEnd = end == timelineEnd, Decision = CandidateDecision.Review, ConfidenceTier = "Review", Reasons = reasons });
        }
        return candidates;
    }

    private sealed class CreditGroup(List<int> indexes)
    {
        public List<int> Indexes { get; } = indexes;
        public int First => Indexes[0];
        public int Last => Indexes[^1];
        public bool ReachesFileEnd { get; set; }
        public HashSet<string> Reasons { get; } = [];
    }
}
