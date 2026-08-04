using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.AgentActivity;

/// <summary>
/// Shared activity state for the taskbar summary and flyout. Provider adapters can report exact steps
/// through Report; the current presence bridge provides a useful fallback until transcript adapters exist.
/// </summary>
public sealed class AgentActivityService
{
    public static AgentActivityService Instance { get; } = new();

    internal static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(10);
    private const int MaxRetainedItems = 100;

    private readonly object _gate = new();
    private readonly Dictionary<string, AgentActivityItem> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scannedIds = new(StringComparer.Ordinal);
    // A run is the set of threads that were alive together. Completed members stay here until the
    // remaining members finish, allowing the taskbar to advance through concurrent project agents.
    private readonly HashSet<string> _runIds = new(StringComparer.Ordinal);
    private readonly AgentActivityScanner _scanner = new();
    private int _refreshInProgress;
    private int _generation;

    public event Action<AgentActivitySnapshot>? Changed;

    public async Task RefreshFromTranscriptsAsync(CancellationToken cancellationToken = default)
    {
        // WMI/process inspection can occasionally outlive a tick; keep the newest completed scan rather
        // than queuing stale scans behind it.
        if (Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
            return;
        var generation = Volatile.Read(ref _generation);

        try
        {
            IReadOnlyList<AgentActivityItem> scanned;
            try
            {
                scanned = await Task.Run(() => _scanner.Scan(cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[activity] transcript scan failed");
                return;
            }

            if (generation == Volatile.Read(ref _generation))
                ApplyScan(scanned);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
        }
    }

    internal void ApplyScan(IReadOnlyList<AgentActivityItem> scanned)
    {
        AgentActivitySnapshot? snapshot = null;
        Action<AgentActivitySnapshot>? handlers = null;
        lock (_gate)
        {
            var before = BuildSnapshot();
            var nextScannedIds = scanned.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

            // Scanner-owned rows are a point-in-time view. If a transcript disappears, ages out, or can
            // no longer be correlated with a process, retaining its previous live state is incorrect.
            foreach (var missingId in _scannedIds.Where(id => !nextScannedIds.Contains(id)).ToArray())
            {
                _items.Remove(missingId);
                _acknowledged.Remove(missingId);
                _runIds.Remove(missingId);
            }

            _scannedIds.Clear();
            foreach (var id in nextScannedIds)
                _scannedIds.Add(id);

            bool trackedRunIsLive = _runIds.Any(id => _items.TryGetValue(id, out var existing) && existing.IsLive);
            var scannedLive = scanned.Where(item => item.IsLive).Select(item => item.Id).ToArray();
            if (scannedLive.Length > 0 && !trackedRunIsLive && _runIds.Count > 0)
                _runIds.Clear();
            foreach (var id in scannedLive)
                _runIds.Add(id);

            foreach (var key in _items.Keys.Where(key => key.StartsWith("presence:", StringComparison.Ordinal)).ToArray())
                if (scanned.Any(item => item.IsLive && _items[key].Provider == item.Provider))
                    RemoveItem(key);

            var completionCutoff = DateTimeOffset.Now - CompletedRetention;
            foreach (var item in scanned)
            {
                if (item.IsLive)
                    _acknowledged.Remove(item.Id);
                if (item.IsLive || item.UpdatedAt >= completionCutoff)
                    _items[item.Id] = item;
                else
                    _items.Remove(item.Id);
            }

            Prune(completionCutoff);
            var after = BuildSnapshot();
            if (!SnapshotsEqual(before, after))
            {
                snapshot = after;
                handlers = Changed;
            }
        }

        Publish(handlers, snapshot);
    }

    public AgentActivitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return BuildSnapshot();
        }
    }

    public void SyncPresence(ProviderId? provider, bool isPresent)
    {
        if (provider is not { } id)
            return;

        string key = $"presence:{id}";
        AgentActivitySnapshot snapshot;
        Action<AgentActivitySnapshot>? handlers;
        lock (_gate)
        {
            if (isPresent)
            {
                if (_items.TryGetValue(key, out var existing) && existing.IsLive)
                {
                    _items[key] = existing with { UpdatedAt = DateTimeOffset.Now };
                }
                else
                {
                    _items[key] = new AgentActivityItem(
                        key, id, DisplayName(id), "Working", AgentActivityStatus.Working,
                        DateTimeOffset.Now, DateTimeOffset.Now);
                }
            }
            else if (_items.TryGetValue(key, out var previous) && previous.IsLive)
            {
                _items[key] = previous with
                {
                    Step = "Finished",
                    Status = AgentActivityStatus.Completed,
                    UpdatedAt = DateTimeOffset.Now,
                };
            }

            snapshot = BuildSnapshot();
            handlers = Changed;
        }
        Publish(handlers, snapshot);
    }

    public void Report(string id, ProviderId provider, string title, string step,
        AgentActivityStatus status, DateTimeOffset? startedAt = null, int subagentCount = 0, string? detail = null)
    {
        AgentActivitySnapshot snapshot;
        Action<AgentActivitySnapshot>? handlers;
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            _items[id] = new AgentActivityItem(
                id, provider, title, step, status, startedAt ?? now, now, subagentCount, detail);
            if (status is AgentActivityStatus.Working or AgentActivityStatus.Waiting)
                _acknowledged.Remove(id);
            Prune(DateTimeOffset.Now - CompletedRetention);
            snapshot = BuildSnapshot();
            handlers = Changed;
        }
        Publish(handlers, snapshot);
    }

    public void Acknowledge(string id)
    {
        AgentActivitySnapshot snapshot;
        Action<AgentActivitySnapshot>? handlers;
        lock (_gate)
        {
            _acknowledged.Add(id);
            _runIds.Remove(id);
            snapshot = BuildSnapshot();
            handlers = Changed;
        }
        Publish(handlers, snapshot);
    }

    public void AcknowledgeAll()
    {
        AgentActivitySnapshot snapshot;
        Action<AgentActivitySnapshot>? handlers;
        lock (_gate)
        {
            foreach (var item in _items.Values)
                if (!item.IsLive)
                    _acknowledged.Add(item.Id);
            _runIds.RemoveWhere(id => _acknowledged.Contains(id));
            snapshot = BuildSnapshot();
            handlers = Changed;
        }
        Publish(handlers, snapshot);
    }

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _scanner.ClearCache();
        AgentActivitySnapshot snapshot;
        Action<AgentActivitySnapshot>? handlers;
        lock (_gate)
        {
            _items.Clear();
            _acknowledged.Clear();
            _scannedIds.Clear();
            _runIds.Clear();
            snapshot = BuildSnapshot();
            handlers = Changed;
        }
        Publish(handlers, snapshot);
    }

    private AgentActivitySnapshot BuildSnapshot()
    {
        var items = _items.Values
            .Where(item => item.IsLive || !_acknowledged.Contains(item.Id))
            .OrderByDescending(item => item.IsLive)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
        var runItems = items
            .Where(item => _runIds.Contains(item.Id))
            .OrderBy(item => item.StartedAt)
            .ToArray();
        return new AgentActivitySnapshot(items, runItems);
    }

    private void Prune(DateTimeOffset completionCutoff)
    {
        foreach (var id in _items.Values
                     .Where(item => !item.IsLive && item.UpdatedAt < completionCutoff)
                     .Select(item => item.Id)
                     .ToArray())
            RemoveItem(id);

        foreach (var id in _items.Values
                     .OrderByDescending(item => item.IsLive)
                     .ThenByDescending(item => item.UpdatedAt)
                     .Skip(MaxRetainedItems)
                     .Select(item => item.Id)
                     .ToArray())
            RemoveItem(id);

        _acknowledged.RemoveWhere(id => !_items.ContainsKey(id) && !_scannedIds.Contains(id));
        _runIds.RemoveWhere(id => !_items.ContainsKey(id));
    }

    private void RemoveItem(string id)
    {
        _items.Remove(id);
        _runIds.Remove(id);
    }

    private static bool SnapshotsEqual(AgentActivitySnapshot left, AgentActivitySnapshot right)
        => left.Items.SequenceEqual(right.Items)
            && left.TrackedItems.SequenceEqual(right.TrackedItems);

    private static void Publish(Action<AgentActivitySnapshot>? handlers, AgentActivitySnapshot? snapshot)
    {
        if (handlers is null || snapshot is null)
            return;

        foreach (Action<AgentActivitySnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(snapshot); }
            catch (Exception ex) { Log.Warning(ex, "[activity] subscriber failed"); }
        }
    }

    private static string DisplayName(ProviderId provider) => provider switch
    {
        ProviderId.ClinePass => "Cline Pass",
        ProviderId.OpenCodeGo => "OpenCode Go",
        _ => provider.ToString(),
    };
}
