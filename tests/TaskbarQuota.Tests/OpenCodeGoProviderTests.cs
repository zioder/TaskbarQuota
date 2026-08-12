using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class OpenCodeGoProviderTests
{
    [Fact]
    public void Provider_HasCorrectId()
    {
        var provider = new OpenCodeGoProvider();
        Assert.Equal(ProviderId.OpenCodeGo, provider.Id);
    }

    [Fact]
    public void Provider_HasCorrectDisplayName()
    {
        var provider = new OpenCodeGoProvider();
        Assert.Equal("OpenCode Go", provider.DisplayName);
    }

    [Fact]
    public void Provider_HasCorrectRollingLabel()
    {
        var provider = new OpenCodeGoProvider();
        Assert.Equal("Rolling", provider.SessionLabel);
    }

    [Fact]
    public void Provider_HasCorrectWeeklyLabel()
    {
        var provider = new OpenCodeGoProvider();
        Assert.Equal("Weekly", provider.WeeklyLabel);
    }

    [Fact]
    public void Provider_IsSubscriptionBilling()
    {
        var provider = new OpenCodeGoProvider();
        Assert.Equal(BillingKind.Subscription, provider.Billing);
    }

    [Fact]
    public void ExtractAllThreeWindows_FromFlatText()
    {
        var text = "rollingUsage,usagePercent:42.5,resetInSec:14400 weeklyUsage,usedPercent:67.8,resetInSec:259200 monthlyUsage,percentUsed:23.1,resetInSec:2592000";

        var rolling = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage", "rolling_usage", "rolling");
        var weekly = OpenCodeProvider.ExtractWindow(text, 10080, "weeklyUsage", "weekly_usage", "weekly");
        var monthly = OpenCodeProvider.ExtractWindow(text, 43200, "monthlyUsage", "monthly_usage", "monthly");

        Assert.NotNull(rolling);
        Assert.Equal(42.5, rolling!.UsedPercent, 1);
        Assert.Equal(300, rolling.WindowMinutes);

        Assert.NotNull(weekly);
        Assert.Equal(67.8, weekly!.UsedPercent, 1);
        Assert.Equal(10080, weekly.WindowMinutes);

        Assert.NotNull(monthly);
        Assert.Equal(23.1, monthly!.UsedPercent, 1);
        Assert.Equal(43200, monthly.WindowMinutes);
    }

    [Fact]
    public void ExtractWindows_FromServerFunctionResponse()
    {
        var text = "data:rollingUsage,usagePercent:55.2,resetInSec:10800|weeklyUsage,usedPercent:78.9,resetInSec:172800|monthlyUsage,percentUsed:34.5,resetInSec:1296000";

        var rolling = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage", "rolling_usage", "rolling");
        var weekly = OpenCodeProvider.ExtractWindow(text, 10080, "weeklyUsage", "weekly_usage", "weekly");
        var monthly = OpenCodeProvider.ExtractWindow(text, 43200, "monthlyUsage", "monthly_usage", "monthly");

        Assert.NotNull(rolling);
        Assert.Equal(55.2, rolling!.UsedPercent, 1);

        Assert.NotNull(weekly);
        Assert.Equal(78.9, weekly!.UsedPercent, 1);

        Assert.NotNull(monthly);
        Assert.Equal(34.5, monthly!.UsedPercent, 1);
    }

    [Fact]
    public void ExtractWindows_WithOnlyRollingPresent_WeeklyAndMonthlyNull()
    {
        var text = "rollingUsage,usagePercent:90,resetInSec:3600";

        var rolling = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage", "rolling_usage", "rolling");
        var weekly = OpenCodeProvider.ExtractWindow(text, 10080, "weeklyUsage", "weekly_usage", "weekly");
        var monthly = OpenCodeProvider.ExtractWindow(text, 43200, "monthlyUsage", "monthly_usage", "monthly");

        Assert.NotNull(rolling);
        Assert.Equal(90.0, rolling!.UsedPercent, 1);
        Assert.Null(weekly);
        Assert.Null(monthly);
    }

    [Fact]
    public void ExtractWindows_WithAlternateKeyNames()
    {
        var text = "rolling_usage,utilization:60,reset_sec:7200 weekly_usage,utilizationPercent:45,reset_sec:432000 monthly_usage,usage:88,reset_sec:2160000";

        var rolling = OpenCodeProvider.ExtractWindow(text, 300, "rollingUsage", "rolling_usage", "rolling");
        var weekly = OpenCodeProvider.ExtractWindow(text, 10080, "weeklyUsage", "weekly_usage", "weekly");
        var monthly = OpenCodeProvider.ExtractWindow(text, 43200, "monthlyUsage", "monthly_usage", "monthly");

        Assert.NotNull(rolling);
        Assert.Equal(60.0, rolling!.UsedPercent, 1);

        Assert.NotNull(weekly);
        Assert.Equal(45.0, weekly!.UsedPercent, 1);

        Assert.NotNull(monthly);
        Assert.Equal(88.0, monthly!.UsedPercent, 1);
    }

    [Fact]
    public void LooksSignedOut_WithOpenCodeGoPage_ReturnsFalse()
    {
        var html = "<html><body><div class=\"usage-card\"><h3>Rolling Usage</h3></div></body></html>";
        Assert.False(OpenCodeProvider.LooksSignedOut(html));
    }

    [Fact]
    public void LooksSignedOut_WithRedirectToLogin_ReturnsTrue()
    {
        var html = "<html><head><meta http-equiv=\"refresh\" content=\"0;url=/auth/authorize?redirect=/workspace/wrk_123/go\"></head></html>";
        Assert.True(OpenCodeProvider.LooksSignedOut(html));
    }

    [Theory]
    [InlineData("<title>OpenAuth</title>")]
    [InlineData("<button>Continue with GitHub</button>")]
    [InlineData("<button>Continue with Google</button>")]
    public void LooksSignedOut_WithOpenAuthPage_ReturnsTrue(string html)
        => Assert.True(OpenCodeProvider.LooksSignedOut(html));

    [Fact]
    public void GoPagePayload_ParsesCurrentCodexBarStyleEmbeddedUsage()
    {
        var html = "<script>window.__data={rollingUsage:{usagePercent:37.5,resetInSec:7200}," +
                   "weeklyUsage:{usagePercent:51,resetInSec:172800}," +
                   "monthlyUsage:{usagePercent:12,resetInSec:1209600}}</script>";

        Assert.Equal(37.5, OpenCodeProvider.ExtractWindow(html, 300, "rollingUsage")!.UsedPercent, 1);
        Assert.Equal(51, OpenCodeProvider.ExtractWindow(html, 10080, "weeklyUsage")!.UsedPercent, 1);
        Assert.Equal(12, OpenCodeProvider.ExtractWindow(html, 43200, "monthlyUsage")!.UsedPercent, 1);
    }

    [Fact]
    public void GoPagePayload_OnePercentWindowIsNotExpandedToOneHundredPercent()
    {
        var html = "<script>window.__data={rollingUsage:{usagePercent:1,resetInSec:7200}," +
                   "weeklyUsage:{usagePercent:1,resetInSec:172800}," +
                   "monthlyUsage:{usagePercent:1,resetInSec:1209600}}</script>";

        Assert.Equal(1, OpenCodeProvider.ExtractWindow(html, 300, "rollingUsage")!.UsedPercent);
        Assert.Equal(1, OpenCodeProvider.ExtractWindow(html, 10080, "weeklyUsage")!.UsedPercent);
        Assert.Equal(1, OpenCodeProvider.ExtractWindow(html, 43200, "monthlyUsage")!.UsedPercent);
    }

    [Fact]
    public void BuildResult_ParsesApiUsageJson()
    {
        const string json = "{\"usage\":{\"rolling\":{\"status\":\"ok\",\"percent\":12,\"resetsAt\":\"2026-08-12T22:13:10.85Z\"}," +
                            "\"weekly\":{\"status\":\"ok\",\"percent\":8,\"resetsAt\":\"2026-08-17T00:00:00.85Z\"}," +
                            "\"monthly\":{\"status\":\"ok\",\"percent\":35,\"resetsAt\":\"2026-09-07T14:16:57.85Z\"}}}";

        using var doc = JsonDocument.Parse(json);
        var result = OpenCodeGoProvider.BuildResult(doc.RootElement);

        Assert.Equal("api", result.SourceLabel);
        Assert.Equal(12, result.Usage.Primary.UsedPercent, 1);
        Assert.Equal(300, result.Usage.Primary.WindowMinutes);
        Assert.NotNull(result.Usage.Primary.ResetAt);
        Assert.Equal(8, result.Usage.Secondary!.UsedPercent, 1);
        Assert.Equal(10080, result.Usage.Secondary.WindowMinutes);
        Assert.Equal(35, result.Usage.Monthly!.UsedPercent, 1);
        Assert.Equal(43200, result.Usage.Monthly.WindowMinutes);
        Assert.Equal("Go", result.Usage.LoginMethod);
    }

    [Fact]
    public void BuildResult_MissingUsage_ThrowsParse()
    {
        using var doc = JsonDocument.Parse("{\"error\":\"nope\"}");
        var ex = Assert.Throws<ProviderException>(() => OpenCodeGoProvider.BuildResult(doc.RootElement));
        Assert.Equal(ProviderErrorKind.Parse, ex.Kind);
    }

    [Fact]
    public void BuildResult_MissingRolling_ThrowsParse()
    {
        using var doc = JsonDocument.Parse("{\"usage\":{\"weekly\":{\"percent\":1},\"monthly\":{\"percent\":1}}}");
        var ex = Assert.Throws<ProviderException>(() => OpenCodeGoProvider.BuildResult(doc.RootElement));
        Assert.Equal(ProviderErrorKind.Parse, ex.Kind);
    }

    [Fact]
    public void TryLoadApiKeyFromAuth_ReadsOpenCodeGoKey()
    {
        var profile = Path.Combine(Path.GetTempPath(), "wincheck-opencode-go-" + Guid.NewGuid().ToString("N"));
        var authDir = Path.Combine(profile, ".local", "share", "opencode");
        Directory.CreateDirectory(authDir);
        try
        {
            File.WriteAllText(Path.Combine(authDir, "auth.json"),
                "{\"opencode-go\":{\"type\":\"api\",\"key\":\"sk-test-12345\"}}");

            Assert.Equal("sk-test-12345", OpenCodeGoProvider.TryLoadApiKeyFromAuth(profile));
        }
        finally
        {
            try { Directory.Delete(profile, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryLoadApiKeyFromAuth_MissingEntry_ReturnsNull()
    {
        var profile = Path.Combine(Path.GetTempPath(), "wincheck-opencode-go-" + Guid.NewGuid().ToString("N"));
        var authDir = Path.Combine(profile, ".local", "share", "opencode");
        Directory.CreateDirectory(authDir);
        try
        {
            File.WriteAllText(Path.Combine(authDir, "auth.json"), "{\"something-else\":{\"key\":\"nope\"}}");
            Assert.Null(OpenCodeGoProvider.TryLoadApiKeyFromAuth(profile));
        }
        finally
        {
            try { Directory.Delete(profile, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ParseResponse_MalformedJson_ThrowsParseWithInnerException()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not json"));

        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => OpenCodeGoProvider.ParseResponse(stream, CancellationToken.None));

        Assert.Equal(ProviderErrorKind.Parse, ex.Kind);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }
}
