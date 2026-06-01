using TaskbarQuota;

namespace TaskbarQuota.Tests;

public class AppStartupTests
{
    [Fact]
    public void IsWidgetStartup_WhenActivationArgumentsContainStartupFlag_ReturnsTrue()
    {
        Assert.True(App.IsWidgetStartup("--startup-widget"));
    }

    [Fact]
    public void IsWidgetStartup_WhenCommandLineArgumentsContainStartupFlag_ReturnsTrue()
    {
        Assert.True(App.IsWidgetStartup(null, ["TaskbarQuota.exe", "--startup-widget"]));
    }

    [Fact]
    public void IsWidgetStartup_WhenNoStartupFlag_ReturnsFalse()
    {
        Assert.False(App.IsWidgetStartup(null, ["TaskbarQuota.exe"]));
    }
}
