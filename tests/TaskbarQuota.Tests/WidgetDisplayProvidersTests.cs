using System;
using System.Collections.Generic;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

/// <summary>
/// Ordering and capping rules for the taskbar's multi-provider tile list (issue #25): the active
/// provider always leads, pinned providers trail it most-recently-active first, and the list never
/// exceeds the tile cap.
/// </summary>
public class WidgetDisplayProvidersTests
{
    private static IReadOnlyList<ProviderId> Compute(
        ProviderId? active,
        IReadOnlyList<ProviderId> pinned,
        IReadOnlyList<ProviderId>? recent = null,
        bool present = true,
        ProviderId? fallback = null,
        Func<ProviderId, bool>? isVisible = null,
        Func<ProviderId, bool>? isAvailable = null)
        => UsageCoordinator.ComputeWidgetDisplayProviders(
            active,
            present,
            recent ?? Array.Empty<ProviderId>(),
            Enum.GetValues<ProviderId>(),
            p => pinned.Contains(p),
            isVisible ?? (_ => true),
            isAvailable ?? (_ => true),
            fallback);

    [Fact]
    public void ActiveProviderLeadsAndPinnedTrailInRecencyOrder()
    {
        // The scenario from the issue thread: Claude used just before Codex, Z.AI never focused.
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai },
            recent: new[] { ProviderId.Codex, ProviderId.Claude });

        Assert.Equal(new[] { ProviderId.Codex, ProviderId.Claude, ProviderId.Zai }, result);
    }

    [Fact]
    public void ActiveProviderLeadsEvenWhenItIsItselfPinned()
    {
        var result = Compute(
            active: ProviderId.Zai,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai },
            recent: new[] { ProviderId.Zai, ProviderId.Claude });

        Assert.Equal(new[] { ProviderId.Zai, ProviderId.Claude }, result);
    }

    [Fact]
    public void PinnedProvidersStayWhenNoToolIsActive()
    {
        var result = Compute(
            active: null,
            pinned: new[] { ProviderId.Claude },
            present: false);

        Assert.Equal(new[] { ProviderId.Claude }, result);
    }

    [Fact]
    public void WithoutPinsFallsBackToTheSingleDisplayProvider()
    {
        var result = Compute(
            active: null,
            pinned: Array.Empty<ProviderId>(),
            fallback: ProviderId.Codex);

        Assert.Equal(new[] { ProviderId.Codex }, result);
    }

    [Fact]
    public void HiddenOrUnavailablePinnedProvidersAreDropped()
    {
        var hidden = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude },
            isVisible: p => p != ProviderId.Claude);
        Assert.Equal(new[] { ProviderId.Codex }, hidden);

        var unavailable = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude },
            isAvailable: p => p != ProviderId.Claude);
        Assert.Equal(new[] { ProviderId.Codex }, unavailable);
    }

    [Fact]
    public void ActiveProviderHiddenFromTheWidgetDoesNotTakeATile()
    {
        var result = Compute(
            active: ProviderId.Cursor,
            pinned: new[] { ProviderId.Claude },
            isVisible: p => p != ProviderId.Cursor);

        Assert.Equal(new[] { ProviderId.Claude }, result);
    }

    [Fact]
    public void NeverExceedsTheTileCap()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai, ProviderId.Cursor, ProviderId.Grok });

        Assert.Equal(UsageCoordinator.MaxWidgetTiles, result.Count);
        Assert.Equal(ProviderId.Codex, result[0]);
    }

    [Fact]
    public void EmptyWhenNothingIsPinnedActiveOrAvailable()
    {
        var result = Compute(
            active: null,
            pinned: Array.Empty<ProviderId>(),
            present: false,
            fallback: ProviderId.Codex);

        Assert.Empty(result);
    }
}
