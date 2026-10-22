using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class FlyoutNavigationTests
{
    [Theory]
    [InlineData(false, false, false, 1, ProviderId.Claude, ProviderId.Claude, false)]
    [InlineData(true, false, false, 1, ProviderId.Claude, ProviderId.Claude, true)]
    [InlineData(true, false, false, 1, ProviderId.Codex, ProviderId.Claude, false)]
    [InlineData(true, false, false, 1, ProviderId.Claude, ProviderId.Codex, false)]
    [InlineData(true, false, false, 2, ProviderId.Claude, ProviderId.Claude, false)]
    [InlineData(true, true, false, 1, ProviderId.Claude, ProviderId.Claude, false)]
    [InlineData(true, false, true, 1, ProviderId.Claude, ProviderId.Claude, false)]
    [InlineData(true, false, false, 1, null, ProviderId.Claude, false)]
    public void TileClickClosesOnlyTheSameProviderAtTheSameAnchor(
        bool shown, bool activity, bool cost, int anchor,
        ProviderId? selected, ProviderId requested, bool shouldHide)
        => Assert.Equal(shouldHide, FlyoutNavigation.ShouldHideProvider(
            shown, activity, cost, new IntPtr(1), new IntPtr(anchor), selected, requested));
}
