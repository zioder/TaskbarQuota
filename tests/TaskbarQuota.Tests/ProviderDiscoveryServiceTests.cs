using TaskbarQuota;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class ProviderDiscoveryServiceTests
{
    public ProviderDiscoveryServiceTests()
    {
        ProviderDiscoveryService.ResetForTesting();
        WidgetSettingsService.ResetProviderVisibilityForTesting();
        WidgetSettingsService.ResetDashboardProviderVisibilityForTesting();
        WidgetSettingsService.ApplyAutoHideUnavailable(true);
        ProviderInstallDetector.IsInstalledOverrideForTesting = _ => false;
        ProviderInstallDetector.ResetCliCacheForTesting();
    }

    [Fact]
    public void RecordFetchResult_AutoHidesNotInstalledProvider()
    {
        var result = UsageResult.Failure(ProviderId.Grok, "Run grok login", kind: ProviderErrorKind.NotInstalled);

        ProviderDiscoveryService.RecordFetchResult(result);

        Assert.False(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Grok));
        Assert.False(WidgetSettingsService.IsProviderVisible(ProviderId.Grok));
        Assert.True(ProviderDiscoveryService.IsProbed(ProviderId.Grok));
    }

    [Fact]
    public void RecordFetchResult_MarksConfiguredOnSuccess()
    {
        var provider = new UsageService().Get(ProviderId.Codex)!;
        var result = UsageResult.Success(
            ProviderId.Codex,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(10)), "test"));

        ProviderDiscoveryService.RecordFetchResult(result);

        Assert.True(ProviderDiscoveryService.IsConfigured(ProviderId.Codex));
        Assert.True(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Codex));
        Assert.True(WidgetSettingsService.IsProviderVisible(ProviderId.Codex));
    }

    [Fact]
    public void RecordFetchResult_RestoresWidgetAfterNewlyConfigured()
    {
        WidgetSettingsService.SetProviderVisibleForTesting(ProviderId.Grok, false);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Grok, false);
        ProviderDiscoveryService.MarkProbedForTesting(ProviderId.Grok);

        var provider = new UsageService().Get(ProviderId.Grok)!;
        var result = UsageResult.Success(
            ProviderId.Grok,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(12)), "grok auth.json"));

        ProviderDiscoveryService.RecordFetchResult(result);

        Assert.True(WidgetSettingsService.IsProviderVisible(ProviderId.Grok));
        Assert.True(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Grok));
    }

    [Fact]
    public void ShouldShowInAvailable_ReturnsHiddenNotInstalled()
    {
        ProviderDiscoveryService.MarkProbedForTesting(ProviderId.Devin);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Devin, false);

        var result = UsageResult.Failure(ProviderId.Devin, "Not installed", kind: ProviderErrorKind.NotInstalled);

        Assert.True(ProviderDiscoveryService.ShouldShowInAvailable(result, active: null));
        Assert.False(ProviderDiscoveryService.ShouldShowInDashboard(result, active: null));
    }

    [Fact]
    public void RecordFetchResult_DoesNotAutoHideNotRunningProvider()
    {
        var result = UsageResult.Failure(
            ProviderId.Antigravity,
            "Waiting for Antigravity to be open.",
            kind: ProviderErrorKind.NotRunning);

        ProviderDiscoveryService.RecordFetchResult(result);

        Assert.True(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Antigravity));
        Assert.True(WidgetSettingsService.IsProviderVisible(ProviderId.Antigravity));
    }

    [Fact]
    public void ShouldShowInDashboard_ReturnsNotRunningProvider()
    {
        ProviderDiscoveryService.MarkProbedForTesting(ProviderId.Antigravity);
        var result = UsageResult.Failure(
            ProviderId.Antigravity,
            "Waiting for Antigravity to be open.",
            kind: ProviderErrorKind.NotRunning);

        Assert.True(ProviderDiscoveryService.ShouldShowInDashboard(result, active: null));
        Assert.False(ProviderDiscoveryService.ShouldShowInAvailable(result, active: null));
    }

    [Fact]
    public void SyncInstalledProviderVisibility_EnablesDashboardAndWidget()
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = id => id == ProviderId.Grok;
        WidgetSettingsService.SetProviderVisibleForTesting(ProviderId.Grok, false);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Grok, false);

        ProviderDiscoveryService.RecordFetchResult(
            UsageResult.Failure(ProviderId.Grok, "auth", kind: ProviderErrorKind.AuthRequired));

        Assert.True(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Grok));
        Assert.True(WidgetSettingsService.IsProviderVisible(ProviderId.Grok));
    }

    [Fact]
    public void ShouldFetch_SkipsExplicitlyDisabledInstalledProvider()
    {
        ProviderDiscoveryService.MarkExplicitlyDisabledForTesting(ProviderId.Grok);

        Assert.False(ProviderDiscoveryService.ShouldFetch(ProviderId.Grok, active: null));
    }

    [Fact]
    public void ShouldFetch_SkipsHiddenProbedProvider()
    {
        ProviderDiscoveryService.MarkProbedForTesting(ProviderId.Grok);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Grok, false);
        WidgetSettingsService.SetProviderVisibleForTesting(ProviderId.Grok, false);

        Assert.False(ProviderDiscoveryService.ShouldFetch(ProviderId.Grok, active: null));
        Assert.True(ProviderDiscoveryService.ShouldFetch(ProviderId.Grok, active: ProviderId.Grok));
    }
}