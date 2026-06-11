using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class DevinProviderTests
{
    private static JsonElement UserStatus(string planStatusJson)
    {
        using var doc = JsonDocument.Parse($$"""{ "planStatus": {{planStatusJson}} }""");
        return doc.RootElement.Clone();
    }

    [Fact]
    public void TryBuildUsage_WeeklyIsPrimaryDailyIsSecondary()
    {
        var status = UserStatus("""
        {
          "planInfo": { "planName": "Max" },
          "dailyQuotaRemainingPercent": 100,
          "weeklyQuotaRemainingPercent": 40,
          "overageBalanceMicros": "964220000",
          "weeklyQuotaResetAtUnix": "1774166400"
        }
        """);

        Assert.True(DevinProvider.TryBuildUsage(status, out var usage));
        Assert.Equal(60, usage.Primary.UsedPercent);   // weekly is the headline
        Assert.NotNull(usage.Secondary);
        Assert.Equal(0, usage.Secondary!.UsedPercent);  // daily is secondary
        Assert.Equal("Max", usage.LoginMethod);
        Assert.NotNull(usage.Cost);
        Assert.Equal("Extra Usage", usage.Cost!.Label);
        Assert.Equal(964.22, usage.Cost.Amount, 2);
    }

    [Fact]
    public void TryBuildUsage_HideDailyQuota_OnlyWeekly()
    {
        var status = UserStatus("""
        {
          "planInfo": { "hideDailyQuota": true },
          "dailyQuotaRemainingPercent": 0,
          "weeklyQuotaResetAtUnix": "1774166400"
        }
        """);

        Assert.True(DevinProvider.TryBuildUsage(status, out var usage));
        // With daily hidden and no weekly percent, the daily field stands in for the weekly figure.
        Assert.Equal(100, usage.Primary.UsedPercent);
        Assert.Null(usage.Secondary);
    }

    [Fact]
    public void TryBuildUsage_ReturnsFalseWhenNoQuota()
    {
        var status = UserStatus("""{ "planInfo": { "planName": "Pro" } }""");
        Assert.False(DevinProvider.TryBuildUsage(status, out _));
    }

    [Theory]
    [InlineData("windsurf_api_key = \"abc123\"", "abc123")]
    [InlineData("windsurf_api_key = 'abc123'", "abc123")]
    [InlineData("windsurf_api_key = bareword # comment", "bareword")]
    [InlineData("api_server_url = \"x\"", null)]
    public void ReadTomlString_ParsesKey(string line, string? expected)
    {
        Assert.Equal(expected, DevinProvider.ReadTomlString(line, "windsurf_api_key"));
    }

    [Fact]
    public void ReadAppApiKeyFromJson_ReadsApiKey()
    {
        Assert.Equal("token-1", DevinProvider.ReadAppApiKeyFromJson("""{ "apiKey": "token-1" }"""));
        Assert.Null(DevinProvider.ReadAppApiKeyFromJson("""{ "other": "x" }"""));
        Assert.Null(DevinProvider.ReadAppApiKeyFromJson("not json"));
    }

    // Guards against the provider being defined but never wired into the registry (the original
    // "Provider not available yet" bug): every ProviderId must resolve to a registered provider.
    [Theory]
    [InlineData(ProviderId.Devin)]
    [InlineData(ProviderId.Grok)]
    [InlineData(ProviderId.Antigravity)]
    public void UsageService_RegistersProvider(ProviderId id)
    {
        Assert.NotNull(new UsageService().Get(id));
    }
}
