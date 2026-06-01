using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class CopilotProviderTests
{
    private const string StudentTokenBillingJson = """
        {
          "login": "student-user",
          "access_type_sku": "free_educational_quota",
          "copilot_plan": "individual",
          "token_based_billing": true,
          "quota_reset_date_utc": "2026-07-01T00:00:00.000Z",
          "quota_snapshots": {
            "chat": {
              "percent_remaining": 100.0,
              "unlimited": true,
              "token_based_billing": true,
              "remaining": 0,
              "entitlement": 0
            },
            "completions": {
              "percent_remaining": 100.0,
              "unlimited": true,
              "token_based_billing": true,
              "remaining": 0,
              "entitlement": 0
            },
            "premium_interactions": {
              "percent_remaining": 100.0,
              "quota_remaining": 150.0,
              "unlimited": false,
              "token_based_billing": true,
              "remaining": 150,
              "entitlement": 200,
              "overage_count": 0,
              "overage_permitted": false
            }
          }
        }
        """;

    [Fact]
    public void TryParseAiCredits_StudentPlan_ReturnsRemainingAndLimit()
    {
        using var doc = JsonDocument.Parse(StudentTokenBillingJson);
        Assert.True(CopilotProvider.TryParseAiCredits(doc.RootElement, out var credits));
        Assert.Equal(150, credits.Amount);
        Assert.Equal(200, credits.Limit);
        Assert.Equal("Credits", credits.Label);
    }

    [Fact]
    public void BuildResult_StudentPlan_UsesCreditsAndCopilotStudentPlanName()
    {
        using var doc = JsonDocument.Parse(StudentTokenBillingJson);
        var result = CopilotProvider.BuildResult(doc.RootElement);

        Assert.Equal("Student", result.Usage.LoginMethod);
        Assert.NotNull(result.Usage.Cost);
        Assert.Equal(200, result.Usage.Cost!.Limit);
        Assert.Equal(25, result.Usage.Primary.UsedPercent, 1);
        Assert.NotNull(result.Usage.AdditionalUsage);
        Assert.False(result.Usage.AdditionalUsage!.Enabled);
        Assert.Equal("$0.00 / $0 budget", result.Usage.AdditionalUsage.SpendText);
    }

    [Fact]
    public void TryParseAdditionalUsage_WhenEnabled_UsesCreditPricing()
    {
        const string json = """
            {
              "token_based_billing": true,
              "quota_snapshots": {
                "premium_interactions": {
                  "overage_permitted": true,
                  "overage_count": 250,
                  "overage_budget_usd": 10
                }
              }
            }
            """;

        using var doc = JsonDocument.Parse(json);
        var additional = CopilotProvider.TryParseAdditionalUsage(doc.RootElement);
        Assert.NotNull(additional);
        Assert.True(additional!.Enabled);
        Assert.Equal(2.50, additional.SpentUsd, 2);
        Assert.Equal(10, additional.BudgetUsd);
        Assert.Equal("$2.50 / $10.00 budget", additional.SpendText);
    }

    [Fact]
    public void BuildResult_LegacyPremiumRequests_StillUsesPercentWindows()
    {
        const string legacyJson = """
            {
              "access_type_sku": "plus_monthly_subscriber_quota",
              "quota_snapshots": {
                "premium_interactions": {
                  "entitlement": 1500,
                  "remaining": 1327,
                  "percent_remaining": 88.5,
                  "unlimited": false
                }
              }
            }
            """;

        using var doc = JsonDocument.Parse(legacyJson);
        var result = CopilotProvider.BuildResult(doc.RootElement);

        Assert.Null(result.Usage.Cost);
        Assert.InRange(result.Usage.Primary.UsedPercent, 11, 12);
    }
}
