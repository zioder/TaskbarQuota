using TaskbarQuota.AgentActivity;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
public sealed class WidgetActivityVisibilityTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("TaskbarQuota-widget-visibility-");
    private readonly IDisposable _storageOverride;

    public WidgetActivityVisibilityTests()
    {
        _storageOverride = AppStorage.OverrideAppDataDirectoryForTesting(_directory.FullName);
        Reset();
    }

    public void Dispose()
    {
        Reset();
        _storageOverride.Dispose();
        _directory.Delete(recursive: true);
    }

    private static void Reset()
    {
        ProviderDiscoveryService.ResetForTesting();
        WidgetSettingsService.ResetProviderVisibilityForTesting();
        WidgetSettingsService.ResetDashboardProviderVisibilityForTesting();
        WidgetSettingsService.ResetProviderPinsForTesting();
    }

    [Fact]
    public void Widget_hide_excludes_activity_and_retained_run_without_changing_shared_history()
    {
        var copilot = Item("copilot", ProviderId.Copilot);
        var codex = Item("codex", ProviderId.Codex);
        var source = new AgentActivitySnapshot([copilot, codex], [copilot, codex]);
        ProviderDiscoveryService.SetWidgetVisibilityPreference(ProviderId.Copilot, false);

        var visible = TaskBarManager.VisibleWidgetActivity(source);

        Assert.Equal([codex], visible.Items);
        Assert.Equal([codex], visible.RunItems);
        Assert.Equal([codex], visible.TrackedItems);
        Assert.Equal([copilot, codex], source.Items);
        Assert.Equal([copilot, codex], source.RunItems);
        Assert.True(WidgetSettingsService.IsProviderDashboardVisible(ProviderId.Copilot));
    }

    [Fact]
    public void Hidden_run_cannot_restore_its_provider_through_history_fallback()
    {
        var copilot = Item("copilot", ProviderId.Copilot);
        var codex = Item("codex", ProviderId.Codex);
        var source = new AgentActivitySnapshot([copilot, codex], [copilot]);
        ProviderDiscoveryService.SetWidgetVisibilityPreference(ProviderId.Copilot, false);

        var visible = TaskBarManager.VisibleWidgetActivity(source);

        Assert.Empty(visible.RunItems!);
        Assert.Equal([codex], visible.TrackedItems);
        Assert.Equal(codex, visible.Primary);
    }

    [Fact]
    public void Dashboard_disable_excludes_activity_even_with_a_stale_visible_widget_flag()
    {
        var source = new AgentActivitySnapshot([Item("copilot", ProviderId.Copilot)]);
        ProviderDiscoveryService.DisableProvider(ProviderId.Copilot);
        WidgetSettingsService.SetProviderVisibleForTesting(ProviderId.Copilot, true);

        var visible = TaskBarManager.VisibleWidgetActivity(source);

        Assert.Empty(visible.Items);
        Assert.Null(visible.Primary);
    }

    [Fact]
    public void Widget_reenable_restores_activity_from_the_existing_snapshot()
    {
        var copilot = Item("copilot", ProviderId.Copilot);
        var source = new AgentActivitySnapshot([copilot], [copilot]);
        ProviderDiscoveryService.SetWidgetVisibilityPreference(ProviderId.Copilot, false);
        var hidden = TaskBarManager.VisibleWidgetActivity(source);
        Assert.Null(hidden.Primary);
        Assert.False(TaskBarWidget.ShouldDeferEmptyActivitySnapshot(
            source, hidden, allowEmptyGrace: source.Primary is null));
        ProviderDiscoveryService.SetWidgetVisibilityPreference(ProviderId.Copilot, true);

        Assert.Equal(copilot, TaskBarManager.VisibleWidgetActivity(source).Primary);
    }

    private static AgentActivityItem Item(string id, ProviderId provider)
    {
        var now = DateTimeOffset.UtcNow;
        return new(id, provider, id, "Working", AgentActivityStatus.Working, now, now);
    }
}
