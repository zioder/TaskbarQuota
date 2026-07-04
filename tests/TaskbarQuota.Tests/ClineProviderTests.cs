using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class ClineProviderTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void TryBuildUsage_MapsFiveHourWeeklyMonthly()
    {
        var root = Parse("""
        {
          "data": {
            "limits": [
              { "type": "five_hour", "percentUsed": 0 },
              { "type": "weekly", "percentUsed": 17, "resetsAt": "2026-07-06T17:46:05Z" },
              { "type": "monthly", "percentUsed": 8, "resetsAt": "2026-07-29T17:46:05Z" }
            ]
          },
          "success": true
        }
        """);

        Assert.True(ClineAccount.TryBuildUsage(root, out var usage));
        Assert.Equal(0, usage.Primary.UsedPercent);      // 5-hour is the headline window
        Assert.NotNull(usage.Secondary);
        Assert.Equal(17, usage.Secondary!.UsedPercent);  // weekly
        Assert.NotNull(usage.Monthly);
        Assert.Equal(8, usage.Monthly!.UsedPercent);     // monthly
        Assert.NotNull(usage.Secondary.ResetAt);
    }

    [Fact]
    public void TryBuildUsage_FallsBackToWeeklyWhenNoFiveHour()
    {
        var root = Parse("""
        { "data": { "limits": [
          { "type": "weekly", "percentUsed": 40 },
          { "type": "monthly", "percentUsed": 12 }
        ] } }
        """);

        Assert.True(ClineAccount.TryBuildUsage(root, out var usage));
        Assert.Equal(40, usage.Primary.UsedPercent);
        Assert.Equal(12, usage.Monthly!.UsedPercent);
    }

    [Fact]
    public void TryBuildUsage_ReturnsFalseWhenNoLimits()
    {
        Assert.False(ClineAccount.TryBuildUsage(Parse("""{ "data": { "limits": [] } }"""), out _));
        Assert.False(ClineAccount.TryBuildUsage(Parse("""{ "success": false }"""), out _));
    }

    [Fact]
    public void ReadProviderAuth_PrefersRequestedKey()
    {
        var providers = Parse("""
        {
          "cline-pass": {
            "settings": { "auth": {
              "accessToken": "workos:aaa",
              "refreshToken": "r1",
              "expiresAt": 1782756825000,
              "accountId": "usr-123"
            } }
          }
        }
        """);

        var auth = ClineAccount.ReadProviderAuth(providers, "cline-pass");
        Assert.NotNull(auth);
        Assert.Equal("workos:aaa", auth!.AccessToken);
        Assert.Equal("r1", auth.RefreshToken);
        Assert.Equal("usr-123", auth.AccountId);
        Assert.Equal(1782756825000, auth.ExpiresAt!.Value.ToUnixTimeMilliseconds());
        Assert.Null(ClineAccount.ReadProviderAuth(providers, "cline"));
    }

    [Fact]
    public void ResolveConfiguredProviderKey_ReturnsSubscriptionWhenOnlyClinePassConfigured()
    {
        var providers = Parse("""
        {
          "cline-pass": {
            "settings": { "auth": { "accessToken": "workos:aaa" } }
          }
        }
        """);

        Assert.Equal(ClineAccount.SubscriptionKey, ClineAccount.ResolveConfiguredProviderKey(providers));
    }

    [Fact]
    public void ResolveConfiguredProviderKey_ReturnsUsageBillingWhenOnlyClineConfigured()
    {
        var providers = Parse("""
        {
          "cline": {
            "settings": { "auth": { "accessToken": "workos:bbb" } }
          }
        }
        """);

        Assert.Equal(ClineAccount.UsageBillingKey, ClineAccount.ResolveConfiguredProviderKey(providers));
    }

    [Fact]
    public void ResolveConfiguredProviderKey_ReturnsNullWhenBothSurfacesConfigured()
    {
        var providers = Parse("""
        {
          "cline-pass": {
            "settings": { "auth": { "accessToken": "workos:aaa" } }
          },
          "cline": {
            "settings": { "auth": { "accessToken": "workos:bbb" } }
          }
        }
        """);

        Assert.Null(ClineAccount.ResolveConfiguredProviderKey(providers));
    }

    [Fact]
    public void ReadJwtEmail_DecodesEmailClaim()
    {
        // {"email":"user@example.com"} base64url, no padding, as a middle JWT segment.
        const string payload = "eyJlbWFpbCI6InVzZXJAZXhhbXBsZS5jb20ifQ";
        Assert.Equal("user@example.com", ClineAccount.ReadJwtEmail($"workos:header.{payload}.sig"));
        Assert.Null(ClineAccount.ReadJwtEmail("not-a-jwt"));
    }

    // Guards against a provider being defined but never wired into the registry.
    [Theory]
    [InlineData(ProviderId.Cline)]
    [InlineData(ProviderId.ClinePass)]
    public void UsageService_RegistersClineSurfaces(ProviderId id)
    {
        Assert.NotNull(new UsageService().Get(id));
    }
}
