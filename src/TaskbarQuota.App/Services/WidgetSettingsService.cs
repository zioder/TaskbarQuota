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

    private static readonly string PercentageDisplayModePath =
        Path.Combine(AppStorage.AppDataDirectory, "percentage-display-mode.txt");

    private static readonly string WidgetRowsPath =
        Path.Combine(AppStorage.AppDataDirectory, "widget-rows.json");

    private static readonly string WidgetProvidersPath =
        Path.Combine(AppStorage.AppDataDirectory, "widget-providers.json");

    private static readonly string DashboardProvidersPath =
        Path.Combine(AppStorage.AppDataDirectory, "dashboard-providers.json");

    private static readonly string AutoHideUnavailablePath =
        Path.Combine(AppStorage.AppDataDirectory, "auto-hide-unavailable.txt");

    private static readonly Dictionary<string, bool> RowVisibility = LoadRowVisibility();
    private static readonly Dictionary<string, bool> ProviderVisibility = LoadProviderVisibility();
    private static readonly Dictionary<string, bool> DashboardProviderVisibility = LoadDashboardProviderVisibility();

    public static WidgetDisplayMode Current { get; private set; } = LoadWidgetDisplayMode();
    public static PercentageDisplayMode CurrentPercentageMode { get; private set; } = LoadPercentageDisplayMode();
    public static bool AutoHideUnavailable { get; private set; } = LoadAutoHideUnavailable();
    public static event EventHandler? Changed;
    public static event EventHandler? DashboardCompositionChanged;
    public static event EventHandler? PercentageModeChanged;

    public static void Apply(WidgetDisplayMode mode)
    {
        if (Current == mode)
            return;

        Current = mode;
        Save(WidgetDisplayModePath, (int)mode);
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
        if (!SetProviderVisibleSilent(provider, visible))
            return;

        SaveProviderVisibility();
        Changed?.Invoke(null, EventArgs.Empty);
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

    public static void SetRowVisible(ProviderId provider, string rowId, bool visible)
    {
        var key = RowVisibilityKey(provider, rowId);
        if (IsRowVisible(provider, rowId) == visible)
            return;

        RowVisibility[key] = visible;
        SaveRowVisibility();
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
                $"{provider}:{(IsProviderVisible(provider) ? 1 : 0)}:{(IsProviderDashboardVisible(provider) ? 1 : 0)}");
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

    private static string RowVisibilityKey(ProviderId provider, string rowId)
        => $"{provider}:{rowId}";

    private static bool DefaultRowVisible(ProviderId provider, string rowId)
        => provider != ProviderId.Codex
        || rowId == RowPrimary
        || rowId == RowSecondary
        || rowId == RowResetCredits;
}
