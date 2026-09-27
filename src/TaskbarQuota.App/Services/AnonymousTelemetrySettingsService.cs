using System;
using System.IO;
using System.Text.Json;

namespace TaskbarQuota.Services;

/// <summary>
/// Opt-out anonymous usage ping. An absent settings file means on, matching the goal of
/// estimating active users; Settings can turn it off at any time.
/// </summary>
internal static class AnonymousTelemetrySettingsService
{
    private static readonly AnonymousTelemetrySettingsStore Store = new(DefaultPath);

    internal static string DefaultPath =>
        Path.Combine(AppStorage.AppDataDirectory, "anonymous-telemetry.json");

    public static event EventHandler? Changed
    {
        add => Store.Changed += value;
        remove => Store.Changed -= value;
    }

    public static AnonymousTelemetrySettings Current => Store.Current;

    public static bool IsEnabled => Current.Enabled;

    public static void SetEnabled(bool enabled)
        => Store.Apply(Current with { Enabled = enabled });
}

internal sealed class AnonymousTelemetrySettingsStore
{
    private readonly string _settingsPath;
    private readonly object _lock = new();
    private AnonymousTelemetrySettings _current;

    public AnonymousTelemetrySettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
        _current = Load();
    }

    public event EventHandler? Changed;

    public AnonymousTelemetrySettings Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public void Apply(AnonymousTelemetrySettings settings)
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

    private AnonymousTelemetrySettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return AnonymousTelemetrySettings.Default;

            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<AnonymousTelemetrySettings>(json);
            return (loaded ?? AnonymousTelemetrySettings.Default).Normalized();
        }
        catch
        {
            return AnonymousTelemetrySettings.Default;
        }
    }

    private void Save(AnonymousTelemetrySettings settings)
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

internal sealed record AnonymousTelemetrySettings
{
    public static AnonymousTelemetrySettings Default { get; } = new() { Enabled = true };

    public bool Enabled { get; init; } = true;

    public AnonymousTelemetrySettings Normalized() => this;
}
