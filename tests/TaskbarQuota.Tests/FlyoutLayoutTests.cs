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
        => Assert.Equal(FlyoutLayout.FixedLogicalContentHeight + FlyoutLayout.ChromeLogicalHeight + FlyoutLayout.HeightMeasureBuffer,
            FlyoutLayout.ComputeLogicalHeight(FlyoutLayout.FixedLogicalContentHeight));

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
        Assert.Equal(640, width);
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

    [Fact]
    public void ComputePlacement_PrefersAbove_WhenThereIsRoom()
    {
        // Work 1920x1080; anchor near bottom (taskbar-like); full flyout height fits above.
        var result = FlyoutLayout.ComputePlacement(
            anchorLeft: 1600, anchorTop: 1000, anchorRight: 1800, anchorBottom: 1040,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1080,
            width: 450, height: 800, gap: 8);

        Assert.True(result.PlacedAbove);
        Assert.Equal(800, result.Height);
        Assert.Equal(1000 - 800 - 8, result.Y);
        Assert.Equal(1800 - 450, result.X);
    }

    [Fact]
    public void ComputePlacement_FlipsBelow_WhenAnchorIsNearTop()
    {
        // Anchor near top — not enough room above for full height; plenty below.
        var result = FlyoutLayout.ComputePlacement(
            anchorLeft: 100, anchorTop: 20, anchorRight: 300, anchorBottom: 60,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1080,
            width: 450, height: 800, gap: 8);

        Assert.False(result.PlacedAbove);
        Assert.Equal(800, result.Height);
        Assert.Equal(60 + 8, result.Y);
        // Right-align would be 300-450 = -150; clamp into work area.
        Assert.Equal(0, result.X);
    }

    [Fact]
    public void ComputePlacement_PrefersAbove_WhenBothSidesFit()
    {
        var result = FlyoutLayout.ComputePlacement(
            anchorLeft: 500, anchorTop: 400, anchorRight: 700, anchorBottom: 440,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1080,
            width: 450, height: 300, gap: 8);

        Assert.True(result.PlacedAbove);
        Assert.Equal(300, result.Height);
        Assert.Equal(400 - 300 - 8, result.Y);
    }

    [Fact]
    public void ComputePlacement_UsesLargerSide_WhenNeitherFitsFullHeight()
    {
        // Anchor near top of a short work area; below has more room than above.
        var result = FlyoutLayout.ComputePlacement(
            anchorLeft: 100, anchorTop: 40, anchorRight: 280, anchorBottom: 80,
            workLeft: 0, workTop: 0, workRight: 800, workBottom: 400,
            width: 300, height: 500, gap: 8);

        Assert.False(result.PlacedAbove);
        // spaceBelow = 400 - 80 - 8 = 312
        Assert.Equal(312, result.Height);
        Assert.Equal(80 + 8, result.Y);
    }
}
