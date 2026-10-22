using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Tests;

public class DashboardProviderSelectionTests
{
    [Fact]
    public void WidgetSelectionSurvivesColdLoadAndProgressiveUpdates()
    {
        var selection = new DashboardProviderSelection();
        Assert.True(selection.SelectWidgetProvider(ProviderId.Claude, _ => true));

        Assert.Equal(ProviderId.Claude, selection.Resolve(null, [], _ => true));
        Assert.Equal(ProviderId.Claude, selection.Resolve(ProviderId.Codex, [ProviderId.Codex], _ => true));
        Assert.Equal(ProviderId.Claude, selection.Resolve(
            ProviderId.Claude, [ProviderId.Codex, ProviderId.Claude], _ => true));
    }

    [Fact]
    public void WidgetOnlyProviderDoesNotNeedADashboardEntry()
    {
        var selection = new DashboardProviderSelection();
        ProviderId[] dashboard = [ProviderId.Codex];
        selection.SelectWidgetProvider(ProviderId.Claude, _ => true);

        Assert.Equal(ProviderId.Claude, selection.Resolve(ProviderId.Codex, dashboard, _ => true));
        Assert.Equal([ProviderId.Codex], dashboard);
    }

    [Fact]
    public void NewerTileClickSupersedesAnUnfinishedInitialLoad()
    {
        var selection = new DashboardProviderSelection();
        selection.SelectWidgetProvider(ProviderId.Claude, _ => true);
        selection.SelectWidgetProvider(ProviderId.Codex, _ => true);

        Assert.Equal(ProviderId.Codex, selection.Resolve(null, [], _ => true));
        Assert.Equal(ProviderId.Codex, selection.Resolve(ProviderId.Claude, [ProviderId.Claude], _ => true));
    }

    [Fact]
    public void NormalDashboardSelectionSupersedesWidgetSelection()
    {
        var selection = new DashboardProviderSelection();
        selection.SelectWidgetProvider(ProviderId.Claude, _ => true);
        selection.ClearWidgetProvider();

        Assert.Equal(ProviderId.Codex, selection.Resolve(
            ProviderId.Codex, [ProviderId.Claude, ProviderId.Codex], _ => true));
    }

    [Fact]
    public void IneligibleWidgetRequestDoesNotOverrideCurrentSelection()
    {
        var selection = new DashboardProviderSelection();

        Assert.False(selection.SelectWidgetProvider(ProviderId.Claude, _ => false));
        Assert.Equal(ProviderId.Codex, selection.Resolve(ProviderId.Codex, [ProviderId.Codex], _ => true));
    }

    [Fact]
    public void HidingOrDisablingTheWidgetClearsItsTransientSelection()
    {
        var selection = new DashboardProviderSelection();
        selection.SelectWidgetProvider(ProviderId.Claude, _ => true);

        Assert.Equal(ProviderId.Codex, selection.Resolve(ProviderId.Claude, [ProviderId.Codex], _ => false));
        // Re-enabling the widget later must not resurrect a discarded click.
        Assert.Equal(ProviderId.Codex, selection.Resolve(ProviderId.Codex, [ProviderId.Codex], _ => true));
    }

    [Fact]
    public void InvalidatedTransientSelectionAllowsAnEmptyDashboard()
    {
        var selection = new DashboardProviderSelection();
        selection.SelectWidgetProvider(ProviderId.Claude, _ => true);

        Assert.Null(selection.Resolve(ProviderId.Claude, [], _ => false));
    }

    [Fact]
    public void DashboardRefreshPreservesOrdinarySelectionWithoutAWidgetRequest()
    {
        var selection = new DashboardProviderSelection();

        Assert.Equal(ProviderId.Claude, selection.Resolve(
            ProviderId.Claude, [ProviderId.Codex, ProviderId.Claude], _ => true));
        Assert.Equal(ProviderId.Codex, selection.Resolve(ProviderId.Claude, [ProviderId.Codex], _ => true));
    }
}
