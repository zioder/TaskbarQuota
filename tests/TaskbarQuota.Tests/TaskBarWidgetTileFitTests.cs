using System;
using System.Collections.Generic;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

/// <summary>
/// The taskbar row renders pinned providers exactly as configured (issue #25): every row, every reset
/// countdown, no trimming and no glyph fallback. That used to be enforced by a layout solver whose ladder
/// held a single rung — a search that could only ever return one answer — so the guarantee is structural
/// now instead: there is no reduced form for a tile to fall back to, and keeping the row inside the
/// taskbar is the pin budget's job (see <see cref="PinBudgetServiceTests"/>).
///
/// What survives as testable logic is the order in which the widget holds a tile back when the row still
/// overflows the measured gap: least recently used first, so whatever the user was last working in stays.
/// </summary>
public class TaskBarWidgetTileFitTests
{
    [Fact]
    public void RecencyFollowsTheRecentlyActiveOrder()
    {
        var recent = new List<ProviderId> { ProviderId.Codex, ProviderId.Claude, ProviderId.Zai };

        Assert.Equal(0, TaskBarWidget.RecencyOf(ProviderId.Codex, recent));
        Assert.Equal(1, TaskBarWidget.RecencyOf(ProviderId.Claude, recent));
        Assert.Equal(2, TaskBarWidget.RecencyOf(ProviderId.Zai, recent));
    }

    // A provider that has never been focused is the first thing to give up its tile, so it has to sort
    // behind every provider that has been.
    [Fact]
    public void NeverActiveSortsLeastRecent()
    {
        var recent = new List<ProviderId> { ProviderId.Codex };

        Assert.Equal(int.MaxValue, TaskBarWidget.RecencyOf(ProviderId.Cursor, recent));
        Assert.True(TaskBarWidget.RecencyOf(ProviderId.Cursor, recent)
            > TaskBarWidget.RecencyOf(ProviderId.Codex, recent));
    }

    [Fact]
    public void EmptySlotSortsLeastRecent()
        => Assert.Equal(int.MaxValue, TaskBarWidget.RecencyOf(null, Array.Empty<ProviderId>()));

    [Fact]
    public void FirstOccurrenceWinsWhenAProviderRepeats()
    {
        var recent = new List<ProviderId> { ProviderId.Codex, ProviderId.Claude, ProviderId.Codex };

        Assert.Equal(0, TaskBarWidget.RecencyOf(ProviderId.Codex, recent));
    }
}
