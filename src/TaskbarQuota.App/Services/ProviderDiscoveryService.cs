using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TaskbarQuota.Usage;

namespace TaskbarQuota;

/// <summary>
/// Tracks which providers are probed, configured, and visible in the dashboard vs taskbar widget.
///
/// Display rules (strict — a provider must never leak onto the UI on its own):
/// <list type="bullet">
///   <item>Explicitly disabled → never shown or fetched anywhere. Only the Settings
///   toggle opts back in.</item>
///   <item>Not installed → never shown or fetched. Install state comes from
///   <see cref="ProviderInstallDetector"/> (CLI, credentials, or desktop app).</item>
///   <item>Installed → shown only with open/use evidence: the detected active provider
///   (CLI terminal, desktop app, or a browser tab linking the app), recently-active
///   this session, or explicit user intent (enabled in Settings, pinned).</item>
/// </list>
/// Installed-but-idle providers stay hidden and unfetched.
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

    /// <summary>Test hook for open/use evidence without running foreground detection.</summary>
    internal static Func<ProviderId, bool>? IsRecentlyActiveOverrideForTesting;

    static ProviderDiscoveryService() => Load();

    /// <summary>
    /// Enforces "never shown when not installed" against stale settings: hides providers
    /// with nothing installed unless the user explicitly opted in. Never makes anything
    /// visible — surfacing is driven by open/use evidence (see
    /// <see cref="ShouldShowInDashboard"/>) or explicit opt-in.
    /// </summary>
    public static void SyncInstalledProviderVisibility()
    {
        ProviderInstallDetector.WarmCliCache();

        lock (SyncRoot)
        {
            bool widgetChanged = false;
            bool dashboardChanged = false;
            foreach (ProviderId id in Enum.GetValues<ProviderId>())
            {
                if (ProviderInstallDetector.IsInstalled(id) || ExplicitlyEnabled.Contains(id))
                    continue;

                widgetChanged |= WidgetSettingsService.SetProviderVisibleSilent(id, false);
                dashboardChanged |= WidgetSettingsService.SetProviderDashboardVisibleSilent(id, false);
            }

            if (widgetChanged)
                WidgetSettingsService.SaveProviderVisibilityAndNotify();

            if (dashboardChanged)
                WidgetSettingsService.SaveDashboardProviderVisibilityAndNotify();
        }
    }

    public static void RecordFetchResult(UsageResult result)
    {
        lock (SyncRoot)
        {
            Probed.Add(result.Id);

            if (result.Ok || result.ErrorKind == ProviderErrorKind.AuthRequired)
                Configured.Add(result.Id);

            // Never auto-show: visibility flips only via explicit user action
            // (Settings toggle, pin, OAuth login). A successful fetch of an idle
            // provider must not resurrect it on the dashboard or widget.
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
            bool widgetChanged = WidgetSettingsService.SetProviderVisibleSilent(id, true);
            bool dashboardChanged = WidgetSettingsService.SetProviderDashboardVisibleSilent(id, true);
            if (widgetChanged)
                WidgetSettingsService.SaveProviderVisibilityAndNotify();
            if (dashboardChanged)
                WidgetSettingsService.SaveDashboardProviderVisibilityAndNotify();
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
        if (IsExplicitlyDisabled(id))
            return false;
        if (id == active)
            return true;
        return IsEligible(id);
    }

    public static bool ShouldShowInDashboard(UsageResult result, ProviderId? active)
    {
        if (IsExplicitlyDisabled(result.Id))
            return false;
        if (result.Id == active)
            return true;
        return IsEligible(result.Id);
    }

    /// <summary>
    /// Eligibility beyond the active provider: installed, and either explicitly kept
    /// (enabled in Settings, pinned) or recently used this session. Installed-but-idle
    /// providers stay hidden and unfetched so they can't leak back on their own.
    /// </summary>
    private static bool IsEligible(ProviderId id)
    {
        if (!ProviderInstallDetector.IsInstalled(id))
            return false;
        if (IsExplicitlyEnabled(id))
            return true;
        if (WidgetSettingsService.IsProviderPinned(id))
            return true;
        return IsRecentlyActive(id);
    }

    /// <summary>
    /// Open/use evidence: the provider was detected in the foreground this session via
    /// a CLI terminal, desktop app, host app, or a browser tab linking the app.
    /// </summary>
    private static bool IsRecentlyActive(ProviderId id)
    {
        if (IsRecentlyActiveOverrideForTesting is { } fn)
            return fn(id);
        return UsageCoordinator.Instance.RecentProviders.Contains(id);
    }

    public static bool ShouldShowInAvailable(UsageResult result, ProviderId? active)
    {
        // Discovery surfacing is disabled on purpose: providers appear only with
        // open/use evidence or explicit opt-in (see ShouldShowInDashboard). Anything
        // else — including not-installed providers — stays hidden; Settings is the
        // opt-in surface. Kept (always false) so the Available pipeline stays inert.
        _ = result;
        _ = active;
        return false;
    }

    internal static void ResetForTesting()
    {
        lock (SyncRoot)
        {
            Probed.Clear();
            Configured.Clear();
            ExplicitlyEnabled.Clear();
            ExplicitlyDisabled.Clear();
            IsRecentlyActiveOverrideForTesting = null;
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
