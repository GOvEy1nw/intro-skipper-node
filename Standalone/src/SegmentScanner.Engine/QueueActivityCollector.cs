using IntroSkipper.Data;
using SegmentScanner.Contracts;

namespace SegmentScanner.Engine;

/// <summary>Tracks actual node-local file activity for connected queue updates.</summary>
public sealed class QueueActivityCollector
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, List<ActivityScope>> active = [];
    private readonly HashSet<Guid> completed = [];

    /// <summary>Begins reporting one actual node-local operation for a file.</summary>
    public IDisposable Begin(Guid itemId, RemoteAnalysisMode mode, string phase)
    {
        var scope = new ActivityScope(this, itemId, new ScannerQueueActivity(itemId, mode, phase));
        lock (gate)
        {
            completed.Remove(itemId);
            if (!active.TryGetValue(itemId, out var scopes)) active[itemId] = scopes = [];
            scopes.Add(scope);
        }
        return scope;
    }

    /// <summary>Adapts the shared analyzer callback to the node queue contract.</summary>
    public IDisposable? TrackItem(QueuedEpisode episode, AnalysisMode mode, string phase)
        => Begin(episode.EpisodeId, mode switch
        {
            AnalysisMode.Introduction => RemoteAnalysisMode.Introduction,
            AnalysisMode.Credits => RemoteAnalysisMode.Credits,
            AnalysisMode.Recap => RemoteAnalysisMode.Recap,
            AnalysisMode.Preview => RemoteAnalysisMode.Preview,
            AnalysisMode.Commercial => RemoteAnalysisMode.Commercial,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        }, phase);

    /// <summary>Gets a stable snapshot for the next lease progress update.</summary>
    public (List<ScannerQueueActivity> Active, List<Guid> Completed) Snapshot()
    {
        lock (gate)
        {
            return (active.Values.Select(scopes => scopes[^1].Activity).ToList(), completed.ToList());
        }
    }

    /// <summary>Records that all requested analysis for a file has finished.</summary>
    public void Complete(Guid itemId)
    {
        lock (gate)
        {
            if (!active.ContainsKey(itemId)) completed.Add(itemId);
        }
    }

    private void End(ActivityScope scope)
    {
        lock (gate)
        {
            if (!active.TryGetValue(scope.ItemId, out var scopes)) return;
            scopes.Remove(scope);
            if (scopes.Count != 0) return;
            active.Remove(scope.ItemId);
        }
    }

    private sealed class ActivityScope(QueueActivityCollector owner, Guid itemId, ScannerQueueActivity activity) : IDisposable
    {
        private int disposed;

        public Guid ItemId { get; } = itemId;

        public ScannerQueueActivity Activity { get; } = activity;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.End(this);
        }
    }
}
