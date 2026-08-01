using TaskbarQuota.Controls;

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
        => Assert.True(WidgetSummary.ShouldRevealWithoutTransition(isFirstReveal: true));

    // Slot seeding happens before the layout pass marks the new tile active. The first render must establish
    // a visible resting value regardless; the synchronous layout pass remains responsible for collapsing a
    // tile that ultimately does not fit.
    [Fact]
    public void FirstSuppressedRenderDoesNotDependOnPreLayoutVisibilityState()
        => Assert.True(WidgetSummary.ShouldRevealWithoutTransition(isFirstReveal: true));

    // Later renders are cross-fades over an already visible tile; forcing the root back to 1 there would
    // undo a hide that SetActiveToolVisible had just animated.
    [Fact]
    public void LeavesLaterRendersAlone()
        => Assert.False(WidgetSummary.ShouldRevealWithoutTransition(isFirstReveal: false));
}
