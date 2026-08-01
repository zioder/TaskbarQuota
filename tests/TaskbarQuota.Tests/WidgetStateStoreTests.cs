using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public sealed class WidgetStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "TaskbarQuota.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFilesUseSafeDefaults()
    {
        var store = new WidgetStateStore(_directory);

        Assert.Equal(WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen, store.LoadVisibilityMode());
        Assert.False(store.LoadKeepVisibleWhileBackgroundCliAgentRunning());
        Assert.Null(store.LoadLastProvider());
    }

    [Fact]
    public void ValuesRoundTrip()
    {
        var store = new WidgetStateStore(_directory);

        Assert.True(store.SaveVisibilityMode(WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse));
        Assert.True(store.SaveKeepVisibleWhileBackgroundCliAgentRunning(true));
        Assert.True(store.SaveLastProvider(ProviderId.Claude));

        var reloaded = new WidgetStateStore(_directory);
        Assert.Equal(WidgetVisibilityMode.ShowOnlyWhileSupportedAiToolIsInUse, reloaded.LoadVisibilityMode());
        Assert.True(reloaded.LoadKeepVisibleWhileBackgroundCliAgentRunning());
        Assert.Equal(ProviderId.Claude, reloaded.LoadLastProvider());
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_directory),
            path => Path.GetFileName(path).Contains("override", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("999")]
    public void InvalidVisibilityModeFallsBack(string value)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "widget-visibility-policy.txt"), value);

        var store = new WidgetStateStore(_directory);

        Assert.Equal(WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen, store.LoadVisibilityMode());
    }

    [Fact]
    public void InvalidProviderIsIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "last-widget-provider.txt"), "UnknownProvider");

        Assert.Null(new WidgetStateStore(_directory).LoadLastProvider());
    }

    [Fact]
    public void WriteFailureIsReportedWithoutThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "This path is intentionally a file.");
        var store = new WidgetStateStore(_directory);

        Assert.False(store.SaveVisibilityMode(WidgetVisibilityMode.AlwaysShow));
        Assert.False(store.SaveKeepVisibleWhileBackgroundCliAgentRunning(true));
        Assert.False(store.SaveLastProvider(ProviderId.Codex));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
            else if (File.Exists(_directory))
                File.Delete(_directory);
        }
        catch
        {
            // Test cleanup is best effort.
        }
    }
}
