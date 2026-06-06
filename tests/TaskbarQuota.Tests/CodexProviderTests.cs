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
    public void BuildResult_ProPlan_SurfacesSparkSessionAndWeeklyWindows()
    {
        using var doc = JsonDocument.Parse(CodexUsageJson("pro", includeSpark: true));

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Contains(result.Usage.ExtraRateWindows, w => w.Title == "Spark Session" && w.Window.UsedPercent == 25);
        Assert.Contains(result.Usage.ExtraRateWindows, w => w.Title == "Spark Weekly" && w.Window.UsedPercent == 40);
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
    public void WidgetRows_ForCodex_HidesExtraRowsByDefault()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var result = CodexWidgetResultWithExtraRows();

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.Equal(new[] { "Session", "Weekly" }, labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForCodex_ShowsExtraRowsWhenEnabled()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Codex, WidgetSettingsService.RowExtra, true);
            var result = CodexWidgetResultWithExtraRows();

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.Equal(new[] { "Session", "Weekly", "Spark Session", "Spark Weekly" }, labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
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

    private static UsageResult CodexWidgetResultWithExtraRows()
    {
        var result = UsageResult.Success(ProviderId.Codex, new TestProvider(), new ProviderFetchResult(
            new UsageSnapshot(new RateWindow(10))
            {
                Secondary = new RateWindow(20),
                LoginMethod = "Pro 20x",
            },
            "oauth"));
        result.Fetch!.Usage.ExtraRateWindows.Add(new NamedRateWindow("Spark-session", "Spark Session", new RateWindow(25)));
        result.Fetch!.Usage.ExtraRateWindows.Add(new NamedRateWindow("Spark-weekly", "Spark Weekly", new RateWindow(40)));
        return result;
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
