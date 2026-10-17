using Microsoft.Data.Sqlite;
using SegmentScanner.Contracts;

namespace SegmentScanner.Engine.Persistence;

public sealed record ExportTarget(string Key, string Path, string ItemKey, string Format, long Revision, string Content,
    string DesiredHash, string? LastWrittenHash, string Status);
public sealed record PendingSubmission(JobSnapshot Job, List<AnalysisResult> Results);

public sealed class WorkerStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly FileStream processLock;

    public WorkerStore(string directory)
    {
        Directory.CreateDirectory(directory);
        processLock = new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "worker-v1.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        try
        {
            connection.Open();
            var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);
            if (version > 1) throw new InvalidDataException("Worker state is newer than this binary. Restore a compatible backup; do not downgrade in place.");
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
            Execute("""
                CREATE TABLE IF NOT EXISTS Proposals (AnalysisId TEXT PRIMARY KEY, Payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Canonical (ItemKey TEXT PRIMARY KEY, Revision INTEGER NOT NULL, Payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS ReviewReceipts (Hash TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS Exports (ExportKey TEXT PRIMARY KEY, Path TEXT NOT NULL UNIQUE, ItemKey TEXT NOT NULL,
                  Format TEXT NOT NULL, Revision INTEGER NOT NULL, Content TEXT NOT NULL, DesiredHash TEXT NOT NULL, LastHash TEXT, Status TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Submissions (JobId TEXT PRIMARY KEY, Payload TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
        }
        catch
        {
            connection.Dispose();
            processLock.Dispose();
            throw;
        }
    }

    public static string Key(AnalysisResult result) => $"{result.ServerId ?? "offline"}/{result.ItemId:N}/{result.MediaSourceKey}/{result.Mode}";

    public void SaveProposals(IEnumerable<AnalysisResult> results)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var result in results)
        {
            ContractValidation.Result(result);
            Execute("INSERT INTO Proposals VALUES ($id,$payload) ON CONFLICT(AnalysisId) DO UPDATE SET Payload=excluded.Payload;", transaction,
                ("$id", result.AnalysisId.ToString()), ("$payload", WireJson.Write(result)));
        }
        transaction.Commit();
    }

    public CanonicalResult? GetCanonical(AnalysisResult result) => GetCanonical(Key(result));
    public bool HasChapterExport(string? serverId, JobItem item)
        => Scalar("SELECT 1 FROM Exports WHERE ItemKey=$key AND Format='matroska-xml' LIMIT 1;", null,
            ("$key", $"{serverId ?? "offline"}/{item.ItemId:N}/{item.MediaSourceKey}")) is not null;

    public CanonicalResult? GetCanonical(string key)
        => GetCanonical(key, null);

    private CanonicalResult? GetCanonical(string key, SqliteTransaction? transaction)
    {
        var json = Scalar("SELECT Payload FROM Canonical WHERE ItemKey=$key;", transaction, ("$key", key)) as string;
        return json is null ? null : WireJson.Read<CanonicalResult>(json);
    }

    public List<CanonicalResult> AllCanonical()
    {
        using var command = Command("SELECT Payload FROM Canonical ORDER BY ItemKey;");
        using var reader = command.ExecuteReader();
        var results = new List<CanonicalResult>();
        while (reader.Read()) results.Add(WireJson.Read<CanonicalResult>(reader.GetString(0)));
        return results;
    }

    public List<CanonicalResult> Review(ResultBundle bundle, ReviewDocument review)
    {
        ContractValidation.Require(review.SchemaVersion == 1 && review.Decisions.Select(d => (d.AnalysisId, d.CandidateId)).Distinct().Count() == review.Decisions.Count, "Invalid/duplicate review decisions.");
        var requested = review.Decisions.Select(d => d.AnalysisId).Concat(review.AcceptNoSegments).Distinct().ToHashSet();
        ContractValidation.Require(requested.Count > 0 && requested.All(id => bundle.Results.Any(r => r.AnalysisId == id)), "Review references unknown analyses.");
        var results = bundle.Results.Where(r => requested.Contains(r.AnalysisId)).ToList();
        var receipt = WireJson.Hash(review);
        using var transaction = connection.BeginTransaction();
        if (Scalar("SELECT Hash FROM ReviewReceipts WHERE Hash=$hash;", transaction, ("$hash", receipt)) is not null)
        {
            transaction.Commit();
            return results.Select(GetCanonical).OfType<CanonicalResult>().ToList();
        }
        foreach (var result in results)
        {
            ContractValidation.Result(result);
            ContractValidation.Require(result.Status is AnalysisStatus.CompletedWithSegments or AnalysisStatus.CompletedNoSegments or AnalysisStatus.NeedsReview, "Failed/incomplete analysis cannot replace accepted state.");
            var proposal = Scalar("SELECT Payload FROM Proposals WHERE AnalysisId=$id;", transaction, ("$id", result.AnalysisId.ToString())) as string;
            ContractValidation.Require(proposal is not null && WireJson.Hash(WireJson.Read<AnalysisResult>(proposal)) == WireJson.Hash(result), "Review requires an unchanged recorded proposal.");
            var old = GetCanonical(Key(result), transaction);
            ContractValidation.Require(result.ExpectedItemModeRevision == (old?.ResultRevision ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), "Offline canonical revision changed; review a fresh snapshot.");
            var decisions = review.Decisions.Where(d => d.AnalysisId == result.AnalysisId).ToDictionary(d => d.CandidateId);
            ContractValidation.Require(decisions.Keys.All(id => result.Segments.Any(s => s.CandidateId == id)), "Unknown review candidate.");
            ContractValidation.Require(result.Segments.All(s => decisions.ContainsKey(s.CandidateId))
                && (result.Segments.Count > 0 || review.AcceptNoSegments.Contains(result.AnalysisId)), "Review must explicitly resolve the whole mode, including successful negatives.");
            var segments = result.Segments.Select(s =>
            {
                var decision = decisions[s.CandidateId];
                ContractValidation.Require(decision.Decision != CandidateDecision.Review, "Unresolved review cannot become canonical.");
                return s with { StartTicks = decision.StartTicks ?? s.StartTicks, EndTicks = decision.EndTicks ?? s.EndTicks,
                    Decision = decision.Decision, ConfidenceTier = "UserReviewed", Reasons = s.Reasons.Concat(new[] { "UserReviewed" }).Distinct().ToList() };
            }).ToList();
            var accepted = segments.Where(s => s.Decision == CandidateDecision.Accept).ToList();
            var canonicalResult = result with { Segments = accepted, Status = accepted.Count == 0 ? AnalysisStatus.CompletedNoSegments : AnalysisStatus.CompletedWithSegments };
            ContractValidation.Result(canonicalResult);
            var suppressed = (old?.SuppressedCandidateIds ?? []).Concat(segments.Where(s => s.Decision == CandidateDecision.Reject).Select(s => s.CandidateId))
                .Except(accepted.Select(s => s.CandidateId)).Distinct().ToList();
            var current = new CanonicalResult { Result = canonicalResult, ResultRevision = checked((old?.ResultRevision ?? 0) + 1), SuppressedCandidateIds = suppressed };
            WriteCanonical(current, transaction);
        }
        Execute("INSERT INTO ReviewReceipts VALUES ($hash);", transaction, ("$hash", receipt));
        transaction.Commit();
        return results.Select(GetCanonical).OfType<CanonicalResult>().ToList();
    }

    public void SaveServerCanonical(CanonicalResult canonical)
    {
        ContractValidation.Result(canonical.Result);
        ContractValidation.Require(canonical.Result.ServerId is not null && canonical.Authority == "Server", "Connected exports require server authority.");
        using var transaction = connection.BeginTransaction();
        var previous = GetCanonical(Key(canonical.Result), transaction);
        if (previous is null || previous.Authority != "Server" || canonical.ResultRevision >= previous.ResultRevision) WriteCanonical(canonical, transaction);
        transaction.Commit();
    }

    public ExportTarget QueueExport(string itemKey, string format, string path, long revision, string content)
    {
        var key = WireJson.Hash(new { itemKey, format, path });
        var existing = Exports().FirstOrDefault(e => e.Path == path);
        if (existing is not null && existing.ItemKey != itemKey) throw new IOException("Export destination belongs to another media source.");
        var hash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(content));
        Execute("""
            INSERT INTO Exports VALUES ($key,$path,$item,$format,$revision,$content,$hash,NULL,'Pending')
            ON CONFLICT(ExportKey) DO UPDATE SET Revision=excluded.Revision, Content=excluded.Content,
              DesiredHash=excluded.DesiredHash, Status=CASE WHEN Exports.LastHash=excluded.DesiredHash THEN 'Complete' ELSE 'Pending' END;
            """, null, ("$key", key), ("$path", path), ("$item", itemKey), ("$format", format), ("$revision", revision), ("$content", content), ("$hash", hash));
        return Exports().Single(e => e.Key == key);
    }

    public List<ExportTarget> Exports()
    {
        using var command = Command("SELECT ExportKey,Path,ItemKey,Format,Revision,Content,DesiredHash,LastHash,Status FROM Exports;");
        using var reader = command.ExecuteReader();
        var targets = new List<ExportTarget>();
        while (reader.Read()) targets.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8)));
        return targets;
    }

    public void MarkExport(ExportTarget target, string status, string? writtenHash = null) => Execute(
        "UPDATE Exports SET Status=$status,LastHash=COALESCE($hash,LastHash) WHERE ExportKey=$key AND DesiredHash=$desired;", null,
        ("$status", status), ("$hash", writtenHash), ("$key", target.Key), ("$desired", target.DesiredHash));

    public void SaveSubmission(PendingSubmission submission) => Execute("INSERT INTO Submissions VALUES ($id,$payload) ON CONFLICT(JobId) DO UPDATE SET Payload=excluded.Payload;", null,
        ("$id", submission.Job.JobId.ToString()), ("$payload", WireJson.Write(submission)));
    public void RemoveSubmission(Guid jobId) => Execute("DELETE FROM Submissions WHERE JobId=$id;", null, ("$id", jobId.ToString()));
    public List<PendingSubmission> PendingSubmissions()
    {
        using var command = Command("SELECT Payload FROM Submissions;");
        using var reader = command.ExecuteReader();
        var submissions = new List<PendingSubmission>();
        while (reader.Read()) submissions.Add(WireJson.Read<PendingSubmission>(reader.GetString(0)));
        return submissions;
    }

    public void Dispose()
    {
        connection.Dispose();
        processLock.Dispose();
    }
    private void WriteCanonical(CanonicalResult canonical, SqliteTransaction transaction) => Execute("INSERT INTO Canonical VALUES ($key,$revision,$payload) ON CONFLICT(ItemKey) DO UPDATE SET Revision=excluded.Revision,Payload=excluded.Payload;",
        transaction, ("$key", Key(canonical.Result)), ("$revision", canonical.ResultRevision), ("$payload", WireJson.Write(canonical)));
    private object? Scalar(string sql, params (string Key, object? Value)[] values) => Scalar(sql, null, values);
    private object? Scalar(string sql, SqliteTransaction? transaction, params (string Key, object? Value)[] values)
    {
        using var command = Command(sql, transaction, values);
        return command.ExecuteScalar();
    }
    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Key, object? Value)[] values)
    {
        using var command = Command(sql, transaction, values);
        command.ExecuteNonQuery();
    }
    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Key, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
}
