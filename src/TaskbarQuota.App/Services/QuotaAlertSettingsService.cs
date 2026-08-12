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

    public static void SetCrossSessionReplenishmentEnabled(bool enabled)
        => Apply(Current with { CrossSessionReplenishmentEnabled = enabled });

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
    private readonly object _lock = new();
    private QuotaAlertSettings _current;

    public QuotaAlertSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
        _current = Load();
    }

    public event EventHandler? Changed;

    public QuotaAlertSettings Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public void Apply(QuotaAlertSettings settings)
    {
        var normalized = settings.Normalized();
        EventHandler? changed;
        lock (_lock)
        {
            if (_current.Equals(normalized))
                return;

            _current = normalized;
            Save(normalized);
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    private QuotaAlertSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return QuotaAlertSettings.Default;

            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<QuotaAlertSettings>(json);
            if (loaded is null)
                return QuotaAlertSettings.Default;

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(nameof(QuotaAlertSettings.ReplenishmentEnabled), out _))
            {
                loaded = loaded with
                {
                    ReplenishmentEnabled = QuotaAlertSettings.Default.ReplenishmentEnabled,
                };
            }

            return loaded.Normalized();
        }
        catch
        {
            return QuotaAlertSettings.Default;
        }
    }

    private void Save(QuotaAlertSettings settings)
    {
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch
        {
            // Settings are best-effort; keep the in-memory values for this run.
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // A failed best-effort save must not surface through temporary-file cleanup.
                }
            }
        }
    }
}

public sealed record QuotaAlertSettings
{
    public static QuotaAlertSettings Default { get; } = new()
    {
        Enabled = false,
        ReplenishmentEnabled = true,
        CrossSessionReplenishmentEnabled = false,
        WarningThreshold = 75,
        CriticalThreshold = 90,
        CooldownMinutes = 30,
    };

    public bool Enabled { get; init; }
    public bool ReplenishmentEnabled { get; init; }
    public bool CrossSessionReplenishmentEnabled { get; init; }
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
