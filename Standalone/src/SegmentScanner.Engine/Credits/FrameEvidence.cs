namespace SegmentScanner.Engine.Credits;

public sealed record FrameFeatures
{
    public double TextFraction { get; init; }
    public int TextCellCount { get; init; }
    public int? ScoreMapTextCellCount { get; init; }
    public double? ScoreMapTextSourceCenterX { get; init; }
    public double? ScoreMapTextSourceCenterY { get; init; }
    public double MaxScore { get; init; }
    public double ScoreP95 { get; init; }
    public double? TextCenterX { get; init; }
    public double? TextCenterY { get; init; }
    public double TextWidth { get; init; }
    public double TextHeight { get; init; }
    public int TextRegions { get; init; }
    public List<double> RowOccupancy { get; init; } = [];
    public List<double> ColumnOccupancy { get; init; } = [];
    public double SpatialDispersion { get; init; }
    public double MeanLuminance { get; init; }
    public double Entropy { get; init; }
    public double? NormalizedByteEntropy { get; init; }
    public double? FlatnessScore { get; init; }
    public double HistogramPeakRatio { get; init; }
    public double NearBlackRatio { get; init; }
    public double? TemporalDifference { get; init; }
}

public sealed record FrameEvidence(string Token, long TimestampTicks, FrameFeatures? Features, string? Error);
public sealed record ExtractedFrame(string Token, long TimestampTicks, string Path, long PresentationTimestamp = 0, int TimeBaseNumerator = 1, int TimeBaseDenominator = 10000000);
public enum EvidenceLabel { CreditLike, OrdinaryContent, BlankTransition, Unknown }
public sealed record LabelledFrame(FrameEvidence Frame, EvidenceLabel Label, string Reason);
