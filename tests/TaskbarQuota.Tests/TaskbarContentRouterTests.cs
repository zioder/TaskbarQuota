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
            provider => provider == ProviderId.Antigravity ? "DISPLAY2" : "DISPLAY1");

        Assert.Equal([antigravity], routed.Items);
    }

    private static IReadOnlyList<ProviderId> Route(
        IReadOnlyList<ProviderId> providers,
        TaskbarPlacementMode mode,
        string selected,
        string target,
        Func<ProviderId, string?> assignment)
        => TaskbarContentRouter.ProvidersForDisplay(
            providers,
            mode,
            selected,
            target,
            "DISPLAY1",
            Displays,
            assignment);
}
