using TaskbarQuota.Usage.Providers;
using System.Text;

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
