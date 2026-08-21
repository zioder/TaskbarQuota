using System;
using System.Collections.Generic;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Taskbar;

/// <summary>
/// Remembers the last provider window focused on each display. Windows exposes one system foreground
/// window, so adaptive mode keeps one independent last-foreground slot per display instead of replacing
/// every taskbar's provider whenever focus crosses a monitor boundary.
/// </summary>
internal sealed class AdaptiveDisplayProviderState
{
    private readonly Dictionary<string, Entry> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Entry(ProviderId Provider, IntPtr Window);

    public ProviderId? GetProvider(string displayKey, Func<IntPtr, bool>? windowIsValid = null)
    {
        if (!_providers.TryGetValue(displayKey, out var entry))
            return null;

        if (windowIsValid is not null && !windowIsValid(entry.Window))
        {
            _providers.Remove(displayKey);
            return null;
        }

        return entry.Provider;
    }

    public IReadOnlyCollection<ProviderId> Providers
    {
        get
        {
            var result = new List<ProviderId>(_providers.Values.Count);
            foreach (var entry in _providers.Values)
                result.Add(entry.Provider);
            return result;
        }
    }

    public bool Observe(ProviderId provider, string displayKey, IntPtr window)
    {
        displayKey = displayKey.Trim();
        if (displayKey.Length == 0)
            return false;

        bool changed = false;
        foreach (string previousDisplay in new List<string>(_providers.Keys))
        {
            if (!string.Equals(previousDisplay, displayKey, StringComparison.OrdinalIgnoreCase)
                && _providers[previousDisplay].Provider == provider)
            {
                _providers.Remove(previousDisplay);
                changed = true;
            }
        }

        var next = new Entry(provider, window);
        if (_providers.TryGetValue(displayKey, out var current) && current == next)
            return changed;

        _providers[displayKey] = next;
        return true;
    }

    public void Clear() => _providers.Clear();
}
