using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TaskbarQuota.Usage;

namespace TaskbarQuota;

/// <summary>
/// Tracks which providers are probed, configured, and visible in the dashboard vs taskbar widget.
/// Auto-hides <see cref="ProviderErrorKind.NotInstalled"/> providers unless the user explicitly enables them.
/// </summary>
public static class ProviderDiscoveryService
{
    private static readonly object SyncRoot = new();
    private static readonly string StatePath =
        Path.Combine(AppStorage.AppDataDirectory, "provider-discovery.json");

    private static readonly HashSet<ProviderId> Probed = new();
    private static readonly HashSet<ProviderId> Configured = new();
    private static readonly HashSet<ProviderId> ExplicitlyEnabled = new();
    private static readonly HashSet<ProviderId> ExplicitlyDisabled = new();

    static ProviderDiscoveryService() => Load();

    /// <summary>
    /// Ensures every detected installed provider is visible in the dashboard and widget unless the user hid it.
    /// </summary>
    public static void SyncInstalledProviderVisibility()
    {
        ProviderInstallDetector.WarmCliCache();

        bool widgetChanged = false;
        bool dashboardChanged = false;
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            if (!ProviderInstallDetector.IsInstalled(id) || IsExplicitlyDisabled(id))
                continue;

            widgetChanged |= TryEnableProviderSilent(id, out bool dashChanged);
            dashboardChanged |= dashChanged;
        }

        if (widgetChanged)
            WidgetSettingsService.SaveProviderVisibilityAndNotify();

        if (dashboardChanged)
            WidgetSettingsService.SaveDashboardProviderVisibilityAndNotify();
    }

    public static void RecordFetchResult(UsageResult result)
    {
        lock (SyncRoot)
        {
            Probed.Add(result.Id);

            if (ProviderInstallDetector.IsInstalled(result.Id) && !ExplicitlyDisabled.Contains(result.Id))
                EnsureProviderEnabled(result.Id);

            bool newlyConfigured = result.Ok && !Configured.Contains(result.Id);

            if (result.Ok || result.ErrorKind == ProviderErrorKind.AuthRequired)
                Configured.Add(result.Id);

            if (result.Ok)
            {
                if (!WidgetSettingsService.IsProviderDashboardVisible(result.Id))
                    WidgetSettingsService.SetProviderDashboardVisible(result.Id, true);

            // First successful fetch after install/login — restore widget visibility
            // (auto-hide turns it off for NotInstalled, but usage should return once set up).
                if (newlyConfigured && !WidgetSettingsService.IsProviderVisible(result.Id))
                    WidgetSettingsService.SetProviderVisible(result.Id, true);
            }

            if (result.ErrorKind == ProviderErrorKind.NotRunning
                && ProviderInstallDetector.IsInstalled(result.Id))
            {
                if (!WidgetSettingsService.IsProviderDashboardVisible(result.Id))
                    WidgetSettingsService.SetProviderDashboardVisible(result.Id, true);
                if (!WidgetSettingsService.IsProviderVisible(result.Id))
                    WidgetSettingsService.SetProviderVisible(result.Id, true);
            }

            if (result.ErrorKind == ProviderErrorKind.NotInstalled
                && !ProviderInstallDetector.IsInstalled(result.Id)
                && WidgetSettingsService.AutoHideUnavailable
                && !ExplicitlyEnabled.Contains(result.Id))
            {
                WidgetSettingsService.SetProviderDashboardVisible(result.Id, false);
                WidgetSettingsService.SetProviderVisible(result.Id, false);
            }

            Save();
        }
    }

    public static bool IsProbed(ProviderId id)
    {
        lock (SyncRoot)
            return Probed.Contains(id);
    }

    public static bool IsConfigured(ProviderId id)
    {
        lock (SyncRoot)
            return Configured.Contains(id);
    }

    public static bool IsExplicitlyEnabled(ProviderId id)
    {
        lock (SyncRoot)
            return ExplicitlyEnabled.Contains(id);
    }

    public static bool IsExplicitlyDisabled(ProviderId id)
    {
        lock (SyncRoot)
            return ExplicitlyDisabled.Contains(id);
    }

    public static void EnableProvider(ProviderId id)
    {
        lock (SyncRoot)
        {
            ExplicitlyEnabled.Add(id);
            ExplicitlyDisabled.Remove(id);
            EnsureProviderEnabled(id);
            Save();
        }
    }

    public static void DisableProvider(ProviderId id)
    {
        lock (SyncRoot)
        {
            ExplicitlyEnabled.Remove(id);
            ExplicitlyDisabled.Add(id);
            WidgetSettingsService.SetProviderDashboardVisible(id, false);
            WidgetSettingsService.SetProviderVisible(id, false);
            Save();
        }
    }

    public static bool ShouldFetch(ProviderId id, ProviderId? active)
    {
        if (id == active)
            return true;
        if (IsExplicitlyDisabled(id))
            return false;
        if (ProviderInstallDetector.IsInstalled(id))
            return true;
        if (!IsProbed(id))
            return true;
        if (WidgetSettingsService.IsProviderDashboardVisible(id))
            return true;
        if (WidgetSettingsService.IsProviderVisible(id))
            return true;
        if (IsExplicitlyEnabled(id))
            return true;
        if (IsConfigured(id))
            return true;
        return false;
    }

    public static bool ShouldShowInDashboard(UsageResult result, ProviderId? active)
    {
        if (IsExplicitlyDisabled(result.Id))
            return false;
        if (ProviderInstallDetector.IsInstalled(result.Id))
            return true;
        if (result.Id == active)
            return true;
        if (WidgetSettingsService.IsProviderDashboardVisible(result.Id))
            return true;
        if (IsExplicitlyEnabled(result.Id))
            return true;
        if (result.Ok)
            return true;
        if (result.ErrorKind == ProviderErrorKind.AuthRequired)
            return true;
        if (result.ErrorKind == ProviderErrorKind.NotRunning)
            return true;
        if (IsConfigured(result.Id) && result.ErrorKind is not ProviderErrorKind.NotInstalled)
            return true;
        return false;
    }

    public static bool ShouldShowInAvailable(UsageResult result, ProviderId? active)
    {
        if (ShouldShowInDashboard(result, active))
            return false;

        return result.ErrorKind == ProviderErrorKind.NotInstalled
            || (!result.Ok && !IsConfigured(result.Id));
    }

    internal static void ResetForTesting()
    {
        lock (SyncRoot)
        {
            Probed.Clear();
            Configured.Clear();
            ExplicitlyEnabled.Clear();
            ExplicitlyDisabled.Clear();
        }
    }

    internal static void MarkProbedForTesting(ProviderId id)
    {
        lock (SyncRoot)
            Probed.Add(id);
    }

    internal static void MarkConfiguredForTesting(ProviderId id)
    {
        lock (SyncRoot)
            Configured.Add(id);
    }

    internal static void MarkExplicitlyDisabledForTesting(ProviderId id)
    {
        lock (SyncRoot)
            ExplicitlyDisabled.Add(id);
    }

    private static void EnsureProviderEnabled(ProviderId id)
    {
        if (TryEnableProviderSilent(id, out bool dashboardChanged))
            WidgetSettingsService.SaveProviderVisibilityAndNotify();

        if (dashboardChanged)
            WidgetSettingsService.SaveDashboardProviderVisibilityAndNotify();
    }

    private static bool TryEnableProviderSilent(ProviderId id, out bool dashboardChanged)
    {
        dashboardChanged = false;
        bool widgetChanged = false;

        if (!WidgetSettingsService.IsProviderDashboardVisible(id))
        {
            WidgetSettingsService.SetProviderDashboardVisibleSilent(id, true);
            dashboardChanged = true;
        }

        if (!WidgetSettingsService.IsProviderVisible(id))
        {
            WidgetSettingsService.SetProviderVisibleSilent(id, true);
            widgetChanged = true;
        }

        return widgetChanged;
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(StatePath))
                return;

            var state = JsonSerializer.Deserialize<DiscoveryState>(File.ReadAllText(StatePath));
            if (state is null)
                return;

            foreach (var id in state.Probed ?? [])
                if (Enum.TryParse<ProviderId>(id, out var parsed))
                    Probed.Add(parsed);

            foreach (var id in state.Configured ?? [])
                if (Enum.TryParse<ProviderId>(id, out var parsed))
                    Configured.Add(parsed);

            foreach (var id in state.ExplicitlyEnabled ?? [])
                if (Enum.TryParse<ProviderId>(id, out var parsed))
                    ExplicitlyEnabled.Add(parsed);

            foreach (var id in state.ExplicitlyDisabled ?? [])
                if (Enum.TryParse<ProviderId>(id, out var parsed))
                    ExplicitlyDisabled.Add(parsed);
        }
        catch
        {
            // Best effort — rediscover on next fetch.
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var state = new DiscoveryState
            {
                Probed = Probed.Select(id => id.ToString()).OrderBy(s => s).ToArray(),
                Configured = Configured.Select(id => id.ToString()).OrderBy(s => s).ToArray(),
                ExplicitlyEnabled = ExplicitlyEnabled.Select(id => id.ToString()).OrderBy(s => s).ToArray(),
                ExplicitlyDisabled = ExplicitlyDisabled.Select(id => id.ToString()).OrderBy(s => s).ToArray(),
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class DiscoveryState
    {
        public string[]? Probed { get; set; }
        public string[]? Configured { get; set; }
        public string[]? ExplicitlyEnabled { get; set; }
        public string[]? ExplicitlyDisabled { get; set; }
    }
}
