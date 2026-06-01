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
}
