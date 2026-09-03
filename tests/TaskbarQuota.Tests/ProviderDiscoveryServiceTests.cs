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
        WidgetSettingsService.ResetProviderPinsForTesting();
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
    public void RecordFetchResult_MarksConfiguredWithoutRestoringVisibility()
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = id => id == ProviderId.Codex;
        WidgetSettingsService.SetProviderVisibleForTesting(ProviderId.Codex, false);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Codex, false);

        var provider = new UsageService().Get(ProviderId.Codex)!;
        var result = UsageResult.Success(
            ProviderId.Codex,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(10)), "test"));

        ProviderDiscoveryService.RecordFetchResult(result);

        Assert.True(ProviderDiscoveryService.IsConfigured(ProviderId.Codex));
        // A successful fetch of an idle provider must not resurrect it (issue #83).
        Assert.False(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Codex));
        Assert.False(WidgetSettingsService.IsProviderVisible(ProviderId.Codex));
    }

    [Fact]
    public void RecordFetchResult_DoesNotRestoreIdleInstalledProvider()
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = id => id == ProviderId.Grok;
        WidgetSettingsService.SetProviderVisibleForTesting(ProviderId.Grok, false);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Grok, false);
        ProviderDiscoveryService.MarkProbedForTesting(ProviderId.Grok);

        var provider = new UsageService().Get(ProviderId.Grok)!;
        var result = UsageResult.Success(
            ProviderId.Grok,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(12)), "grok auth.json"));

        ProviderDiscoveryService.RecordFetchResult(result);

        Assert.False(WidgetSettingsService.IsProviderVisible(ProviderId.Grok));
        Assert.False(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Grok));
    }

    [Theory]
    [InlineData(ProviderId.OpenCode)]
    [InlineData(ProviderId.OpenCodeGo)]
    public void RecordFetchResult_DoesNotRestoreExplicitlyDisabledProvider(ProviderId id)
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = installedId => installedId == id;
        WidgetSettingsService.SetProviderVisibleForTesting(id, false);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(id, false);
        ProviderDiscoveryService.MarkExplicitlyDisabledForTesting(id);

        var provider = new UsageService().Get(id)!;
        ProviderDiscoveryService.RecordFetchResult(UsageResult.Success(
            id,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(10)), "test")));
        ProviderDiscoveryService.RecordFetchResult(
            UsageResult.Failure(id, "waiting", kind: ProviderErrorKind.NotRunning));

        Assert.True(ProviderDiscoveryService.IsConfigured(id));
        Assert.False(WidgetSettingsService.IsProviderDashboardVisible(id));
        Assert.False(WidgetSettingsService.IsProviderVisible(id));
    }

    [Fact]
    public void ShouldShowInAvailable_NeverSurfacesProviders()
    {
        ProviderDiscoveryService.MarkProbedForTesting(ProviderId.Devin);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Devin, false);

        var result = UsageResult.Failure(ProviderId.Devin, "Not installed", kind: ProviderErrorKind.NotInstalled);

        // Discovery surfacing is disabled: Settings is the opt-in surface (issue #83).
        Assert.False(ProviderDiscoveryService.ShouldShowInAvailable(result, active: null));
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
    public void ShouldShowInDashboard_HidesNotInstalledProvider()
    {
        ProviderDiscoveryService.MarkProbedForTesting(ProviderId.Antigravity);
        var result = UsageResult.Failure(
            ProviderId.Antigravity,
            "Waiting for Antigravity to be open.",
            kind: ProviderErrorKind.NotRunning);

        Assert.False(ProviderDiscoveryService.ShouldShowInDashboard(result, active: null));
        Assert.False(ProviderDiscoveryService.ShouldShowInAvailable(result, active: null));
    }

    [Fact]
    public void SyncInstalledProviderVisibility_DoesNotAutoShowInstalledProvider()
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = id => id == ProviderId.Grok;
        WidgetSettingsService.SetProviderVisibleForTesting(ProviderId.Grok, false);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Grok, false);

        ProviderDiscoveryService.SyncInstalledProviderVisibility();

        Assert.False(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Grok));
        Assert.False(WidgetSettingsService.IsProviderVisible(ProviderId.Grok));
    }

    [Fact]
    public void SyncInstalledProviderVisibility_HidesStaleNotInstalledProvider()
    {
        WidgetSettingsService.SetProviderVisibleForTesting(ProviderId.Grok, true);
        WidgetSettingsService.SetProviderDashboardVisibleForTesting(ProviderId.Grok, true);

        ProviderDiscoveryService.SyncInstalledProviderVisibility();

        Assert.False(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Grok));
        Assert.False(WidgetSettingsService.IsProviderVisible(ProviderId.Grok));
    }

    [Fact]
    public void ShouldShowInDashboard_ShowsActiveProviderEvenWhenNotInstalled()
    {
        // Browser-tab detection can make a provider active with nothing installed
        // (web use counts as open) — the open app always shows.
        var result = UsageResult.Failure(ProviderId.Claude, "Not installed", kind: ProviderErrorKind.NotInstalled);

        Assert.True(ProviderDiscoveryService.ShouldShowInDashboard(result, active: ProviderId.Claude));
    }

    [Fact]
    public void ShouldShowInDashboard_HidesDisabledActiveProvider()
    {
        ProviderDiscoveryService.MarkExplicitlyDisabledForTesting(ProviderId.Claude);
        var provider = new UsageService().Get(ProviderId.Claude)!;
        var result = UsageResult.Success(
            ProviderId.Claude,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(10)), "test"));

        Assert.False(ProviderDiscoveryService.ShouldShowInDashboard(result, active: ProviderId.Claude));
    }

    [Fact]
    public void ShouldShowInDashboard_HidesIdleInstalledProvider()
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = _ => true;
        var provider = new UsageService().Get(ProviderId.Grok)!;
        var result = UsageResult.Success(
            ProviderId.Grok,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(10)), "test"));

        Assert.False(ProviderDiscoveryService.ShouldShowInDashboard(result, active: null));
        Assert.False(ProviderDiscoveryService.ShouldShowInAvailable(result, active: null));
        Assert.False(ProviderDiscoveryService.ShouldFetch(ProviderId.Grok, active: null));
    }

    [Fact]
    public void ShouldShowInDashboard_ShowsExplicitlyEnabledInstalledProvider()
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = id => id == ProviderId.Codex;
        ProviderDiscoveryService.EnableProvider(ProviderId.Codex);
        var provider = new UsageService().Get(ProviderId.Codex)!;
        var result = UsageResult.Success(
            ProviderId.Codex,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(10)), "test"));

        Assert.True(ProviderDiscoveryService.ShouldShowInDashboard(result, active: null));
        Assert.True(ProviderDiscoveryService.ShouldFetch(ProviderId.Codex, active: null));
    }

    [Fact]
    public void ShouldShowInDashboard_ShowsRecentlyActiveInstalledProvider()
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = _ => true;
        ProviderDiscoveryService.IsRecentlyActiveOverrideForTesting = id => id == ProviderId.Grok;
        var provider = new UsageService().Get(ProviderId.Grok)!;
        var result = UsageResult.Success(
            ProviderId.Grok,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(10)), "test"));

        Assert.True(ProviderDiscoveryService.ShouldShowInDashboard(result, active: null));
        Assert.True(ProviderDiscoveryService.ShouldFetch(ProviderId.Grok, active: null));
    }

    [Fact]
    public void ShouldShowInDashboard_ShowsPinnedInstalledProvider()
    {
        ProviderInstallDetector.IsInstalledOverrideForTesting = id => id == ProviderId.Grok;
        WidgetSettingsService.SetProviderPinnedForTesting(ProviderId.Grok, true);
        var provider = new UsageService().Get(ProviderId.Grok)!;
        var result = UsageResult.Success(
            ProviderId.Grok,
            provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(10)), "test"));

        Assert.True(ProviderDiscoveryService.ShouldShowInDashboard(result, active: null));
        Assert.True(ProviderDiscoveryService.ShouldFetch(ProviderId.Grok, active: null));
    }

    [Fact]
    public void ShouldFetch_FetchesActiveProviderEvenWhenNotInstalled()
    {
        Assert.True(ProviderDiscoveryService.ShouldFetch(ProviderId.Grok, active: ProviderId.Grok));
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
