using System;
using System.Collections.Generic;
using System.Linq;
using TaskbarQuota.AgentActivity;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Taskbar;

/// <summary>Pure routing rules for distributing quota and activity content between taskbars.</summary>
internal static class TaskbarContentRouter
{
    public static IReadOnlyList<ProviderId> AdaptiveCandidatesForDisplay(
        IReadOnlyList<ProviderId> providers,
        ProviderId? currentProvider,
        Func<ProviderId, bool> isVisible,
        Func<ProviderId, bool> isPinned)
    {
        var result = new List<ProviderId>();
        if (currentProvider is { } current && isVisible(current))
            result.Add(current);

        foreach (var provider in providers)
        {
            if (isPinned(provider) && !result.Contains(provider))
                result.Add(provider);
        }

        return result;
    }

    public static IReadOnlyList<ProviderId> ProvidersForDisplay(
        IReadOnlyList<ProviderId> providers,
        TaskbarPlacementMode mode,
        string selectedDisplayKey,
        string targetDisplayKey,
        string primaryDisplayKey,
        IReadOnlySet<string> availableDisplayKeys,
        Func<ProviderId, string?> adaptiveDisplayForProvider,
        Func<ProviderId, bool> isPinned,
        Func<ProviderId, string?> pinnedDisplayForProvider)
        => providers
            .Where(provider => IsRoutedToDisplay(
                provider,
                mode,
                selectedDisplayKey,
                targetDisplayKey,
                primaryDisplayKey,
                availableDisplayKeys,
                adaptiveDisplayForProvider,
                isPinned,
                pinnedDisplayForProvider))
            .ToArray();

    public static AgentActivitySnapshot ActivityForDisplay(
        AgentActivitySnapshot snapshot,
        TaskbarPlacementMode mode,
        string selectedDisplayKey,
        string targetDisplayKey,
        string primaryDisplayKey,
        IReadOnlySet<string> availableDisplayKeys,
        Func<ProviderId, string?> adaptiveDisplayForProvider)
    {
        if (mode == TaskbarPlacementMode.AllDisplays)
            return snapshot;

        var items = snapshot.Items
            .Where(item => IsRoutedToDisplay(
                item.Provider,
                mode,
                selectedDisplayKey,
                targetDisplayKey,
                primaryDisplayKey,
                availableDisplayKeys,
                adaptiveDisplayForProvider))
            .ToArray();
        var runItems = snapshot.RunItems?
            .Where(item => IsRoutedToDisplay(
                item.Provider,
                mode,
                selectedDisplayKey,
                targetDisplayKey,
                primaryDisplayKey,
                availableDisplayKeys,
                adaptiveDisplayForProvider))
            .ToArray();
        return new AgentActivitySnapshot(items, runItems);
    }

    internal static bool IsRoutedToDisplay(
        ProviderId provider,
        TaskbarPlacementMode mode,
        string selectedDisplayKey,
        string targetDisplayKey,
        string primaryDisplayKey,
        IReadOnlySet<string> availableDisplayKeys,
        Func<ProviderId, string?> adaptiveDisplayForProvider,
        Func<ProviderId, bool>? isPinned = null,
        Func<ProviderId, string?>? pinnedDisplayForProvider = null)
    {
        if (mode == TaskbarPlacementMode.AllDisplays)
            return true;

        string destination;
        if (mode == TaskbarPlacementMode.SelectedDisplay)
        {
            destination = selectedDisplayKey;
        }
        else
        {
            string? pinnedDestination = isPinned?.Invoke(provider) == true
                ? pinnedDisplayForProvider?.Invoke(provider)
                : null;
            if (string.Equals(
                pinnedDestination,
                WidgetSettingsService.AllDisplaysPinDestination,
                StringComparison.Ordinal))
            {
                return true;
            }
            destination = pinnedDestination ?? adaptiveDisplayForProvider(provider) ?? string.Empty;
        }

        // A disconnected/unknown selection must not make content disappear. It temporarily falls back
        // to the primary taskbar while retaining the persisted destination for when that display returns.
        if (destination.Length == 0 || !availableDisplayKeys.Contains(destination))
            destination = primaryDisplayKey;

        return string.Equals(destination, targetDisplayKey, StringComparison.OrdinalIgnoreCase);
    }
}
