using System.Reflection;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
public sealed class AppStorageIsolationTests
{
    [Fact]
    public void Assembly_storage_is_isolated_before_settings_capture_their_paths()
    {
        Assert.Equal(TestEnvironment.AppDataDirectory, AppStorage.AppDataDirectory);
        Assert.NotEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppStorage.AppFolderName),
            AppStorage.AppDataDirectory);
        Assert.True(Directory.Exists(AppStorage.AppDataDirectory));

        var capturedPaths = typeof(WidgetSettingsService).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.IsInitOnly && field.FieldType == typeof(string) && field.Name.EndsWith("Path", StringComparison.Ordinal))
            .Select(field => Assert.IsType<string>(field.GetValue(null)))
            .ToArray();
        Assert.NotEmpty(capturedPaths);
        Assert.All(capturedPaths, path => Assert.Equal(TestEnvironment.AppDataDirectory, Path.GetDirectoryName(path)));
    }
}
