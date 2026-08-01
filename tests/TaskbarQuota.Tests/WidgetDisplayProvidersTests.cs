using System;
using System.Collections.Generic;
using TaskbarQuota.ActiveApp;
using TaskbarQuota.Taskbar;
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

    [Fact]
    public void DisplayProviderSelectionUsesEligibleRecentThenPersistedProvider()
    {
        var fromRecent = UsageCoordinator.SelectWidgetDisplayProvider(
            active: ProviderId.Codex,
            recent: new[] { ProviderId.Claude },
            persisted: ProviderId.Cursor,
            ordered: Enum.GetValues<ProviderId>(),
            isVisible: _ => true,
            isAvailable: provider => provider is ProviderId.Claude or ProviderId.Cursor);
        var fromPersisted = UsageCoordinator.SelectWidgetDisplayProvider(
            active: ProviderId.Codex,
            recent: Array.Empty<ProviderId>(),
            persisted: ProviderId.Cursor,
            ordered: Enum.GetValues<ProviderId>(),
            isVisible: _ => true,
            isAvailable: provider => provider == ProviderId.Cursor);

        Assert.Equal(ProviderId.Claude, fromRecent);
        Assert.Equal(ProviderId.Cursor, fromPersisted);
    }

    [Fact]
    public void DisplayProviderSelectionReturnsNullWhenNoProviderIsEligible()
    {
        var result = UsageCoordinator.SelectWidgetDisplayProvider(
            active: ProviderId.Codex,
            recent: new[] { ProviderId.Claude },
            persisted: ProviderId.Cursor,
            ordered: Enum.GetValues<ProviderId>(),
            isVisible: _ => true,
            isAvailable: _ => false);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(ProviderSourceKind.DesktopApp, true, false, false)]
    [InlineData(ProviderSourceKind.HostApp, true, false, false)]
    [InlineData(ProviderSourceKind.Cli, false, true, false)]
    [InlineData(ProviderSourceKind.Browser, false, false, true)]
    public void SupportedSurfaceStateSeparatesForegroundCategory(
        ProviderSourceKind sourceKind,
        bool desktopActive,
        bool cliActive,
        bool browserActive)
    {
        var state = UsageCoordinator.ComputeSupportedSurfaces(
            new SupportedToolPresence(false, false),
            ProviderId.Claude,
            new ProviderSource(sourceKind));

        Assert.Equal(desktopActive, state.DesktopAppActive);
        Assert.Equal(cliActive, state.CliAgentActive);
        Assert.Equal(browserActive, state.BrowserTabActive);
    }

    [Fact]
    public void SupportedSurfaceStateKeepsBackgroundCliSeparateFromForeground()
    {
        var state = UsageCoordinator.ComputeSupportedSurfaces(
            new SupportedToolPresence(false, true),
            detected: null,
            ProviderSource.Unknown);

        Assert.True(state.CliAgentPresent);
        Assert.False(state.CliAgentActive);
        Assert.False(state.BrowserTabActive);
    }

    [Fact]
    public void SupportedSurfaceStateCarriesBackgroundDesktopAgentSeparately()
    {
        var state = UsageCoordinator.ComputeSupportedSurfaces(
            new SupportedToolPresence(
                DesktopAppPresent: true,
                CliAgentPresent: false,
                BackgroundAgentRunning: true),
            detected: null,
            ProviderSource.Unknown);

        Assert.True(state.DesktopAppPresent);
        Assert.True(state.BackgroundAgentRunning);
        Assert.False(state.DesktopAppActive);
        Assert.False(state.CliAgentPresent);
        Assert.False(state.CliAgentActive);
    }
}
