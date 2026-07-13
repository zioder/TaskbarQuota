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
    public void BuildResult_SessionAndWeekly_LeavesPrimaryUnlabeled()
    {
        using var doc = JsonDocument.Parse(CodexUsageJson("plus"));

        var result = CodexProvider.BuildResult(doc.RootElement);

        // Both windows present: primary keeps its default "Session" label (Label override stays null).
        Assert.Null(result.Usage.Primary.Label);
        Assert.NotNull(result.Usage.Secondary);
    }

    [Fact]
    public void BuildResult_WeeklyOnlyWindow_RelabelsPrimaryAsWeekly()
    {
        // OpenAI dropped the 5h session, so the API returns only the weekly window (issue #18).
        var json = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 0,
                  "limit_window_seconds": 604800,
                  "reset_at": 1893542400
                }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Equal("Weekly", result.Usage.Primary.Label);
        Assert.Equal(0, result.Usage.Primary.UsedPercent);
        Assert.True(result.Usage.HasPrimaryWindow);
        Assert.Null(result.Usage.Secondary);
    }

    [Fact]
    public void BuildResult_SessionOnlyShortWindow_KeepsSessionLabel()
    {
        // A lone sub-day window is still a real 5h session — must not be relabeled Weekly.
        var json = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 30,
                  "limit_window_seconds": 18000,
                  "reset_at": 1893456000
                }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Null(result.Usage.Primary.Label);
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
    public void BuildResult_WithResetCredits_SurfacesAvailableCountAndTimes()
    {
        using var usage = JsonDocument.Parse(CodexUsageJson("pro"));
        using var resetCredits = JsonDocument.Parse("""
            {
              "credits": [
                {
                  "status": "available",
                  "granted_at": "2026-06-12T03:43:26.144717Z",
                  "expires_at": "2026-07-12T03:43:26.144717Z"
                },
                {
                  "status": "redeemed",
                  "granted_at": "2026-06-10T03:43:26.144717Z",
                  "expires_at": "2026-07-10T03:43:26.144717Z"
                },
                {
                  "status": "available",
                  "granted_at": "2026-06-18T00:14:18.923019Z",
                  "expires_at": "2026-07-18T00:14:18.923019Z"
                }
              ],
              "available_count": 2
            }
            """);

        var result = CodexProvider.BuildResult(usage.RootElement, resetCreditsJson: resetCredits.RootElement);

        Assert.NotNull(result.Usage.ResetCredits);
        Assert.Equal(2, result.Usage.ResetCredits!.AvailableCount);
        Assert.Equal(2, result.Usage.ResetCredits.Credits.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-06-12T03:43:26.144717Z"), result.Usage.ResetCredits.Credits[0].GrantedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T00:14:18.923019Z"), result.Usage.ResetCredits.Credits[1].ExpiresAt);
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
    public void WidgetRows_ForCodex_ShowsResetCreditsByDefault()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var result = CodexWidgetResultWithResetCredits();

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.Equal(new[] { "Session", "Weekly", "Resets" }, labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForCodex_HidesResetCreditsWhenNoneAvailable()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var result = CodexWidgetResultWithResetCredits(0);

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.Equal(new[] { "Session", "Weekly" }, labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Theory]
    [InlineData("Free")]
    [InlineData("Plus")]
    [InlineData("Pro 20x")]
    [InlineData("Pro 5x")]
    public void WidgetRows_ForCodex_NormalPlansHideZeroCreditsByDefault(string plan)
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var result = CodexWidgetResultWithCredits(plan, amount: 0);

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.DoesNotContain("Credits", labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForCodex_NormalPlanShowsPositiveCreditsByDefault()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var result = CodexWidgetResultWithCredits("Plus", amount: 250);

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.Contains("Credits", labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForCodex_NormalPlanShowsCreditsWhenEnabled()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Codex, WidgetSettingsService.RowCredits, true);
            var result = CodexWidgetResultWithCredits("Plus", amount: 0);

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.Contains("Credits", labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForCodex_NormalPlanHidesPositiveCreditsWhenDisabled()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Codex, WidgetSettingsService.RowCredits, false);
            var result = CodexWidgetResultWithCredits("Plus", amount: 250);

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.DoesNotContain("Credits", labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForCodex_CreditPlansShowCreditsByDefault()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var result = CodexWidgetResultWithCredits("Business", amount: 250);

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.Contains("Credits", labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForCodex_CreditPlansHideCreditsWhenDisabled()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            WidgetSettingsService.SetRowVisibleForTesting(ProviderId.Codex, WidgetSettingsService.RowCredits, false);
            var result = CodexWidgetResultWithCredits("Business");

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.DoesNotContain("Credits", labels);
        }
        finally
        {
            WidgetSettingsService.ResetRowVisibilityForTesting();
        }
    }

    [Fact]
    public void WidgetRows_ForClaude_UsesModelSpecificLabelOverride()
    {
        WidgetSettingsService.ResetRowVisibilityForTesting();
        try
        {
            var result = UsageResult.Success(ProviderId.Claude, new TestProvider(ProviderId.Claude), new ProviderFetchResult(
                new UsageSnapshot(new RateWindow(10))
                {
                    Secondary = new RateWindow(20),
                    ModelSpecific = new RateWindow(30, label: "Sonnet"),
                },
                "oauth"));

            var labels = WidgetSummary.BuildRowLabelsForTesting(result, result.Fetch!.Usage);

            Assert.Contains("Sonnet", labels);
            Assert.DoesNotContain("Fable", labels);
            Assert.DoesNotContain("Model", labels);
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

    private static UsageResult CodexWidgetResultWithResetCredits()
        => CodexWidgetResultWithResetCredits(2);

    private static UsageResult CodexWidgetResultWithResetCredits(int availableCount)
    {
        return UsageResult.Success(ProviderId.Codex, new TestProvider(), new ProviderFetchResult(
            new UsageSnapshot(new RateWindow(10))
            {
                Secondary = new RateWindow(20),
                ResetCredits = new ResetCreditsSnapshot(availableCount,
                [
                    new ResetCreditGrant("available", DateTimeOffset.Parse("2026-06-12T03:43:26Z"), DateTimeOffset.Parse("2026-07-12T03:43:26Z")),
                    new ResetCreditGrant("available", DateTimeOffset.Parse("2026-06-18T00:14:18Z"), DateTimeOffset.Parse("2026-07-18T00:14:18Z")),
                ]),
            },
            "oauth"));
    }

    private static UsageResult CodexWidgetResultWithCredits(string plan, double amount = 250)
    {
        var cost = new CostSnapshot(amount, "credits", "Credits").WithLimit(1000);
        return UsageResult.Success(ProviderId.Codex, new TestProvider(), new ProviderFetchResult(
            new UsageSnapshot(new RateWindow(10))
            {
                Secondary = new RateWindow(20),
                LoginMethod = plan,
                Cost = cost,
            },
            "oauth"));
    }

    private sealed class TestProvider(ProviderId id = ProviderId.Codex) : IUsageProvider
    {
        public ProviderId Id => id;
        public string DisplayName => id.ToString();
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
