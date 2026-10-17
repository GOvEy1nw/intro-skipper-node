using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Remote;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Introductions;
using SegmentScanner.Engine.Media;
using Xunit;
using Mode = SegmentScanner.Contracts.RemoteAnalysisMode;
using UpstreamMode = IntroSkipper.Data.AnalysisMode;

namespace SegmentScanner.Tests;

public sealed class IntroductionParityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "scanner-intro-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoredChapterRawAndAdjustedBoundariesMatchSharedAnalyzerAndGeneratedChaptersAreIgnored(bool connected)
    {
        var configuration = new PluginConfiguration { ChapterAnalyzerIntroductionPattern = "^Intro$", PreferChromaprint = false,
            AdjustIntroBasedOnSilence = false, SnapToKeyframe = false, IntroStartOffset = 2, IntroEndOffset = 3, EndSnapThreshold = 0 };
        var settings = RemoteAnalysisDispatcher.GetIntroductionSettings(configuration);
        var item = new JobItem { ItemId = Guid.NewGuid(), MediaSourceKey = "episode", SourcePath = "unused.mkv", FileVersionToken = "1",
            RuntimeTicks = 240 * TimeSpan.TicksPerSecond, ExpectedModeRevisions = new() { [Mode.Introduction] = "0" }, Chapters =
            [
                new() { Name = "Programme", StartTicks = 0, Origin = ChapterOrigin.Authored },
                new() { Name = "Intro", StartTicks = 30 * TimeSpan.TicksPerSecond, Origin = ChapterOrigin.Authored },
                new() { Name = "Programme", StartTicks = 90 * TimeSpan.TicksPerSecond, Origin = ChapterOrigin.Authored },
                new() { Name = "Intro", StartTicks = 150 * TimeSpan.TicksPerSecond, Origin = ChapterOrigin.ScannerGenerated },
                new() { Name = "Programme", StartTicks = 200 * TimeSpan.TicksPerSecond, Origin = ChapterOrigin.ScannerGenerated },
            ] };
        var job = new JobSnapshot { JobId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), ServerId = connected ? "fixture-server" : null,
            Modes = [Mode.Introduction], Items = [item], IntroductionSettings = settings, IntroductionProfileHash = WireJson.Hash(settings) };
        var signature = new MediaSignature { Length = 1, LastWriteUtcTicks = 1, TimelineEndTicks = item.RuntimeTicks, SampleHash = "fixture", StreamLayoutHash = "fixture" };
        var media = new ResolvedMedia(item, item.SourcePath, signature, [], 0);
        var chapters = item.Chapters.Where(c => c.Origin == ChapterOrigin.Authored).Select(c => new ChapterInfo { Name = c.Name, StartPositionTicks = c.StartTicks }).ToList();
        var environment = new ExplicitAnalysisEnvironment(configuration, "must-not-run", _ => chapters);
        var episode = new QueuedEpisode { EpisodeId = item.ItemId, Path = item.SourcePath, Duration = 240 };
        var raw = Assert.Single(new ChapterAnalyzer(NullLogger<ChapterAnalyzer>.Instance, null!, null!, configuration, environment: environment)
            .FindChapterCandidates(episode, UpstreamMode.Introduction));
        var adjusted = await new TimeAdjustmentHelper(NullLogger.Instance, configuration, UpstreamMode.Introduction, null!, environment)
            .AdjustIntroTimesAsync(episode, raw, false);
        var worker = new WorkerConfiguration { Paths = new() { CacheDirectory = directory, Ffmpeg = "must-not-run", Ffprobe = "must-not-run" } };
        var result = Assert.Single(await new IntroductionDetector(worker).AnalyzeAsync(job, [media], CancellationToken.None));
        ContractValidation.Result(result);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(TimeSpan.FromSeconds(raw.Start).Ticks, segment.RawStartTicks);
        Assert.Equal(TimeSpan.FromSeconds(raw.End).Ticks, segment.RawEndTicks);
        Assert.Equal(32 * TimeSpan.TicksPerSecond, segment.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(adjusted.End).Ticks, segment.EndTicks);
        Assert.Equal(connected ? AnalysisStatus.CompletedWithSegments : AnalysisStatus.NeedsReview, result.Status);
        Assert.Equal(connected, result.Detector.ProductionApproved);
        Assert.Equal(job.IntroductionProfileHash, result.Detector.ProfileHash);
        Assert.Equal("upstream-chapters", result.Detector.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => new IntroductionDetector(worker).AnalyzeAsync(job with { IntroductionProfileHash = "changed" }, [media], CancellationToken.None));
    }

    [Fact]
    public void UnknownChapterProvenanceBlocksChapterDependentAcceptanceAndNegatives()
    {
        var item = new JobItem { ItemId = Guid.NewGuid(), MediaSourceKey = "fixture", SourcePath = "fixture", FileVersionToken = "1",
            Chapters = [new() { Name = "Intro", StartTicks = 0, Origin = ChapterOrigin.Unknown }] };
        var result = new AnalysisResult { AnalysisId = Guid.NewGuid(), JobId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), ItemId = item.ItemId,
            MediaSourceKey = item.MediaSourceKey, SourcePath = item.SourcePath, FileVersionToken = item.FileVersionToken,
            Mode = Mode.Introduction, Status = AnalysisStatus.CompletedWithSegments, TimelineEndTicks = 100 * TimeSpan.TicksPerSecond,
            Detector = new() { Id = "upstream-chromaprint", ProfileHash = "fixture" },
            Segments = [new() { CandidateId = "intro", Type = Mode.Introduction, StartTicks = 0, EndTicks = 60 * TimeSpan.TicksPerSecond }] };
        Assert.True(RemoteAnalysisDispatcher.HasUnknownChapterDependency(result, item, IntroductionDetector.CreateDefaultSettings()));
        var independent = RemoteAnalysisDispatcher.GetIntroductionSettings(new PluginConfiguration { PreferChromaprint = true, AdjustIntroBasedOnChapters = false });
        Assert.False(RemoteAnalysisDispatcher.HasUnknownChapterDependency(result, item, independent));
        Assert.True(RemoteAnalysisDispatcher.HasUnknownChapterDependency(result with { Segments = [], Status = AnalysisStatus.CompletedNoSegments }, item, independent));
        Assert.False(RemoteAnalysisDispatcher.HasUnknownChapterDependency(result, item with { Chapters = [item.Chapters[0] with { Origin = ChapterOrigin.UserReviewed }] }, IntroductionDetector.CreateDefaultSettings()));
    }

    [Fact]
    public void PortableFingerprintCacheRejectsChangedMediaDecoderAndUnscopedLegacyRows()
    {
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<DetectionCacheDbContext>().UseSqlite($"Data Source={Path.Combine(directory, "cache.db")}").Options;
        var database = new DetectionCacheDatabase(new PooledDbContextFactory<DetectionCacheDbContext>(options), NullLogger<DetectionCacheDatabase>.Instance);
        var itemId = Guid.NewGuid();
        var configuration = new PluginConfiguration();
        var identity = "media-a/decoder-a";
        var portable = new DetectionCacheService(NullLogger<DetectionCacheService>.Instance, database,
            new ExplicitAnalysisEnvironment(configuration, "unused", _ => [], cacheIdentity: _ => identity));
        var server = new DetectionCacheService(NullLogger<DetectionCacheService>.Instance, database,
            new ExplicitAnalysisEnvironment(configuration, "unused", _ => []));
        server.Write(itemId, UpstreamMode.Introduction, CacheEntryType.Chromaprint, 0, 60, new uint[] { 1, 2 }, "audio:0");
        Assert.True(server.TryRead<uint>(itemId, UpstreamMode.Introduction, CacheEntryType.Chromaprint, 0, 60, out _, "audio:0"));
        Assert.False(portable.TryRead<uint>(itemId, UpstreamMode.Introduction, CacheEntryType.Chromaprint, 0, 60, out _, "audio:0"));
        portable.Write(itemId, UpstreamMode.Introduction, CacheEntryType.Chromaprint, 0, 60, new uint[] { 3, 4 }, "audio:0");
        Assert.True(portable.TryRead<uint>(itemId, UpstreamMode.Introduction, CacheEntryType.Chromaprint, 0, 60, out var points, "audio:0"));
        Assert.Equal(new uint[] { 3, 4 }, points);
        identity = "media-b/decoder-a";
        Assert.False(portable.TryRead<uint>(itemId, UpstreamMode.Introduction, CacheEntryType.Chromaprint, 0, 60, out _, "audio:0"));
        identity = "media-a/decoder-b";
        Assert.False(portable.TryRead<uint>(itemId, UpstreamMode.Introduction, CacheEntryType.Chromaprint, 0, 60, out _, "audio:0"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
