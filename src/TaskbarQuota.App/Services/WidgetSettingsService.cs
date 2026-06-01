using System;
using System.Globalization;
using System.IO;

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

public static class WidgetSettingsService
{
    private static readonly string WidgetDisplayModePath =
        Path.Combine(AppStorage.AppDataDirectory, "widget-display-mode.txt");

    private static readonly string PercentageDisplayModePath =
        Path.Combine(AppStorage.AppDataDirectory, "percentage-display-mode.txt");

    public static WidgetDisplayMode Current { get; private set; } = LoadWidgetDisplayMode();
    public static PercentageDisplayMode CurrentPercentageMode { get; private set; } = LoadPercentageDisplayMode();
    public static event EventHandler? Changed;
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
}
