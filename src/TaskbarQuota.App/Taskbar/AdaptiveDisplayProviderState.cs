using System;
using System.Collections.Generic;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Taskbar;

/// <summary>
/// Remembers provider windows focused on each display in most-recent-first order. Windows exposes one
/// system foreground window, so adaptive mode keeps independent per-display history and can restore the
/// previous valid provider when the current one moves to another monitor or closes.
/// </summary>
internal sealed class AdaptiveDisplayProviderState
{
    private readonly Dictionary<string, List<Entry>> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Entry(ProviderId Provider, IntPtr Window);

    public ProviderId? GetProvider(string displayKey, Func<IntPtr, bool>? windowIsValid = null)
    {
        if (!_providers.TryGetValue(displayKey, out var entries))
            return null;

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (windowIsValid is null || windowIsValid(entry.Window))
                return entry.Provider;

            entries.RemoveAt(i);
        }

        _providers.Remove(displayKey);
        return null;
    }

    public IReadOnlyCollection<ProviderId> Providers
    {
        get
        {
            var result = new HashSet<ProviderId>();
            foreach (var entries in _providers.Values)
                foreach (var entry in entries)
                    result.Add(entry.Provider);
            return result;
        }
    }

    public bool Observe(ProviderId provider, string displayKey, IntPtr window)
    {
        displayKey = displayKey.Trim();
        if (displayKey.Length == 0)
            return false;

        var next = new Entry(provider, window);
        bool alreadyCurrent = _providers.TryGetValue(displayKey, out var currentEntries)
            && currentEntries.Count > 0
            && currentEntries[^1] == next;
        bool hasConflictingEntry = false;
        foreach (var pair in _providers)
        {
            for (int i = 0; i < pair.Value.Count; i++)
            {
                var entry = pair.Value[i];
                bool isCurrent = string.Equals(pair.Key, displayKey, StringComparison.OrdinalIgnoreCase)
                    && i == pair.Value.Count - 1
                    && entry == next;
                if (!isCurrent && (entry.Provider == provider || entry.Window == window))
                {
                    hasConflictingEntry = true;
                    break;
                }
            }
            if (hasConflictingEntry)
                break;
        }
        if (alreadyCurrent && !hasConflictingEntry)
            return false;

        foreach (string previousDisplay in new List<string>(_providers.Keys))
        {
            var entries = _providers[previousDisplay];
            // A window can change provider without moving (for example, switching tools inside one
            // terminal). Its previous classification must not survive as a fallback entry.
            entries.RemoveAll(entry => entry.Provider == provider || entry.Window == window);
            if (entries.Count == 0)
            {
                _providers.Remove(previousDisplay);
            }
        }

        if (!_providers.TryGetValue(displayKey, out var targetEntries))
        {
            targetEntries = new List<Entry>();
            _providers[displayKey] = targetEntries;
        }
        targetEntries.Add(next);
        return true;
    }

    public void Clear() => _providers.Clear();
}
