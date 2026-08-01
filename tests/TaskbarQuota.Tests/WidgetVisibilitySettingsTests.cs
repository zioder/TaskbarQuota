using TaskbarQuota.Taskbar;
using TaskbarQuota.Views;

namespace TaskbarQuota.Tests;

public class WidgetVisibilitySettingsTests
{
    [Theory]
    [InlineData("AlwaysShow", WidgetVisibilityMode.AlwaysShow)]
    [InlineData("ShowWhileOpen", WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen)]
    [InlineData("ShowWhileInUse", WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse)]
    [InlineData("unknown", WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen)]
    [InlineData(null, WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen)]
    public void ParseWidgetVisibilityModeTag_UsesStableXamlTags(
        string? tag,
        WidgetVisibilityMode expected)
        => Assert.Equal(expected, SettingsPage.ParseWidgetVisibilityModeTag(tag));

    [Theory]
    [InlineData(WidgetVisibilityMode.AlwaysShow, "AlwaysShow")]
    [InlineData(WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen, "ShowWhileOpen")]
    [InlineData(WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse, "ShowWhileInUse")]
    public void WidgetVisibilityModeTag_RoundTripsKnownModes(
        WidgetVisibilityMode mode,
        string expected)
        => Assert.Equal(expected, SettingsPage.WidgetVisibilityModeTag(mode));

    [Fact]
    public void TryPersistAndApply_DoesNotCommitWhenPersistenceFails()
    {
        bool applied = false;

        bool result = WidgetSettingsService.TryPersistAndApply(
            () => false,
            () => applied = true);

        Assert.False(result);
        Assert.False(applied);
    }

    [Fact]
    public void TryPersistAndApply_CommitsAfterPersistenceSucceeds()
    {
        bool persisted = false;
        bool applied = false;

        bool result = WidgetSettingsService.TryPersistAndApply(
            () =>
            {
                persisted = true;
                return true;
            },
            () =>
            {
                Assert.True(persisted);
                applied = true;
            });

        Assert.True(result);
        Assert.True(applied);
    }
}
