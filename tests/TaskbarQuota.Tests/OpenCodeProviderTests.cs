using System;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class OpenCodeProviderTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]
    public void Money_NonFiniteJsonStrings_AreIgnored(string value)
    {
        Assert.Null(OpenCodeProvider.FindMoneyValue("{\"balance\":\"" + value + "\"}", "balance"));
        Assert.Null(OpenCodeProvider.ParseBillingBalance("{\"customerID\":\"cus_test\",\"balance\":\"" + value + "\"}"));
    }

    [Fact]
    public void Money_NonFiniteNumbersAndText_AreIgnored()
    {
        Assert.Null(OpenCodeProvider.FindMoneyValue("{\"balance\":1e999}", "balance"));
        Assert.Null(OpenCodeProvider.FindMoneyValue("balance:1e999", "balance"));
        Assert.Null(OpenCodeProvider.FindMoneyValue("balance: $1e999", "balance"));
        Assert.Null(OpenCodeProvider.ParseBillingBalance("{\"customerID\":\"cus_test\",\"balance\":1e999}"));
        Assert.Null(OpenCodeProvider.ParseBillingBalance("customerID:\"cus_test\",balance:1e999"));
    }

    [Theory]
    [InlineData("{\"rollingUsage\":{\"usagePercent\":1e999}}")]
    [InlineData("{\"rollingUsage\":{\"used\":1e999,\"limit\":100}}")]
    [InlineData("{\"rollingUsage\":{\"used\":100,\"limit\":1e999}}")]
    [InlineData("{\"rollingUsage\":{\"used\":1e308,\"limit\":1e-308}}")]
    [InlineData("{\"rollingUsage\":{\"used\":100,\"limit\":0}}")]
    public void ParseUsage_InvalidPercentageOrRatio_IsIgnored(string json)
        => Assert.Null(OpenCodeProvider.ParseUsage(json).rolling);

    [Fact]
    public void ExtractWindow_NonFinitePercentage_IsIgnored()
        => Assert.Null(OpenCodeProvider.ExtractWindow("rollingUsage,usagePercent:1e999", 300, "rollingUsage"));

    [Theory]
    [InlineData("monthly_usage")]
    [InlineData("monthly-usage")]
    [InlineData("monthlyusage")]
    public void FindMoneyValue_JsonKeyAliases_ArePreserved(string key)
        => Assert.Equal(33.33, OpenCodeProvider.FindMoneyValue("{\"" + key + "\":33.33}", "monthly_usage"));

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    public void NumericContracts_UseInvariantCulture(string culture)
    {
        using var scope = new CultureScope(culture);

        Assert.Equal(125.75, OpenCodeProvider.FindMoneyValue("{\"balance\":\"125.75\"}", "balance"));
        Assert.Equal(1234.50, OpenCodeProvider.FindMoneyValue("{\"balance\":\"1,234.50\"}", "balance"));
        Assert.Equal(125.75, OpenCodeProvider.FindMoneyValue("balance=125.75", "balance"));
        Assert.Equal(57.30, OpenCodeProvider.FindMoneyValue("current balance: $57.30 remaining", "balance"));
        Assert.Equal(75.5, OpenCodeProvider.ExtractNumber(@"usagePercent:([0-9.]+)", "usagePercent:75.5"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_735_689_600), OpenCodeProvider.ExtractRenewal("renewAt:1735689600.5"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_735_689_600), OpenCodeProvider.ParseUsage("{\"renewAt\":\"1735689600.5\"}").renewsAt);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e300")]
    [InlineData("1e999")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Renewal_InvalidTimestampText_IsIgnored(string timestamp)
    {
        Assert.Null(OpenCodeProvider.ExtractRenewal("renewAt:" + timestamp));
        Assert.Null(OpenCodeProvider.ParseUsage("{\"renewAt\":\"" + timestamp + "\"}").renewsAt);
    }

    [Theory]
    [InlineData("1e300")]
    [InlineData("1e999")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Renewal_InvalidNumericTimestamp_IsIgnored(string timestamp)
        => Assert.Null(OpenCodeProvider.ParseUsage("{\"renewAt\":" + timestamp + "}").renewsAt);

    [Theory]
    [InlineData("1735689600", 1_735_689_600L)]
    [InlineData("1735689600000", 1_735_689_600L)]
    [InlineData("253402300799000", 253_402_300_799L)]
    public void Renewal_ValidTimestampSecondsOrMilliseconds_IsPreserved(string timestamp, long expectedSeconds)
        => Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expectedSeconds), OpenCodeProvider.ExtractRenewal("renewAt:" + timestamp));

    [Theory]
    [InlineData("1e300")]
    [InlineData("1e999")]
    public void ParseUsage_UnrepresentableResetDelay_PreservesUsageWithoutReset(string seconds)
    {
        var usage = OpenCodeProvider.ParseUsage("{\"rollingUsage\":{\"usagePercent\":42.5,\"resetInSec\":" + seconds + "}}");

        Assert.NotNull(usage.rolling);
        Assert.Equal(42.5, usage.rolling.UsedPercent);
        Assert.Null(usage.rolling.ResetAt);
        Assert.Null(usage.rolling.ResetDescription);
    }

    [Fact]
    public void ExtractWindow_UnrepresentableResetDelay_PreservesUsageWithoutReset()
    {
        var window = OpenCodeProvider.ExtractWindow("rollingUsage,usagePercent:42.5,resetInSec:" + new string('9', 400), 300, "rollingUsage");

        Assert.NotNull(window);
        Assert.Equal(42.5, window.UsedPercent);
        Assert.Null(window.ResetAt);
        Assert.Null(window.ResetDescription);
    }

    [Theory]
    [InlineData("auth=abc; other=value", "auth=abc; other=value")]
    [InlineData("Cookie: auth=abc; other=value", "auth=abc; other=value")]
    [InlineData("curl 'https://opencode.ai/workspace/wrk_abc' -H 'accept: text/html' -H 'cookie: auth=abc; other=value'", "auth=abc; other=value")]
    [InlineData("curl.exe \"https://opencode.ai\" --header=\"Cookie: __Host-auth=abc; other=value\"", "__Host-auth=abc; other=value")]
    [InlineData("curl https://opencode.ai --cookie 'auth=abc; other=value'", "auth=abc; other=value")]
    public void NormalizeCookieHeader_AcceptsRawHeaderOrCopiedCurl(string input, string expected)
        => Assert.Equal(expected, CookieHelper.NormalizeCookieHeader(input));

    [Fact]
    public void NormalizeCookieHeader_RejectsCurlWithoutCookies()
        => Assert.Null(CookieHelper.NormalizeCookieHeader("curl https://opencode.ai/workspace/wrk_abc"));

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

    [Theory]
    [InlineData("wrk_abc123", "wrk_abc123")]
    [InlineData("https://opencode.ai/workspace/wrk_abc123/go", "wrk_abc123")]
    [InlineData("  wrk_team-123  ", "wrk_team-123")]
    [InlineData("not-a-workspace", null)]
    public void NormalizeWorkspaceId_AcceptsIdsAndWorkspaceUrls(string input, string? expected)
        => Assert.Equal(expected, OpenCodeProvider.NormalizeWorkspaceId(input));

    [Fact]
    public void ParseWorkspaceIds_DeduplicatesStructuredAndJsonOccurrences()
    {
        var ids = OpenCodeProvider.ParseWorkspaceIds(
            "id: \"wrk_first\", data: { workspace: \"wrk_second\", again: \"wrk_first\" }");

        Assert.Equal(new[] { "wrk_first", "wrk_second" }, ids);
    }

    [Fact]
    public void ParseBillingBalance_ConvertsRawBillingUnitsToUsd()
    {
        var balance = OpenCodeProvider.ParseBillingBalance(
            "{\"customerID\":\"cus_123\",\"balance\":4250000000}");

        Assert.Equal(42.5, balance);
    }

    [Fact]
    public void ParseBillingBalance_IgnoresUnscopedBalanceFields()
    {
        var balance = OpenCodeProvider.ParseBillingBalance("{\"balance\":4250000000}");

        Assert.Null(balance);
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

    [Theory]
    [InlineData("<title>OpenAuth</title>")]
    [InlineData("<button>Continue with GitHub</button>")]
    [InlineData("<button>Continue with Google</button>")]
    public void LooksSignedOut_WithOpenAuthPage_ReturnsTrue(string html)
        => Assert.True(OpenCodeProvider.LooksSignedOut(html));

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
    public void ExtractWindow_WithFractionalPercent_PreservesPercentage()
    {
        var text = "rollingUsage,usagePercent:0.653,resetInSec:14400";
        var window = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage");
        Assert.NotNull(window);
        Assert.Equal(0.653, window!.UsedPercent, 3);
    }

    [Fact]
    public void ExtractWindow_WithOnePercent_PreservesOnePercent()
    {
        var text = "monthlyUsage,usagePercent:1,resetInSec:2592000";
        var window = OpenCodeProvider.ExtractWindow(text, 43200, "monthlyUsage");
        Assert.NotNull(window);
        Assert.Equal(1, window!.UsedPercent);
    }

    [Fact]
    public void ParseUsage_WithJsonPercentages_PreservesPercentageScale()
    {
        var json = """
            {
              "rollingUsage": { "usagePercent": 0.653, "resetInSec": 14400 },
              "weeklyUsage": { "usagePercent": 1, "resetInSec": 259200 }
            }
            """;

        var (rolling, weekly, _) = OpenCodeProvider.ParseUsage(json);

        Assert.NotNull(rolling);
        Assert.Equal(0.653, rolling!.UsedPercent, 3);
        Assert.NotNull(weekly);
        Assert.Equal(1, weekly!.UsedPercent);
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
