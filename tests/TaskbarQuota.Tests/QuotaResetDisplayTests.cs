using System.Globalization;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Tests;

public class QuotaResetDisplayTests
{
    [Fact]
    public void Known_reset_uses_local_calendar_date_and_time()
    {
        var localReset = new DateTimeOffset(new DateTime(2026, 11, 20, 18, 45, 0, DateTimeKind.Local));
        var reset = localReset.ToUniversalTime();
        var credit = new ResetCreditViewModel(1, new ResetCreditGrant("available", null, reset));
        string expected = localReset.ToString("MMM d, yyyy", CultureInfo.CurrentCulture) + " 18:45";

        Assert.Equal(expected, credit.ExpiresText);
        Assert.Equal(expected, QuotaResetDisplay.WindowCaption(
            ProviderId.Codex, new RateWindow(13, resetAt: reset, resetDescription: "5d 11h")));
        Assert.Equal(expected, QuotaResetDisplay.WindowCaption(
            ProviderId.Kimi, new RateWindow(0, resetAt: reset)));
    }

    [Fact]
    public void Unknown_reset_keeps_provider_countdown()
        => Assert.Equal("resets in 5d 11h", QuotaResetDisplay.WindowCaption(
            ProviderId.Codex, new RateWindow(13, resetDescription: "5d 11h")));

    [Fact]
    public void Unused_codex_window_does_not_show_an_unstarted_reset_date()
    {
        var window = new RateWindow(0, resetAt: DateTimeOffset.UnixEpoch);

        Assert.Empty(QuotaResetDisplay.WindowCaption(ProviderId.Codex, window));
    }

    [Fact]
    public void Excluded_window_does_not_show_a_reset()
    {
        var window = new RateWindow(13, resetAt: DateTimeOffset.UnixEpoch) { IsIncluded = false };

        Assert.Empty(QuotaResetDisplay.WindowCaption(ProviderId.Codex, window));
    }
}
