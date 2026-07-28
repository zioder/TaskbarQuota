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
        Assert.Equal(QuotaAlertSettings.Default, store.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"ReplenishmentEnabled":"yes"}""")]
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

        store.Apply(store.Current with { ReplenishmentEnabled = true });

        Assert.True(store.Current.ReplenishmentEnabled);
        Assert.Equal(1, changed);
        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.True(JsonSerializer.Deserialize<QuotaAlertSettings>(File.ReadAllText(SettingsPath))!.ReplenishmentEnabled);
        Assert.True(new QuotaAlertSettingsStore(SettingsPath).Current.ReplenishmentEnabled);
    }

    [Fact]
    public void Apply_CanDisableAndDoesNotRaiseForAnUnchangedValue()
    {
        var store = new QuotaAlertSettingsStore(SettingsPath);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Apply(store.Current with { ReplenishmentEnabled = true });
        store.Apply(store.Current with { ReplenishmentEnabled = false });
        store.Apply(store.Current with { ReplenishmentEnabled = false });

        Assert.False(store.Current.ReplenishmentEnabled);
        Assert.Equal(2, changed);
        Assert.False(new QuotaAlertSettingsStore(SettingsPath).Current.ReplenishmentEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
