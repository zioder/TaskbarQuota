using System;
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
    public void Provider_HasCorrectSessionLabel()
    {
        var provider = new OpenCodeGoProvider();
        Assert.Equal("Session", provider.SessionLabel);
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
}
