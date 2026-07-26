using System.Collections.Generic;
using TaskbarQuota.Services;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

/// <summary>
/// Pinned providers are priced by weight rather than counted: a provider showing three or more rows is
/// twice as wide as one showing two, because rows pack two to a column group. The user spends a budget of
/// five slots however they like — three short, two long, or a long plus two short.
/// </summary>
public class PinBudgetServiceTests
{
    [Theory]
    [InlineData(1, PinBudgetService.ShortSlots)]
    [InlineData(2, PinBudgetService.ShortSlots)]
    [InlineData(3, PinBudgetService.LongSlots)]
    [InlineData(4, PinBudgetService.LongSlots)]
    [InlineData(6, PinBudgetService.LongSlots)]
    public void RowsDecideWhetherAProviderIsShortOrLong(int rows, int expected)
        => Assert.Equal(expected, PinBudgetService.SlotCostForRows(rows));

    [Fact]
    public void TheCombinationsFromTheDesignAllFitTheBudget()
    {
        // three short
        Assert.Equal(3, 3 * PinBudgetService.ShortSlots);
        // a long plus two short
        Assert.Equal(4, PinBudgetService.LongSlots + (2 * PinBudgetService.ShortSlots));
        // two long
        Assert.Equal(4, 2 * PinBudgetService.LongSlots);
        // two long plus a short — the widest allowed
        Assert.Equal(PinBudgetService.TotalSlots, (2 * PinBudgetService.LongSlots) + PinBudgetService.ShortSlots);
    }

    [Fact]
    public void ThreeLongProvidersExceedTheBudget()
        => Assert.True(3 * PinBudgetService.LongSlots > PinBudgetService.TotalSlots);

    [Fact]
    public void NothingIsDroppedWhileTheSetFits()
    {
        var pinned = Pinned((ProviderId.Zai, 1), (ProviderId.Claude, 2), (ProviderId.Codex, 2));

        Assert.Empty(PinBudgetService.SelectDrops(pinned, budget: 5, maxCount: 3));
    }

    [Fact]
    public void DropsFromTheFrontUntilTheWeightFits()
    {
        // Least worth keeping first: three long providers weigh six against a budget of five.
        var pinned = Pinned((ProviderId.Zai, 2), (ProviderId.Claude, 2), (ProviderId.Codex, 2));

        var dropped = PinBudgetService.SelectDrops(pinned, budget: 5, maxCount: 3);

        Assert.Equal(new[] { ProviderId.Zai }, dropped);
    }

    [Fact]
    public void DropsMoreThanOneWhenASingleDropIsNotEnough()
    {
        var pinned = Pinned((ProviderId.Zai, 2), (ProviderId.Claude, 2), (ProviderId.Codex, 2));

        var dropped = PinBudgetService.SelectDrops(pinned, budget: 2, maxCount: 3);

        Assert.Equal(new[] { ProviderId.Zai, ProviderId.Claude }, dropped);
    }

    // A provider growing from two rows to three doubles its weight, which can push an already-pinned set
    // over budget. The row toggle wins and the least recently used pin makes way.
    [Fact]
    public void GrowingAProviderCanEvictTheLeastRecentPin()
    {
        var pinned = Pinned((ProviderId.Zai, 1), (ProviderId.Claude, 2), (ProviderId.Codex, 2));
        Assert.Empty(PinBudgetService.SelectDrops(pinned, budget: 5, maxCount: 3));

        // Z.AI gains a third row: 1 -> 2, total 6.
        var grown = Pinned((ProviderId.Zai, 2), (ProviderId.Claude, 2), (ProviderId.Codex, 2));
        Assert.Equal(new[] { ProviderId.Zai }, PinBudgetService.SelectDrops(grown, budget: 5, maxCount: 3));
    }

    [Fact]
    public void TheTileCapAppliesEvenWhenTheWeightFits()
    {
        // Four one-row providers weigh four of five, but the taskbar only renders three tiles.
        var pinned = Pinned(
            (ProviderId.Zai, 1), (ProviderId.Claude, 1), (ProviderId.Codex, 1), (ProviderId.Cursor, 1));

        var dropped = PinBudgetService.SelectDrops(pinned, budget: 5, maxCount: 3);

        Assert.Equal(new[] { ProviderId.Zai }, dropped);
    }

    // Width, not weight, is the constraint that actually decides whether a row renders. A tile is one icon
    // plus a column group per two rows, so three rows costs a whole extra group.
    [Fact]
    public void ATileGrowsByAColumnGroupEveryTwoRows()
    {
        Assert.Equal(PinBudgetService.EstimateTileWidth(1), PinBudgetService.EstimateTileWidth(2));
        Assert.Equal(PinBudgetService.EstimateTileWidth(3), PinBudgetService.EstimateTileWidth(4));
        Assert.True(PinBudgetService.EstimateTileWidth(3) > PinBudgetService.EstimateTileWidth(2));
    }

    [Fact]
    public void RowWidthCountsMarginsAndDividers()
    {
        int one = PinBudgetService.RowWidth(new List<int> { 200 });
        int two = PinBudgetService.RowWidth(new List<int> { 200, 200 });

        // Each extra tile adds its own width plus one margin and one divider.
        Assert.Equal(one + 200 + 4 + 7, two);
    }

    // The reported case: Claude (2 rows) + Copilot (1 row) + ClinePass (3 rows) on a 684px bar. It was
    // being allowed, then the middle tile rendered as a bare glyph.
    [Fact]
    public void RefusesASetThatWouldNotFitTheMeasuredTaskbar()
    {
        Assert.False(PinBudgetService.FitsTaskbar(new List<int> { 2, 1, 3 }, availableWidth: 684));
        Assert.True(PinBudgetService.FitsTaskbar(new List<int> { 2, 1 }, availableWidth: 684));
    }

    [Fact]
    public void AWiderTaskbarAcceptsTheSameSet()
        => Assert.True(PinBudgetService.FitsTaskbar(new List<int> { 2, 1, 3 }, availableWidth: 1326));

    // Reported bug: with only Cline (3 rows) pinned, pinning Claude (2 rows) was refused even though the
    // two render side by side perfectly well. The budget was reserving room for a THIRD active tile that
    // does not exist when the provider being pinned is the one in use.
    [Fact]
    public void JudgesOnlyThePinnedSet()
    {
        // Cline + Claude comes to 655 plus the safety margin — comfortably inside a 750px bar.
        Assert.True(PinBudgetService.FitsTaskbar(new List<int> { 3, 2 }, availableWidth: 750));
    }

    // Before a widget has measured anything there is no free-span figure to judge against; refusing every
    // pin at that point would make pinning look broken on a cold start.
    [Fact]
    public void FallsBackToTheWeightBudgetBeforeAnythingIsMeasured()
        => Assert.True(PinBudgetService.FitsTaskbar(new List<int> { 2, 3, 3 }, availableWidth: 0));

    private static List<(ProviderId, int)> Pinned(params (ProviderId, int)[] entries) => [.. entries];
}
