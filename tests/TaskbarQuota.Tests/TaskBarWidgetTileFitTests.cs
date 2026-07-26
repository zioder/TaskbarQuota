using System;
using System.Collections.Generic;
using System.Linq;
using TaskbarQuota.Controls;
using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

using Form = WidgetSummary.SummaryForm;

/// <summary>
/// The taskbar row renders pinned providers exactly as configured (issue #25): every row, every reset
/// countdown, no trimming and no glyph fallback. Keeping the row inside the taskbar is the pin budget's
/// job — see <see cref="PinBudgetServiceTests"/> — so the layout solver only has one form per tile.
/// </summary>
public class TaskBarWidgetTileFitTests
{
    private const int GlyphWidth = 44;

    private static int Expected(params int[] widths)
    {
        int total = 0;
        for (int i = 0; i < widths.Length; i++)
            total += widths[i] + 8 + (i > 0 ? 9 : 0);
        return total;
    }

    private static Func<int, Form, int> Measure(params int[] fullWidths)
        => (position, form) => form.IsGlyph ? GlyphWidth : fullWidths[position];

    [Fact]
    public void EveryTileRendersInFullWhenThereIsRoom()
    {
        var forms = TaskBarWidget.SolveTileLayout(
            new List<int> { 2, 1, 2 }, activeIndex: 0, Measure(227, 180, 180), Expected(227, 180, 180));

        Assert.Equal(new[] { 2, 1, 2 }, forms.Select(f => f.Rows));
        Assert.All(forms, f => Assert.False(f.HideReset));
    }

    // The regression that matters: nothing is ever trimmed or hidden to make room, however tight it is.
    // A provider that cannot fit should have been refused at pin time, not quietly degraded here.
    [Fact]
    public void NeverTrimsOrHidesATileHoweverTightTheSpace()
    {
        var forms = TaskBarWidget.SolveTileLayout(
            new List<int> { 2, 3, 2 }, activeIndex: 0, Measure(227, 407, 242), budget: 100);

        Assert.Equal(new[] { 2, 3, 2 }, forms.Select(f => f.Rows));
        Assert.All(forms, f =>
        {
            Assert.False(f.IsGlyph);
            Assert.False(f.IsCompact);
            Assert.False(f.HideReset);
        });
    }

    [Fact]
    public void RowCountsAreHonouredExactly()
    {
        var forms = TaskBarWidget.SolveTileLayout(
            new List<int> { 4, 1 }, activeIndex: 1, Measure(405, 225), budget: 700);

        Assert.Equal(new[] { 4, 1 }, forms.Select(f => f.Rows));
    }

    [Fact]
    public void EmptyRowSolvesToNothing()
        => Assert.Empty(TaskBarWidget.SolveTileLayout(
            new List<int>(), activeIndex: -1, Measure(), budget: 500));
}
