using System;
using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class KimiProviderTests
{
    [Fact]
    public void TryLoadCliAccessToken_ReadsFreshOfficialCliCredential()
    {
        var home = Path.Combine(Path.GetTempPath(), $"kimi-code-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, "credentials"));
        try
        {
            File.WriteAllText(Path.Combine(home, "credentials", "kimi-code.json"),
                "{\"access_token\":\"oauth-token\",\"refresh_token\":\"refresh\",\"expires_at\":2000000000}");
            Assert.Equal("oauth-token", KimiProvider.TryLoadCliAccessToken(home, DateTimeOffset.FromUnixTimeSeconds(1900000000)));
            Assert.Null(KimiProvider.TryLoadCliAccessToken(home, DateTimeOffset.FromUnixTimeSeconds(2000000000)));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void BuildCodeAPIResult_ParsesWeeklyAndRateLimit()
    {
        const string json = """
        {
          "usage": {
            "limit": "200",
            "used": "50",
            "remaining": "150",
            "resetTime": "2026-07-12T00:00:00Z"
          },
          "limits": [
            {
              "window": { "duration": 300, "timeUnit": "MINUTES" },
              "detail": { "limit": "60", "used": "20", "remaining": "40", "resetTime": "2026-07-05T18:00:00Z" }
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = KimiProvider.BuildCodeAPIResult(doc.RootElement);
        Assert.Equal("api", result.SourceLabel);
        Assert.InRange(result.Usage.Primary.UsedPercent, 32, 34);
        Assert.Equal(300, result.Usage.Primary.WindowMinutes);
        Assert.NotNull(result.Usage.Secondary);
        Assert.InRange(result.Usage.Secondary!.UsedPercent, 24, 26);
        Assert.Null(result.Usage.Secondary.WindowMinutes);
    }

    [Fact]
    public void BuildCodeAPIResult_ParsesWeeklyOnly()
    {
        const string json = """
        {
          "usage": { "limit": "100", "used": "10", "remaining": "90" }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = KimiProvider.BuildCodeAPIResult(doc.RootElement);
        Assert.InRange(result.Usage.Primary.UsedPercent, 9, 11);
        Assert.Null(result.Usage.Secondary);
    }

    [Fact]
    public void ParseWebUsageResponse_FindsWeeklyAndRateLimit()
    {
        const string json = """
        {
          "usages": [
            {
              "scope": "WEEKLY",
              "detail": { "limit": "200", "used": "80", "remaining": "120" },
              "limits": [
                {
                  "window": { "duration": 300, "timeUnit": "MINUTES" },
                  "detail": { "limit": "60", "used": "45", "remaining": "15" }
                }
              ]
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (weekly, rateLimit) = KimiProvider.ParseWebUsageResponse(doc.RootElement);
        Assert.NotNull(weekly);
        Assert.Equal("200", weekly!.Limit);
        Assert.NotNull(rateLimit);
        Assert.Equal("60", rateLimit!.Limit);
    }

    [Fact]
    public void ParseUsageDetail_HandlesStringAndNumericValues()
    {
        const string json = """
        { "limit": "500", "used": 123, "remaining": "377", "reset_time": "2026-07-12T00:00:00Z" }
        """;
        using var doc = JsonDocument.Parse(json);
        var detail = KimiProvider.ParseUsageDetail(doc.RootElement);
        Assert.NotNull(detail);
        Assert.Equal("500", detail!.Limit);
        Assert.Equal("123", detail.Used);
        Assert.Equal("377", detail.Remaining);
        Assert.Equal("2026-07-12T00:00:00Z", detail.ResetTime);
    }

    [Fact]
    public void ParseUsageDetail_HandlesSnakeCaseFields()
    {
        const string json = """
        { "limit": 100, "used": 30, "remaining": 70, "reset_at": "2026-07-10T12:00:00Z" }
        """;
        using var doc = JsonDocument.Parse(json);
        var detail = KimiProvider.ParseUsageDetail(doc.RootElement);
        Assert.NotNull(detail);
        Assert.Equal("2026-07-10T12:00:00Z", detail!.ResetTime);
    }

    [Fact]
    public void ParseSubscriptionBalance_ExtractsRatio()
    {
        const string json = """
        {
          "subscriptionBalance": {
            "feature": "FEATURE_OMNI",
            "type": "SUBSCRIPTION",
            "amountUsedRatio": 0.35,
            "expireTime": "2026-08-01T00:00:00Z"
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var balance = KimiProvider.ParseSubscriptionBalance(doc.RootElement);
        Assert.NotNull(balance);
        Assert.Equal(0.35, balance!.AmountUsedRatio);
        Assert.Equal("FEATURE_OMNI", balance.Feature);
    }

    [Fact]
    public void ParseSubscriptionBalance_ReturnsNullWhenMissing()
    {
        const string json = """{ "other": "data" }""";
        using var doc = JsonDocument.Parse(json);
        Assert.Null(KimiProvider.ParseSubscriptionBalance(doc.RootElement));
    }

    [Fact]
    public void BuildCodeAPIResult_HandlesMissingUsage()
    {
        const string json = """{ "usage": {} }""";
        using var doc = JsonDocument.Parse(json);
        // Missing limit field should result in no weekly detail
        var result = KimiProvider.BuildCodeAPIResult(doc.RootElement);
        Assert.Equal(0, result.Usage.Primary.UsedPercent);
    }

    [Fact]
    public void BuildCodeAPIResult_SetsDashboardUrl()
    {
        const string json = """
        { "usage": { "limit": "100", "used": "10" } }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = KimiProvider.BuildCodeAPIResult(doc.RootElement);
        Assert.Equal("https://www.kimi.com/code/console", result.Usage.UsageDashboardUrl);
    }

    [Fact]
    public void Provider_HasCorrectLabels()
    {
        var provider = new KimiProvider();
        Assert.Equal(ProviderId.Kimi, provider.Id);
        Assert.Equal("Kimi", provider.DisplayName);
        Assert.Equal("Session", provider.SessionLabel);
        Assert.Equal("Weekly", provider.WeeklyLabel);
    }
}
