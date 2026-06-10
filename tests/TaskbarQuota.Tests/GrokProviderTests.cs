using System;
using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class GrokProviderTests
{
    [Fact]
    public void ReadCredentials_PrefersOidcEntryOverLegacySession()
    {
        const string json = """
        {
          "https://accounts.x.ai/sign-in::old": { "key": "legacy-token", "email": "legacy@example.com" },
          "https://auth.x.ai::client-id": { "key": "oidc-token", "email": "user@example.com", "team_id": "team-1", "auth_mode": "oidc" }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var creds = GrokProvider.ReadCredentials(doc.RootElement);

        Assert.Equal("oidc-token", creds.AccessToken);
        Assert.Equal("user@example.com", creds.Email);
        Assert.Equal("team-1", creds.TeamId);
        Assert.Equal("oidc", creds.AuthMode);
    }

    [Fact]
    public void ReadCredentials_SkipsEntriesWithoutAKey()
    {
        const string json = """
        {
          "https://auth.x.ai::stale": { "email": "no-token@example.com" },
          "https://accounts.x.ai/sign-in": { "key": "session-token", "email": "session@example.com", "auth_mode": "session" }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var creds = GrokProvider.ReadCredentials(doc.RootElement);

        Assert.Equal("session-token", creds.AccessToken);
        Assert.Equal("session@example.com", creds.Email);
    }

    [Fact]
    public void ParseBilling_ComputesPercentAndReset()
    {
        const string json = """
        {
          "config": {
            "monthlyLimit": { "val": 15000 },
            "used": { "val": 15 },
            "onDemandCap": { "val": 0 },
            "billingPeriodEnd": "2026-07-01T00:00:00+00:00"
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var snapshot = GrokProvider.ParseBilling(doc.RootElement);

        Assert.Equal(0.1, snapshot.UsedPercent, 3);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T00:00:00+00:00"), snapshot.ResetAt);
    }

    [Fact]
    public void ParseBilling_ClampsOverLimitUsage()
    {
        const string json = """
        { "config": { "monthlyLimit": { "val": 100 }, "used": { "val": 250 }, "billingPeriodEnd": "2026-07-01T00:00:00Z" } }
        """;
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(100, GrokProvider.ParseBilling(doc.RootElement).UsedPercent);
    }

    [Fact]
    public void ParseBilling_ThrowsWhenShapeChanged()
    {
        using var doc = JsonDocument.Parse("""{ "config": { "used": { "val": 1 } } }""");

        var ex = Assert.Throws<ProviderException>(() => GrokProvider.ParseBilling(doc.RootElement));
        Assert.Equal(ProviderErrorKind.Parse, ex.Kind);
    }

    [Theory]
    [InlineData("SuperGrok")]
    [InlineData("SuperGrok Heavy")]
    [InlineData("X Premium+")]
    public void PlanFromSettings_ReturnsDisplayNameVerbatim(string display)
    {
        string json = $$"""{ "subscription_tier_display": "{{display}}", "release_channel": "stable" }""";
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(display, GrokProvider.PlanFromSettings(doc.RootElement));
    }

    [Fact]
    public void PlanFromSettings_ReturnsNullWhenAbsent()
    {
        using var doc = JsonDocument.Parse("""{ "release_channel": "stable" }""");

        Assert.Null(GrokProvider.PlanFromSettings(doc.RootElement));
    }
}
