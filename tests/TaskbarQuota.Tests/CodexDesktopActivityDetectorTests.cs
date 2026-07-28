using TaskbarQuota.ActiveApp;

namespace TaskbarQuota.Tests;

public sealed class CodexDesktopActivityDetectorTests
{
    [Theory]
    [InlineData("cursor-grab active:cursor-grabbing", true)]
    [InlineData("active:cursor-grabbing cursor-grab other", true)]
    [InlineData("cursor-grab", false)]
    [InlineData("active:cursor-grabbing", false)]
    [InlineData(null, false)]
    public void IsTaskRowClass_RequiresBothStructuralTokens(string? className, bool expected)
        => Assert.Equal(expected, CodexDesktopActivityDetector.IsTaskRowClass(className));

    [Theory]
    [InlineData("icon-xs shrink-0", true)]
    [InlineData("shrink-0 icon-xs", true)]
    [InlineData("icon-2xs text-token-description-foreground no-drag shrink-0", false)]
    [InlineData("icon-xs no-drag shrink-0", false)]
    [InlineData("icon-xs", false)]
    [InlineData(null, false)]
    public void IsRunningTaskStatusClass_SeparatesRunningFromIdleRows(
        string? className,
        bool expected)
        => Assert.Equal(expected, CodexDesktopActivityDetector.IsRunningTaskStatusClass(className));

    [Theory]
    [InlineData("Stop", "size-token-button-composer bg-token-foreground", true)]
    [InlineData("Stop generating", "size-token-button-composer", true)]
    [InlineData("Detener", "size-token-button-composer bg-token-foreground", true)]
    [InlineData("Detener generación", "size-token-button-composer", true)]
    [InlineData("Stop", "sidebar-item", false)]
    [InlineData("Send", "size-token-button-composer", false)]
    [InlineData(null, "size-token-button-composer", false)]
    public void IsRunningComposerButton_RequiresStopLabelAndComposerControl(
        string? name,
        string? className,
        bool expected)
        => Assert.Equal(
            expected,
            CodexDesktopActivityDetector.IsRunningComposerButton(name, className));
}
