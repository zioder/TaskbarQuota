using TaskbarQuota.AgentActivity;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class TaskbarContentRouterTests
{
    private static readonly HashSet<string> Displays = new(StringComparer.OrdinalIgnoreCase)
    {
        "DISPLAY1",
        "DISPLAY2",
    };

    [Fact]
    public void AllDisplays_routes_every_provider_to_every_taskbar()
    {
        ProviderId[] providers = [ProviderId.Codex, ProviderId.Antigravity];

        var routed = Route(providers, TaskbarPlacementMode.AllDisplays, "DISPLAY2", "DISPLAY1", _ => null);

        Assert.Equal(providers, routed);
    }

    [Fact]
    public void SelectedDisplay_routes_all_content_only_to_selection()
    {
        ProviderId[] providers = [ProviderId.Codex, ProviderId.Antigravity];

        Assert.Empty(Route(providers, TaskbarPlacementMode.SelectedDisplay, "DISPLAY2", "DISPLAY1", _ => null));
        Assert.Equal(
            providers,
            Route(providers, TaskbarPlacementMode.SelectedDisplay, "DISPLAY2", "DISPLAY2", _ => null));
    }

    [Fact]
    public void Missing_selected_display_falls_back_to_primary()
    {
        ProviderId[] providers = [ProviderId.Codex];

        Assert.Equal(
            providers,
            Route(providers, TaskbarPlacementMode.SelectedDisplay, "DISPLAY9", "DISPLAY1", _ => null));
    }

    [Fact]
    public void Adaptive_routes_each_provider_to_its_observed_display()
    {
        ProviderId[] providers = [ProviderId.Codex, ProviderId.Antigravity, ProviderId.Claude];
        string? Assignment(ProviderId provider) => provider switch
        {
            ProviderId.Codex => "DISPLAY1",
            ProviderId.Antigravity => "DISPLAY2",
            _ => null,
        };

        Assert.Equal(
            [ProviderId.Codex, ProviderId.Claude],
            Route(providers, TaskbarPlacementMode.Adaptive, string.Empty, "DISPLAY1", Assignment));
        Assert.Equal(
            [ProviderId.Antigravity],
            Route(providers, TaskbarPlacementMode.Adaptive, string.Empty, "DISPLAY2", Assignment));
    }

    [Fact]
    public void Adaptive_candidates_exclude_stale_unpinned_provider_during_move()
    {
        ProviderId[] providers = [ProviderId.OpenCode, ProviderId.Codex];

        var candidates = TaskbarContentRouter.AdaptiveCandidatesForDisplay(
            providers,
            ProviderId.OpenCode,
            _ => true,
            _ => false);

        Assert.Equal([ProviderId.OpenCode], candidates);
    }

    [Fact]
    public void Adaptive_candidates_preserve_explicit_pins_beside_current_provider()
    {
        ProviderId[] providers = [ProviderId.OpenCode, ProviderId.Codex];

        var candidates = TaskbarContentRouter.AdaptiveCandidatesForDisplay(
            providers,
            ProviderId.OpenCode,
            _ => true,
            provider => provider == ProviderId.Codex);

        Assert.Equal([ProviderId.OpenCode, ProviderId.Codex], candidates);
    }

    [Fact]
    public void Adaptive_filters_agent_activity_by_provider_display()
    {
        var now = DateTimeOffset.UtcNow;
        var codex = new AgentActivityItem(
            "codex", ProviderId.Codex, "Codex", "Working", AgentActivityStatus.Working, now, now);
        var antigravity = new AgentActivityItem(
            "antigravity", ProviderId.Antigravity, "Antigravity", "Working", AgentActivityStatus.Working, now, now);
        var snapshot = new AgentActivitySnapshot([codex, antigravity]);

        var routed = TaskbarContentRouter.ActivityForDisplay(
            snapshot,
            TaskbarPlacementMode.Adaptive,
            string.Empty,
            "DISPLAY2",
            "DISPLAY1",
            Displays,
            provider => provider == ProviderId.Antigravity ? "DISPLAY2" : "DISPLAY1",
            _ => false,
            _ => null);

        Assert.Equal([antigravity], routed.Items);
    }

    [Fact]
    public void Adaptive_activity_honors_a_fixed_pin_destination()
    {
        var now = DateTimeOffset.UtcNow;
        var codex = new AgentActivityItem(
            "codex", ProviderId.Codex, "Codex", "Working", AgentActivityStatus.Working, now, now);
        var snapshot = new AgentActivitySnapshot([codex]);

        var routed = TaskbarContentRouter.ActivityForDisplay(
            snapshot,
            TaskbarPlacementMode.Adaptive,
            string.Empty,
            "DISPLAY2",
            "DISPLAY1",
            Displays,
            _ => "DISPLAY1",
            _ => true,
            _ => "DISPLAY2");

        Assert.Equal([codex], routed.Items);
    }

    [Fact]
    public void Adaptive_fixed_pin_destination_overrides_the_apps_display()
    {
        ProviderId[] providers = [ProviderId.Codex];

        var appDisplay = Route(
            providers,
            TaskbarPlacementMode.Adaptive,
            string.Empty,
            "DISPLAY1",
            _ => "DISPLAY1",
            _ => true,
            _ => "DISPLAY2");
        var pinDisplay = Route(
            providers,
            TaskbarPlacementMode.Adaptive,
            string.Empty,
            "DISPLAY2",
            _ => "DISPLAY1",
            _ => true,
            _ => "DISPLAY2");

        Assert.Empty(appDisplay);
        Assert.Equal(providers, pinDisplay);
    }

    [Fact]
    public void Adaptive_unpinned_provider_ignores_a_saved_pin_destination()
    {
        ProviderId[] providers = [ProviderId.Codex];

        var routed = Route(
            providers,
            TaskbarPlacementMode.Adaptive,
            string.Empty,
            "DISPLAY1",
            _ => "DISPLAY1",
            _ => false,
            _ => "DISPLAY2");

        Assert.Equal(providers, routed);
    }

    [Fact]
    public void Adaptive_disconnected_pin_destination_temporarily_falls_back_to_primary()
    {
        ProviderId[] providers = [ProviderId.Codex];

        var routed = Route(
            providers,
            TaskbarPlacementMode.Adaptive,
            string.Empty,
            "DISPLAY1",
            _ => "DISPLAY2",
            _ => true,
            _ => "DISPLAY9");

        Assert.Equal(providers, routed);
    }

    [Fact]
    public void Adaptive_all_screens_pin_routes_to_every_display()
    {
        ProviderId[] providers = [ProviderId.Codex];

        var primary = Route(
            providers,
            TaskbarPlacementMode.Adaptive,
            string.Empty,
            "DISPLAY1",
            _ => "DISPLAY1",
            _ => true,
            _ => WidgetSettingsService.AllDisplaysPinDestination);
        var secondary = Route(
            providers,
            TaskbarPlacementMode.Adaptive,
            string.Empty,
            "DISPLAY2",
            _ => "DISPLAY1",
            _ => true,
            _ => WidgetSettingsService.AllDisplaysPinDestination);

        Assert.Equal(providers, primary);
        Assert.Equal(providers, secondary);
    }

    private static IReadOnlyList<ProviderId> Route(
        IReadOnlyList<ProviderId> providers,
        TaskbarPlacementMode mode,
        string selected,
        string target,
        Func<ProviderId, string?> assignment,
        Func<ProviderId, bool>? isPinned = null,
        Func<ProviderId, string?>? pinAssignment = null)
        => TaskbarContentRouter.ProvidersForDisplay(
            providers,
            mode,
            selected,
            target,
            "DISPLAY1",
            Displays,
            assignment,
            isPinned ?? (_ => false),
            pinAssignment ?? (_ => null));
}
