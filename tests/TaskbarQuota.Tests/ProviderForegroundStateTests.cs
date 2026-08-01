using System;

namespace TaskbarQuota.Tests;

/// <summary>
/// Focus-follows-provider state machine behind the opt-in "hide the widget when no AI app is focused"
/// setting: showing is instant, hiding waits out a grace period so alt-tab transits don't blink the
/// widget, and our own UI taking the foreground (the flyout) changes nothing.
/// </summary>
public class ProviderForegroundStateTests
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(120);
    private static readonly DateTime Now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);

    private static (bool Active, DateTime UnfocusedSinceUtc) Resolve(
        bool providerForeground,
        bool ownUiEngaged = false,
        bool current = true,
        DateTime? unfocusedSince = null,
        DateTime? now = null)
        => UsageCoordinator.ResolveProviderForegroundState(
            providerForeground,
            ownUiEngaged,
            current,
            unfocusedSince ?? DateTime.MinValue,
            now ?? Now,
            HideDelay);

    [Fact]
    public void FocusingAProviderShowsImmediatelyAndClearsTheGracePeriod()
    {
        var result = Resolve(providerForeground: true, current: false, unfocusedSince: Now.AddSeconds(-30));

        Assert.True(result.Active);
        Assert.Equal(DateTime.MinValue, result.UnfocusedSinceUtc);
    }

    [Fact]
    public void LeavingTheProviderStartsTheGracePeriodWithoutHiding()
    {
        var result = Resolve(providerForeground: false);

        Assert.True(result.Active);
        Assert.Equal(Now, result.UnfocusedSinceUtc);
    }

    [Fact]
    public void StillInsideTheGracePeriodKeepsTheTile()
    {
        var since = Now.AddMilliseconds(-50);
        var result = Resolve(providerForeground: false, unfocusedSince: since);

        Assert.True(result.Active);
        Assert.Equal(since, result.UnfocusedSinceUtc);
    }

    [Fact]
    public void PastTheGracePeriodHidesTheTile()
    {
        var since = Now.AddMilliseconds(-150);
        var result = Resolve(providerForeground: false, unfocusedSince: since);

        Assert.False(result.Active);
    }

    [Fact]
    public void OurOwnUiInFrontNeitherHidesNorStartsTheGracePeriod()
    {
        var result = Resolve(providerForeground: false, ownUiEngaged: true);

        Assert.True(result.Active);
        Assert.Equal(DateTime.MinValue, result.UnfocusedSinceUtc);
    }

    [Fact]
    public void OurOwnUiInFrontDoesNotResumeAnExpiredGracePeriod()
    {
        // Flyout opened after the widget had already faded out: it must stay hidden, not flicker back.
        var since = Now.AddMilliseconds(-5000);
        var result = Resolve(providerForeground: false, ownUiEngaged: true, current: false, unfocusedSince: since);

        Assert.False(result.Active);
        Assert.Equal(since, result.UnfocusedSinceUtc);
    }

    [Fact]
    public void ReturningToTheProviderAfterAHideShowsAgain()
    {
        var result = Resolve(providerForeground: true, current: false, unfocusedSince: Now.AddMinutes(-5));

        Assert.True(result.Active);
        Assert.Equal(DateTime.MinValue, result.UnfocusedSinceUtc);
    }
}
