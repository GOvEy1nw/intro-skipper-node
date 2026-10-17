using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;

namespace SegmentScanner.Engine;

public static class ResultFactory
{
    public static AnalysisResult Create(JobSnapshot job, JobItem item, RemoteAnalysisMode mode, AnalysisStatus status, DetectorIdentity detector, ResolvedMedia? media = null) => new()
    {
        AnalysisId = Guid.NewGuid(),
        JobId = job.JobId, AttemptId = job.AttemptId, ServerId = job.ServerId, ItemId = item.ItemId,
        MediaSourceKey = item.MediaSourceKey, SourcePath = item.SourcePath, FileVersionToken = item.FileVersionToken,
        PathMapId = item.PathMapId,
        ExpectedItemModeRevision = item.ExpectedModeRevisions.GetValueOrDefault(mode, "0"), Mode = mode, Status = status,
        TimelineEndTicks = media?.TimelineEndTicks ?? item.RuntimeTicks, MediaValidation = media?.Signature, Detector = detector,
    };
}
