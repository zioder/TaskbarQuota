using System;
using System.Collections.Generic;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels;

/// <summary>
/// Keeps an explicit widget selection through progressive dashboard loads, including providers
/// whose dashboard card is hidden. This selection does not change saved provider visibility.
/// </summary>
internal sealed class DashboardProviderSelection
{
    private ProviderId? _widgetProvider;

    public bool SelectWidgetProvider(ProviderId id, Func<ProviderId, bool> canSelect)
    {
        if (!canSelect(id))
            return false;

        _widgetProvider = id;
        return true;
    }

    public void ClearWidgetProvider() => _widgetProvider = null;

    public ProviderId? Resolve(
        ProviderId? selectedProvider,
        IReadOnlyList<ProviderId> dashboardProviders,
        Func<ProviderId, bool> canSelectWidgetProvider)
    {
        if (_widgetProvider is { } widgetProvider)
        {
            if (canSelectWidgetProvider(widgetProvider))
                return widgetProvider;

            _widgetProvider = null;
        }

        if (selectedProvider is { } selected)
        {
            foreach (var provider in dashboardProviders)
                if (provider == selected)
                    return selected;
        }

        return dashboardProviders.Count > 0 ? dashboardProviders[0] : null;
    }
}
