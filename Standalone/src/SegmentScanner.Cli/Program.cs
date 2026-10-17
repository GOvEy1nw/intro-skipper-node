using System.Text.Json;
using SegmentScanner.Contracts;
using SegmentScanner.Engine;
using SegmentScanner.Engine.Export;
using SegmentScanner.Engine.Jellyfin;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;
using SegmentScanner.Engine.Settings;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    return await RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException) { await Console.Error.WriteLineAsync("Cancelled; previously accepted state is preserved."); return 130; }
catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
{ await Console.Error.WriteLineAsync(exception.Message); return 2; }
catch (HttpRequestException exception)
{ await Console.Error.WriteLineAsync(exception.Message); return exception.StatusCode == System.Net.HttpStatusCode.Conflict ? 6 : 3; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
{ await Console.Error.WriteLineAsync(exception.Message); return 3; }

static async Task<int> RunAsync(string[] args, CancellationToken token)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Help(); return 0; }
    var command = args[0];
    var options = Parse(args.Skip(1).ToArray(), command);
    if (options.ContainsKey("help")) { Help(); return 0; }
    string Required(string name) => options.GetValueOrDefault(name) ?? throw new InvalidDataException("Missing --" + name);
    var apply = options.ContainsKey("apply");
    if (options.ContainsKey("dry-run") && apply) throw new InvalidDataException("Choose either --dry-run or --apply.");
    if (command == "settings")
    {
        await using var server = await SettingsServer.StartAsync(options.GetValueOrDefault("config") ?? "segmentscanner.json", token);
        Console.WriteLine("Open scanner settings: " + server.BrowserUrl);
        if (!options.ContainsKey("no-browser"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(server.BrowserUrl) { UseShellExecute = true });
        await server.WaitForShutdownAsync(token);
        return 0;
    }
    if (command == "report")
    {
        var output = OutputPath(Required("out"), ".html", Required("results"));
        Reporting.Report(Read<ResultBundle>(Required("results")), output, Required("results"));
        Console.WriteLine(output);
        return 0;
    }
    if (command is "evaluate" or "compare")
    {
        var labels = Read<LabelDocument>(Required("labels"));
        object report = command == "evaluate" ? Evaluation.Score(Read<ResultBundle>(Required("results")), labels)
            : Compare(Read<ResultBundle>(Required("baseline")), Read<ResultBundle>(Required("candidate")), labels);
        var output = OutputPath(Required("out"), ".json", Required("labels"));
        AtomicFile.Write(output, WireJson.Write(report));
        Console.WriteLine(output);
        return 0;
    }
    var configuration = ConfigurationLoader.Load(options.GetValueOrDefault("config") ?? "segmentscanner.json");
    if (command == "doctor")
    {
        var checks = await Diagnostics.DoctorAsync(configuration, token);
        Console.WriteLine(WireJson.Write(checks));
        return checks.All(c => c.Passed) ? 0 : 3;
    }
    using var store = new WorkerStore(configuration.Paths.StateDirectory);
    if (command == "scan")
    {
        var manifest = Read<JobSnapshot>(Required("manifest"));
        if (options.TryGetValue("modes", out var modes)) manifest = manifest with { Modes = modes!.Split(',').Select(ParseMode).Distinct().ToList() };
        var output = OutputPath(Required("out"), ".json", Required("manifest"));
        var results = await new ScanService(configuration, store).ScanAsync(manifest, token);
        AtomicFile.Write(output, WireJson.Write(results));
        Console.WriteLine(output);
        return BatchExit(results);
    }
    if (command == "review")
    {
        if (!apply) throw new InvalidDataException("Review changes require --apply after inspecting the decisions JSON.");
        var results = Read<ResultBundle>(Required("results"));
        var decisions = Read<ReviewDocument>(Required("decisions"));
        var output = OutputPath(options.GetValueOrDefault("out") ?? Path.ChangeExtension(Required("results"), ".accepted.json"), ".json", Required("results"));
        if (configuration.Mode == "ConnectedWorker")
        {
            var receipts = await new ConnectedWorker(configuration, store).ReviewAsync(results, decisions, token);
            AtomicFile.Write(output, WireJson.Write(results with { Accepted = receipts.Where(r => r.Outcome == "Accepted" && r.Current is not null).Select(r => r.Current!).ToList() }));
            Console.WriteLine(output);
            return receipts.All(r => r.Outcome == "Accepted") ? 0 : 6;
        }
        foreach (var result in results.Results)
            await VerifyMediaAsync(configuration, result, token);
        var accepted = store.Review(results, decisions);
        AtomicFile.Write(output, WireJson.Write(results with { Accepted = accepted }));
        Console.WriteLine(output);
        return 0;
    }
    if (command == "export")
    {
        var bundle = Read<ResultBundle>(Required("results"));
        var canonical = bundle.Accepted;
        if (canonical.Count == 0) throw new InvalidDataException("No canonical accepted results. Review proposals first.");
        foreach (var result in canonical) await VerifyMediaAsync(configuration, result.Result, token);
        var formats = (options.GetValueOrDefault("format") ?? "json,matroska-xml").Split(',');
        if (formats.Any(f => f is not "json" and not "matroska-xml")) throw new InvalidDataException("Unknown export format.");
        var outcomes = await new ExportService(configuration, store).ExportAsync(canonical, formats.Contains("json"), formats.Contains("matroska-xml"), apply, token);
        Console.WriteLine(WireJson.Write(outcomes));
        return outcomes.Any(o => o.Status is "Conflict" or "ExportPending") ? 7 : 0;
    }
    if (command == "work") { await new ConnectedWorker(configuration, store).WorkAsync(token); return 0; }
    if (command == "sync")
    {
        if (!options.ContainsKey("exports-only")) throw new InvalidDataException("Version 1 sync requires --exports-only.");
        var outcomes = configuration.Server is null ? await new ExportService(configuration, store).RetryPendingAsync(token)
            : await new ConnectedWorker(configuration, store).SyncAsync(token);
        Console.WriteLine(WireJson.Write(outcomes));
        return outcomes.Any(o => o.Status is "Conflict" or "ExportPending") ? 7 : 0;
    }
    throw new InvalidDataException("Unknown command: " + command);
}

static T Read<T>(string path) where T : class
{
    if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Input JSON exceeds 8 MiB.");
    return WireJson.Read<T>(File.ReadAllText(path));
}

static string OutputPath(string path, string extension, string input)
{
    path = Path.GetFullPath(path);
    if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || string.Equals(path, Path.GetFullPath(input), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new InvalidDataException("Reports/results need a separate output file with extension " + extension);
    PathMapper.RejectLinks(path);
    return path;
}

static async Task VerifyMediaAsync(WorkerConfiguration configuration, AnalysisResult result, CancellationToken token)
{
    if (result.MediaValidation is null) throw new InvalidDataException("Unverified media cannot be reviewed or exported.");
    await new MediaProbe(configuration).ProbeAsync(new JobItem { ItemId = result.ItemId, MediaSourceKey = result.MediaSourceKey, SourcePath = result.SourcePath,
        PathMapId = result.PathMapId, FileVersionToken = result.FileVersionToken, ExpectedSignature = result.MediaValidation }, token);
}

static RemoteAnalysisMode ParseMode(string mode) => mode.ToLowerInvariant() switch
{
    "intro" or "introduction" => RemoteAnalysisMode.Introduction,
    "credits" => RemoteAnalysisMode.Credits,
    "recap" => RemoteAnalysisMode.Recap,
    "preview" => RemoteAnalysisMode.Preview,
    "commercial" => RemoteAnalysisMode.Commercial,
    _ => throw new InvalidDataException("Unknown mode: " + mode),
};

static int BatchExit(ResultBundle bundle) => bundle.Results.Any(r => r.Status is AnalysisStatus.Failed or AnalysisStatus.Interrupted or AnalysisStatus.Cancelled) ? 4
    : bundle.Results.Any(r => r.Status == AnalysisStatus.SkippedUnsupported) ? 3 : bundle.Results.Any(r => r.Status == AnalysisStatus.NeedsReview) ? 5 : 0;

static object Compare(ResultBundle baseline, ResultBundle candidate, LabelDocument labels)
{
    var before = Evaluation.Score(baseline, labels);
    var after = Evaluation.Score(candidate, labels);
    return new { baseline = before, candidate = after, programmeSkippedDeltaTicks = after.ProgrammeSkippedTicks - before.ProgrammeSkippedTicks,
        missedCreditDeltaTicks = after.MissedCreditTicks - before.MissedCreditTicks, falsePositiveSegmentDelta = after.FalsePositiveSegments - before.FalsePositiveSegments };
}

static Dictionary<string, string?> Parse(string[] args, string command)
{
    var allowed = command switch
    {
        "doctor" => new[] { "config", "help" },
        "settings" => ["config", "no-browser", "help"],
        "scan" => ["config", "manifest", "modes", "dry-run", "out", "help"],
        "report" => ["results", "out", "help"],
        "review" => ["config", "results", "decisions", "apply", "out", "help"],
        "export" => ["config", "results", "format", "dry-run", "apply", "help"],
        "work" => ["config", "help"],
        "sync" => ["config", "exports-only", "help"],
        "evaluate" => ["results", "labels", "out", "help"],
        "compare" => ["baseline", "candidate", "labels", "out", "help"],
        _ => throw new InvalidDataException("Unknown command: " + command),
    };
    var options = new Dictionary<string, string?>();
    var flags = new[] { "dry-run", "apply", "exports-only", "no-browser", "help" };
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new InvalidDataException("Options use --name value.");
        var name = args[index][2..];
        if (!allowed.Contains(name) || options.ContainsKey(name)) throw new InvalidDataException("Unknown or duplicate option: --" + name);
        string? value = null;
        if (!flags.Contains(name))
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new InvalidDataException("Missing value for --" + name);
            value = args[index];
        }
        options.Add(name, value);
    }
    return options;
}

static void Help() => Console.WriteLine("""
    IntroSkipper-Node — standalone segment analysis node
    doctor   --config FILE
    settings --config FILE [--no-browser]  (node connection and start/stop)
    scan     --config FILE --manifest FILE --modes intro,credits,recap,preview,commercial --dry-run --out FILE.json
    report   --results FILE --out FILE.html  (also writes a decisions template)
    review   --config FILE --results FILE --decisions FILE --apply [--out FILE.json]
    export   --config FILE --results FILE --format json,matroska-xml --dry-run | --apply
    work     --config FILE
    sync     --config FILE --exports-only
    evaluate --results FILE --labels FILE --out FILE.json
    compare  --baseline FILE --candidate FILE --labels FILE --out FILE.json
    scan writes local proposals/cache only. review creates local canonical decisions.
    Connected review explicitly imports unchanged held proposals through server admission.
    Connected work writes result bundles under the configured state/results directory.
    export writes owned sidecars; connected work publishes through the compatible bridge.
    Exit codes: 0 completed, 2 invalid input, 3 dependency/unsupported, 4 analysis failure,
                5 review required, 6 import conflict, 7 export pending/conflict, 130 cancelled.
    """);
