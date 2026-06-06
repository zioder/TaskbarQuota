using System;
using System.IO;
using System.Text.Json;

namespace TaskbarQuota;

public static class QuotaAlertSettingsService
{
    private static readonly string SettingsPath =
        Path.Combine(AppStorage.AppDataDirectory, "quota-alerts.json");

    private static QuotaAlertSettings _current = Load();

    public static event EventHandler? Changed;

    public static QuotaAlertSettings Current => _current;

    public static void Apply(QuotaAlertSettings settings)
    {
        var normalized = settings.Normalized();
        if (_current.Equals(normalized))
            return;

        _current = normalized;
        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetEnabled(bool enabled)
        => Apply(_current with { Enabled = enabled });

    public static void SetWarningThreshold(double value)
        => Apply(_current with { WarningThreshold = value });

    public static void SetCriticalThreshold(double value)
        => Apply(_current with { CriticalThreshold = value });

    public static void SetCooldownMinutes(double value)
        => Apply(_current with { CooldownMinutes = value });

    private static QuotaAlertSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return QuotaAlertSettings.Default;

            var loaded = JsonSerializer.Deserialize<QuotaAlertSettings>(File.ReadAllText(SettingsPath));
            return (loaded ?? QuotaAlertSettings.Default).Normalized();
        }
        catch
        {
            return QuotaAlertSettings.Default;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));
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
        WarningThreshold = 75,
        CriticalThreshold = 90,
        CooldownMinutes = 30,
    };

    public bool Enabled { get; init; }
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
