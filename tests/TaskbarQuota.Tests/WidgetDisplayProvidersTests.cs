using System;
using System.Collections.Generic;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

/// <summary>
/// Ordering rules for the taskbar's multi-provider candidate list (issue #88): visible, available pins lead
/// most-recently-active first, followed by the active provider when visible and not already present. The
/// per-display cap is applied after routing.
/// </summary>
public class WidgetDisplayProvidersTests
{
    private static IReadOnlyList<ProviderId> Compute(
        ProviderId? active,
        IReadOnlyList<ProviderId> pinned,
        IReadOnlyList<ProviderId>? recent = null,
        bool present = true,
        Func<ProviderId, bool>? isVisible = null,
        Func<ProviderId, bool>? isAvailable = null)
        => UsageCoordinator.ComputeWidgetDisplayProviders(
            active,
            present,
            recent ?? Array.Empty<ProviderId>(),
            Enum.GetValues<ProviderId>(),
            p => pinned.Contains(p),
            isVisible ?? (_ => true),
            isAvailable ?? (_ => true));

    [Fact]
    public void PinnedProvidersLeadActiveInRecencyOrder()
    {
        // The scenario from the issue thread: Claude used just before Codex, Z.AI never focused.
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai },
            recent: new[] { ProviderId.Codex, ProviderId.Claude });

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Zai, ProviderId.Codex }, result);
    }

    [Fact]
    public void ActiveProviderIsNotDuplicatedWhenItIsPinned()
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
    public void WithoutActiveProviderOrPins_IsEmptyEvenWhenAToolIsPresent()
    {
        var result = Compute(
            active: null,
            pinned: Array.Empty<ProviderId>(),
            present: true);

        Assert.Empty(result);
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
    public void CandidateOrderingDoesNotGloballyTruncatePinsBeforeRouting()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai, ProviderId.Cursor, ProviderId.Grok });

        Assert.Equal(
            new[]
            {
                ProviderId.Claude,
                ProviderId.Cursor,
                ProviderId.Grok,
                ProviderId.Zai,
                ProviderId.Codex,
            },
            result);
    }

    [Fact]
    public void ActivityWidgetDoesNotChangeCandidateOrdering()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai });

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Zai, ProviderId.Codex }, result);
    }

    [Fact]
    public void OnePinLeavesOneSlotForActiveProvider()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude });

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex }, result);
    }

    [Fact]
    public void NoPinsShowsOnlyTheActiveProvider()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: Array.Empty<ProviderId>());

        Assert.Equal(new[] { ProviderId.Codex }, result);
    }

    [Fact]
    public void EmptyWhenNothingIsPinnedActiveOrAvailable()
    {
        var result = Compute(
            active: null,
            pinned: Array.Empty<ProviderId>(),
            present: false);

        Assert.Empty(result);
    }
}
