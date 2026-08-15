using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TaskbarQuota.Usage;

namespace TaskbarQuota;

public enum WidgetDisplayMode
{
    BarsOnly = 0,
    PercentagesOnly = 1,
    BarsAndPercentages = 2,
}

public enum PercentageDisplayMode
{
    Consumed = 0,
    Remaining = 1,
}

/// <summary>
/// Where the compact usage UI is hosted: injected into each taskbar, or a single always-on-top
/// floating window. Modes are mutually exclusive.
/// </summary>
public enum WidgetSurfaceMode
{
    Taskbar = 0,
    Floating = 1,
}

public readonly record struct WidgetRowOption(string Id, string Label);

public static class WidgetSettingsService
{
    public const string RowPrimary = "primary";
    public const string RowSecondary = "secondary";
    public const string RowModelSpecific = "model";
    public const string RowMonthly = "monthly";
    public const string RowExtra = "extra";
    public const string RowUsage = "usage";
    public const string RowBalance = "balance";
    public const string RowCredits = "credits";
    public const string RowAdditionalUsage = "additional";
    public const string RowResetCredits = "reset_credits";

    private static readonly string WidgetDisplayModePath =
        Path.Combine(AppStorage.AppDataDirectory, "widget-display-mode.txt");

    private static string WidgetSurfaceModePath =>
        Path.Combine(AppStorage.AppDataDirectory, "widget-surface-mode.txt");

    private static string FloatingOpacityPath =>
        Path.Combine(AppStorage.AppDataDirectory, "floating-opacity.txt");

    private static readonly string PercentageDisplayModePath =
        Path.Combine(AppStorage.AppDataDirectory, "percentage-display-mode.txt");

    private static readonly string WidgetRowsPath =
        Path.Combine(AppStorage.AppDataDirectory, "widget-rows.json");

    private static readonly string WidgetProvidersPath =
        Path.Combine(AppStorage.AppDataDirectory, "widget-providers.json");

    private static readonly string WidgetPinsPath =
        Path.Combine(AppStorage.AppDataDirectory, "widget-pins.json");

    private static readonly string DashboardProvidersPath =
        Path.Combine(AppStorage.AppDataDirectory, "dashboard-providers.json");

    private static readonly string AutoHideUnavailablePath =
        Path.Combine(AppStorage.AppDataDirectory, "auto-hide-unavailable.txt");

    private static readonly string HideWhenUnfocusedPath =
        Path.Combine(AppStorage.AppDataDirectory, "hide-when-unfocused.txt");

    private static readonly string ShowAgentActivityInWidgetPath =
        Path.Combine(AppStorage.AppDataDirectory, "show-agent-activity-in-widget.txt");
    private static readonly string EnableAgentActivityMonitoringPath =
        Path.Combine(AppStorage.AppDataDirectory, "enable-agent-activity-monitoring.txt");

    private static readonly Dictionary<string, bool> RowVisibility = LoadRowVisibility();
    private static readonly Dictionary<string, bool> ProviderVisibility = LoadProviderVisibility();
    private static readonly Dictionary<string, bool> DashboardProviderVisibility = LoadDashboardProviderVisibility();
    private static readonly Dictionary<string, bool> ProviderPins = LoadProviderPins();

    /// <summary>Minimum material strength for the floating usage window (35%).</summary>
    public const double FloatingOpacityMin = 0.35;
    /// <summary>Maximum material strength for the floating usage window.</summary>
    public const double FloatingOpacityMax = 1.0;
    /// <summary>Default floating Acrylic strength.</summary>
    public const double FloatingOpacityDefault = 0.90;

    public static WidgetDisplayMode Current { get; private set; } = LoadWidgetDisplayMode();
    public static WidgetSurfaceMode CurrentSurface { get; private set; } = LoadWidgetSurfaceMode();
    /// <summary>Floating window Acrylic strength in the range [<see cref="FloatingOpacityMin"/>, <see cref="FloatingOpacityMax"/>].</summary>
    public static double FloatingOpacity { get; private set; } = LoadFloatingOpacity();
    public static PercentageDisplayMode CurrentPercentageMode { get; private set; } = LoadPercentageDisplayMode();
    public static bool AutoHideUnavailable { get; private set; } = LoadAutoHideUnavailable();
    /// <summary>
    /// Opt-in: the active provider's tile only stays on the taskbar while that provider's app is the
    /// foreground window. Off (the default) keeps today's behaviour, where the last active provider
    /// remains on the bar after the user switches to a browser or any other unrelated app. Pinned tiles
    /// are never affected — a pin is an explicit "always show this" request.
    /// </summary>
    public static bool HideWhenProviderUnfocused { get; private set; } = LoadHideWhenUnfocused();
    /// <summary>Whether the separate Agent Activity island is shown on the taskbar. Enabled by default.</summary>
    public static bool ShowAgentActivityInWidget { get; private set; } = LoadShowAgentActivityInWidget();
    /// <summary>Whether local agent processes and transcript stores are inspected for activity visualization.</summary>
    public static bool EnableAgentActivityMonitoring { get; private set; } = LoadEnableAgentActivityMonitoring();
    public static event EventHandler? Changed;
    public static event EventHandler? DashboardCompositionChanged;
    public static event EventHandler? PercentageModeChanged;

    internal static void ReloadSurfaceSettingsForTesting()
    {
        CurrentSurface = LoadWidgetSurfaceMode();
        FloatingOpacity = LoadFloatingOpacity();
    }

    internal static void RestoreSurfaceSettingsForTesting(
        WidgetSurfaceMode surface, double floatingOpacity)
    {
        CurrentSurface = surface;
        FloatingOpacity = floatingOpacity;
    }

    public static void Apply(WidgetDisplayMode mode)
    {
        if (Current == mode)
            return;

        Current = mode;
        Save(WidgetDisplayModePath, (int)mode);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplySurface(WidgetSurfaceMode mode)
    {
        if (CurrentSurface == mode)
            return;

        CurrentSurface = mode;
        Save(WidgetSurfaceModePath, (int)mode);
        // TaskBarManager enforces the taskbar budget after a current widget measurement exists.
        // Doing it here would use the span cached before floating mode and could remove valid pins.
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Sets floating window Acrylic strength. Values outside
    /// [<see cref="FloatingOpacityMin"/>, <see cref="FloatingOpacityMax"/>] are clamped.
    /// </summary>
    public static void ApplyFloatingOpacity(double opacity)
    {
        double clamped = Math.Clamp(opacity, FloatingOpacityMin, FloatingOpacityMax);
        // Ignore sub-percent noise from slider drag so we don't rewrite disk every pixel.
        if (Math.Abs(FloatingOpacity - clamped) < 0.005)
            return;

        FloatingOpacity = clamped;
        // Persist as integer percent for stable round-trips.
        Save(FloatingOpacityPath, (int)Math.Round(clamped * 100));
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Apply(PercentageDisplayMode mode)
    {
        if (CurrentPercentageMode == mode)
            return;

        CurrentPercentageMode = mode;
        Save(PercentageDisplayModePath, (int)mode);
        PercentageModeChanged?.Invoke(null, EventArgs.Empty);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static double DisplayPercent(double usedPercent)
    {
        double used = Math.Clamp(usedPercent, 0, 100);
        return CurrentPercentageMode == PercentageDisplayMode.Remaining ? 100 - used : used;
    }

    public static string FormatDisplayPercent(double usedPercent)
        => $"{DisplayPercent(usedPercent):0}%";

    public static bool IsRowVisible(ProviderId provider, string rowId)
    {
        var key = RowVisibilityKey(provider, rowId);
        return RowVisibility.TryGetValue(key, out bool visible)
            ? visible
            : DefaultRowVisible(provider, rowId);
    }

    public static bool TryGetRowVisibilityOverride(ProviderId provider, string rowId, out bool visible)
        => RowVisibility.TryGetValue(RowVisibilityKey(provider, rowId), out visible);

    public static bool IsProviderVisible(ProviderId provider)
    {
        var key = provider.ToString();
        return ProviderVisibility.TryGetValue(key, out bool visible) ? visible : true;
    }

    public static bool IsProviderDashboardVisible(ProviderId provider)
    {
        var key = provider.ToString();
        return DashboardProviderVisibility.TryGetValue(key, out bool visible) ? visible : true;
    }

    public static void SetProviderVisible(ProviderId provider, bool visible)
    {
        bool visibilityChanged = SetProviderVisibleSilent(provider, visible);
        // Hiding a provider from the widget drops its pin too: a pin that can never render is a state the
        // user can't see, and it would silently resurrect the tile when they re-enable the provider.
        bool pinChanged = !visible && SetProviderPinnedSilent(provider, false);
        if (!visibilityChanged && !pinChanged)
            return;

        if (visibilityChanged)
            SaveProviderVisibility();
        if (pinChanged)
            SaveProviderPins();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static bool IsProviderPinned(ProviderId provider)
        => ProviderPins.TryGetValue(provider.ToString(), out bool pinned) && pinned;

    /// <summary>
    /// Pins a provider so the taskbar widget keeps a tile for it regardless of which tool is active.
    /// Pinning implies widget-visible — a provider hidden from the widget can't hold a permanent tile —
    /// so turning a pin on also turns the provider's widget visibility on. Unpinning leaves it alone.
    /// </summary>
    public static void SetProviderPinned(ProviderId provider, bool pinned)
    {
        bool pinChanged = SetProviderPinnedSilent(provider, pinned);
        bool visibilityChanged = pinned && SetProviderVisibleSilent(provider, true);
        if (!pinChanged && !visibilityChanged)
            return;

        if (pinChanged)
            SaveProviderPins();
        if (visibilityChanged)
            SaveProviderVisibility();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static bool SetProviderPinnedSilent(ProviderId provider, bool pinned)
    {
        if (IsProviderPinned(provider) == pinned)
            return false;

        ProviderPins[provider.ToString()] = pinned;
        return true;
    }

    public static void SetProviderDashboardVisible(ProviderId provider, bool visible)
    {
        if (!SetProviderDashboardVisibleSilent(provider, visible))
            return;

        SaveDashboardProviderVisibility();
        DashboardCompositionChanged?.Invoke(null, EventArgs.Empty);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static bool SetProviderVisibleSilent(ProviderId provider, bool visible)
    {
        if (IsProviderVisible(provider) == visible)
            return false;

        ProviderVisibility[provider.ToString()] = visible;
        return true;
    }

    internal static bool SetProviderDashboardVisibleSilent(ProviderId provider, bool visible)
    {
        if (IsProviderDashboardVisible(provider) == visible)
            return false;

        DashboardProviderVisibility[provider.ToString()] = visible;
        return true;
    }

    internal static void SaveProviderPinsAndNotify()
    {
        SaveProviderPins();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void SaveProviderVisibilityAndNotify()
    {
        SaveProviderVisibility();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void SaveDashboardProviderVisibilityAndNotify()
    {
        SaveDashboardProviderVisibility();
        DashboardCompositionChanged?.Invoke(null, EventArgs.Empty);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyAutoHideUnavailable(bool enabled)
    {
        if (AutoHideUnavailable == enabled)
            return;

        AutoHideUnavailable = enabled;
        Save(AutoHideUnavailablePath, enabled ? 1 : 0);
        DashboardCompositionChanged?.Invoke(null, EventArgs.Empty);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyHideWhenProviderUnfocused(bool enabled)
    {
        if (HideWhenProviderUnfocused == enabled)
            return;

        HideWhenProviderUnfocused = enabled;
        Save(HideWhenUnfocusedPath, enabled ? 1 : 0);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyShowAgentActivityInWidget(bool enabled)
    {
        if (ShowAgentActivityInWidget == enabled)
            return;

        ShowAgentActivityInWidget = enabled;
        Save(ShowAgentActivityInWidgetPath, enabled ? 1 : 0);
        // Activity shares the taskbar area with quota tiles. Rebalance pins immediately so enabling it
        // leaves room for the active quota tile plus one pinned tile (the normal cap is three quota tiles).
        Services.PinBudgetService.EnforceBudget(notify: false);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyEnableAgentActivityMonitoring(bool enabled)
    {
        if (EnableAgentActivityMonitoring == enabled)
            return;

        EnableAgentActivityMonitoring = enabled;
        Save(EnableAgentActivityMonitoringPath, enabled ? 1 : 0);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetRowVisible(ProviderId provider, string rowId, bool visible)
    {
        var key = RowVisibilityKey(provider, rowId);
        if (IsRowVisible(provider, rowId) == visible)
            return;

        RowVisibility[key] = visible;
        SaveRowVisibility();
        // Enabling a row can promote a pinned provider from short to long and push the pinned set over
        // budget. The row toggle always wins — it is this provider's own display setting — so the budget
        // is rebalanced by dropping the least recently used pin instead of refusing the change.
        //
        // Silent, because the single Changed below already covers both edits. Letting the rebalance raise
        // its own notification meant one row toggle rebuilt the nav badges, the flyout strip and every
        // widget tile twice.
        Services.PinBudgetService.EnforceBudget(notify: false);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ResetRows(ProviderId provider)
    {
        var prefix = $"{provider}:";
        var keys = new List<string>();
        foreach (var key in RowVisibility.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                keys.Add(key);

        if (keys.Count == 0)
            return;

        foreach (var key in keys)
            RowVisibility.Remove(key);

        SaveRowVisibility();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string RowVisibilitySignature(ProviderId provider)
    {
        string[] rowIds =
        [
            RowPrimary,
            RowSecondary,
            RowModelSpecific,
            RowMonthly,
            RowExtra,
            RowUsage,
            RowBalance,
            RowCredits,
            RowAdditionalUsage,
            RowResetCredits,
        ];

        var parts = new List<string>(rowIds.Length);
        foreach (var rowId in rowIds)
            parts.Add($"{rowId}:{(IsRowVisible(provider, rowId) ? 1 : 0)}");
        return string.Join(",", parts);
    }

    public static string ProviderVisibilitySignature()
    {
        var parts = Enum.GetValues<ProviderId>()
            .OrderBy(provider => provider.ToString())
            .Select(provider =>
                $"{provider}:{(IsProviderVisible(provider) ? 1 : 0)}:{(IsProviderDashboardVisible(provider) ? 1 : 0)}:{(IsProviderPinned(provider) ? 1 : 0)}");
        return string.Join(",", parts);
    }

    internal static void ResetRowVisibilityForTesting()
        => RowVisibility.Clear();

    internal static void SetRowVisibleForTesting(ProviderId provider, string rowId, bool visible)
        => RowVisibility[RowVisibilityKey(provider, rowId)] = visible;

    internal static void ResetProviderVisibilityForTesting()
        => ProviderVisibility.Clear();

    internal static void SetProviderVisibleForTesting(ProviderId provider, bool visible)
        => ProviderVisibility[provider.ToString()] = visible;

    internal static void SetProviderDashboardVisibleForTesting(ProviderId provider, bool visible)
        => DashboardProviderVisibility[provider.ToString()] = visible;

    internal static void ResetDashboardProviderVisibilityForTesting()
        => DashboardProviderVisibility.Clear();

    internal static void SetProviderPinnedForTesting(ProviderId provider, bool pinned)
        => ProviderPins[provider.ToString()] = pinned;

    internal static void ResetProviderPinsForTesting()
        => ProviderPins.Clear();

    public static IReadOnlyList<WidgetRowOption> RowOptions(ProviderId provider)
        => provider switch
        {
            ProviderId.Antigravity =>
            [
                new(RowPrimary, "Gemini Weekly"),
                new(RowModelSpecific, "Gemini 5h"),
                new(RowSecondary, "Non-Gemini Weekly"),
                new(RowMonthly, "Non-Gemini 5h"),
            ],
            ProviderId.OpenCode =>
            [
                new(RowUsage, "Usage"),
                new(RowBalance, "Balance"),
            ],
            ProviderId.Copilot =>
            [
                new(RowCredits, "Credits"),
                new(RowAdditionalUsage, "Additional usage"),
                new(RowPrimary, "Session"),
                new(RowSecondary, "Weekly"),
                new(RowModelSpecific, "Completions"),
                new(RowExtra, "Extra quota rows"),
            ],
            ProviderId.Zai =>
            [
                new(RowPrimary, "Session"),
                new(RowSecondary, "Weekly"),
                new(RowCredits, "Credits"),
                new(RowExtra, "MCP"),
            ],
            ProviderId.Claude =>
            [
                new(RowPrimary, "Session"),
                new(RowSecondary, "Weekly"),
                new(RowModelSpecific, "Model weekly"),
                new(RowExtra, "Extra weekly rows"),
            ],
            ProviderId.Cursor =>
            [
                new(RowSecondary, "Auto + Composer"),
                new(RowModelSpecific, "API usage"),
                new(RowPrimary, "Total usage"),
            ],
            ProviderId.Codex =>
            [
                new(RowPrimary, "Session"),
                new(RowSecondary, "Weekly"),
                new(RowModelSpecific, "Model"),
                new(RowMonthly, "Monthly"),
                new(RowCredits, "Credits"),
                new(RowResetCredits, "Reset credits"),
                new(RowExtra, "Extra model rows"),
            ],
            _ =>
            [
                new(RowPrimary, "Session"),
                new(RowSecondary, "Weekly"),
                new(RowModelSpecific, "Model"),
                new(RowMonthly, "Monthly"),
                new(RowExtra, "Extra quota rows"),
            ],
        };

    /// <summary>
    /// Theme brush key for a usage value already in display form (consumed or remaining per settings).
    /// Matches taskbar widget thresholds: red at 90%+, yellow at 75%+ consumed (or 25%/10% remaining).
    /// </summary>
    public static string GetUsageBrushResourceKeyForDisplayPercent(double displayPercent)
    {
        displayPercent = Math.Clamp(displayPercent, 0, 100);
        if (CurrentPercentageMode == PercentageDisplayMode.Remaining)
        {
            if (displayPercent <= 10)
                return "SystemFillColorCriticalBrush";
            if (displayPercent <= 25)
                return "SystemFillColorCautionBrush";
            return "TextFillColorPrimaryBrush";
        }

        if (displayPercent >= 90)
            return "SystemFillColorCriticalBrush";
        if (displayPercent >= 75)
            return "SystemFillColorCautionBrush";
        return "TextFillColorPrimaryBrush";
    }

    /// <summary>Brush key from raw consumed percent, honoring the current percentage display mode.</summary>
    public static string GetUsageBrushResourceKey(double usedPercent)
        => GetUsageBrushResourceKeyForDisplayPercent(DisplayPercent(usedPercent));

    /// <summary>Brush key for consumed-only meters (credits) regardless of display mode.</summary>
    public static string GetConsumedUsageBrushResourceKey(double consumedPercent)
    {
        consumedPercent = Math.Clamp(consumedPercent, 0, 100);
        if (consumedPercent >= 90)
            return "SystemFillColorCriticalBrush";
        if (consumedPercent >= 75)
            return "SystemFillColorCautionBrush";
        return "TextFillColorPrimaryBrush";
    }

    private static WidgetDisplayMode LoadWidgetDisplayMode()
    {
        try
        {
            if (!File.Exists(WidgetDisplayModePath))
                return WidgetDisplayMode.BarsOnly;

            string raw = File.ReadAllText(WidgetDisplayModePath);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && Enum.IsDefined(typeof(WidgetDisplayMode), value)
                ? (WidgetDisplayMode)value
                : WidgetDisplayMode.BarsOnly;
        }
        catch
        {
            return WidgetDisplayMode.BarsOnly;
        }
    }

    private static WidgetSurfaceMode LoadWidgetSurfaceMode()
    {
        try
        {
            if (!File.Exists(WidgetSurfaceModePath))
                return WidgetSurfaceMode.Taskbar;

            string raw = File.ReadAllText(WidgetSurfaceModePath);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && Enum.IsDefined(typeof(WidgetSurfaceMode), value)
                ? (WidgetSurfaceMode)value
                : WidgetSurfaceMode.Taskbar;
        }
        catch
        {
            return WidgetSurfaceMode.Taskbar;
        }
    }

    private static double LoadFloatingOpacity()
    {
        try
        {
            if (!File.Exists(FloatingOpacityPath))
                return FloatingOpacityDefault;

            string raw = File.ReadAllText(FloatingOpacityPath).Trim();
            // Stored as integer percent (35–100). Also accept a fractional 0–1 for hand-edited files.
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent)
                && percent is >= 1 and <= 100)
            {
                return Math.Clamp(percent / 100d, FloatingOpacityMin, FloatingOpacityMax);
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction)
                && fraction is > 0 and <= 1)
            {
                return Math.Clamp(fraction, FloatingOpacityMin, FloatingOpacityMax);
            }

            return FloatingOpacityDefault;
        }
        catch
        {
            return FloatingOpacityDefault;
        }
    }

    private static PercentageDisplayMode LoadPercentageDisplayMode()
    {
        try
        {
            if (!File.Exists(PercentageDisplayModePath))
                return PercentageDisplayMode.Consumed;

            string raw = File.ReadAllText(PercentageDisplayModePath);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && Enum.IsDefined(typeof(PercentageDisplayMode), value)
                ? (PercentageDisplayMode)value
                : PercentageDisplayMode.Consumed;
        }
        catch
        {
            return PercentageDisplayMode.Consumed;
        }
    }

    private static void Save(string path, int value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best effort. The widget can still use the in-memory value for this run.
        }
    }

    private static Dictionary<string, bool> LoadRowVisibility()
    {
        try
        {
            if (!File.Exists(WidgetRowsPath))
                return new Dictionary<string, bool>();

            var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(WidgetRowsPath));
            return loaded ?? new Dictionary<string, bool>();
        }
        catch
        {
            return new Dictionary<string, bool>();
        }
    }

    private static void SaveRowVisibility()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WidgetRowsPath)!);
            File.WriteAllText(WidgetRowsPath, JsonSerializer.Serialize(RowVisibility, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort. The widget can still use the in-memory value for this run.
        }
    }

    private static Dictionary<string, bool> LoadProviderVisibility()
    {
        try
        {
            if (!File.Exists(WidgetProvidersPath))
                return new Dictionary<string, bool>();

            var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(WidgetProvidersPath));
            return loaded ?? new Dictionary<string, bool>();
        }
        catch
        {
            return new Dictionary<string, bool>();
        }
    }

    private static void SaveProviderVisibility()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WidgetProvidersPath)!);
            File.WriteAllText(WidgetProvidersPath, JsonSerializer.Serialize(ProviderVisibility, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort. The widget can still use the in-memory value for this run.
        }
    }

    private static Dictionary<string, bool> LoadProviderPins()
    {
        try
        {
            if (!File.Exists(WidgetPinsPath))
                return new Dictionary<string, bool>();

            var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(WidgetPinsPath));
            return loaded ?? new Dictionary<string, bool>();
        }
        catch
        {
            return new Dictionary<string, bool>();
        }
    }

    internal static void SaveProviderPins()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WidgetPinsPath)!);
            File.WriteAllText(WidgetPinsPath, JsonSerializer.Serialize(ProviderPins, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort. The widget can still use the in-memory value for this run.
        }
    }

    private static Dictionary<string, bool> LoadDashboardProviderVisibility()
    {
        try
        {
            if (!File.Exists(DashboardProvidersPath))
                return new Dictionary<string, bool>();

            var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(DashboardProvidersPath));
            return loaded ?? new Dictionary<string, bool>();
        }
        catch
        {
            return new Dictionary<string, bool>();
        }
    }

    private static void SaveDashboardProviderVisibility()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DashboardProvidersPath)!);
            File.WriteAllText(DashboardProvidersPath, JsonSerializer.Serialize(DashboardProviderVisibility, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }

    private static bool LoadAutoHideUnavailable()
    {
        try
        {
            if (!File.Exists(AutoHideUnavailablePath))
                return true;

            string raw = File.ReadAllText(AutoHideUnavailablePath);
            return !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value != 0;
        }
        catch
        {
            return true;
        }
    }

    // Opt-in, so an absent file means off — the opposite default from AutoHideUnavailable, whose file
    // absence means "on". Existing installs must not silently start hiding the widget after an update.
    private static bool LoadHideWhenUnfocused()
    {
        try
        {
            if (!File.Exists(HideWhenUnfocusedPath))
                return false;

            string raw = File.ReadAllText(HideWhenUnfocusedPath);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool LoadShowAgentActivityInWidget()
    {
        try
        {
            if (!File.Exists(ShowAgentActivityInWidgetPath))
                return true;

            string raw = File.ReadAllText(ShowAgentActivityInWidgetPath);
            return !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool LoadEnableAgentActivityMonitoring()
    {
        try
        {
            if (!File.Exists(EnableAgentActivityMonitoringPath))
                return true;

            string raw = File.ReadAllText(EnableAgentActivityMonitoringPath);
            return !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value != 0;
        }
        catch
        {
            return true;
        }
    }

    private static string RowVisibilityKey(ProviderId provider, string rowId)
        => $"{provider}:{rowId}";

    private static bool DefaultRowVisible(ProviderId provider, string rowId)
        => provider == ProviderId.Zai && rowId is RowExtra or RowCredits
            || provider != ProviderId.Codex
              || rowId == RowPrimary
              || rowId == RowSecondary
              || rowId == RowCredits
              || rowId == RowResetCredits;
}
