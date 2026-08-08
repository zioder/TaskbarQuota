using TaskbarQuota.Controls;

namespace TaskbarQuota.Tests;

public class FloatingWidgetLayoutTests
{
    [Fact]
    public void ComputeContentLogicalWidth_IncludesFullActivityWidthWhenShown()
    {
        int tilesOnly = QuotaWidgetContent.ComputeContentLogicalWidth(
            tileWidths: [225, 225],
            showActivity: false);

        int withActivity = QuotaWidgetContent.ComputeContentLogicalWidth(
            tileWidths: [225, 225],
            showActivity: true,
            activityLogicalWidth: AgentActivitySummary.DesiredLogicalWidth);

        // Two tiles + margin/separator + 400 activity + margin + slack
        Assert.True(withActivity > tilesOnly);
        Assert.Equal(
            tilesOnly - 8 + AgentActivitySummary.DesiredLogicalWidth + 8 + 8,
            withActivity);
        // Explicit: activity reservation is the full preferred strip, not a narrower clamp.
        Assert.Equal(
            225 + 4 + 7 + 225 + 4 + AgentActivitySummary.DesiredLogicalWidth + 8 + 8,
            withActivity);
    }

    [Fact]
    public void ComputeContentLogicalWidth_ShrinksWhenActivityHidden()
    {
        int withActivity = QuotaWidgetContent.ComputeContentLogicalWidth(
            tileWidths: [300],
            showActivity: true,
            activityLogicalWidth: AgentActivitySummary.DesiredLogicalWidth);

        int without = QuotaWidgetContent.ComputeContentLogicalWidth(
            tileWidths: [300],
            showActivity: false);

        Assert.True(without < withActivity);
        Assert.Equal(300 + 4 + 8, without);
    }

    [Fact]
    public void ComputeContentLogicalWidth_EmptyUsesDefault()
    {
        int width = QuotaWidgetContent.ComputeContentLogicalWidth(
            tileWidths: [],
            showActivity: false);
        Assert.Equal(172 + 8, width);
    }
}
