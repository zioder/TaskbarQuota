using System;

namespace TaskbarQuota.Taskbar;

public enum WidgetVisibilityMode
{
    AlwaysShow = 0,
    ShowWhileAnySupportedAiToolIsOpen = 1,
    ShowOnlyWhileSupportedAiToolIsInUse = 2,
}

internal enum WidgetVisibilityOverride
{
    Automatic = 0,
    ForceShow = 1,
    ForceHide = 2,
}

internal enum WidgetVisibilityReason
{
    ManualForceShow,
    ManualForceHide,
    NoValidProvider,
    AlwaysShow,
    OpenDesktopApp,
    OpenCliAgent,
    ActiveDesktopApp,
    ActiveCliAgent,
    ActiveBrowserTab,
    BackgroundCliAgent,
    BackgroundDesktopAgent,
    NoQualifyingSurface,
    HideDebounce,
}

internal readonly record struct SupportedSurfaceState(
    bool DesktopAppPresent,
    bool DesktopAppActive,
    bool CliAgentPresent,
    bool CliAgentActive,
    bool BrowserTabActive,
    bool BackgroundAgentRunning = false)
{
    public static SupportedSurfaceState None => new(false, false, false, false, false);
}

internal readonly record struct WidgetVisibilityInput(
    WidgetVisibilityMode Mode,
    WidgetVisibilityOverride Override,
    bool HasValidProvider,
    bool KeepVisibleWhileBackgroundAgentRunning,
    SupportedSurfaceState Surfaces);

internal readonly record struct WidgetVisibilityDecision(
    bool ShouldShowWidget,
    WidgetVisibilityReason Reason);

internal static class WidgetVisibilityPolicy
{
    public static WidgetVisibilityDecision Evaluate(WidgetVisibilityInput input)
    {
        if (input.Override == WidgetVisibilityOverride.ForceHide)
            return new(false, WidgetVisibilityReason.ManualForceHide);

        if (!input.HasValidProvider)
            return new(false, WidgetVisibilityReason.NoValidProvider);

        if (input.Override == WidgetVisibilityOverride.ForceShow)
            return new(true, WidgetVisibilityReason.ManualForceShow);

        if (input.Mode == WidgetVisibilityMode.AlwaysShow)
            return new(true, WidgetVisibilityReason.AlwaysShow);

        var surfaces = input.Surfaces;
        if (input.Mode == WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen)
        {
            if (surfaces.DesktopAppPresent)
                return new(true, WidgetVisibilityReason.OpenDesktopApp);
            if (surfaces.CliAgentPresent)
                return new(true, WidgetVisibilityReason.OpenCliAgent);
            if (surfaces.BrowserTabActive)
                return new(true, WidgetVisibilityReason.ActiveBrowserTab);

            return new(false, WidgetVisibilityReason.NoQualifyingSurface);
        }

        if (surfaces.DesktopAppActive)
            return new(true, WidgetVisibilityReason.ActiveDesktopApp);
        if (surfaces.CliAgentActive)
            return new(true, WidgetVisibilityReason.ActiveCliAgent);
        if (surfaces.BrowserTabActive)
            return new(true, WidgetVisibilityReason.ActiveBrowserTab);
        if (input.KeepVisibleWhileBackgroundAgentRunning)
        {
            if (surfaces.BackgroundAgentRunning)
                return new(true, WidgetVisibilityReason.BackgroundDesktopAgent);
            if (surfaces.CliAgentPresent)
                return new(true, WidgetVisibilityReason.BackgroundCliAgent);
        }

        return new(false, WidgetVisibilityReason.NoQualifyingSurface);
    }
}

/// <summary>
/// Keeps automatic surface transitions from flickering the native host. Showing is immediate; hiding
/// waits until the same non-qualifying state has been continuous for the configured delay.
/// </summary>
internal sealed class WidgetVisibilityStabilizer
{
    private readonly TimeSpan _hideDelay;
    private DateTimeOffset? _hideCandidateSince;
    private bool _isVisible;

    public WidgetVisibilityStabilizer(TimeSpan? hideDelay = null)
    {
        _hideDelay = hideDelay ?? TimeSpan.FromSeconds(2);
    }

    public WidgetVisibilityDecision Apply(
        WidgetVisibilityDecision rawDecision,
        DateTimeOffset now,
        bool hideImmediately = false)
    {
        if (rawDecision.ShouldShowWidget)
        {
            _hideCandidateSince = null;
            _isVisible = true;
            return rawDecision;
        }

        if (hideImmediately || !_isVisible)
        {
            _hideCandidateSince = null;
            _isVisible = false;
            return rawDecision;
        }

        _hideCandidateSince ??= now;
        if (now - _hideCandidateSince.Value < _hideDelay)
            return new(true, WidgetVisibilityReason.HideDebounce);

        _hideCandidateSince = null;
        _isVisible = false;
        return rawDecision;
    }

    public void Reset(bool isVisible)
    {
        _hideCandidateSince = null;
        _isVisible = isVisible;
    }
}
