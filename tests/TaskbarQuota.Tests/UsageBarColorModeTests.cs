using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
public class UsageBarColorModeTests
{
    private static void WithSettings(Action<string> body)
    {
        var previousColor = WidgetSettingsService.CurrentUsageBarColorMode;
        var previousPercentage = WidgetSettingsService.CurrentPercentageMode;
        var directory = Path.Combine(Path.GetTempPath(), "tbq-bar-colors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var storageOverride = AppStorage.OverrideAppDataDirectoryForTesting(directory);
        try
        {
            WidgetSettingsService.ReloadUsageBarColorModeForTesting();
            body(directory);
        }
        finally
        {
            WidgetSettingsService.Apply(previousColor);
            WidgetSettingsService.Apply(previousPercentage);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-mode")]
    [InlineData("-1")]
    [InlineData("2")]
    public void MissingOrInvalidSetting_PreservesForecastColors(string? setting)
    {
        WithSettings(directory =>
        {
            if (setting is not null)
                File.WriteAllText(Path.Combine(directory, "usage-bar-color-mode.txt"), setting);
            WidgetSettingsService.ReloadUsageBarColorModeForTesting();

            Assert.Equal(UsageBarColorMode.UsageForecast, WidgetSettingsService.CurrentUsageBarColorMode);
            Assert.Equal("SystemFillColorCriticalBrush",
                WidgetSettingsService.GetUsageBarBrushResourceKey(66, PaceSeverity.RunningOut));
        });
    }

    [Fact]
    public void ColorMode_PersistsReloadsAndNotifiesWithoutDuplicateEvents()
    {
        WithSettings(directory =>
        {
            int colorsChanged = 0, settingsChanged = 0;
            void OnColorsChanged(object? sender, EventArgs e) => colorsChanged++;
            void OnSettingsChanged(object? sender, EventArgs e) => settingsChanged++;
            WidgetSettingsService.UsageBarColorModeChanged += OnColorsChanged;
            WidgetSettingsService.Changed += OnSettingsChanged;
            try
            {
                WidgetSettingsService.Apply(UsageBarColorMode.QuotaLevel);
                WidgetSettingsService.Apply(UsageBarColorMode.QuotaLevel);
                WidgetSettingsService.Apply((UsageBarColorMode)99);
                Assert.Equal(1, colorsChanged);
                Assert.Equal(1, settingsChanged);
                Assert.Equal("0", File.ReadAllText(Path.Combine(directory, "usage-bar-color-mode.txt")));
                WidgetSettingsService.ReloadUsageBarColorModeForTesting();
                Assert.Equal(UsageBarColorMode.QuotaLevel, WidgetSettingsService.CurrentUsageBarColorMode);
            }
            finally
            {
                WidgetSettingsService.UsageBarColorModeChanged -= OnColorsChanged;
                WidgetSettingsService.Changed -= OnSettingsChanged;
            }
        });
    }

    [Theory]
    [InlineData(0, "TextFillColorPrimaryBrush")]
    [InlineData(34, "TextFillColorPrimaryBrush")]
    [InlineData(74.9, "TextFillColorPrimaryBrush")]
    [InlineData(75, "SystemFillColorCautionBrush")]
    [InlineData(89.9, "SystemFillColorCautionBrush")]
    [InlineData(90, "SystemFillColorCriticalBrush")]
    [InlineData(100, "SystemFillColorCriticalBrush")]
    public void QuotaLevel_UsesActualQuotaInBothPercentageModes(double used, string expected)
    {
        WithSettings(_ =>
        {
            WidgetSettingsService.Apply(UsageBarColorMode.QuotaLevel);
            foreach (var mode in Enum.GetValues<PercentageDisplayMode>())
            {
                WidgetSettingsService.Apply(mode);
                double display = WidgetSettingsService.DisplayPercent(used);
                foreach (var severity in Enum.GetValues<PaceSeverity>())
                    Assert.Equal(expected, WidgetSettingsService.GetUsageBarBrushResourceKey(display, severity));
            }
        });
    }

    [Theory]
    [InlineData(PaceSeverity.Healthy, "AccentFillColorDefaultBrush")]
    [InlineData(PaceSeverity.Close, "SystemFillColorCautionBrush")]
    [InlineData(PaceSeverity.RunningOut, "SystemFillColorCriticalBrush")]
    [InlineData(PaceSeverity.Spent, "SystemFillColorCriticalBrush")]
    [InlineData(PaceSeverity.Untracked, "TextFillColorPrimaryBrush")]
    [InlineData(null, "TextFillColorPrimaryBrush")]
    public void ForecastMode_UsesForecastWhenAvailable(PaceSeverity? severity, string expected)
    {
        WithSettings(_ =>
        {
            WidgetSettingsService.Apply(PercentageDisplayMode.Remaining);
            Assert.Equal(expected, WidgetSettingsService.GetUsageBarBrushResourceKey(66, severity));
        });
    }
}
