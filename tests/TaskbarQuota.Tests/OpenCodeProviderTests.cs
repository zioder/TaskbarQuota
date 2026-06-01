using System;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class OpenCodeProviderTests
{
    [Fact]
    public void LooksSignedOut_WithAuthAuthorize_ReturnsTrue()
    {
        var text = "redirect to /auth/authorize?client_id=abc";
        Assert.True(OpenCodeProvider.LooksSignedOut(text));
    }

    [Fact]
    public void LooksSignedOut_WithSigninJson_ReturnsTrue()
    {
        var text = "{\"page\":\"signin\",\"redirect\":\"/dashboard\"}";
        Assert.True(OpenCodeProvider.LooksSignedOut(text));
    }

    [Fact]
    public void LooksSignedOut_WithPleaseSignIn_ReturnsTrue()
    {
        var text = "<html>Please Sign In to continue</html>";
        Assert.True(OpenCodeProvider.LooksSignedOut(text));
    }

    [Fact]
    public void WorkspacePageUrl_BuildsWorkspacePaths()
    {
        Assert.Equal(
            "https://opencode.ai/workspace/wrk_abc123",
            OpenCodeProvider.WorkspacePageUrl("wrk_abc123"));
        Assert.Equal(
            "https://opencode.ai/workspace/wrk_abc123/go",
            OpenCodeProvider.WorkspacePageUrl("wrk_abc123", "go"));
        Assert.Equal(
            "https://opencode.ai/workspace/wrk_abc123/usage",
            OpenCodeProvider.WorkspacePageUrl("wrk_abc123", "/usage"));
    }

    [Fact]
    public void LooksSignedOut_WithNormalContent_ReturnsFalse()
    {
        var text = "{\"workspace\":\"wrk_abc123\",\"plan\":\"pro\"}";
        Assert.False(OpenCodeProvider.LooksSignedOut(text));
    }

    [Fact]
    public void LooksSignedOut_WithEmptyString_ReturnsFalse()
    {
        Assert.False(OpenCodeProvider.LooksSignedOut(""));
    }

    [Fact]
    public void FindMoneyValue_WithJsonNumber_ReturnsValue()
    {
        var text = "{\"monthlyUsage\": 42.50, \"balance\": 100}";
        var result = OpenCodeProvider.FindMoneyValue(text, "monthlyUsage");
        Assert.NotNull(result);
        Assert.Equal(42.50, result!.Value, 2);
    }

    [Fact]
    public void FindMoneyValue_WithJsonString_ReturnsValue()
    {
        var text = "{\"balance\": \"125.75\"}";
        var result = OpenCodeProvider.FindMoneyValue(text, "balance");
        Assert.NotNull(result);
        Assert.Equal(125.75, result!.Value, 2);
    }

    [Fact]
    public void FindMoneyValue_WithDollarSign_ReturnsValue()
    {
        var text = "current balance: $57.30 remaining";
        var result = OpenCodeProvider.FindMoneyValue(text, "balance");
        Assert.NotNull(result);
        Assert.Equal(57.30, result!.Value, 2);
    }

    [Fact]
    public void FindMoneyValue_WithNestedJson_FindsValue()
    {
        var text = "{\"data\":{\"billing\":{\"monthlyUsage\":99.99}}}";
        var result = OpenCodeProvider.FindMoneyValue(text, "monthlyUsage");
        Assert.NotNull(result);
        Assert.Equal(99.99, result!.Value, 2);
    }

    [Fact]
    public void FindMoneyValue_WithLargeRawCents_Normalizes()
    {
        var text = "{\"monthlyUsage\": 4250000000}";
        var result = OpenCodeProvider.FindMoneyValue(text, "monthlyUsage");
        Assert.NotNull(result);
        Assert.Equal(42.5, result!.Value, 2);
    }

    [Fact]
    public void FindMoneyValue_WithNoMatch_ReturnsNull()
    {
        var text = "{\"plan\":\"free\",\"active\":true}";
        var result = OpenCodeProvider.FindMoneyValue(text, "monthlyUsage", "balance");
        Assert.Null(result);
    }

    [Fact]
    public void FindMoneyValue_WithUnderscoreOrHyphen_Matches()
    {
        var text = "{\"monthly_usage\": 33.33}";
        var result = OpenCodeProvider.FindMoneyValue(text, "monthly_usage");
        Assert.NotNull(result);
        Assert.Equal(33.33, result!.Value, 2);
    }

    [Fact]
    public void ExtractNumber_WithValidMatch_ReturnsNumber()
    {
        var result = OpenCodeProvider.ExtractNumber(@"usagePercent\s*[:=]\s*([0-9]+(?:\.[0-9]+)?)", "usagePercent: 75.5");
        Assert.NotNull(result);
        Assert.Equal(75.5, result!.Value, 1);
    }

    [Fact]
    public void ExtractNumber_WithNoMatch_ReturnsNull()
    {
        var result = OpenCodeProvider.ExtractNumber(@"usagePercent\s*[:=]\s*([0-9]+)", "no match here");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractNumber_WithInteger_ReturnsInteger()
    {
        var result = OpenCodeProvider.ExtractNumber(@"percent\s*[:=]\s*([0-9]+(?:\.[0-9]+)?)", "percent=42");
        Assert.NotNull(result);
        Assert.Equal(42.0, result!.Value, 1);
    }

    [Fact]
    public void ExtractWindow_WithFlatRollingUsage_ReturnsWindow()
    {
        var text = "rollingUsage,usagePercent:65.3,resetInSec:14400";
        var window = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage");
        Assert.NotNull(window);
        Assert.Equal(65.3, window!.UsedPercent, 1);
        Assert.Equal(300, window.WindowMinutes);
        Assert.NotNull(window.ResetAt);
    }

    [Fact]
    public void ExtractWindow_WithFlatWeeklyUsage_ReturnsWindow()
    {
        var text = "weeklyUsage,usedPercent:80,resetInSec:259200";
        var window = OpenCodeProvider.ExtractWindow(text, 10080, "weeklyUsage");
        Assert.NotNull(window);
        Assert.Equal(80.0, window!.UsedPercent, 1);
        Assert.Equal(10080, window.WindowMinutes);
    }

    [Fact]
    public void ExtractWindow_WithFlatMonthlyUsage_ReturnsWindow()
    {
        var text = "monthlyUsage,percentUsed:45.2,resetInSec:2592000";
        var window = OpenCodeProvider.ExtractWindow(text, 43200, "monthlyUsage");
        Assert.NotNull(window);
        Assert.Equal(45.2, window!.UsedPercent, 1);
        Assert.Equal(43200, window.WindowMinutes);
    }

    [Fact]
    public void ExtractWindow_WithFractionalPercent_NormalizesToPercentage()
    {
        var text = "rollingUsage,usagePercent:0.653,resetInSec:14400";
        var window = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage");
        Assert.NotNull(window);
        Assert.Equal(65.3, window!.UsedPercent, 1);
    }

    [Fact]
    public void ExtractWindow_WithNoMatch_ReturnsNull()
    {
        var text = "plan:free,active:true";
        var window = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage", "rolling");
        Assert.Null(window);
    }

    [Fact]
    public void ExtractWindow_WithMultipleNames_MatchesFirst()
    {
        var text = "rolling_usage,usagePercent:55,resetInSec:7200";
        var window = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage", "rolling_usage", "rolling");
        Assert.NotNull(window);
        Assert.Equal(55.0, window!.UsedPercent, 1);
    }

    [Fact]
    public void ExtractWindow_WithResetSeconds_SetsResetAt()
    {
        var text = "rollingUsage,usagePercent:50,resetInSec:3600";
        var window = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage");
        Assert.NotNull(window);
        Assert.NotNull(window!.ResetAt);
        var expected = DateTimeOffset.Now.AddSeconds(3600);
        Assert.True(Math.Abs((window.ResetAt!.Value - expected).TotalSeconds) < 5);
    }

    [Fact]
    public void ExtractWindow_ClampsPercentTo100()
    {
        var text = "rollingUsage,usagePercent:150,resetInSec:3600";
        var window = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage");
        Assert.NotNull(window);
        Assert.Equal(100.0, window!.UsedPercent, 1);
    }

    [Fact]
    public void ExtractRenewal_WithRenewAtIso_ReturnsDate()
    {
        var text = "{\"renewAt\":\"2026-06-15T00:00:00Z\"}";
        var result = OpenCodeProvider.ExtractRenewal(text);
        Assert.NotNull(result);
        Assert.Equal(2026, result!.Value.Year);
        Assert.Equal(6, result.Value.Month);
        Assert.Equal(15, result.Value.Day);
    }

    [Fact]
    public void ExtractRenewal_WithRenewAtTimestamp_ReturnsDate()
    {
        var text = "{\"renewAt\":\"1750000000\"}";
        var result = OpenCodeProvider.ExtractRenewal(text);
        Assert.NotNull(result);
        Assert.True(result!.Value.Year >= 2025);
    }

    [Fact]
    public void ExtractRenewal_WithNoMatch_ReturnsNull()
    {
        var text = "{\"plan\":\"pro\",\"active\":true}";
        var result = OpenCodeProvider.ExtractRenewal(text);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractRenewal_WithRenewAtSnakeCase_ReturnsDate()
    {
        var text = "{\"renew_at\":\"2026-07-01T12:00:00Z\"}";
        var result = OpenCodeProvider.ExtractRenewal(text);
        Assert.NotNull(result);
        Assert.Equal(7, result!.Value.Month);
    }

    [Fact]
    public void FormatTimeUntil_WithDays_ReturnsDaysFormat()
    {
        var at = DateTimeOffset.Now.AddDays(5);
        var result = OpenCodeProvider.FormatTimeUntil(at);
        Assert.Contains("d", result);
    }

    [Fact]
    public void FormatTimeUntil_WithHours_ReturnsHoursMinutesFormat()
    {
        var at = DateTimeOffset.Now.AddHours(3).AddMinutes(30);
        var result = OpenCodeProvider.FormatTimeUntil(at);
        Assert.Contains("h", result);
        Assert.Contains("m", result);
    }

    [Fact]
    public void FormatTimeUntil_WithMinutes_ReturnsMinutesFormat()
    {
        var at = DateTimeOffset.Now.AddMinutes(45);
        var result = OpenCodeProvider.FormatTimeUntil(at);
        Assert.Contains("m", result);
        Assert.DoesNotContain("h", result);
    }

    [Fact]
    public void FormatTimeUntil_WithPastTime_ReturnsZeroMinutes()
    {
        var at = DateTimeOffset.Now.AddHours(-1);
        var result = OpenCodeProvider.FormatTimeUntil(at);
        Assert.Equal("0m", result);
    }

    [Fact]
    public void FormatTimeUntil_WithOverTwoDays_ReturnsDaysFormat()
    {
        var at = DateTimeOffset.Now.AddDays(3);
        var result = OpenCodeProvider.FormatTimeUntil(at);
        Assert.Contains("d", result);
    }
}
