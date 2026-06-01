using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class PlanDisplayNamesTests
{
    [Theory]
    [InlineData(ProviderId.Codex, "ChatGPT Plus", "Plus")]
    [InlineData(ProviderId.Claude, "Claude Pro", "Pro")]
    [InlineData(ProviderId.Copilot, "Copilot Student", "Student")]
    [InlineData(ProviderId.OpenCodeGo, "OpenCode Go", "Go")]
    [InlineData(ProviderId.Cursor, "Cursor Pro", "Pro")]
    public void Shorten_StripsProviderPrefix(ProviderId id, string input, string expected)
        => Assert.Equal(expected, PlanDisplayNames.Shorten(id, input));

    [Theory]
    [InlineData("OpenCode Go", "Go", true)]
    [InlineData("OpenCode Zen", "Zen", true)]
    [InlineData("GitHub Copilot", "Student", false)]
    [InlineData("Codex", "Plus", false)]
    public void ForTitle_HidesPlanWhenAlreadyInDisplayName(string displayName, string plan, bool hidden)
        => Assert.Equal(hidden ? string.Empty : plan, PlanDisplayNames.ForTitle(ProviderId.OpenCodeGo, displayName, plan));

    [Fact]
    public void ForPageHeader_OpenCodeGo_UsesOpenCodePrefixAndGoAccent()
    {
        var (primary, accent) = PlanDisplayNames.ForPageHeader(ProviderId.OpenCodeGo, "OpenCode Go", "Go");
        Assert.Equal(("OpenCode", "Go"), (primary, accent));
    }

    [Fact]
    public void ForPageHeader_OpenCodeZen_UsesOpenCodePrefixAndZenAccent()
    {
        var (primary, accent) = PlanDisplayNames.ForPageHeader(ProviderId.OpenCode, "OpenCode Zen", "Zen");
        Assert.Equal(("OpenCode", "Zen"), (primary, accent));
    }

    [Fact]
    public void ForPageHeader_OtherProviders_KeepDisplayNameAndShortPlan()
    {
        var (primary, accent) = PlanDisplayNames.ForPageHeader(ProviderId.Codex, "Codex", "ChatGPT Plus");
        Assert.Equal(("Codex", "Plus"), (primary, accent));
    }
}
