using System.Text.Json;
using IntroSkipper.Configuration;
using SegmentScanner.Contracts;
using SegmentScanner.Engine;
using SegmentScanner.Engine.Media;
using Xunit;

namespace SegmentScanner.Tests;

public sealed class PluginModeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "segmentscanner-plugin-modes-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AuthoredCommercialChaptersProduceMultipleHeldCandidates()
    {
        var configuration = new PluginConfiguration { ChapterAnalyzerCommercialPattern = "Commercial" };
        var item = Item(chapters:
        [
            Chapter("Commercial", 10),
            Chapter("Programme", 40),
            Chapter("Commercial", 50),
            Chapter("Programme", 80),
        ]);

        var result = Assert.Single(await Detector().AnalyzeAsync(Job(item, RemoteAnalysisMode.Commercial, configuration), [Media(item)], RemoteAnalysisMode.Commercial, CancellationToken.None));

        ContractValidation.Result(result);
        Assert.Equal(AnalysisStatus.NeedsReview, result.Status);
        Assert.Equal(2, result.Segments.Count);
        Assert.All(result.Segments, segment => Assert.Equal(RemoteAnalysisMode.Commercial, segment.Type));
        Assert.Equal(new TimeRange(0, item.RuntimeTicks), Assert.Single(result.SearchedRanges));
    }

    [Fact]
    public async Task RecapKeepsIntroContextInScratchAndUsesThePluginChain()
    {
        var configuration = new PluginConfiguration { ChapterAnalyzerRecapPattern = "Recap" };
        var item = Item(
            chapters: [Chapter("Recap", 0), Chapter("Programme", 30)],
            context: [new ContextSegment(RemoteAnalysisMode.Introduction, TimeSpan.FromSeconds(30).Ticks, TimeSpan.FromSeconds(50).Ticks)]);

        var result = Assert.Single(await Detector().AnalyzeAsync(Job(item, RemoteAnalysisMode.Recap, configuration), [Media(item)], RemoteAnalysisMode.Recap, CancellationToken.None));

        ContractValidation.Result(result);
        Assert.Equal(AnalysisStatus.NeedsReview, result.Status);
        Assert.Equal(RemoteAnalysisMode.Recap, Assert.Single(result.Segments).Type);
        Assert.Equal(0, Assert.Single(result.SearchedRanges).StartTicks);
    }

    [Fact]
    public async Task AnimePreviewDerivesFromCreditsContextWhenNoSubtitleOrChapterWins()
    {
        var configuration = new PluginConfiguration { AnimePreviewFromCreditsEnd = true };
        var item = Item(
            isAnime: true,
            context: [new ContextSegment(RemoteAnalysisMode.Credits, TimeSpan.FromSeconds(60).Ticks, TimeSpan.FromSeconds(120).Ticks)]);

        var result = Assert.Single(await Detector().AnalyzeAsync(Job(item, RemoteAnalysisMode.Preview, configuration), [Media(item)], RemoteAnalysisMode.Preview, CancellationToken.None));

        ContractValidation.Result(result);
        var preview = Assert.Single(result.Segments);
        Assert.Equal(AnalysisStatus.NeedsReview, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(120).Ticks, preview.StartTicks);
        Assert.Equal(item.RuntimeTicks, preview.EndTicks);
        Assert.Equal(new TimeRange(0, item.RuntimeTicks), Assert.Single(result.SearchedRanges));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private PluginModeDetector Detector()
    {
        Directory.CreateDirectory(directory);
        return new PluginModeDetector(new WorkerConfiguration
        {
            Paths = new() { CacheDirectory = Path.Combine(directory, "cache"), AllowedMediaRoots = [directory] },
        });
    }

    private static JobSnapshot Job(JobItem item, RemoteAnalysisMode mode, PluginConfiguration configuration) => new()
    {
        JobId = Guid.NewGuid(),
        AttemptId = Guid.NewGuid(),
        Modes = [mode],
        Items = [item],
        ScanSettings = new JobScanSettings
        {
            Configuration = JsonSerializer.SerializeToElement(configuration, WireJson.Options),
            Action = "Default",
        },
    };

    private static JobItem Item(
        List<ChapterSnapshot>? chapters = null,
        List<ContextSegment>? context = null,
        bool isAnime = false) => new()
        {
            ItemId = Guid.NewGuid(),
            MediaSourceKey = "episode",
            SourcePath = "unused.mkv",
            FileVersionToken = "1",
            RuntimeTicks = TimeSpan.FromSeconds(180).Ticks,
            IsAnime = isAnime,
            Chapters = chapters ?? [],
            ContextSegments = context,
        };

    private static ResolvedMedia Media(JobItem item) => new(
        item,
        item.SourcePath,
        new MediaSignature { Length = 1, LastWriteUtcTicks = 1, TimelineEndTicks = item.RuntimeTicks, SampleHash = "fixture", StreamLayoutHash = "fixture" },
        [],
        0);

    private static ChapterSnapshot Chapter(string name, int seconds) => new()
    {
        Name = name,
        StartTicks = TimeSpan.FromSeconds(seconds).Ticks,
        Origin = ChapterOrigin.Authored,
    };
}
