using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Engine.Export;

public sealed record ExportOutcome(string Path, string Status, string? Reason = null);

public sealed class ExportService(WorkerConfiguration configuration, WorkerStore store)
{
    public async Task<List<ExportOutcome>> ExportAsync(IReadOnlyList<CanonicalResult> canonical, bool json, bool xml, bool apply, CancellationToken token)
    {
        if (apply && !configuration.Exports.Enabled) throw new InvalidDataException("Enable exports in configuration after inspecting a dry-run preview.");
        var outcomes = new List<ExportOutcome>();
        foreach (var group in canonical.GroupBy(c => (c.Result.ServerId, c.Result.ItemId, c.Result.MediaSourceKey)))
        {
            foreach (var value in group)
            {
                if (value.Result.ServerId is not null && value.Authority != "Server")
                    throw new InvalidDataException("Server-context exports require an authoritative bridge receipt/canonical response.");
                var stored = store.GetCanonical(value.Result);
                if (stored is null || WireJson.Hash(stored) != WireJson.Hash(value)) throw new InvalidDataException("Export requires current canonical state from the authority, not a proposed/edited bundle.");
            }
            var first = group.First();
            var mediaPath = new PathMapper(configuration.Paths).Resolve(first.Result.SourcePath, first.Result.PathMapId);
            if (group.Select(c => c.Result.MediaValidation).Distinct().Count() != 1 || first.Result.MediaValidation is null) throw new InvalidDataException("Canonical modes refer to different/unverified media versions.");
            if (!await MediaProbe.UnchangedAsync(new ResolvedMedia(new JobItem { ItemId = first.Result.ItemId, MediaSourceKey = first.Result.MediaSourceKey,
                SourcePath = first.Result.SourcePath, FileVersionToken = first.Result.FileVersionToken }, mediaPath, first.Result.MediaValidation, [], 0), token).ConfigureAwait(false))
                throw new IOException("Media changed since acceptance; stale exports are refused.");
            var itemKey = $"{first.Result.ServerId ?? "offline"}/{first.Result.ItemId:N}/{first.Result.MediaSourceKey}";
            var outputDirectory = Path.GetDirectoryName(mediaPath)!;
            if (configuration.Exports.MirroredRoot is { } mirror)
            {
                outputDirectory = Path.Combine(mirror, WireJson.Hash(new { first.Result.ServerId, first.Result.ItemId, first.Result.MediaSourceKey })[..20]);
                PathMapper.EnsureWithin(outputDirectory, mirror);
            }
            var segments = group.Where(c => !c.Disabled).SelectMany(c => c.Result.Segments).Where(s => s.Decision == CandidateDecision.Accept).ToList();
            var chapterXml = ChapterTimeline.Xml(itemKey, first.Result.TimelineEndTicks, segments);
            var xmlHash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(chapterXml));
            var revision = group.Max(c => c.ResultRevision);
            if (xml && configuration.Exports.Writer == "Worker")
            {
                if (configuration.Exports.XmlLayout == "MsChapterCompatible" && configuration.Exports.MirroredRoot is null)
                {
                    var stem = Path.GetFileNameWithoutExtension(mediaPath);
                    var mediaExtensions = new[] { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".m2ts", ".webm", ".wmv", ".mpg" };
                    if (Directory.EnumerateFiles(Path.GetDirectoryName(mediaPath)!).Count(p => Path.GetFileNameWithoutExtension(p).Equals(stem, StringComparison.OrdinalIgnoreCase) && mediaExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) > 1)
                        throw new IOException("MS Chapter stem collision; choose the extension-preserving Adjacent layout.");
                }
                var path = configuration.Exports.XmlLayout == "MsChapterCompatible" ? Path.Combine(outputDirectory, ".chapters", Path.GetFileNameWithoutExtension(mediaPath) + "_chapters.xml")
                    : Path.Combine(outputDirectory, Path.GetFileName(mediaPath) + ".chapters.xml");
                if (segments.Count > 0 || store.Exports().Any(e => e.Path == path && e.ItemKey == itemKey))
                    outcomes.Add(Target(itemKey, "matroska-xml", path, revision, chapterXml, apply));
            }
            if (json && configuration.Exports.JsonSidecar)
            {
                var content = WireJson.Write(new { schemaVersion = 1, generatedBy = "SegmentScanner/v1", origin = ChapterOrigin.ScannerGenerated,
                    mediaSignature = first.Result.MediaValidation, xmlSha256 = xmlHash, canonical = group.OrderBy(c => c.Result.Mode).ToList() }) + "\n";
                outcomes.Add(Target(itemKey, "json", Path.Combine(outputDirectory, Path.GetFileName(mediaPath) + ".segments.json"), revision, content, apply));
            }
        }
        if (apply)
        {
            // Queue every desired file before replacing either member of a JSON/XML pair.
            for (var index = 0; index < outcomes.Count; index++)
            {
                if (outcomes[index].Status != "Queued") continue;
                var target = store.Exports().Single(e => e.Path == outcomes[index].Path);
                outcomes[index] = WriteOwned(target);
                if (outcomes[index].Status is "Written" or "Unchanged") RetireOldPaths(target, outcomes);
            }
        }
        return outcomes;
    }

    public Task<List<ExportOutcome>> RetryPendingAsync(CancellationToken token)
    {
        if (!configuration.Exports.Enabled) return Task.FromResult(new List<ExportOutcome>());
        return ExportAsync(store.AllCanonical(), json: true, xml: true, apply: true, token);
    }

    private ExportOutcome Target(string itemKey, string format, string path, long revision, string content, bool apply)
    {
        ValidateDestination(path);
        var owned = store.Exports().FirstOrDefault(e => e.Path == path);
        var currentHash = HashFile(path);
        if (apply && owned is not null && owned.ItemKey == itemKey && (owned.Status is "Pending" or "ExportPending") && currentHash == owned.DesiredHash)
        {
            var recovery = WriteOwned(owned);
            if (recovery.Status == "ExportPending") return recovery;
            owned = store.Exports().Single(e => e.Key == owned.Key);
            currentHash = HashFile(path);
        }
        if (currentHash is not null && (owned is null || owned.ItemKey != itemKey || owned.LastWrittenHash != currentHash))
            return new(path, "Conflict", "Unfamiliar or manually modified file is preserved.");
        if (!apply) return new(path, currentHash == WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(content)) ? "Unchanged" : "WouldWrite");
        store.QueueExport(itemKey, format, path, revision, content);
        return new(path, "Queued");
    }

    private ExportOutcome WriteOwned(ExportTarget target)
    {
        try
        {
            ValidateDestination(target.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
            var backup = target.Path + ".segmentscanner-backup-" + target.Key[..16];
            var current = HashFile(target.Path);
            if (File.Exists(backup))
            {
                var backupHash = HashFile(backup);
                if (backupHash != target.LastWrittenHash)
                {
                    if (current == target.DesiredHash) File.Move(backup, target.Path, overwrite: true);
                    throw new IOException("An intervening edit was preserved/restored from the interrupted replacement backup.");
                }
                if (current == target.DesiredHash) File.Delete(backup);
                else throw new IOException("Interrupted replacement needs review; its backup is preserved.");
            }
            if (current == target.DesiredHash && (target.LastWrittenHash is not null || target.Status is "Pending" or "ExportPending"))
            {
                store.MarkExport(target, "Complete", target.DesiredHash);
                return new(target.Path, "Unchanged");
            }
            if (current is not null && current != target.LastWrittenHash) throw new IOException("Export was manually modified; it is preserved.");
            var temporary = target.Path + ".segmentscanner-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(target.Content));
                    stream.Flush(flushToDisk: true);
                }
                if (current is null) File.Move(temporary, target.Path, overwrite: false);
                else
                {
                    File.Replace(temporary, target.Path, backup, ignoreMetadataErrors: true);
                    if (HashFile(backup) != target.LastWrittenHash)
                    {
                        if (HashFile(target.Path) == target.DesiredHash) File.Move(backup, target.Path, overwrite: true);
                        throw new IOException("An intervening sidecar edit was preserved; export is conflicted.");
                    }
                    File.Delete(backup);
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            store.MarkExport(target, "Complete", target.DesiredHash);
            return new(target.Path, "Written");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            store.MarkExport(target, "ExportPending");
            return new(target.Path, "ExportPending", exception.Message);
        }
    }

    private void ValidateDestination(string path)
    {
        var roots = configuration.Exports.MirroredRoot is { } mirror ? new[] { mirror }
            : configuration.Paths.AllowedMediaRoots.Concat(configuration.Paths.Mappings.Select(m => m.WorkerPrefix));
        if (!roots.Any(r => PathMapper.IsWithin(path, r))) throw new InvalidDataException("Export destination escapes permitted roots.");
        PathMapper.RejectLinks(path);
        if (!path.EndsWith(".segments.json", StringComparison.Ordinal) && !path.EndsWith(".chapters.xml", StringComparison.Ordinal) && !path.EndsWith("_chapters.xml", StringComparison.Ordinal))
            throw new InvalidDataException("Only explicit sidecar destinations are writable.");
    }

    private void RetireOldPaths(ExportTarget current, List<ExportOutcome> outcomes)
    {
        foreach (var old in store.Exports().Where(e => e.ItemKey == current.ItemKey && e.Format == current.Format && e.Path != current.Path && e.Status != "Retired"))
        {
            ValidateDestination(old.Path);
            if (HashFile(old.Path) == old.LastWrittenHash && old.LastWrittenHash is not null)
            {
                if (File.Exists(old.Path)) File.Delete(old.Path);
                store.MarkExport(old, "Retired");
            }
            else if (File.Exists(old.Path)) outcomes.Add(new(old.Path, "Conflict", "Renamed media's manually modified prior export is preserved."));
        }
    }

    private static string? HashFile(string path) => File.Exists(path) ? WireJson.Sha256(File.ReadAllBytes(path)) : null;
}
