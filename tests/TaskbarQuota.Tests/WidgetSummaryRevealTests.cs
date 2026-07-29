using TaskbarQuota.Controls;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

/// <summary>
/// A tile's root ships at Opacity 0 and only the reveal raises it. Suppressing the cross-fade (which the
/// host does when a slot is seeded from the boot snapshot, so the movement is carried by the slide instead)
/// must therefore still show the tile — otherwise it measures, lays out and reports itself Visible while
/// painting nothing, which is what made the taskbar widget never appear in 1.1.0.
/// </summary>
public class WidgetSummaryRevealTests
{
    [Fact]
    public void RevealsOutrightWhenTheFirstRenderSkipsTheTransition()
        => Assert.True(WidgetSummary.ShouldRevealWithoutTransition(isFirstReveal: true, isActiveToolVisible: true));

    [Fact]
    public void StaysHiddenWhenTheTileIsNotMeantToShow()
        => Assert.False(WidgetSummary.ShouldRevealWithoutTransition(isFirstReveal: true, isActiveToolVisible: false));

    // Later renders are cross-fades over an already visible tile; forcing the root back to 1 there would
    // undo a hide that SetActiveToolVisible had just animated.
    [Fact]
    public void LeavesLaterRendersAlone()
        => Assert.False(WidgetSummary.ShouldRevealWithoutTransition(isFirstReveal: false, isActiveToolVisible: true));

    [Fact]
    public void SourceBadgeChangeDoesNotInvalidateRenderedUsageContent()
    {
        var result = UsageResult.Failure(ProviderId.Codex, "Unavailable");
        var foreground = result.WithSource(
            new ProviderSource(ProviderSourceKind.DesktopApp, "Codex", "desktop"));

        Assert.True(WidgetSummary.HasSameRenderedContentForTesting(result, foreground));
    }

    [Fact]
    public void SourceAwareTooltipBuilderUsesTheCurrentSource()
    {
        static string Build(ProviderSource source)
            => source.IsKnown ? $"Codex {source.ShortViaText}: unavailable" : "Codex: unavailable";

        var desktop = new ProviderSource(ProviderSourceKind.DesktopApp, "Codex", "desktop");
        var terminal = new ProviderSource(ProviderSourceKind.Cli, "PowerShell", "terminal");

        Assert.Equal(
            "Codex via Codex: unavailable",
            WidgetSummary.BuildTooltipForSource(Build, desktop));
        Assert.Equal(
            "Codex via PowerShell: unavailable",
            WidgetSummary.BuildTooltipForSource(Build, terminal));
    }
}
