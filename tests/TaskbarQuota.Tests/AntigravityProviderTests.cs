using TaskbarQuota.Controls;
using TaskbarQuota.Services;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
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

    // Paid Pro plans with separate Gemini 4 Argon quota add a third group.
    private const string ProWithArgonSummary = """
    {"response":{"groups":[
      {"displayName":"Gemini Models","buckets":[
        {"bucketId":"gemini-weekly","displayName":"Weekly Limit Remaining","window":"weekly","remainingFraction":0.8,"resetTime":"2026-11-20T23:28:03Z"},
        {"bucketId":"gemini-5h","displayName":"Five Hour Limit Remaining","window":"5h","remainingFraction":0.6,"resetTime":"2026-11-14T04:28:03Z"}]},
      {"displayName":"Claude and GPT models","buckets":[
        {"bucketId":"3p-weekly","displayName":"Weekly Limit Remaining","window":"weekly","remainingFraction":1,"resetTime":"2026-11-21T03:55:36Z"},
        {"bucketId":"3p-5h","displayName":"Five Hour Limit Remaining","window":"5h","remainingFraction":1,"resetTime":"2026-11-14T08:55:36Z"}]},
      {"displayName":"Gemini 4 Argon","buckets":[
        {"bucketId":"gemini-argon-wtu-weekly","displayName":"Gemini 4 Argon Weekly Limit","window":"weekly","remainingFraction":0.3,"resetTime":"2026-11-21T00:05:48Z"}]}
    ]}}
    """;

    [Fact]
    public void TryParseQuotaSummary_ProPlanWithArgon_KeepsGeminiWeeklyAndAddsArgonExtra()
    {
        Assert.True(AntigravityProvider.TryParseQuotaSummary(ProWithArgonSummary, out var snap));
        Assert.Equal(20, snap.Primary.UsedPercent, 3);          // Gemini weekly (1 - 0.8), NOT overwritten by Argon
        Assert.NotNull(snap.ModelSpecific);
        Assert.Equal(40, snap.ModelSpecific!.UsedPercent, 3);   // Gemini 5h (1 - 0.6)
        Assert.NotNull(snap.Secondary);
        Assert.Equal(0, snap.Secondary!.UsedPercent, 3);        // Non-Gemini weekly
        Assert.NotNull(snap.Monthly);
        Assert.Equal(0, snap.Monthly!.UsedPercent, 3);          // Non-Gemini 5h

        var argon = Assert.Single(snap.ExtraRateWindows, AntigravityProvider.IsSummaryExtraWindow);
        Assert.Equal("gemini-argon-wtu-weekly", argon.Id);
        Assert.Equal("Argon Weekly", argon.Title);
        Assert.Equal(70, argon.Window.UsedPercent, 3);          // Argon weekly (1 - 0.3)
        Assert.Equal(QuotaWindowLength.Weekly, argon.Window.WindowMinutes);
    }

    [Fact]
    public void ParseUserStatus_SeparatesArgonAndOrchestratorArgonFromGeminiAndNonGemini()
    {
        const string json = """
        {
          "userStatus": {
            "cascadeModelConfigData": {
              "clientModelConfigs": [
                {"label": "Gemini 4 Hydrogen (High)", "quotaInfo": {"remainingFraction": 0.9, "resetTime": "2026-11-14T04:28:03Z"}},
                {"label": "Gemini 4 Argon (High)", "quotaInfo": {"remainingFraction": 0.5, "resetTime": "2026-11-21T00:05:48Z"}},
                {"label": "Orchestrator (Argon)", "quotaInfo": {"remainingFraction": 0.4, "resetTime": "2026-11-21T00:05:48Z"}},
                {"label": "Claude Opus 5.5 (High)", "quotaInfo": {"remainingFraction": 1.0, "resetTime": "2026-11-14T09:15:57Z"}}
              ]
            }
          }
        }
        """;

        var snap = AntigravityProvider.ParseUserStatusForTesting(json);

        Assert.Equal(10, snap.Primary.UsedPercent, 3);          // Hydrogen (1 - 0.9)
        Assert.NotNull(snap.Secondary);
        Assert.Equal(0, snap.Secondary!.UsedPercent, 3);        // Claude only, NOT Orchestrator (Argon)
        var argon = Assert.Single(snap.ExtraRateWindows, AntigravityProvider.IsSummaryExtraWindow);
        Assert.Equal("argon-weekly", argon.Id);
        Assert.Equal("Argon Weekly", argon.Title);
        Assert.Equal(60, argon.Window.UsedPercent, 3);          // max Argon (1 - 0.4)
    }

    [Fact]
    public void WidgetRows_ForAntigravity_ShowsArgonSummaryExtraByDefaultAndRespectsRowExtraToggle()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            Assert.True(AntigravityProvider.TryParseQuotaSummary(ProWithArgonSummary, out var usage));
            // Simulate a legacy per-model window alongside the summary extra window; the widget only renders summary extras.
            usage.ExtraRateWindows.Add(new NamedRateWindow("model-0", "Gemini 4 Hydrogen (High)", new RateWindow(10)));
            var result = UsageResult.Success(ProviderId.Antigravity, new AntigravityProvider(), new ProviderFetchResult(usage, "local"));

            var defaultLabels = WidgetSummary.BuildRowLabelsForTesting(result, usage);
            Assert.Equal(new[] { "Weekly", "5h", "Weekly", "5h", "Argon Weekly" }, defaultLabels);
            Assert.Equal(5, WidgetSummary.CountRenderedRows(result));

            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Antigravity, WidgetSettingsService.RowExtra, false);
            var hiddenExtraLabels = WidgetSummary.BuildRowLabelsForTesting(result, usage);
            Assert.Equal(new[] { "Weekly", "5h", "Weekly", "5h" }, hiddenExtraLabels);
            Assert.Equal(4, WidgetSummary.CountRenderedRows(result));
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
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
