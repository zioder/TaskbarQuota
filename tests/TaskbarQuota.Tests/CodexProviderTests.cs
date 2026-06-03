using System.Text.Json;
using TaskbarQuota.Usage.Providers;
using TaskbarQuota.Controls;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class CodexProviderTests
{
    [Theory]
    [InlineData("pro", "Pro 20x")]
    [InlineData("prolite", "Pro 5x")]
    [InlineData("pro_lite", "Pro 5x")]
    public void BuildResult_ProPlans_UseCodexMultiplierLabels(string planType, string expected)
    {
        using var doc = JsonDocument.Parse(CodexUsageJson(planType));

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Equal(expected, result.Usage.LoginMethod);
    }

    [Fact]
    public void BuildResult_ProPlan_UsesBaseSessionAndWeeklyInsteadOfSparkWindows()
    {
        using var doc = JsonDocument.Parse(CodexUsageJson("pro", includeSpark: true));

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Equal(10, result.Usage.Primary.UsedPercent);
        Assert.Equal(18000 / 60, result.Usage.Primary.WindowMinutes);
        Assert.Equal(20, result.Usage.Secondary?.UsedPercent);
        Assert.Equal(604800 / 60, result.Usage.Secondary?.WindowMinutes);
        Assert.DoesNotContain(result.Usage.ExtraRateWindows, w => w.Title.Contains("Spark"));
    }

    [Fact]
    public void BuildResult_NonProPlan_HidesSparkWindows()
    {
        using var doc = JsonDocument.Parse(CodexUsageJson("plus", includeSpark: true));

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Equal("Plus", result.Usage.LoginMethod);
        Assert.DoesNotContain(result.Usage.ExtraRateWindows, w => w.Title.Contains("Spark"));
    }

    [Fact]
    public void WidgetRows_ForCodex_ShowSessionAndWeeklyWhenSparkLimitExists()
    {
        using var doc = JsonDocument.Parse(CodexUsageJson("pro", includeSpark: true));
        var fetch = CodexProvider.BuildResult(doc.RootElement);
        var result = UsageResult.Success(ProviderId.Codex, new TestProvider(), fetch);

        var labels = WidgetSummary.BuildRowLabelsForTesting(result, fetch.Usage);

        Assert.Equal(new[] { "Session", "Weekly" }, labels);
    }

    private static string CodexUsageJson(string planType, bool includeSpark = false)
    {
        var additional = includeSpark
            ? """
              ,
                "additional_rate_limits": [
                  {
                    "limit_name": "GPT-5.3-Codex-Spark",
                    "rate_limit": {
                      "primary_window": {
                        "used_percent": 25,
                        "limit_window_seconds": 18000,
                        "reset_at": 1893456000
                      },
                      "secondary_window": {
                        "used_percent": 40,
                        "limit_window_seconds": 604800,
                        "reset_at": 1893542400
                      }
                    }
                  }
                ]
              """
            : string.Empty;

        return $$"""
               {
                 "plan_type": "{{planType}}",
                 "rate_limit": {
                   "primary_window": {
                     "used_percent": 10,
                     "limit_window_seconds": 18000,
                     "reset_at": 1893456000
                   },
                   "secondary_window": {
                     "used_percent": 20,
                     "limit_window_seconds": 604800,
                     "reset_at": 1893542400
                   }
                 }
                 {{additional}}
               }
               """;
    }

    private sealed class TestProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.Codex;
        public string DisplayName => "Codex";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
