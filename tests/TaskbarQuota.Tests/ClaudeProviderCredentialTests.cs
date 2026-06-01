using System.Text.Json;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class ClaudeProviderCredentialTests
{
    [Fact]
    public void ReadCredentials_StaleExpiresAt_DoesNotRejectTokenLocally()
    {
        using var doc = JsonDocument.Parse("""
        {
          "claudeAiOauth": {
            "accessToken": "fresh-token",
            "refreshToken": "refresh-token",
            "expiresAt": 1,
            "subscriptionType": "pro",
            "rateLimitTier": "default_claude_ai"
          }
        }
        """);

        var credentials = ClaudeProvider.ReadCredentials(doc.RootElement);

        Assert.Equal("fresh-token", credentials.AccessToken);
        Assert.Equal("pro", credentials.SubscriptionType);
        Assert.Equal("default_claude_ai", credentials.RateLimitTier);
    }

    [Fact]
    public void BuildResult_ClaudeUtilizationOne_RemainsOnePercent()
    {
        using var doc = JsonDocument.Parse("""
        {
          "five_hour": { "utilization": 1, "resets_at": "2026-06-02T12:00:00.000Z" },
          "seven_day": { "utilization": 1, "resets_at": "2026-06-07T12:00:00.000Z" }
        }
        """);

        var result = ClaudeProvider.BuildResultForTesting(
            doc.RootElement,
            new ClaudeProvider.Credentials("token", "pro", "default_claude_ai"));

        Assert.Equal(1, result.Usage.Primary.UsedPercent);
        Assert.Equal(1, result.Usage.Secondary?.UsedPercent);
    }
}
