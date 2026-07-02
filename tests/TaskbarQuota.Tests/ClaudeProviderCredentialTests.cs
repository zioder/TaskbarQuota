using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using TaskbarQuota.Controls;
using TaskbarQuota.Usage;
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

    [Fact]
    public void BuildResult_ClaudeNewUsageFields_AreDisplayed()
    {
        using var doc = JsonDocument.Parse("""
        {
          "five_hour": { "utilization": 9, "resets_at": "2026-06-02T12:00:00.000Z" },
          "seven_day": { "utilization": 21, "resets_at": "2026-06-07T12:00:00.000Z" },
          "seven_day_oauth_apps": { "utilization": 42 },
          "seven_day_omelette": { "utilization": 26 },
          "seven_day_cowork": { "utilization": 11 },
          "extra_usage": {
            "is_enabled": true,
            "used_credits": 500,
            "monthly_limit": 1000,
            "currency": "USD"
          }
        }
        """);

        var result = ClaudeProvider.BuildResultForTesting(
            doc.RootElement,
            new ClaudeProvider.Credentials("token", "pro", "default_claude_ai"));

        Assert.Equal(9, result.Usage.Primary.UsedPercent);
        Assert.Equal(21, result.Usage.Secondary?.UsedPercent);
        Assert.Contains(result.Usage.ExtraRateWindows, w => w.Id == "claude-oauth-apps" && w.Window.UsedPercent == 42);
        Assert.Contains(result.Usage.ExtraRateWindows, w => w.Id == "claude-design" && w.Window.UsedPercent == 26);
        Assert.Contains(result.Usage.ExtraRateWindows, w => w.Id == "claude-routines" && w.Window.UsedPercent == 11);
        Assert.Equal("$5.00 / $10.00", result.Usage.Cost?.Display);
        Assert.Equal(5.0, result.Usage.AdditionalUsage?.SpentUsd);
        Assert.Equal(10.0, result.Usage.AdditionalUsage?.BudgetUsd);
    }

    [Fact]
    public void BuildResult_FableScopedWeeklyLimit_IsSeparateExtraWeeklyRow()
    {
        using var doc = JsonDocument.Parse("""
        {
          "five_hour": { "utilization": 10, "resets_at": "2099-01-01T00:00:00.000Z" },
          "seven_day": { "utilization": 20, "resets_at": "2099-01-08T00:00:00.000Z" },
          "seven_day_sonnet": null,
          "limits": [
            { "kind": "session", "group": "session", "percent": 10, "resets_at": "2099-01-01T00:00:00.000Z" },
            { "kind": "weekly_all", "group": "weekly", "percent": 20, "resets_at": "2099-01-08T00:00:00.000Z" },
            {
              "kind": "weekly_scoped",
              "group": "weekly",
              "percent": 7,
              "resets_at": "2099-01-08T00:00:00.000Z",
              "scope": { "model": { "display_name": "Fable", "id": null }, "surface": null }
            }
          ]
        }
        """);

        var result = ClaudeProvider.BuildResultForTesting(
            doc.RootElement,
            new ClaudeProvider.Credentials("token", "max", "default_claude_ai"));

        var fable = Assert.Single(result.Usage.ExtraRateWindows, w => w.Id == "claude-fable");
        Assert.Equal("Fable", fable.Title);
        Assert.Equal(7, fable.Window.UsedPercent);
        Assert.Equal(10080, fable.Window.WindowMinutes);
        Assert.Null(result.Usage.ModelSpecific);
    }

    [Fact]
    public void WidgetRows_ForClaude_FableIsVisibleByDefaultAndDoesNotReplaceCoreRows()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var usage = new UsageSnapshot(new RateWindow(10))
            {
                Secondary = new RateWindow(20),
            };
            usage.ExtraRateWindows.Add(new NamedRateWindow("claude-fable", "Fable", new RateWindow(7)));
            var result = UsageResult.Success(ProviderId.Claude, new TestProvider(), new ProviderFetchResult(usage, "oauth"));

            var defaultLabels = WidgetSummary.BuildRowLabelsForTesting(result, usage);
            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Claude, WidgetSettingsService.RowExtra, false);
            var disabledLabels = WidgetSummary.BuildRowLabelsForTesting(result, usage);

            Assert.Equal(new[] { "Session", "Weekly", "Fable" }, defaultLabels);
            Assert.Equal(new[] { "Session", "Weekly" }, disabledLabels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void BuildResult_UncappedExtraUsage_DoesNotCreateFakePrimaryWindow()
    {
        using var doc = JsonDocument.Parse("""
        {
          "extra_usage": {
            "is_enabled": true,
            "used_credits": 927,
            "currency": "USD"
          }
        }
        """);

        var result = ClaudeProvider.BuildResultForTesting(
            doc.RootElement,
            new ClaudeProvider.Credentials("token", "enterprise", "default_claude_ai"));

        Assert.False(result.Usage.HasPrimaryWindow);
        Assert.Equal("Spend limit", result.Usage.Cost?.Label);
        Assert.Equal("$9.27", result.Usage.Cost?.Display);
    }

    [Fact]
    public void ParseRetryAfter_Delta_ReturnsFutureTime()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));

        var before = DateTimeOffset.Now.AddMinutes(9);
        var parsed = ClaudeProvider.ParseRetryAfter(response);
        var after = DateTimeOffset.Now.AddMinutes(11);

        Assert.NotNull(parsed);
        Assert.InRange(parsed.Value, before, after);
    }

    private sealed class TestProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.Claude;
        public string DisplayName => "Claude Code";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
