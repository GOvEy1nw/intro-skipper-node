using System.Globalization;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Credits;
using SegmentScanner.Engine;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;
using Xunit;

namespace SegmentScanner.Tests;

public sealed class MediaTimelineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "segmentscanner-timeline-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("mkv", 0, false)]
    [InlineData("mkv", 3, false)]
    [InlineData("mp4", -1, true)]
    public async Task SeekExpansionUsesActualRationalPtsOnOneNormalizedTimeline(string extension, int offset, bool variableRate)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fractional shifted Épisode." + extension);
        var arguments = new List<string> { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30000/1001:duration=12", "-an" };
        if (variableRate) arguments.AddRange(["-vf", "setpts=(N+floor(N/30))*1001/(30000*TB)", "-fps_mode", "vfr"]);
        arguments.AddRange(["-c:v", "mpeg4", "-q:v", "4", "-output_ts_offset", offset.ToString(CultureInfo.InvariantCulture), "-avoid_negative_ts", "disabled", path]);
        await ProcessRunner.RunAsync("ffmpeg", arguments, 30, CancellationToken.None);
        var configuration = new WorkerConfiguration { Paths = new() { CacheDirectory = Path.Combine(directory, "cache"), AllowedMediaRoots = [directory] } };
        var item = new JobItem { ItemId = Guid.NewGuid(), MediaSourceKey = "fractional-fixture", SourcePath = path, FileVersionToken = "1" };
        var media = await new MediaProbe(configuration).ProbeAsync(item, CancellationToken.None);
        using var extractor = new FrameExtractor(configuration, media);
        var initial = await extractor.ExtractAsync(6 * TimeSpan.TicksPerSecond, 10 * TimeSpan.TicksPerSecond, 2, CancellationToken.None);
        var expanded = await extractor.ExtractAsync(4 * TimeSpan.TicksPerSecond, 10 * TimeSpan.TicksPerSecond, 2, CancellationToken.None);
        Assert.InRange(initial[0].TimestampTicks, 6 * TimeSpan.TicksPerSecond, (long)(6.06 * TimeSpan.TicksPerSecond));
        Assert.Equal(initial.Select(f => f.TimestampTicks), expanded.Where(f => f.TimestampTicks >= 6 * TimeSpan.TicksPerSecond).Select(f => f.TimestampTicks));
        Assert.All(initial, frame => Assert.Equal(frame.TimestampTicks, (long)decimal.Round((decimal)frame.PresentationTimestamp * frame.TimeBaseNumerator * TimeSpan.TicksPerSecond / frame.TimeBaseDenominator)));
        var tail = await extractor.ExtractAsync(Math.Max(0, media.TimelineEndTicks - 4 * TimeSpan.TicksPerSecond), media.TimelineEndTicks, 0.25, CancellationToken.None);
        Assert.InRange(media.TimelineEndTicks - tail[^1].TimestampTicks, 1, (long)(0.35 * TimeSpan.TicksPerSecond));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task OriginalCreditsUsesChaptersOrScanningWithoutPythonModelOrSavedGpuOptions(bool chaptered, bool legacy)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "original-credits.mkv");
        await ProcessRunner.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=black:size=160x90:rate=5:duration=32", "-an", "-c:v", "mpeg4", path], 30, CancellationToken.None);
        var configuration = new WorkerConfiguration
        {
            Paths = new() { StateDirectory = Path.Combine(directory, "state"), CacheDirectory = Path.Combine(directory, "cache"),
                AllowedMediaRoots = [directory], Python = "missing-python", InferenceScript = "missing-helper.py" },
            Credits = new() { Method = CreditsDetectionMethod.IntroSkipper, ModelManifest = "missing-model.json",
                InferenceBackend = CreditInferenceBackend.OpenClGpu, VideoDecoder = CreditVideoDecoder.NvidiaNvdec },
        };
        var job = new JobSnapshot
        {
            JobId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), Modes = [RemoteAnalysisMode.Credits],
            CreditsSettings = System.Text.Json.JsonSerializer.SerializeToElement(new { action = "Default", configuration = new { useLegacyBlackFrameAnalyzer = legacy } }),
            Items = [new JobItem { ItemId = Guid.NewGuid(), MediaSourceKey = "chaptered", SourcePath = path, FileVersionToken = "1",
                ChaptersAreAuthoritative = true, Chapters = chaptered ? [new ChapterSnapshot { Name = "Ending", StartTicks = TimeSpan.FromSeconds(10).Ticks, Origin = ChapterOrigin.Authored }] : [] }],
        };
        var checks = await Diagnostics.DoctorAsync(configuration, CancellationToken.None);
        Assert.All(checks, check => Assert.True(check.Passed, check.Name + ": " + check.Detail));
        Assert.DoesNotContain(checks, check => check.Name == "Python" || check.Name == "Model / actual backend");
        using var store = new WorkerStore(configuration.Paths.StateDirectory);
        var result = Assert.Single((await new ScanService(configuration, store).ScanAsync(job, CancellationToken.None)).Results);
        Assert.Equal("introskipper-credits-cpu-v1", result.Detector.Id);
        Assert.Null(result.Detector.ModelSha256);
        Assert.Equal(AnalysisStatus.NeedsReview, result.Status);
        Assert.Equal(["OriginalIntroSkipperCredits"], result.Warnings);
        if (chaptered)
        {
            var segment = Assert.Single(result.Segments);
            Assert.Equal(TimeSpan.FromSeconds(10).Ticks, segment.StartTicks);
            Assert.Equal(TimeSpan.FromSeconds(32).Ticks, segment.EndTicks);
        }
        else
        {
            Assert.Equal(new SegmentScanner.Contracts.TimeRange(0, TimeSpan.FromSeconds(32).Ticks), Assert.Single(result.SearchedRanges));
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        PathMapper.EnsureWithin(directory, Path.GetTempPath());
        PathMapper.RejectLinks(directory);
        Directory.Delete(directory, recursive: true);
    }
}
