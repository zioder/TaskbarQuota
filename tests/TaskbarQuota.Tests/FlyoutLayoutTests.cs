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
        Assert.Equal(FlyoutLayout.MinLogicalWidth, width);
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

    [Theory]
    [InlineData(0, FlyoutLayout.CompactMinLogicalHeight)]
    [InlineData(3, 400)]
    [InlineData(20, FlyoutLayout.CompactMaxLogicalHeight)]
    public void ComputeCompactLogicalHeight_ClampsToCompactBounds(int providers, int expected)
        => Assert.Equal(expected, FlyoutLayout.ComputeCompactLogicalHeight(providers));
}
