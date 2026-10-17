using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SegmentScanner.Contracts;
using SegmentScanner.Engine;
using SegmentScanner.Engine.Credits;
using SegmentScanner.Engine.Export;
using SegmentScanner.Engine.Jellyfin;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;
using Xunit;

namespace SegmentScanner.Tests;

public sealed class SafetyContractTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "segmentscanner-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void FileWorkerChoicesKeepOneConnectedJob(int concurrentFiles)
    {
        var configuration = new WorkerConfiguration { Resources = new() { ConcurrentFiles = concurrentFiles } };

        configuration.Validate();
        Assert.Equal(1, configuration.Resources.ConcurrentJobs);
        Assert.Throws<InvalidDataException>(() => (configuration with { Resources = configuration.Resources with { ConcurrentFiles = 3 } }).Validate());
    }

    [Theory]
    [InlineData("vp9", "yuv420p")]
    [InlineData("av1", "yuv444p")]
    public async Task UnsupportedHardwareVideoInputsFailBeforeStartingFfmpeg(string codec, string pixelFormat)
    {
        var configuration = new WorkerConfiguration
        {
            Paths = new() { CacheDirectory = directory, Ffmpeg = Path.Combine(directory, "must-not-start.exe") },
            Credits = new() { VideoDecoder = CreditVideoDecoder.NvidiaNvdec },
        };
        var media = new ResolvedMedia(new() { ItemId = Guid.NewGuid(), MediaSourceKey = "fixture", SourcePath = "unused.mkv", FileVersionToken = "1" },
            "unused.mkv", new() { Length = 1, LastWriteUtcTicks = 1, TimelineEndTicks = TimeSpan.TicksPerSecond, SampleHash = "fixture", StreamLayoutHash = "fixture" },
            [new(0, "video", codec, null, 0, false, false)], 0) { VideoPixelFormat = pixelFormat };
        using var extractor = new FrameExtractor(configuration, media);
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(0, TimeSpan.TicksPerSecond, 2, CancellationToken.None));
    }

    [Fact]
    public void ExactTicksRoundTripAndNumericOrOverflowTicksAreRejected()
    {
        var range = new TimeRange(9007199254740993, 9007199254740994);
        Assert.Equal(range, WireJson.Read<TimeRange>(WireJson.Write(range)));
        Assert.Throws<System.Text.Json.JsonException>(() => WireJson.Read<TimeRange>("{\"startTicks\":9007199254740993,\"endTicks\":\"9007199254740994\"}"));
        Assert.Throws<System.Text.Json.JsonException>(() => WireJson.Read<TimeRange>("{\"startTicks\":\"9223372036854775808\",\"endTicks\":\"3\"}"));
        Assert.Throws<System.Text.Json.JsonException>(() => WireJson.Read<TimeRange>("{\"startTicks\":\"1\"}"));
    }

    [Fact]
    public void QueueActivityScopesCompleteExplicitlyAndResetWhenReentered()
    {
        var collector = new QueueActivityCollector();
        var itemId = Guid.NewGuid();
        using (collector.Begin(itemId, RemoteAnalysisMode.Credits, "TextCredits"))
        {
            var snapshot = collector.Snapshot();
            Assert.Equal(itemId, Assert.Single(snapshot.Active).ItemId);
            Assert.Empty(snapshot.Completed);
        }

        var inactive = collector.Snapshot();
        Assert.Empty(inactive.Active);
        Assert.Empty(inactive.Completed);

        collector.Complete(itemId);
        Assert.Equal(itemId, Assert.Single(collector.Snapshot().Completed));

        using (collector.Begin(itemId, RemoteAnalysisMode.Credits, "Keyframes"))
        {
            var reentered = collector.Snapshot();
            Assert.Equal(itemId, Assert.Single(reentered.Active).ItemId);
            Assert.Empty(reentered.Completed);
        }

        var disposed = collector.Snapshot();
        Assert.Empty(disposed.Active);
        Assert.Empty(disposed.Completed);
    }

    [Fact]
    public void Result_AcceptsSignedFileVersionTokenButRejectsInvalidTokensAndNegativeRevision()
    {
        var signed = Result() with { FileVersionToken = long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var roundTripped = WireJson.Read<AnalysisResult>(WireJson.Write(signed));

        Assert.Equal(signed.FileVersionToken, roundTripped.FileVersionToken);
        ContractValidation.Result(roundTripped);
        ContractValidation.Result(Result());
        Assert.Throws<InvalidDataException>(() => ContractValidation.Result(signed with { FileVersionToken = "-01" }));
        Assert.Throws<InvalidDataException>(() => ContractValidation.Result(signed with { FileVersionToken = "9223372036854775808" }));
        Assert.Throws<InvalidDataException>(() => ContractValidation.Result(signed with { ExpectedItemModeRevision = "-1" }));
    }

    [Theory]
    [InlineData(true, "12.1.0", true)]
    [InlineData(true, "12.2.0", true)]
    [InlineData(true, "12.3.0", false)]
    [InlineData(false, "12.1.0", false)]
    public async Task RemoteHandshakeAuthenticatesWithoutLegacyTokenHeaders(bool validCredential, string serverVersion, bool supportedServer)
    {
        const string token = "local-handshake-test-key";
        var variable = "SEGMENTSCANNER_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var response = new RemoteCapabilities { ServerId = "test-server", ServerVersion = serverVersion, PluginVersion = "12.0.5.0", SupportsNodeConfiguration = true };
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = await reader.ReadLineAsync(timeout.Token);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
            {
                var parts = line.Split(':', 2);
                headers.Add(parts[0], parts[1].Trim());
            }

            var authorized = AuthenticationHeaderValue.TryParse(headers.GetValueOrDefault("Authorization"), out var authorization)
                && authorization.Scheme.Equals("MediaBrowser", StringComparison.OrdinalIgnoreCase)
                && authorization.Parameter?.Contains($"Token=\"{token}\"", StringComparison.Ordinal) == true;
            var body = authorized ? WireJson.Write(response) : "";
            var status = authorized ? "200 OK" : "401 Unauthorized";
            var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(bytes, timeout.Token);
            return (request, headers);
        });
        Environment.SetEnvironmentVariable(variable, validCredential ? token : "invalid-key");
        try
        {
            using var client = new RemoteClient(new ServerConfiguration { BaseUrl = $"http://127.0.0.1:{port}", CredentialEnvironmentVariable = variable });
            if (validCredential && supportedServer)
            {
                var capabilities = await client.HandshakeAsync(timeout.Token);
                Assert.Equal("test-server", capabilities.ServerId);
            }
            else if (!validCredential)
            {
                var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.HandshakeAsync(timeout.Token));
                Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
            }
            else await Assert.ThrowsAsync<InvalidDataException>(() => client.HandshakeAsync(timeout.Token));

            var captured = await server;
            Assert.StartsWith("GET /IntroSkipper/Remote/v3/capabilities ", captured.request, StringComparison.Ordinal);
            Assert.False(captured.headers.ContainsKey("X-Emby-Token"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task ConnectedCreditsRequireCurrentProfileBeforeMediaAccess()
    {
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "model.json");
        await File.WriteAllTextAsync(manifest, "{}");
        var configuration = new WorkerConfiguration { Paths = new() { StateDirectory = Path.Combine(directory, "state"),
            CacheDirectory = Path.Combine(directory, "cache") }, Credits = new() { ModelManifest = manifest } };
        var job = new JobSnapshot { JobId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), ServerId = "server", Modes = [RemoteAnalysisMode.Credits],
            Items = [new() { ItemId = Guid.NewGuid(), MediaSourceKey = "episode", SourcePath = "unavailable.mkv", FileVersionToken = "0",
                ExpectedModeRevisions = new() { [RemoteAnalysisMode.Credits] = "0" } }] };
        Assert.Throws<InvalidDataException>(() => ContractValidation.Job(job));
        ContractValidation.Job(job with { ServerId = null });
        var hash = new CreditsDetector(configuration).ProfileHash();
        ContractValidation.Job(job with { CreditsProfileHash = hash });
        var changed = configuration with { Credits = configuration.Credits with { StartOffsetSeconds = 1 } };
        Assert.NotEqual(hash, new CreditsDetector(changed).ProfileHash());
        using var store = new WorkerStore(configuration.Paths.StateDirectory);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ScanService(changed, store).ScanAsync(job with { CreditsProfileHash = hash }, default));
    }

    [Fact]
    public void CreditsBackendsInvalidateTheGrainedAv1ProfileAndRejectInvalidEnums()
    {
        var configuration = new WorkerConfiguration();
        using var cpuJson = System.Text.Json.JsonDocument.Parse(WireJson.Write(configuration));
        Assert.False(cpuJson.RootElement.GetProperty("credits").TryGetProperty("inferenceBackend", out _));
        Assert.False(cpuJson.RootElement.GetProperty("credits").TryGetProperty("videoDecoder", out _));
        Assert.False(cpuJson.RootElement.GetProperty("credits").TryGetProperty("method", out _));
        Assert.False(cpuJson.RootElement.GetProperty("credits").TryGetProperty("useGpu", out _));
        // The published v2 CPU profile must not identify the new grain-free AV1 policy.
        Assert.NotEqual("8c5eafc8978b96d31596a4d6b1fab61c8774e3a23f9558aecc946729cea5ba03", new CreditsDetector(configuration).ProfileHash());

        var gpu = configuration with { Credits = configuration.Credits with { InferenceBackend = CreditInferenceBackend.OpenClGpu } };
        using var gpuJson = System.Text.Json.JsonDocument.Parse(WireJson.Write(gpu));
        Assert.Equal("OpenClGpu", gpuJson.RootElement.GetProperty("credits").GetProperty("inferenceBackend").GetString());
        Assert.NotEqual(new CreditsDetector(configuration).ProfileHash(), new CreditsDetector(gpu).ProfileHash());
        var nvidia = configuration with { Credits = configuration.Credits with { VideoDecoder = CreditVideoDecoder.NvidiaNvdec } };
        using var nvidiaJson = System.Text.Json.JsonDocument.Parse(WireJson.Write(nvidia));
        Assert.Equal("NvidiaNvdec", nvidiaJson.RootElement.GetProperty("credits").GetProperty("videoDecoder").GetString());
        Assert.NotEqual(new CreditsDetector(configuration).ProfileHash(), new CreditsDetector(nvidia).ProfileHash());
        Assert.Throws<InvalidDataException>(() => (configuration with { Credits = configuration.Credits with { InferenceBackend = (CreditInferenceBackend)99 } }).Validate());
        Assert.Throws<InvalidDataException>(() => (configuration with { Credits = configuration.Credits with { VideoDecoder = (CreditVideoDecoder)99 } }).Validate());
        Assert.Throws<InvalidDataException>(() => (configuration with { Credits = configuration.Credits with { Method = (CreditsDetectionMethod)99 } }).Validate());
        var original = configuration with { Credits = configuration.Credits with { Method = CreditsDetectionMethod.IntroSkipper } };
        var originalHash = new CreditsDetector(original).ProfileHash();
        Assert.NotEqual(new CreditsDetector(configuration).ProfileHash(), originalHash);
        Assert.Equal("fad06638becefb342a49be57b26bee98506c3ff17678cfb23a4bdd921463858b", originalHash);
        Assert.Equal(originalHash, new CreditsDetector(original with { Credits = original.Credits with
        {
            InferenceBackend = CreditInferenceBackend.OpenClGpu, VideoDecoder = CreditVideoDecoder.NvidiaNvdec,
            ModelManifest = "missing-model.json",
        } }).ProfileHash());
        var originalGpu = original with { Credits = original.Credits with { UseGpu = true, VideoDecoder = CreditVideoDecoder.NvidiaNvdec } };
        Assert.NotEqual(originalHash, new CreditsDetector(originalGpu).ProfileHash());
        Assert.Equal(new CreditsDetector(originalGpu).ProfileHash(), new CreditsDetector(originalGpu with { Credits = originalGpu.Credits with
        {
            InferenceBackend = CreditInferenceBackend.OpenClGpu,
            ModelManifest = "missing-model.json",
        } }).ProfileHash());
        Assert.NotEqual(new CreditsDetector(originalGpu).ProfileHash(), new CreditsDetector(originalGpu with
        {
            Credits = originalGpu.Credits with { VideoDecoder = CreditVideoDecoder.IntelQuickSync },
        }).ProfileHash());
    }

    [Fact]
    public void MappingUsesDirectoryBoundariesAndLongestPrefixAndRejectsTraversal()
    {
        var paths = new WorkerPaths { Mappings = [new() { ServerPrefix = "/media", WorkerPrefix = directory }, new() { ServerPrefix = "/media/tv", WorkerPrefix = Path.Combine(directory, "tv") }] };
        var mapper = new PathMapper(paths);
        Assert.Equal(Path.Combine(directory, "tv", "Épisode.mkv"), mapper.Resolve("/media/tv/Épisode.mkv"));
        Assert.Throws<InvalidDataException>(() => mapper.Resolve("/media/tv/../private.mkv"));
        Assert.Throws<InvalidDataException>(() => mapper.Resolve("/media-other/Episode.mkv"));
        Assert.Throws<InvalidDataException>(() => mapper.Resolve("https://example.invalid/Episode.mkv"));
    }

    [Fact]
    public void CreditsOrderedGroupsRejectStaticTextAndApplyLengthAndOpeningGates()
    {
        List<FrameEvidence> Frames(int count, double entropy = 0.65, double y = 0.5, int opening = 6)
            => Enumerable.Range(0, 11).Select(i => Evidence(i * 2, i >= 5) with { Features = new()
            {
                NormalizedByteEntropy = i >= 5 ? entropy : 0.6, FlatnessScore = 0.8,
                ScoreMapTextCellCount = i >= 5 ? count == 12 && i - 5 >= opening ? 9 : count : 0,
                ScoreMapTextSourceCenterX = i >= 5 && count > 0 ? 0.5 : null,
                ScoreMapTextSourceCenterY = i >= 5 && count > 0 ? y : null,
            } }).ToList();
        var strong = Assert.Single(CreditPolicy.Candidates(Frames(12), new(), 0, 22 * TimeSpan.TicksPerSecond, 2));
        Assert.Equal(10 * TimeSpan.TicksPerSecond, strong.StartTicks);
        Assert.Equal(20 * TimeSpan.TicksPerSecond, strong.RawEndTicks);
        Assert.Equal(22 * TimeSpan.TicksPerSecond, strong.EndTicks);
        Assert.True(strong.ReachesFileEnd);
        Assert.Equal(CandidateDecision.Review, strong.Decision);
        var variedTail = Frames(12).Append(Evidence(22, false) with { Features = new()
            { NormalizedByteEntropy = 0.8, FlatnessScore = 0.8, ScoreMapTextCellCount = 0 } }).ToList();
        var finalSampleException = Assert.Single(CreditPolicy.Candidates(variedTail, new(), 0, 24 * TimeSpan.TicksPerSecond, 2));
        Assert.Equal(20 * TimeSpan.TicksPerSecond, finalSampleException.RawEndTicks);
        Assert.Equal(24 * TimeSpan.TicksPerSecond, finalSampleException.EndTicks);
        Assert.True(finalSampleException.ReachesFileEnd);
        var shortOpening = Assert.Single(CreditPolicy.Candidates(Frames(12, opening: 4), new(), 0, 22 * TimeSpan.TicksPerSecond, 2));
        Assert.Equal(18 * TimeSpan.TicksPerSecond, shortOpening.StartTicks);
        Assert.Empty(CreditPolicy.Candidates(Frames(12, entropy: 0.8), new(), 0, 22 * TimeSpan.TicksPerSecond, 2, out var staticRejections));
        Assert.Contains("StaticTextGroupRejected", staticRejections);
        var movingCounts = Frames(12, entropy: 0.8).Select((f, i) => i < 5 ? f : f with
            { Features = f.Features! with { ScoreMapTextCellCount = 10 + (i - 5) * 2 } }).ToList();
        Assert.Single(CreditPolicy.Candidates(movingCounts, new(), 0, 22 * TimeSpan.TicksPerSecond, 2));
        var laterAnchor = Frames(12, entropy: 0.8).Select((f, i) => i < 5 ? f : f with
            { Features = f.Features! with { ScoreMapTextCellCount = i == 5 ? 10 : i < 9 ? 12 : 13 } }).ToList();
        Assert.Empty(CreditPolicy.Candidates(laterAnchor, new(), 0, 22 * TimeSpan.TicksPerSecond, 2, out var anchoredRejections));
        Assert.Contains("StaticTextGroupRejected", anchoredRejections);
        Assert.Empty(CreditPolicy.Candidates(Frames(12).Take(10).ToList(), new(), 0, 20 * TimeSpan.TicksPerSecond, 2, out var shortRejections));
        Assert.Contains("FiveSampleGroupRejected", shortRejections);
        Assert.Empty(CreditPolicy.Candidates(Frames(12, y: 0.9), new(), 0, 22 * TimeSpan.TicksPerSecond, 2, out var positionRejections));
        Assert.Contains("TextPositionRejected", positionRejections);
        Assert.Single(CreditPolicy.Candidates(Frames(8, entropy: 0.2, y: 0.9), new(), 0, 22 * TimeSpan.TicksPerSecond, 2));
    }

    [Fact]
    public void CreditsOrderedGroupsJoinTenSecondsBeforePositionAndKeepLongStoryGapsSeparate()
    {
        List<FrameEvidence> Frames(int secondStart, int groupLength)
            => Enumerable.Range(0, 70).Select(i =>
            {
                var first = i >= 5 && i < 5 + groupLength;
                var second = i >= secondStart && i < secondStart + groupLength;
                var count = first || second ? 12 : 0;
                return new FrameEvidence(i.ToString(System.Globalization.CultureInfo.InvariantCulture), i * 2 * TimeSpan.TicksPerSecond, new()
                { NormalizedByteEntropy = 0.65, FlatnessScore = 0.8, ScoreMapTextCellCount = count,
                    ScoreMapTextSourceCenterX = count > 0 ? 0.5 : null, ScoreMapTextSourceCenterY = count > 0 ? 0.5 : null }, null);
            }).ToList();
        var joined = Assert.Single(CreditPolicy.Candidates(Frames(15, 6), new(), 0, 140 * TimeSpan.TicksPerSecond, 2));
        Assert.Contains("ShortGapJoined", joined.Reasons);
        Assert.Equal(40 * TimeSpan.TicksPerSecond, joined.EndTicks);
        var separated = CreditPolicy.Candidates(Frames(24, 14), new(), 0, 140 * TimeSpan.TicksPerSecond, 2);
        Assert.Equal(2, separated.Count);
        Assert.All(separated, c => Assert.False(c.ReachesFileEnd));
    }

    [Theory]
    [InlineData(0.82, true, 26)]
    [InlineData(0.82, false, 0)]
    [InlineData(0.65, true, 0)]
    public void CreditsOrderedGroupsRejectSparseHighEntropyNarrowSceneTextBeforeMerging(double entropy, bool narrow, int expectedStart)
    {
        var sparseSeconds = new[] { 0, 6, 12, 20 };
        var sparseCounts = new[] { 16, 41, 24, 15 };
        var frames = Enumerable.Range(0, 20).Select(i =>
        {
            var seconds = i * 2;
            var sparse = Array.IndexOf(sparseSeconds, seconds);
            var dense = seconds is >= 26 and <= 36;
            var count = sparse >= 0 ? sparseCounts[sparse] : dense ? 327 + i : 0;
            var text = count > 0;
            return new FrameEvidence(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), seconds * TimeSpan.TicksPerSecond, new()
            {
                TextFraction = text ? dense ? 0.05 : 0.003 : 0,
                TextCenterX = dense ? 0.5 : 0.2 + sparse * 0.15,
                TextCenterY = dense ? 0.5 : 0.3 + sparse * 0.1,
                TextWidth = dense ? 0.72 : narrow ? 0.12 : 0.6,
                TextHeight = dense ? 0.5 : narrow ? 0.03 : 0.3,
                NearBlackRatio = 0.1,
                TemporalDifference = 64,
                NormalizedByteEntropy = dense ? 0.65 : entropy,
                FlatnessScore = 0.8,
                ScoreMapTextCellCount = count,
                ScoreMapTextSourceCenterX = text ? dense ? 0.5 : 0.2 + sparse * 0.15 : null,
                ScoreMapTextSourceCenterY = text ? dense ? 0.5 : 0.3 + sparse * 0.1 : null,
            }, null);
        }).ToList();

        var candidate = Assert.Single(CreditPolicy.Candidates(frames, new(), 0, 40 * TimeSpan.TicksPerSecond, 2, out var rejections));

        Assert.Equal(expectedStart * TimeSpan.TicksPerSecond, candidate.StartTicks);
        Assert.Equal(narrow && entropy >= 0.7, rejections.Contains("SparseSceneTextGroupRejected"));
    }

    [Theory]
    [InlineData(99999999L, true)]
    [InlineData(100000000L, true)]
    [InlineData(100000001L, false)]
    public void CreditsJoinGapsAtMostTenSeconds(long gapTicks, bool joins)
    {
        var first = Segment("first", RemoteAnalysisMode.Credits, 10, 20);
        var second = Segment("second", RemoteAnalysisMode.Credits, 30, 40) with
        {
            StartTicks = first.EndTicks + gapTicks,
            EndTicks = first.EndTicks + gapTicks + 10 * TimeSpan.TicksPerSecond
        };
        var joined = CreditPolicy.JoinShortGaps([first, second]);
        Assert.Equal(joins ? 1 : 2, joined.Count);
        Assert.Equal(second.EndTicks, joined[^1].EndTicks);
        if (joins) Assert.Equal(CandidateDecision.Review, joined[0].Decision);
    }

    [Fact]
    public void CreditsJoinChainsWithoutLosingBoundaryOrWeakEvidence()
    {
        var first = Segment("first", RemoteAnalysisMode.Credits, 10, 20) with
        {
            RawStartTicks = 9 * TimeSpan.TicksPerSecond, StartUncertaintyTicks = 2500000,
            Reasons = ["SustainedTextLayout"]
        };
        var middle = Segment("middle", RemoteAnalysisMode.Credits, 29, 40) with
        {
            Decision = CandidateDecision.Review, Reasons = ["RefinementUnknown"]
        };
        var last = Segment("last", RemoteAnalysisMode.Credits, 49, 60) with
        {
            RawEndTicks = 59 * TimeSpan.TicksPerSecond, EndUncertaintyTicks = 5000000,
            ReachesFileEnd = true, Reasons = ["EndUncertain"]
        };
        var joined = Assert.Single(CreditPolicy.JoinShortGaps([last, first, middle]));
        Assert.Equal(first.CandidateId, joined.CandidateId);
        Assert.Equal(first.StartTicks, joined.StartTicks);
        Assert.Equal(first.RawStartTicks, joined.RawStartTicks);
        Assert.Equal(first.StartUncertaintyTicks, joined.StartUncertaintyTicks);
        Assert.Equal(last.EndTicks, joined.EndTicks);
        Assert.Equal(last.RawEndTicks, joined.RawEndTicks);
        Assert.Equal(last.EndUncertaintyTicks, joined.EndUncertaintyTicks);
        Assert.True(joined.ReachesFileEnd);
        Assert.Equal(CandidateDecision.Review, joined.Decision);
        Assert.Equal("Review", joined.ConfidenceTier);
        Assert.Contains("RefinementUnknown", joined.Reasons);
        Assert.Contains("EndUncertain", joined.Reasons);
        Assert.Single(joined.Reasons, r => r == "ShortGapJoined");
        Assert.Equal(2, CreditPolicy.JoinShortGaps([first, middle with { Decision = CandidateDecision.Reject }]).Count);
    }

    [Fact]
    public void ChapterTimelineRetainsShortGapsAndPrecisionBeyondTwentyFourHours()
    {
        var intro = Segment("intro", RemoteAnalysisMode.Introduction, 10, 20);
        var credits = Segment("credits-1", RemoteAnalysisMode.Credits, 21, 30);
        var credits2 = Segment("credits-2", RemoteAnalysisMode.Credits, 31, 40);
        var xml = ChapterTimeline.Xml("media", 41 * TimeSpan.TicksPerSecond, [intro, credits, credits2]);
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        var atoms = document.Descendants("ChapterAtom").ToList();
        Assert.Equal(7, atoms.Count);
        Assert.Contains(atoms, a => a.Element("ChapterTimeStart")!.Value == "00:00:30.0000000" && a.Element("ChapterTimeEnd")!.Value == "00:00:31.0000000");
        Assert.Equal(xml, ChapterTimeline.Xml("media", 41 * TimeSpan.TicksPerSecond, [intro, credits, credits2]));
        Assert.Equal("25:00:00.0000001", ChapterTimeline.FormatTicks(25 * TimeSpan.TicksPerHour + 1));
        var item = new JobItem { ItemId = Guid.NewGuid(), MediaSourceKey = "media", SourcePath = "media", FileVersionToken = "1",
            Chapters = [new() { Name = "Intro", StartTicks = 10 * TimeSpan.TicksPerSecond, Origin = ChapterOrigin.Authored, OriginId = "original-container" }] };
        var media = new ResolvedMedia(item, item.SourcePath, new() { Length = 1, LastWriteUtcTicks = 1, TimelineEndTicks = 41 * TimeSpan.TicksPerSecond, SampleHash = "fixture", StreamLayoutHash = "fixture" }, [], 0);
        Assert.Equal(ChapterOrigin.Authored, Assert.Single(MediaProbe.ApplyGeneratedChapterGuard(media, false).Item.Chapters).Origin);
        Assert.Equal(ChapterOrigin.Unknown, Assert.Single(MediaProbe.ApplyGeneratedChapterGuard(media, true).Item.Chapters).Origin);
        Assert.Equal(ChapterOrigin.Authored, Assert.Single(MediaProbe.ApplyGeneratedChapterGuard(media with { Item = item with { ChaptersAreAuthoritative = true } }, true).Item.Chapters).Origin);
    }

    [Fact]
    public void CreditsLongGapsUseMeasuredFlatnessAndEntropyDependentTextLevels()
    {
        var first = Segment("first", RemoteAnalysisMode.Credits, 10, 20);
        var second = Segment("second", RemoteAnalysisMode.Credits, 34, 44);
        List<SegmentCandidate> Run(double flatness, double entropy, Func<int, int> cells, bool missing = false)
        {
            var frames = Enumerable.Range(0, 30).Select(i =>
            {
                var seconds = i * 2;
                var frame = Evidence(seconds, seconds is >= 10 and <= 20 or >= 34 and <= 44);
                var gap = seconds is > 20 and < 34;
                var count = gap ? cells(i) : 0;
                return frame with { Features = frame.Features! with
                {
                    NormalizedByteEntropy = gap ? entropy : 0.7, FlatnessScore = gap ? flatness : 0.8,
                    ScoreMapTextCellCount = count,
                    ScoreMapTextSourceCenterX = count > 0 ? 0.5 : null, ScoreMapTextSourceCenterY = count > 0 ? 0.5 : null
                }, Error = missing && seconds == 26 ? "Unavailable" : null };
            }).ToList();
            return CreditPolicy.ApplySpanRules([first, second], frames, new(), 60 * TimeSpan.TicksPerSecond, 2);
        }
        Assert.Contains("FlatGapJoined", Assert.Single(Run(0.30, 0.8, _ => 0)).Reasons);
        Assert.Contains("TextGapJoined", Assert.Single(Run(0.8, 0.62, _ => 60)).Reasons);
        Assert.Single(Run(0.8, 0.619, i => i % 2 == 0 ? 3 : 4));
        Assert.Equal(2, Run(0.8, 0.62, _ => 59).Count);
        Assert.Equal(2, Run(0.8, 0.619, _ => 3).Count);
        Assert.All(Run(0.1, 0.1, _ => 100, missing: true), c => Assert.Contains("GapEvidenceIncomplete", c.Reasons));
        var exactlyTen = second with { StartTicks = first.EndTicks + 10 * TimeSpan.TicksPerSecond };
        var flatFrames = Enumerable.Range(0, 30).Select(i => SpanEvidence(i * 2, 0.1)).ToList();
        Assert.Equal(2, CreditPolicy.ApplySpanRules([first, exactlyTen], flatFrames, new(), 60 * TimeSpan.TicksPerSecond, 2).Count);
    }

    [Fact]
    public void CreditsPositionFilterUsesStrictEntropyBoundsAndWholeGroupEligibility()
    {
        FrameEvidence Positioned(int seconds, double x, double y, double entropy = 0.74, int cells = 9)
            => SpanEvidence(seconds, 0.5) with { Features = SpanEvidence(seconds, 0.5).Features! with
                { ScoreMapTextCellCount = cells, ScoreMapTextSourceCenterX = x, ScoreMapTextSourceCenterY = y, NormalizedByteEntropy = entropy } };
        bool? Matches(double x, double y, double entropy = 0.74, int cells = 9)
            => CreditPolicy.GroupPositionMatches([Positioned(10, x, y, entropy, cells)]);
        Assert.False(Matches(0.10, 0.5));
        Assert.False(Matches(0.90, 0.5));
        Assert.False(Matches(0.5, 0.10));
        Assert.False(Matches(0.5, 0.70));
        Assert.True(Matches(0.5, 0.699, 0.749));
        Assert.False(Matches(0.5, 0.65, 0.75));
        Assert.True(Matches(0.5, 0.649, 0.75));
        var group = Enumerable.Range(0, 5).Select(i => Positioned(10 + i * 2, 0.5, i < 3 ? 0.5 : 0.9)).ToList();
        Assert.True(CreditPolicy.GroupPositionMatches(group));
        group[2] = Positioned(14, 0.5, 0.9);
        Assert.False(CreditPolicy.GroupPositionMatches(group));
        var lowCount = group.Select(f => f with { Features = f.Features! with { ScoreMapTextCellCount = 8 } }).ToList();
        Assert.True(CreditPolicy.GroupPositionMatches(lowCount));
        var incomplete = group.Select(f => f with { Features = f.Features! with { ScoreMapTextSourceCenterY = null } }).ToList();
        Assert.Null(CreditPolicy.GroupPositionMatches(incomplete));
    }

    [Fact]
    public void CreditsAbsorbOnlySupportedTailsAndKeepRawEndAndPlaybackOffset()
    {
        var segment = Segment("credits", RemoteAnalysisMode.Credits, 10, 52) with
            { RawStartTicks = 10 * TimeSpan.TicksPerSecond, RawEndTicks = 52 * TimeSpan.TicksPerSecond, EndUncertaintyTicks = 2500000 };
        var frames = Enumerable.Range(0, 30).Select(i => SpanEvidence(i * 2, i * 2 > 52 ? 0.1 : 0.8)).ToList();
        var joined = Assert.Single(CreditPolicy.ApplySpanRules([segment], frames, new(), 60 * TimeSpan.TicksPerSecond, 2));
        Assert.Equal(60 * TimeSpan.TicksPerSecond, joined.EndTicks);
        Assert.Equal(segment.RawEndTicks, joined.RawEndTicks);
        Assert.Equal(segment.EndUncertaintyTicks, joined.EndUncertaintyTicks);
        Assert.True(joined.ReachesFileEnd);
        Assert.Equal(CandidateDecision.Review, joined.Decision);
        Assert.Contains("FlatTailAbsorbed", joined.Reasons);
        var varied = frames.Select(f => f with { Features = f.Features! with { FlatnessScore = 0.8 } }).ToList();
        Assert.Equal(segment.EndTicks, Assert.Single(CreditPolicy.ApplySpanRules([segment], varied, new(), 60 * TimeSpan.TicksPerSecond, 2)).EndTicks);
        var incomplete = frames.Select(f => f.TimestampTicks == 56 * TimeSpan.TicksPerSecond ? f with { Error = "Unavailable" } : f).ToList();
        var held = Assert.Single(CreditPolicy.ApplySpanRules([segment], incomplete, new(), 60 * TimeSpan.TicksPerSecond, 2));
        Assert.Equal(segment.EndTicks, held.EndTicks);
        Assert.Contains("TailEvidenceIncomplete", held.Reasons);
        var offset = Assert.Single(CreditPolicy.ApplySpanRules([segment], frames, new(), 60 * TimeSpan.TicksPerSecond, 2, -TimeSpan.TicksPerSecond));
        Assert.Equal(59 * TimeSpan.TicksPerSecond, offset.EndTicks);
        Assert.False(offset.ReachesFileEnd);
        var finalTwo = segment with { EndTicks = 56 * TimeSpan.TicksPerSecond, RawEndTicks = 56 * TimeSpan.TicksPerSecond };
        var finalTwoFrames = varied.Select(f => f.TimestampTicks == 56 * TimeSpan.TicksPerSecond
            ? f with { Features = f.Features! with { TextFraction = 0.1 } } : f).ToList();
        Assert.Contains("FinalSamplesReachEOF", Assert.Single(CreditPolicy.ApplySpanRules([finalTwo], finalTwoFrames, new(), 60 * TimeSpan.TicksPerSecond, 2)).Reasons);
        var legacy = frames.Select(f => f with { Features = f.Features! with { NormalizedByteEntropy = null } }).ToList();
        Assert.Equal(segment.EndTicks, Assert.Single(CreditPolicy.ApplySpanRules([segment], legacy, new(), 60 * TimeSpan.TicksPerSecond, 2)).EndTicks);
    }

    [Fact]
    public void WholeModeReviewIsDurableAndStaleOrFailedResultsCannotClearIt()
    {
        Directory.CreateDirectory(directory);
        using var store = new WorkerStore(directory);
        var result = Result();
        store.SaveProposals([result]);
        var decisions = Decisions(result, CandidateDecision.Accept);
        var accepted = Assert.Single(store.Review(new() { Results = [result] }, decisions));
        Assert.Equal(1, accepted.ResultRevision);
        Assert.Equal(1, Assert.Single(store.Review(new() { Results = [result] }, decisions)).ResultRevision);
        var stale = result with { AnalysisId = Guid.NewGuid() };
        store.SaveProposals([stale]);
        Assert.Throws<InvalidDataException>(() => store.Review(new() { Results = [stale] }, Decisions(stale, CandidateDecision.Reject)));
        var failed = result with { AnalysisId = Guid.NewGuid(), ExpectedItemModeRevision = "1", Status = AnalysisStatus.Failed, Segments = [] };
        store.SaveProposals([failed]);
        Assert.Throws<InvalidDataException>(() => store.Review(new() { Results = [failed] }, new() { Decisions = [], AcceptNoSegments = [failed.AnalysisId] }));
        Assert.Single(store.GetCanonical(result)!.Result.Segments);
        var negative = result with { AnalysisId = Guid.NewGuid(), ExpectedItemModeRevision = "1", Status = AnalysisStatus.CompletedNoSegments, Segments = [], SearchedRanges = [new(0, result.TimelineEndTicks)] };
        store.SaveProposals([negative]);
        Assert.Empty(Assert.Single(store.Review(new() { Results = [negative] }, new() { Decisions = [], AcceptNoSegments = [negative.AnalysisId] })).Result.Segments);
    }

    [Fact]
    public async Task ExportsPreserveEditedFilesMediaAndUnfamiliarDestinationsAcrossRestart()
    {
        Directory.CreateDirectory(directory);
        var media = Path.Combine(directory, "Episode.mkv");
        await File.WriteAllBytesAsync(media, Enumerable.Range(0, 1000).Select(n => (byte)n).ToArray());
        var bytes = await File.ReadAllBytesAsync(media);
        var hash = WireJson.Sha256(bytes);
        var signature = new MediaSignature { Length = bytes.Length, LastWriteUtcTicks = File.GetLastWriteTimeUtc(media).Ticks, TimelineEndTicks = 100 * TimeSpan.TicksPerSecond,
            SampleHash = WireJson.Sha256(bytes), StreamLayoutHash = "fixture" };
        // A file shorter than the sampling block has one unique region at offset zero.
        var result = Result() with { SourcePath = media, MediaValidation = signature };
        var configuration = new WorkerConfiguration { Paths = new() { AllowedMediaRoots = [directory] }, Exports = new() { Enabled = true, XmlLayout = "Adjacent" } };
        List<CanonicalResult> accepted;
        using (var store = new WorkerStore(Path.Combine(directory, "state")))
        {
            store.SaveProposals([result]);
            accepted = store.Review(new() { Results = [result] }, Decisions(result, CandidateDecision.Accept));
            var service = new ExportService(configuration, store);
            var serverProposal = result with { AnalysisId = Guid.NewGuid(), ServerId = "server-context" };
            store.SaveProposals([serverProposal]);
            var localServerReview = store.Review(new() { Results = [serverProposal] }, Decisions(serverProposal, CandidateDecision.Accept));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportAsync(localServerReview, true, true, true, CancellationToken.None));
            await File.WriteAllTextAsync(media + ".chapters.xml", "unfamiliar authored XML");
            Assert.Contains(await service.ExportAsync(accepted, true, true, false, CancellationToken.None), o => o.Status == "Conflict");
            Assert.Equal("unfamiliar authored XML", await File.ReadAllTextAsync(media + ".chapters.xml"));
            File.Delete(media + ".chapters.xml");
            var itemKey = $"offline/{result.ItemId:N}/{result.MediaSourceKey}";
            var xml = ChapterTimeline.Xml(itemKey, result.TimelineEndTicks, accepted[0].Result.Segments);
            store.QueueExport(itemKey, "matroska-xml", media + ".chapters.xml", accepted[0].ResultRevision, xml);
            await File.WriteAllTextAsync(media + ".chapters.xml", xml);
            // Simulate a crash after the atomic file creation but before its completion receipt.
            Assert.All(await service.ExportAsync(accepted, true, true, true, CancellationToken.None), o => Assert.True(o.Status is "Written" or "Unchanged"));
        }
        await File.WriteAllTextAsync(media + ".chapters.xml", "human edit");
        using (var store = new WorkerStore(Path.Combine(directory, "state")))
        {
            var outcomes = await new ExportService(configuration, store).ExportAsync(accepted, true, true, true, CancellationToken.None);
            Assert.Contains(outcomes, o => o.Status == "Conflict");
            Assert.Equal("human edit", await File.ReadAllTextAsync(media + ".chapters.xml"));
            Assert.Equal(hash, WireJson.Sha256(await File.ReadAllBytesAsync(media)));
            Assert.Equal(signature.LastWriteUtcTicks, File.GetLastWriteTimeUtc(media).Ticks);
            await File.WriteAllBytesAsync(media, [1, 2, 3]);
            await Assert.ThrowsAsync<IOException>(() => new ExportService(configuration, store).ExportAsync(accepted, true, true, true, CancellationToken.None));
        }
    }

    [Fact]
    public void RecoveryRemovesOnlyOwnedOrphanFramesAndAllowsOneStateWriter()
    {
        var cache = Path.Combine(directory, "cache");
        var orphan = Path.Combine(cache, "frames", Guid.NewGuid().ToString("N"));
        var unfamiliar = Path.Combine(cache, "frames", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(unfamiliar);
        File.WriteAllText(Path.Combine(orphan, ".segmentscanner-owner"), "SegmentScannerFrames/v1\n2147483647\n0");
        File.WriteAllText(Path.Combine(orphan, "frame.jpg"), "owned temporary");
        File.WriteAllText(Path.Combine(unfamiliar, "frame.jpg"), "unfamiliar");
        FrameExtractor.CleanupOwnedTemporary(new() { Paths = new() { CacheDirectory = cache } });
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(unfamiliar));
        var state = Path.Combine(directory, "state");
        using (var store = new WorkerStore(state)) Assert.Throws<IOException>(() => new WorkerStore(state));
        using var reopened = new WorkerStore(state);
        Assert.Empty(reopened.AllCanonical());
    }

    [Fact]
    public void ReportPreservesItsInputAndExistingReviewDecisions()
    {
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "review.decisions.json");
        var output = Path.Combine(directory, "review.html");
        File.WriteAllText(input, "input bundle");
        Assert.Throws<InvalidDataException>(() => Reporting.Report(new() { Results = [] }, output, input));
        Assert.Equal("input bundle", File.ReadAllText(input));
        Assert.False(File.Exists(output));
        Reporting.Report(new() { Results = [] }, output, Path.Combine(directory, "results.json"));
        Assert.Equal("input bundle", File.ReadAllText(input));
    }

    [Fact]
    public void EvaluationPairsSafetyWithRecallAndReviewBurden()
    {
        var result = Result();
        var labels = new LabelDocument { Items = [new() { ItemId = result.ItemId, Mode = RemoteAnalysisMode.Credits,
            Segments = [new() { StartTicks = 10 * TimeSpan.TicksPerSecond, EndTicks = 20 * TimeSpan.TicksPerSecond }], Programme = [new(0, 10 * TimeSpan.TicksPerSecond)] }] };
        var unsafeResult = result with { Segments = [Segment("credits", RemoteAnalysisMode.Credits, 5, 20)] };
        var score = Evaluation.Score(new() { Results = [unsafeResult] }, labels);
        Assert.Equal(5 * TimeSpan.TicksPerSecond, score.ProgrammeSkippedTicks);
        Assert.Equal(1, score.AcceptedCoverage);
        var review = Evaluation.Score(new() { Results = [result with { Status = AnalysisStatus.NeedsReview, Segments = [result.Segments[0] with { Decision = CandidateDecision.Review }] }] }, labels);
        Assert.Equal(0, review.AcceptedCoverage);
        Assert.Equal(1, review.ReviewRate);
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        PathMapper.EnsureWithin(directory, Path.GetTempPath());
        PathMapper.RejectLinks(directory);
        Directory.Delete(directory, recursive: true);
    }

    private static FrameEvidence Evidence(int seconds, bool text) => new(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), seconds * TimeSpan.TicksPerSecond,
        new() { TextFraction = text ? 0.1 : 0, TextCenterX = 0.5, TextCenterY = 0.5, TextWidth = 0.6, TextHeight = 0.5, NearBlackRatio = 0.1, TemporalDifference = 64 }, null);
    private static FrameEvidence SpanEvidence(int seconds, double flatness) => Evidence(seconds, seconds is >= 10 and <= 52) with
        { Features = Evidence(seconds, seconds is >= 10 and <= 52).Features! with { NormalizedByteEntropy = 0.7, FlatnessScore = flatness, ScoreMapTextCellCount = 0 } };
    private static SegmentCandidate Segment(string id, RemoteAnalysisMode mode, int start, int end) => new() { CandidateId = id, Type = mode,
        StartTicks = start * TimeSpan.TicksPerSecond, EndTicks = end * TimeSpan.TicksPerSecond, Decision = CandidateDecision.Accept };
    private static AnalysisResult Result() => new() { AnalysisId = Guid.NewGuid(), JobId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), ItemId = Guid.Parse("a108812f-c05a-468a-8b9f-000000000005"),
        MediaSourceKey = "fixture", SourcePath = "fixture.mkv", FileVersionToken = "1", Mode = RemoteAnalysisMode.Credits, Status = AnalysisStatus.CompletedWithSegments,
        TimelineEndTicks = 100 * TimeSpan.TicksPerSecond, Detector = new() { Id = "fixture", ProfileHash = "fixture" }, Segments = [Segment("credits", RemoteAnalysisMode.Credits, 10, 20)] };
    private static ReviewDocument Decisions(AnalysisResult result, CandidateDecision decision) => new() { Decisions = result.Segments.Select(s => new ReviewDecision
        { AnalysisId = result.AnalysisId, CandidateId = s.CandidateId, Decision = decision }).ToList() };
}
