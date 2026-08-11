using System.Text.Json;

namespace TaskbarQuota.Tests;

public sealed class QuotaAlertSettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"taskbarquota-alert-settings-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "quota-alerts.json");

    [Fact]
    public void MissingFile_UsesDisabledReplenishmentDefault()
    {
        var store = new QuotaAlertSettingsStore(SettingsPath);

        Assert.False(store.Current.ReplenishmentEnabled);
        Assert.False(store.Current.CrossSessionReplenishmentEnabled);
        Assert.Equal(QuotaAlertSettings.Default, store.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"ReplenishmentEnabled":"yes"}""")]
    [InlineData("""{"CrossSessionReplenishmentEnabled":"yes"}""")]
    public void InvalidFile_UsesSafeDefaults(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, contents);

        var store = new QuotaAlertSettingsStore(SettingsPath);

        Assert.Equal(QuotaAlertSettings.Default, store.Current);
    }

    [Fact]
    public void LegacyFile_PreservesExistingValuesAndDefaultsReplenishmentOff()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath,
            """
            {
              "Enabled": true,
              "WarningThreshold": 70,
              "CriticalThreshold": 95,
              "CooldownMinutes": 45
            }
            """);

        var store = new QuotaAlertSettingsStore(SettingsPath);

        Assert.True(store.Current.Enabled);
        Assert.False(store.Current.ReplenishmentEnabled);
        Assert.False(store.Current.CrossSessionReplenishmentEnabled);
        Assert.Equal(70, store.Current.WarningThreshold);
        Assert.Equal(95, store.Current.CriticalThreshold);
        Assert.Equal(45, store.Current.CooldownMinutes);
    }

    [Fact]
    public void Apply_ChangesImmediatelyRaisesEventAndPersistsAtomically()
    {
        var store = new QuotaAlertSettingsStore(SettingsPath);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Apply(store.Current with
        {
            ReplenishmentEnabled = true,
            CrossSessionReplenishmentEnabled = true,
        });

        Assert.True(store.Current.ReplenishmentEnabled);
        Assert.True(store.Current.CrossSessionReplenishmentEnabled);
        Assert.Equal(1, changed);
        Assert.True(File.Exists(SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        Assert.True(JsonSerializer.Deserialize<QuotaAlertSettings>(File.ReadAllText(SettingsPath))!.ReplenishmentEnabled);
        var restored = new QuotaAlertSettingsStore(SettingsPath).Current;
        Assert.True(restored.ReplenishmentEnabled);
        Assert.True(restored.CrossSessionReplenishmentEnabled);
    }

    [Fact]
    public void Apply_CanDisableAndDoesNotRaiseForAnUnchangedValue()
    {
        var store = new QuotaAlertSettingsStore(SettingsPath);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Apply(store.Current with { ReplenishmentEnabled = true });
        store.Apply(store.Current with { CrossSessionReplenishmentEnabled = true });
        store.Apply(store.Current with { CrossSessionReplenishmentEnabled = false });
        store.Apply(store.Current with { ReplenishmentEnabled = false });
        store.Apply(store.Current with { ReplenishmentEnabled = false });

        Assert.False(store.Current.ReplenishmentEnabled);
        Assert.False(store.Current.CrossSessionReplenishmentEnabled);
        Assert.Equal(4, changed);
        Assert.False(new QuotaAlertSettingsStore(SettingsPath).Current.ReplenishmentEnabled);
    }

    [Fact]
    public async Task ConcurrentApply_PersistsCurrentValueWithoutTemporaryFiles()
    {
        var store = new QuotaAlertSettingsStore(SettingsPath);
        var updates = Enumerable.Range(1, 32)
            .Select(index => Task.Run(() => store.Apply(store.Current with
            {
                WarningThreshold = 40 + index,
                CriticalThreshold = 80 + index % 20,
            })));

        await Task.WhenAll(updates);

        var persisted = JsonSerializer.Deserialize<QuotaAlertSettings>(File.ReadAllText(SettingsPath));
        Assert.Equal(store.Current, persisted);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
            return;

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Cleanup should not replace a test assertion failure.
        }
    }
}
