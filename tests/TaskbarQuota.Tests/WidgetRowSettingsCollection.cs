namespace TaskbarQuota.Tests;

/// <summary>
/// Serializes the test classes that drive <see cref="WidgetSettingsService"/>'s row-visibility state.
/// That state is process-wide static: xUnit runs separate classes in parallel, so one class's
/// SetRowVisibleForTesting / ResetRowVisibilityForTesting could land in the middle of another's
/// assertions and flip a row it expected to be hidden. Sharing one collection makes them run
/// sequentially instead.
/// </summary>
[CollectionDefinition(WidgetRowSettingsCollection.Name, DisableParallelization = true)]
public sealed class WidgetRowSettingsCollection
{
    public const string Name = "widget-row-settings";
}
