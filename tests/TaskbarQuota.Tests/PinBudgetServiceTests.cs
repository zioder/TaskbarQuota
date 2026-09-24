using System.Collections.Generic;
using TaskbarQuota.Services;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

/// <summary>
/// Pinning is limited by one thing: whether the tiles fit the taskbar space actually measured. A pinned
/// tile is never trimmed, so a set that would not fit has to be refused rather than rendered badly.
/// </summary>
[Collection(WidgetRowSettingsCollection.Name)]
public class PinBudgetServiceTests
{
    // Measured tile widths: a two-row provider renders around 223px, a three-row one around 405px.
    private const int ShortTile = 223;
    private const int LongTile = 405;

    [Theory]
    [InlineData(1, 225)]
    [InlineData(2, 225)]
    [InlineData(3, 405)]
    [InlineData(4, 405)]
    public void ATileGrowsByAColumnGroupEveryTwoRows(int rows, int expected)
        => Assert.Equal(expected, PinBudgetService.EstimateTileWidth(rows));

    [Fact]
    public void RowWidthCountsMarginsAndDividers()
    {
        int one = PinBudgetService.RowWidth(new List<int> { 200 });
        int two = PinBudgetService.RowWidth(new List<int> { 200, 200 });

        // Each extra tile adds its own width plus one margin and one divider.
        Assert.Equal(one + 200 + 4 + 7, two);
    }

    /// <summary>
    /// What each combination costs against real taskbar spans. These are the limits users actually hit,
    /// and they depend entirely on how much room the bar has — which is why an abstract slot allowance was
    /// removed: it refused three long providers outright, though they fit a left-aligned taskbar.
    /// </summary>
    [Theory]
    // A centre-aligned Windows taskbar leaves roughly 684-750px between the icon cluster and the tray.
    [InlineData(728, new[] { ShortTile, ShortTile }, true)]
    [InlineData(728, new[] { ShortTile, ShortTile, ShortTile }, true)]
    [InlineData(728, new[] { LongTile, ShortTile }, true)]
    [InlineData(728, new[] { LongTile, ShortTile, ShortTile }, false)]
    [InlineData(728, new[] { LongTile, LongTile }, false)]
    // A narrower span drops the three-short case too.
    [InlineData(684, new[] { ShortTile, ShortTile, ShortTile }, false)]
    [InlineData(684, new[] { LongTile, ShortTile }, true)]
    // Left-aligning the taskbar frees roughly 1326px, where the wide combinations fit — including three
    // long providers, which the old weight budget refused as "six slots of five".
    [InlineData(1326, new[] { LongTile, LongTile }, true)]
    [InlineData(1326, new[] { LongTile, LongTile, ShortTile }, true)]
    [InlineData(1326, new[] { LongTile, LongTile, LongTile }, true)]
    public void SpaceIsTheOnlyLimit(int available, int[] tiles, bool expected)
        => Assert.Equal(expected, PinBudgetService.RowWidth(tiles) <= available);

    // Before a widget has measured anything there is no span to judge against; refusing every pin at that
    // point would make pinning look broken on a cold start.
    [Fact]
    public void FallsBackToTheTileCapBeforeAnythingIsMeasured()
        => Assert.True(PinBudgetService.FitsTaskbar(new List<int> { 2, 3, 3 }, availableWidth: 0));

    [Fact]
    public void NothingIsDroppedWhileTheSetFits()
    {
        var pinned = Pinned((ProviderId.Zai, ShortTile), (ProviderId.Claude, ShortTile));

        Assert.Empty(PinBudgetService.SelectDrops(pinned, availableWidth: 728, maxCount: 3));
    }

    // Least recently used first, so what the user has just been working in is what survives.
    [Fact]
    public void DropsFromTheFrontUntilTheRowFits()
    {
        var pinned = Pinned(
            (ProviderId.Zai, LongTile), (ProviderId.Claude, LongTile), (ProviderId.Codex, ShortTile));

        var dropped = PinBudgetService.SelectDrops(pinned, availableWidth: 728, maxCount: 3);

        Assert.Equal(new[] { ProviderId.Zai }, dropped);
    }

    [Fact]
    public void DropsMoreThanOneWhenASingleDropIsNotEnough()
    {
        var pinned = Pinned(
            (ProviderId.Zai, LongTile), (ProviderId.Claude, LongTile), (ProviderId.Codex, LongTile));

        var dropped = PinBudgetService.SelectDrops(pinned, availableWidth: 500, maxCount: 3);

        Assert.Equal(new[] { ProviderId.Zai, ProviderId.Claude }, dropped);
    }

    // Enabling a row can add a whole column group, which is what pushes an already-pinned set over.
    [Fact]
    public void GrowingAProviderCanEvictTheLeastRecentPin()
    {
        var before = Pinned(
            (ProviderId.Zai, ShortTile), (ProviderId.Claude, ShortTile), (ProviderId.Codex, ShortTile));
        Assert.Empty(PinBudgetService.SelectDrops(before, availableWidth: 728, maxCount: 3));

        // Z.AI gains a third row and becomes a two-group tile.
        var after = Pinned(
            (ProviderId.Zai, LongTile), (ProviderId.Claude, ShortTile), (ProviderId.Codex, ShortTile));
        Assert.Equal(
            new[] { ProviderId.Zai },
            PinBudgetService.SelectDrops(after, availableWidth: 728, maxCount: 3));
    }

    [Fact]
    public void TheTileCapAppliesEvenWhenTheWidthFits()
    {
        // Four narrow providers would fit a wide bar, but the widget only renders three tiles.
        var pinned = Pinned(
            (ProviderId.Zai, 100), (ProviderId.Claude, 100), (ProviderId.Codex, 100), (ProviderId.Cursor, 100));

        var dropped = PinBudgetService.SelectDrops(pinned, availableWidth: 2000, maxCount: 3);

        Assert.Equal(new[] { ProviderId.Zai }, dropped);
    }

    [Fact]
    public void PerDisplayWidthsDoNotDropPinsThatFitOnTheirOwnTargets()
    {
        var pinned = Pinned(
            (ProviderId.Zai, LongTile),
            (ProviderId.Claude, ShortTile));
        var displays = new[]
        {
            new PinBudgetDisplay("DISPLAY1", 500, new[] { ProviderId.Zai }),
            new PinBudgetDisplay("DISPLAY2", 250, new[] { ProviderId.Claude }),
        };

        Assert.Empty(PinBudgetService.SelectDropsForDisplays(pinned, displays, maxCount: 3));
    }

    [Fact]
    public void PerDisplayCapsAllowTwoPinsOnEachDisplay()
    {
        var pinned = Pinned(
            (ProviderId.Claude, ShortTile),
            (ProviderId.Codex, ShortTile),
            (ProviderId.Cursor, ShortTile),
            (ProviderId.Grok, ShortTile));
        var displays = new[]
        {
            new PinBudgetDisplay("DISPLAY1", 1000, new[] { ProviderId.Claude, ProviderId.Codex }),
            new PinBudgetDisplay("DISPLAY2", 1000, new[] { ProviderId.Cursor, ProviderId.Grok }),
        };

        Assert.Empty(PinBudgetService.SelectDropsForDisplays(pinned, displays, maxCount: 2));
    }

    [Fact]
    public void PerDisplayCapOnlyDropsTheLeastRecentPinFromTheOverCapDisplay()
    {
        var pinned = Pinned(
            (ProviderId.Cursor, ShortTile),
            (ProviderId.Claude, ShortTile),
            (ProviderId.Codex, ShortTile),
            (ProviderId.Zai, ShortTile));
        var displays = new[]
        {
            new PinBudgetDisplay("DISPLAY1", 1000, new[]
            {
                ProviderId.Claude,
                ProviderId.Codex,
                ProviderId.Zai,
            }),
            new PinBudgetDisplay("DISPLAY2", 1000, new[] { ProviderId.Cursor }),
        };

        Assert.Equal(
            new[] { ProviderId.Claude },
            PinBudgetService.SelectDropsForDisplays(pinned, displays, maxCount: 2));
    }

    [Fact]
    public void CanPinEvaluatesTheProspectiveDestinationInsteadOfTheOldRoute()
    {
        var providers = Enum.GetValues<ProviderId>();
        var previousPins = providers.ToDictionary(
            provider => provider,
            WidgetSettingsService.IsProviderPinned);
        var previousMode = WidgetSettingsService.CurrentTaskbarPlacement;
        var previousSelection = WidgetSettingsService.SelectedTaskbarDisplayKey;
        var previousAdaptiveDisplays = providers
            .Select(provider => (Provider: provider.ToString(), Display: WidgetSettingsService.GetAdaptiveProviderDisplay(provider)))
            .Where(pair => pair.Display is not null)
            .ToDictionary(pair => pair.Provider, pair => pair.Display!);
        var previousPinnedDisplays = providers
            .Select(provider => (Provider: provider.ToString(), Display: WidgetSettingsService.GetPinnedProviderDisplay(provider)))
            .Where(pair => pair.Display is not null)
            .ToDictionary(pair => pair.Provider, pair => pair.Display!);
        var previousSurface = WidgetSettingsService.CurrentSurface;
        var previousOpacity = WidgetSettingsService.FloatingOpacity;

        try
        {
            WidgetSettingsService.RestoreSurfaceSettingsForTesting(
                WidgetSurfaceMode.Taskbar,
                previousOpacity);
            WidgetSettingsService.ResetProviderPinsForTesting();

            ProviderId candidate = ProviderId.Codex;
            int maxTiles = UsageCoordinator.MaxDisplayedWidgetTiles;
            var existingPins = providers
                .Where(provider => provider != candidate)
                .Take(maxTiles)
                .ToArray();
            foreach (var provider in existingPins)
                WidgetSettingsService.SetProviderPinnedForTesting(provider, true);

            var pinnedDisplays = existingPins.ToDictionary(
                provider => provider.ToString(),
                _ => "DISPLAY2");
            WidgetSettingsService.RestoreTaskbarPlacementForTesting(
                TaskbarPlacementMode.Adaptive,
                string.Empty,
                new Dictionary<string, string> { [candidate.ToString()] = "DISPLAY1" },
                pinnedDisplays);
            TaskbarSpace.ResetAvailableWidth();
            TaskbarSpace.ReportAvailableWidth("DISPLAY1", TaskbarSpace.UnknownWidth, isPrimary: true);
            TaskbarSpace.ReportAvailableWidth("DISPLAY2", TaskbarSpace.UnknownWidth);

            Assert.True(PinBudgetService.CanPin(candidate, out _));
            Assert.False(PinBudgetService.CanPin(candidate, "DISPLAY2", out string reason));
            Assert.Contains($"{maxTiles + 1}/{maxTiles} routed tiles", reason);

            WidgetSettingsService.SetProviderPinnedForTesting(candidate, true);
            pinnedDisplays[candidate.ToString()] = "DISPLAY1";
            WidgetSettingsService.RestoreTaskbarPlacementForTesting(
                TaskbarPlacementMode.Adaptive,
                string.Empty,
                new Dictionary<string, string> { [candidate.ToString()] = "DISPLAY1" },
                pinnedDisplays);

            Assert.True(PinBudgetService.CanPin(candidate, out _));
            Assert.False(PinBudgetService.CanPin(candidate, "DISPLAY2", out _));
        }
        finally
        {
            WidgetSettingsService.ResetProviderPinsForTesting();
            foreach (var pair in previousPins.Where(pair => pair.Value))
                WidgetSettingsService.SetProviderPinnedForTesting(pair.Key, true);
            WidgetSettingsService.RestoreTaskbarPlacementForTesting(
                previousMode,
                previousSelection,
                previousAdaptiveDisplays,
                previousPinnedDisplays);
            WidgetSettingsService.RestoreSurfaceSettingsForTesting(previousSurface, previousOpacity);
            TaskbarSpace.ResetAvailableWidth();
        }
    }

    [Fact]
    public void OverBudgetDisplayOnlyDropsPinsRoutedToThatDisplay()
    {
        var pinned = Pinned(
            (ProviderId.Claude, ShortTile),
            (ProviderId.Zai, LongTile));
        var displays = new[]
        {
            new PinBudgetDisplay("DISPLAY1", 300, new[] { ProviderId.Zai }),
            new PinBudgetDisplay("DISPLAY2", 250, new[] { ProviderId.Claude }),
        };

        Assert.Equal(
            new[] { ProviderId.Zai },
            PinBudgetService.SelectDropsForDisplays(pinned, displays, maxCount: 3));
    }

    [Fact]
    public void UnknownDisplayWidthIsPermissive()
    {
        var pinned = Pinned(
            (ProviderId.Zai, LongTile),
            (ProviderId.Claude, ShortTile));
        var displays = new[]
        {
            new PinBudgetDisplay("DISPLAY2", TaskbarSpace.UnknownWidth, new[]
            {
                ProviderId.Zai,
                ProviderId.Claude,
            }),
        };

        Assert.Empty(PinBudgetService.SelectDropsForDisplays(pinned, displays, maxCount: 3));
    }

    [Fact]
    public void NoDisplayIdentityRetainsTheGlobalTileCap()
    {
        var pinned = Pinned(
            (ProviderId.Zai, ShortTile),
            (ProviderId.Claude, ShortTile),
            (ProviderId.Codex, ShortTile));

        Assert.Equal(
            new[] { ProviderId.Zai },
            PinBudgetService.SelectDropsForDisplays(
                pinned,
                Array.Empty<PinBudgetDisplay>(),
                maxCount: 2));
    }

    [Fact]
    public void BudgetHysteresisRequiresThreeConsecutiveOverBudgetEvaluations()
    {
        var state = default(BudgetHysteresisState);

        state = PinBudgetService.AdvanceBudgetHysteresis(state, "same-set", overBudget: true);
        Assert.False(PinBudgetService.IsBudgetEvictionDue(state));

        state = PinBudgetService.AdvanceBudgetHysteresis(state, "same-set", overBudget: true);
        Assert.False(PinBudgetService.IsBudgetEvictionDue(state));

        state = PinBudgetService.AdvanceBudgetHysteresis(state, "same-set", overBudget: true);
        Assert.True(PinBudgetService.IsBudgetEvictionDue(state));
    }

    [Fact]
    public void BudgetHysteresisResetsWhenTheConditionFitsOrTheSignatureChanges()
    {
        var state = new BudgetHysteresisState("same-set", 2);

        state = PinBudgetService.AdvanceBudgetHysteresis(state, "same-set", overBudget: false);
        Assert.Equal(default, state);

        state = PinBudgetService.AdvanceBudgetHysteresis(state, "new-set", overBudget: true);
        Assert.Equal(1, state.ConsecutiveOverBudget);
    }

    [Fact]
    public void AvailableWidthIsTrackedIndependentlyForEachDisplay()
    {
        TaskbarSpace.ResetAvailableWidth();
        try
        {
            TaskbarSpace.ReportAvailableWidth("DISPLAY1", 700, isPrimary: true);
            TaskbarSpace.ReportAvailableWidth("DISPLAY2", 400);

            Assert.True(TaskbarSpace.TryGetAvailableWidth("DISPLAY1", out int primary));
            Assert.True(TaskbarSpace.TryGetAvailableWidth("DISPLAY2", out int secondary));
            Assert.Equal(700, primary);
            Assert.Equal(400, secondary);
            Assert.Equal(700, TaskbarSpace.AvailableLogicalWidth);
        }
        finally
        {
            TaskbarSpace.ResetAvailableWidth();
        }
    }

    [Fact]
    public void ResetAvailableWidthClearsPerDisplayMeasurements()
    {
        TaskbarSpace.ReportAvailableWidth("DISPLAY1", 700, isPrimary: true);
        TaskbarSpace.ResetAvailableWidth();

        Assert.False(TaskbarSpace.TryGetAvailableWidth("DISPLAY1", out _));
        Assert.Empty(TaskbarSpace.KnownDisplayKeys);
        Assert.Equal(TaskbarSpace.UnknownWidth, TaskbarSpace.AvailableLogicalWidth);
    }

    private static List<(ProviderId, int)> Pinned(params (ProviderId, int)[] entries) => [.. entries];
}
