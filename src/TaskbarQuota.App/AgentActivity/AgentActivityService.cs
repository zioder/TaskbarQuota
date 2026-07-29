using System;
using System.Collections.Generic;
using System.IO;
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

    private readonly object _gate = new();
    private readonly Dictionary<string, AgentActivityItem> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);
    // A run is the set of threads that were alive together. Completed members stay here until the
    // remaining members finish, allowing the taskbar to advance through concurrent project agents.
    private readonly HashSet<string> _runIds = new(StringComparer.Ordinal);
    private readonly AgentActivityScanner _scanner = new();
    private int _refreshInProgress;

    public event Action<AgentActivitySnapshot>? Changed;

    public async Task RefreshFromTranscriptsAsync()
    {
        // WMI/process inspection can occasionally outlive a tick; keep the newest completed scan rather
        // than queuing stale scans behind it.
        if (Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
            return;

        Log.Information("[activity] refresh started");
        try
        {
            IReadOnlyList<AgentActivityItem> scanned;
            try
            {
                scanned = await Task.Run(() => _scanner.Scan()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "[activity] transcript scan failed");
                return;
            }

            lock (_gate)
            {
                bool trackedRunIsLive = _runIds.Any(id => _items.TryGetValue(id, out var existing) && existing.IsLive);
                var scannedLive = scanned.Where(item => item.IsLive).Select(item => item.Id).ToArray();
                if (scannedLive.Length > 0 && !trackedRunIsLive && _runIds.Count > 0)
                    _runIds.Clear();
                foreach (var id in scannedLive)
                    _runIds.Add(id);

                foreach (var key in _items.Keys.Where(key => key.StartsWith("presence:", StringComparison.Ordinal)).ToArray())
                    if (scanned.Any(item => item.IsLive && _items[key].Provider == item.Provider))
                        _items.Remove(key);

                foreach (var item in scanned)
                {
                    if (item.IsLive)
                        _acknowledged.Remove(item.Id);
                    _items[item.Id] = item;
                }
                Log.Information($"[activity] transcript scan: {scanned.Count} session(s), live={scanned.Count(item => item.IsLive)}");
                Publish();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
        }
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

            Publish();
        }
    }

    public void Report(string id, ProviderId provider, string title, string step,
        AgentActivityStatus status, DateTimeOffset? startedAt = null, int subagentCount = 0, string? detail = null)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            _items[id] = new AgentActivityItem(
                id, provider, title, step, status, startedAt ?? now, now, subagentCount, detail);
            if (status is AgentActivityStatus.Working or AgentActivityStatus.Waiting)
                _acknowledged.Remove(id);
            Publish();
        }
    }

    public void Acknowledge(string id)
    {
        lock (_gate)
        {
            _acknowledged.Add(id);
            Publish();
        }
    }

    public void AcknowledgeAll()
    {
        lock (_gate)
        {
            foreach (var item in _items.Values)
                if (!item.IsLive)
                    _acknowledged.Add(item.Id);
            Publish();
        }
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

    private void Publish() => Changed?.Invoke(BuildSnapshot());

    private static string DisplayName(ProviderId provider) => provider switch
    {
        ProviderId.ClinePass => "Cline Pass",
        ProviderId.OpenCodeGo => "OpenCode Go",
        _ => provider.ToString(),
    };
}
