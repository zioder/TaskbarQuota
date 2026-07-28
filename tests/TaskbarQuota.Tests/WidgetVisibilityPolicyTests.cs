using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public sealed class WidgetVisibilityPolicyTests
{
    public static IEnumerable<object[]> AutomaticCases =>
        new[]
        {
            new object[] { WidgetVisibilityMode.AlwaysShow, SupportedSurfaceState.None, true, WidgetVisibilityReason.AlwaysShow },
            new object[]
            {
                WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen,
                new SupportedSurfaceState(true, false, false, false, false),
                true,
                WidgetVisibilityReason.OpenDesktopApp
            },
            new object[]
            {
                WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen,
                new SupportedSurfaceState(false, false, true, false, false),
                true,
                WidgetVisibilityReason.OpenCliAgent
            },
            new object[]
            {
                WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen,
                new SupportedSurfaceState(false, false, false, false, true),
                true,
                WidgetVisibilityReason.ActiveBrowserTab
            },
            new object[]
            {
                WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
                new SupportedSurfaceState(true, false, false, false, false),
                false,
                WidgetVisibilityReason.NoQualifyingSurface
            },
            new object[]
            {
                WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
                new SupportedSurfaceState(false, true, false, false, false),
                true,
                WidgetVisibilityReason.ActiveDesktopApp
            },
            new object[]
            {
                WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
                new SupportedSurfaceState(false, false, true, true, false),
                true,
                WidgetVisibilityReason.ActiveCliAgent
            },
            new object[]
            {
                WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
                new SupportedSurfaceState(false, false, false, false, true),
                true,
                WidgetVisibilityReason.ActiveBrowserTab
            },
        };

    [Theory]
    [MemberData(nameof(AutomaticCases))]
    public void AutomaticPolicyUsesExpectedSurface(
        object modeValue,
        object surfacesValue,
        bool expectedVisible,
        object expectedReasonValue)
    {
        var mode = Assert.IsType<WidgetVisibilityMode>(modeValue);
        var surfaces = Assert.IsType<SupportedSurfaceState>(surfacesValue);
        var expectedReason = Assert.IsType<WidgetVisibilityReason>(expectedReasonValue);
        var result = Evaluate(mode, WidgetVisibilityOverride.Automatic, surfaces);

        Assert.Equal(expectedVisible, result.ShouldShowWidget);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void ForceHideWinsEvenWithoutAProvider()
    {
        var result = Evaluate(
            WidgetVisibilityMode.AlwaysShow,
            WidgetVisibilityOverride.ForceHide,
            SupportedSurfaceState.None,
            hasValidProvider: false);

        Assert.False(result.ShouldShowWidget);
        Assert.Equal(WidgetVisibilityReason.ManualForceHide, result.Reason);
    }

    [Fact]
    public void ForceShowIsRetainedButCannotShowWithoutAValidProvider()
    {
        var unavailable = Evaluate(
            WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
            WidgetVisibilityOverride.ForceShow,
            SupportedSurfaceState.None,
            hasValidProvider: false);
        var available = Evaluate(
            WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
            WidgetVisibilityOverride.ForceShow,
            SupportedSurfaceState.None);

        Assert.Equal(new(false, WidgetVisibilityReason.NoValidProvider), unavailable);
        Assert.Equal(new(true, WidgetVisibilityReason.ManualForceShow), available);
    }

    [Fact]
    public void BackgroundCliOptionOnlyAffectsInUseMode()
    {
        var surfaces = new SupportedSurfaceState(false, false, true, false, false);

        var disabled = Evaluate(
            WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
            WidgetVisibilityOverride.Automatic,
            surfaces);
        var enabled = Evaluate(
            WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
            WidgetVisibilityOverride.Automatic,
            surfaces,
            backgroundAgent: true);

        Assert.False(disabled.ShouldShowWidget);
        Assert.Equal(new(true, WidgetVisibilityReason.BackgroundCliAgent), enabled);
    }

    [Fact]
    public void BackgroundAgentOptionIncludesRunningCodexDesktopTurn()
    {
        var surfaces = new SupportedSurfaceState(
            DesktopAppPresent: true,
            DesktopAppActive: false,
            CliAgentPresent: false,
            CliAgentActive: false,
            BrowserTabActive: false,
            BackgroundAgentRunning: true);

        var disabled = Evaluate(
            WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
            WidgetVisibilityOverride.Automatic,
            surfaces);
        var enabled = Evaluate(
            WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse,
            WidgetVisibilityOverride.Automatic,
            surfaces,
            backgroundAgent: true);

        Assert.Equal(new(false, WidgetVisibilityReason.NoQualifyingSurface), disabled);
        Assert.Equal(new(true, WidgetVisibilityReason.BackgroundDesktopAgent), enabled);
    }

    [Fact]
    public void StabilizerShowsImmediatelyAndHidesAfterContinuousDelay()
    {
        var stabilizer = new WidgetVisibilityStabilizer(TimeSpan.FromSeconds(2));
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var show = new WidgetVisibilityDecision(true, WidgetVisibilityReason.OpenDesktopApp);
        var hide = new WidgetVisibilityDecision(false, WidgetVisibilityReason.NoQualifyingSurface);

        Assert.True(stabilizer.Apply(show, now).ShouldShowWidget);
        Assert.Equal(WidgetVisibilityReason.HideDebounce, stabilizer.Apply(hide, now).Reason);
        Assert.Equal(WidgetVisibilityReason.HideDebounce, stabilizer.Apply(hide, now.AddMilliseconds(1999)).Reason);
        Assert.Equal(hide, stabilizer.Apply(hide, now.AddSeconds(2)));
    }

    [Fact]
    public void StabilizerCancelsPendingHideWhenSurfaceReturns()
    {
        var stabilizer = new WidgetVisibilityStabilizer(TimeSpan.FromSeconds(2));
        var now = DateTimeOffset.UtcNow;
        var show = new WidgetVisibilityDecision(true, WidgetVisibilityReason.ActiveCliAgent);
        var hide = new WidgetVisibilityDecision(false, WidgetVisibilityReason.NoQualifyingSurface);

        stabilizer.Apply(show, now);
        stabilizer.Apply(hide, now);
        Assert.Equal(show, stabilizer.Apply(show, now.AddSeconds(1)));
        Assert.Equal(WidgetVisibilityReason.HideDebounce, stabilizer.Apply(hide, now.AddSeconds(2)).Reason);
    }

    [Fact]
    public void StabilizerCanBypassDelayForSettingsAndProviderChanges()
    {
        var stabilizer = new WidgetVisibilityStabilizer(TimeSpan.FromSeconds(2));
        var now = DateTimeOffset.UtcNow;
        stabilizer.Apply(new(true, WidgetVisibilityReason.AlwaysShow), now);

        var result = stabilizer.Apply(
            new(false, WidgetVisibilityReason.NoValidProvider),
            now,
            hideImmediately: true);

        Assert.Equal(new(false, WidgetVisibilityReason.NoValidProvider), result);
    }

    private static WidgetVisibilityDecision Evaluate(
        WidgetVisibilityMode mode,
        WidgetVisibilityOverride visibilityOverride,
        SupportedSurfaceState surfaces,
        bool hasValidProvider = true,
        bool backgroundAgent = false)
        => WidgetVisibilityPolicy.Evaluate(
            new(mode, visibilityOverride, hasValidProvider, backgroundAgent, surfaces));
}
