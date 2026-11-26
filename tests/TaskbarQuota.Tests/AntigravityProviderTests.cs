using TaskbarQuota.Controls;
using TaskbarQuota.Services;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;
using System.Text;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryParseQuotaSummary_ArgonBucketNeverReplacesGeminiInRestResponse(bool sameGroup)
    {
        const string gemini = """{"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.8}""";
        const string argon = """{"bucketId":"gemini-argon-wtu-weekly","window":"weekly","remainingFraction":0.3}""";
        const string disabled = """{"bucketId":"gemini-5h","window":"5h","remainingFraction":0.1,"disabled":true}""";
        string groups = sameGroup
            ? $$"""{"displayName":"Localized","buckets":[{{gemini}},{{argon}},{{disabled}}]}"""
            : $$"""{"displayName":"Localized","buckets":[{{gemini}},{{disabled}}]},{"displayName":"Gemini 4 Argon","buckets":[{{argon}}]}""";

        Assert.True(AntigravityProvider.TryParseQuotaSummary($$"""{"groups":[{{groups}}]}""", out var snapshot));
        Assert.Equal(20, snapshot.Primary.UsedPercent, 3);
        Assert.Null(snapshot.ModelSpecific);
        var extra = Assert.Single(snapshot.ExtraRateWindows);
        Assert.Equal("Argon Weekly", extra.Title);
        Assert.Equal(70, extra.Window.UsedPercent, 3);
    }

    [Fact]
    public void ParseUserStatus_WithOnlyArgon_LeavesGeminiUnavailable()
    {
        const string json = """
            {"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[
                {"label":"Gemini 4 Argon","quotaInfo":{"remainingFraction":0.3}}
            ]}}}
            """;

        var snapshot = AntigravityProvider.ParseUserStatusForTesting(json);

        Assert.False(snapshot.Primary.IsIncluded);
        var argon = Assert.Single(snapshot.ExtraRateWindows, AntigravityProvider.IsSummaryExtraWindow);
        Assert.Equal(70, argon.Window.UsedPercent, 3);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ParseKeyringOAuthCreds_DecodesNestedGoToken(bool unicode, bool base64)
    {
        const string json = """
        {"token":{"access_token":"test-access","refresh_token":"test-refresh","expiry":"2026-01-01T00:00:00Z"},"auth_method":"oauth"}
        """;
        var payload = base64 ? "go-keyring-base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) : json;
        var bytes = (unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes(payload + "\0");
        var creds = AntigravityProvider.ParseKeyringOAuthCreds(bytes);
        Assert.NotNull(creds);
        Assert.Equal("test-access", creds.AccessToken);
        Assert.Equal("test-refresh", creds.RefreshToken);
        Assert.Equal(2026, creds.Expiry!.Value.Year);
        Assert.Null(creds.ClientId);
    }

    [Fact]
    public void ParseKeyringOAuthCreds_PreservesFlatAndRootClientFields()
    {
        var creds = AntigravityProvider.ParseKeyringOAuthCreds(Encoding.UTF8.GetBytes(
            """{"accessToken":"test-access","refreshToken":"test-refresh","clientId":"test-client","clientSecret":"test-secret"}"""));
        Assert.Equal("test-client", creds!.ClientId);
        Assert.Equal("test-secret", creds.ClientSecret);
        creds = AntigravityProvider.ParseKeyringOAuthCreds(Encoding.UTF8.GetBytes(
            """{"token":{"access_token":"test-access"},"client_id":"root-client"}"""));
        Assert.Equal("root-client", creds!.ClientId);
    }

    [Theory]
    [InlineData(null, null, "installed-id", "installed-secret")]
    [InlineData("installed-id", null, "installed-id", "installed-secret")]
    [InlineData("installed-id", "explicit-secret", "installed-id", "explicit-secret")]
    [InlineData("other-id", null, "other-id", null)]
    public void WithInstalledOAuthClient_FillsOnlyMatchingMissingFields(
        string? id, string? secret, string? expectedId, string? expectedSecret)
    {
        var creds = new AntigravityProvider.KeyringOAuthCreds("test-access", "test-refresh", id, secret, null);
        var result = AntigravityProvider.WithInstalledOAuthClient(creds, ("installed-id", "installed-secret"));
        Assert.Equal(expectedId, result.ClientId);
        Assert.Equal(expectedSecret, result.ClientSecret);
        Assert.Equal(creds.RefreshToken, result.RefreshToken);
    }

    [Fact]
    public void ParseKeyringOAuthCreds_InvalidEnvelope_ReturnsNull()
    {
        Assert.Null(AntigravityProvider.ParseKeyringOAuthCreds(Encoding.UTF8.GetBytes("go-keyring-base64:invalid")));
        Assert.Null(AntigravityProvider.ParseKeyringOAuthCreds(Encoding.UTF8.GetBytes("[]")));
    }

    [Fact]
    public void CachedGoogleAccessToken_RequiresTheCurrentCredentialAndAUsableExpiry()
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var credential = new AntigravityProvider.KeyringOAuthCreds("access-a", "refresh-a", null, null, now);
        var cache = new AntigravityProvider.CachedGoogleAccessToken(credential, "refreshed-a", now.AddHours(1));

        Assert.Equal("refreshed-a", cache.GetFor(credential with { }, now));
        Assert.Null(cache.GetFor(credential with { AccessToken = "access-b", RefreshToken = "refresh-b" }, now));
        Assert.Null(cache.GetFor(credential, now.AddMinutes(59)));
    }

    [Fact]
    public void TryParseQuotaSummary_LiveRestSchema_UsesIdsAndSkipsDisabledFiveHour()
    {
        const string json = """
        {"groups":[{"displayName":"Localized group","buckets":[
          {"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.8977753,"resetTime":"2026-12-03T09:40:11Z"},
          {"bucketId":"gemini-5h","window":"5h","remainingFraction":0.59110105,"resetTime":"2026-11-26T14:40:11Z"}]},
          {"displayName":"Claude and GPT models","buckets":[
          {"bucketId":"3p-weekly","window":"weekly","remainingFraction":0,"resetTime":"2026-11-28T07:28:56Z"},
          {"bucketId":"3p-5h","window":"5h","remainingFraction":1,"disabled":true}]}]}
        """;
        Assert.True(AntigravityProvider.TryParseQuotaSummary(json, out var snap));
        Assert.Equal(10.22247, snap.Primary.UsedPercent, 5);
        Assert.Equal(40.889895, snap.ModelSpecific!.UsedPercent, 4);
        Assert.Equal(100, snap.Secondary!.UsedPercent);
        Assert.Null(snap.Monthly);
        Assert.NotNull(snap.Primary.ResetAt);
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
