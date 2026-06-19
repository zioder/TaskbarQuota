namespace TaskbarQuota.Tests;

public class FlyoutLayoutTests
{
    [Fact]
    public void LogicalHeight_IsSizedForTheExpandedFlyout()
        => Assert.Equal(482, FlyoutLayout.LogicalHeight);

    [Fact]
    public void ComputeLogicalHeight_GrowsWithDetailContent()
        => Assert.Equal(782, FlyoutLayout.ComputeLogicalHeight(620));

    [Fact]
    public void ComputeLogicalHeight_ClampsTallContent()
        => Assert.Equal(922, FlyoutLayout.ComputeLogicalHeight(1200));

    [Fact]
    public void ComputeLogicalWidth_UsesWiderOfStripAndDetailContent()
    {
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 3, detailContentWidth: 360);
        Assert.Equal(400, width);
    }

    [Fact]
    public void ComputeLogicalWidth_GrowsWithInstalledProviders()
    {
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 9, detailContentWidth: 300);
        Assert.Equal(532, width);
    }

    [Fact]
    public void ComputeLogicalWidth_RespectsMinimumWidth()
    {
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 1, detailContentWidth: 200);
        Assert.Equal(FlyoutLayout.MinLogicalWidth, width);
    }

    [Fact]
    public void ComputeLogicalWidth_AllowsManyProvidersWithoutClamping()
    {
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 10, detailContentWidth: 900);
        Assert.Equal(940, width);
    }
}
