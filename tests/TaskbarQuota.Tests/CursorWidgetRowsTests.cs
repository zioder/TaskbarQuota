using TaskbarQuota;
using TaskbarQuota.Controls;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public sealed class CursorWidgetRowsTests
{
    [Fact]
    public void WidgetRows_ForCursor_ToggleGrokBotWithoutHidingOtherMeters()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var usage = new UsageSnapshot(new RateWindow(50));
            usage.Secondary = new RateWindow(10);
            usage.ModelSpecific = new RateWindow(20);
            usage.ExtraRateWindows.Add(
                new NamedRateWindow(CursorProvider.GrokBotWindowId, "Grok Bot", new RateWindow(30)));

            var result = UsageResult.Success(ProviderId.Cursor, new CursorProvider(), new ProviderFetchResult(usage, "api"));

            var defaultLabels = WidgetSummary.BuildRowLabelsForTesting(result, usage);
            Assert.Equal(new[] { "Auto+Composer", "API", "Total", "Grok Bot" }, defaultLabels);

            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Cursor, WidgetSettingsService.RowCursorGrokBot, false);
            Assert.Equal(
                new[] { "Auto+Composer", "API", "Total" },
                WidgetSummary.BuildRowLabelsForTesting(result, usage));

            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Cursor, WidgetSettingsService.RowCursorGrokBot, true);
            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Cursor, WidgetSettingsService.RowPrimary, false);
            Assert.Equal(
                new[] { "Auto+Composer", "API", "Grok Bot" },
                WidgetSummary.BuildRowLabelsForTesting(result, usage));
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForCursor_OmitsGrokBotWhenNotInSnapshot()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var usage = new UsageSnapshot(new RateWindow(50));
            usage.Secondary = new RateWindow(10);
            var result = UsageResult.Success(ProviderId.Cursor, new CursorProvider(), new ProviderFetchResult(usage, "api"));

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, usage);
            Assert.Equal(new[] { "Auto+Composer", "Total" }, labels);
            Assert.DoesNotContain("Grok Bot", labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }
}
