using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
public class TaskbarPlacementSettingsTests
{
    [Fact]
    public void Placement_and_adaptive_assignments_round_trip()
    {
        var previousMode = WidgetSettingsService.CurrentTaskbarPlacement;
        var previousSelection = WidgetSettingsService.SelectedTaskbarDisplayKey;
        var previousAssignments = Enum.GetValues<ProviderId>()
            .Select(provider => (Provider: provider.ToString(), Display: WidgetSettingsService.GetAdaptiveProviderDisplay(provider)))
            .Where(pair => pair.Display is not null)
            .ToDictionary(pair => pair.Provider, pair => pair.Display!);
        string directory = Path.Combine(Path.GetTempPath(), "taskbarquota-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var storageOverride = AppStorage.OverrideAppDataDirectoryForTesting(directory);
            WidgetSettingsService.ReloadTaskbarPlacementForTesting();
            Assert.Equal(TaskbarPlacementMode.AllDisplays, WidgetSettingsService.CurrentTaskbarPlacement);

            WidgetSettingsService.ApplyTaskbarPlacement(TaskbarPlacementMode.SelectedDisplay, "DISPLAY2");
            Assert.True(WidgetSettingsService.SetAdaptiveProviderDisplay(ProviderId.Codex, "DISPLAY1"));
            Assert.Equal("1", File.ReadAllText(Path.Combine(directory, "taskbar-placement-mode.txt")));

            WidgetSettingsService.ReloadTaskbarPlacementForTesting();
            Assert.Equal(TaskbarPlacementMode.SelectedDisplay, WidgetSettingsService.CurrentTaskbarPlacement);
            Assert.Equal("DISPLAY2", WidgetSettingsService.SelectedTaskbarDisplayKey);
            Assert.Equal("DISPLAY1", WidgetSettingsService.GetAdaptiveProviderDisplay(ProviderId.Codex));
        }
        finally
        {
            WidgetSettingsService.RestoreTaskbarPlacementForTesting(
                previousMode,
                previousSelection,
                previousAssignments);
            Directory.Delete(directory, recursive: true);
        }
    }
}
