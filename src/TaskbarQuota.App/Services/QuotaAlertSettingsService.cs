using System;
using System.IO;
using System.Text.Json;

namespace TaskbarQuota;

public static class QuotaAlertSettingsService
{
    private static readonly string SettingsPath =
        Path.Combine(AppStorage.AppDataDirectory, "quota-alerts.json");

    private static readonly QuotaAlertSettingsStore Store = new(SettingsPath);

    public static event EventHandler? Changed
    {
        add => Store.Changed += value;
        remove => Store.Changed -= value;
    }

    public static QuotaAlertSettings Current => Store.Current;

    public static void Apply(QuotaAlertSettings settings)
        => Store.Apply(settings);

    public static void SetEnabled(bool enabled)
        => Apply(Current with { Enabled = enabled });

    public static void SetReplenishmentEnabled(bool enabled)
        => Apply(Current with { ReplenishmentEnabled = enabled });

    public static void SetWarningThreshold(double value)
        => Apply(Current with { WarningThreshold = value });

    public static void SetCriticalThreshold(double value)
        => Apply(Current with { CriticalThreshold = value });

    public static void SetCooldownMinutes(double value)
        => Apply(Current with { CooldownMinutes = value });
}

internal sealed class QuotaAlertSettingsStore
{
    private readonly string _settingsPath;

    public QuotaAlertSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
        Current = Load();
    }

    public event EventHandler? Changed;

    public QuotaAlertSettings Current { get; private set; }

    public void Apply(QuotaAlertSettings settings)
    {
        var normalized = settings.Normalized();
        if (Current.Equals(normalized))
            return;

        Current = normalized;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private QuotaAlertSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return QuotaAlertSettings.Default;

            var loaded = JsonSerializer.Deserialize<QuotaAlertSettings>(File.ReadAllText(_settingsPath));
            return (loaded ?? QuotaAlertSettings.Default).Normalized();
        }
        catch
        {
            return QuotaAlertSettings.Default;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch
        {
            // Settings are best-effort; keep the in-memory values for this run.
        }
    }
}

public sealed record QuotaAlertSettings
{
    public static QuotaAlertSettings Default { get; } = new()
    {
        Enabled = false,
        ReplenishmentEnabled = false,
        WarningThreshold = 75,
        CriticalThreshold = 90,
        CooldownMinutes = 30,
    };

    public bool Enabled { get; init; }
    public bool ReplenishmentEnabled { get; init; }
    public double WarningThreshold { get; init; }
    public double CriticalThreshold { get; init; }
    public double CooldownMinutes { get; init; }

    public QuotaAlertSettings Normalized()
    {
        var warning = Math.Clamp(WarningThreshold, 1, 99);
        var critical = Math.Clamp(CriticalThreshold, 1, 100);
        if (critical <= warning)
            critical = Math.Min(100, warning + 1);

        return this with
        {
            WarningThreshold = Math.Round(warning),
            CriticalThreshold = Math.Round(critical),
            CooldownMinutes = Math.Clamp(Math.Round(CooldownMinutes), 1, 1440),
        };
    }
}
