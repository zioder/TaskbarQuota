namespace TaskbarQuota.Tests;

public class FlyoutLayoutTests
{
    [Fact]
    public void LogicalHeight_IsSizedForTheExpandedFlyout()
        => Assert.Equal(556, FlyoutLayout.LogicalHeight);

    [Fact]
    public void LogicalWidth_FitsEightProviderIconsAndSettings()
        => Assert.Equal(476, FlyoutLayout.LogicalWidth);
}
