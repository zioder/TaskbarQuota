using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class AntigravityProviderTests
{
    // Free / starter tiers expose only a weekly bucket per group.
    private const string StarterSummary = """
    {"response":{"groups":[
      {"displayName":"Gemini Models","buckets":[
        {"bucketId":"gemini-weekly","displayName":"Weekly Limit","window":"weekly","remainingFraction":0.4,"resetTime":"2026-06-18T20:29:05Z"}]},
      {"displayName":"Claude and GPT models","buckets":[
        {"bucketId":"3p-weekly","displayName":"Weekly Limit","window":"weekly","remainingFraction":1,"resetTime":"2026-06-18T20:29:05Z"}]}
    ]}}
    """;

    // Plus / Pro / Ultra tiers add a 5-hour bucket to each group.
    private const string ProSummary = """
    {"response":{"groups":[
      {"displayName":"Gemini Models","buckets":[
        {"bucketId":"gemini-weekly","displayName":"Weekly Limit","window":"weekly","remainingFraction":0.06,"resetTime":"2026-06-18T20:29:05Z"},
        {"bucketId":"gemini-5h","displayName":"Five Hour Limit","window":"five_hour","remainingFraction":0.31,"resetTime":"2026-06-12T01:00:00Z"}]},
      {"displayName":"Claude and GPT models","buckets":[
        {"bucketId":"3p-weekly","displayName":"Weekly Limit","window":"weekly","remainingFraction":1,"resetTime":"2026-06-18T20:29:05Z"},
        {"bucketId":"3p-5h","displayName":"Five Hour Limit","window":"five_hour","remainingFraction":1,"resetTime":"2026-06-12T01:00:00Z"}]}
    ]}}
    """;

    [Fact]
    public void TryParseQuotaSummary_StarterPlan_HasWeeklyOnly()
    {
        Assert.True(AntigravityProvider.TryParseQuotaSummary(StarterSummary, out var snap));
        Assert.Equal(60, snap.Primary.UsedPercent, 3);       // Gemini weekly (1 - 0.4)
        Assert.Null(snap.ModelSpecific);                     // no Gemini 5h
        Assert.NotNull(snap.Secondary);
        Assert.Equal(0, snap.Secondary!.UsedPercent, 3);     // Non-Gemini weekly (1 - 1)
        Assert.Null(snap.Monthly);                           // no Non-Gemini 5h
    }

    [Fact]
    public void TryParseQuotaSummary_ProPlan_HasWeeklyAndFiveHourForBothGroups()
    {
        Assert.True(AntigravityProvider.TryParseQuotaSummary(ProSummary, out var snap));
        Assert.Equal(94, snap.Primary.UsedPercent, 3);          // Gemini weekly
        Assert.NotNull(snap.ModelSpecific);
        Assert.Equal(69, snap.ModelSpecific!.UsedPercent, 3);   // Gemini 5h
        Assert.NotNull(snap.Secondary);
        Assert.Equal(0, snap.Secondary!.UsedPercent, 3);        // Non-Gemini weekly
        Assert.NotNull(snap.Monthly);
        Assert.Equal(0, snap.Monthly!.UsedPercent, 3);          // Non-Gemini 5h
    }

    [Fact]
    public void TryParseQuotaSummary_GarbageJson_ReturnsFalse()
    {
        Assert.False(AntigravityProvider.TryParseQuotaSummary("{\"foo\":1}", out _));
    }

    [Theory]
    [InlineData("agy.exe", "\"C:\\Users\\ziedk\\AppData\\Local\\agy\\bin\\agy.exe\"")]
    [InlineData("antigravity-cli.exe", "\"C:\\Program Files\\Antigravity\\antigravity-cli.exe\"")]
    [InlineData("powershell.exe", "C:\\Users\\ziedk\\AppData\\Local\\agy\\bin\\agy.exe")]
    [InlineData("bash", "/home/user/.local/bin/agy")]
    public void IsAntigravityCliProcess_DetectsTerminalOnlyAgyHosts(string name, string commandLine)
    {
        Assert.True(AntigravityProvider.IsAntigravityCliProcess(name, commandLine));
    }

    [Theory]
    [InlineData("language_server.exe", "\"C:\\tools\\language_server.exe\"")]
    [InlineData("powershell.exe", "claude")]
    [InlineData("cmd.exe", "grok")]
    public void IsAntigravityCliProcess_IgnoresUnrelatedProcesses(string name, string commandLine)
    {
        Assert.False(AntigravityProvider.IsAntigravityCliProcess(name, commandLine));
    }
}
