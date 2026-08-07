namespace TaskbarQuota.Tests;

public class FlyoutLayoutTests
{
    [Fact]
    public void LogicalHeight_IsSizedForTheExpandedFlyout()
        => Assert.Equal(
            FlyoutLayout.MinLogicalContentHeight + FlyoutLayout.ChromeLogicalHeight + FlyoutLayout.HeightMeasureBuffer,
            FlyoutLayout.LogicalHeight);

    [Fact]
    public void ComputeLogicalHeight_GrowsWithDetailContent()
        // Fixed content + ChromeLogicalHeight (incl. surface/opacity bar) + HeightMeasureBuffer
        => Assert.Equal(620 + FlyoutLayout.ChromeLogicalHeight + FlyoutLayout.HeightMeasureBuffer,
            FlyoutLayout.ComputeLogicalHeight(620));

    [Fact]
    public void ComputeLogicalHeight_ClampsTallContent()
        => Assert.Equal(FlyoutLayout.MaxLogicalContentHeight + FlyoutLayout.ChromeLogicalHeight + FlyoutLayout.HeightMeasureBuffer,
            FlyoutLayout.ComputeLogicalHeight(1200));

    [Fact]
    public void ComputeLogicalWidth_UsesWiderOfStripAndDetailContent()
    {
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 3, detailContentWidth: 360);
        Assert.Equal(FlyoutLayout.MinLogicalWidth, width);
    }

    [Fact]
    public void ComputeLogicalWidth_GrowsWithInstalledProviders()
    {
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 9, detailContentWidth: 300);
        Assert.Equal(582, width);
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

    [Fact]
    public void ComputeLogicalWidth_CanForceMinimumWidthForManualTesting()
    {
        var previous = Environment.GetEnvironmentVariable(FlyoutLayout.ForceMinWidthEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FlyoutLayout.ForceMinWidthEnvironmentVariable, "1");

            int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 12, detailContentWidth: 900);

            Assert.Equal(FlyoutLayout.MinLogicalWidth, width);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlyoutLayout.ForceMinWidthEnvironmentVariable, previous);
        }
    }
}
