using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;
using Xunit;

namespace TaskbarQuota.Tests;

public sealed class CursorGrokBotUsageTests
{
    [Fact]
    public void ParseGrokBotWindow_BuildsWeeklyMeter()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "usagePercent": 37,
              "nextResetTimestampUtc": "2026-11-17T12:00:00Z",
              "hasNonZeroIncludedLimit": true
            }
            """);

        var window = CursorProvider.ParseGrokBotWindow(doc.RootElement);

        Assert.NotNull(window);
        Assert.Equal(CursorProvider.GrokBotWindowId, window.Id);
        Assert.Equal("Grok Bot", window.Title);
        Assert.Equal(37, window.Window.UsedPercent);
        Assert.Equal(QuotaWindowLength.Weekly, window.Window.WindowMinutes);
        Assert.NotNull(window.Window.ResetAt);
    }

    [Theory]
    [InlineData("""{"usagePercent":10,"includedLimitZero":true}""")]
    [InlineData("""{"usagePercent":10,"hasNonZeroIncludedLimit":false}""")]
    [InlineData("""{"usage_percent":5,"uses_pooled_enterprise_allowance":true}""")]
  public void ParseGrokBotWindow_IsNullWithoutGrant(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(CursorProvider.ParseGrokBotWindow(doc.RootElement));
    }

    [Fact]
    public void ParseGrokBotWindow_AcceptsSnakeCaseFields()
    {
        using var doc = JsonDocument.Parse(
            """{"usage_percent":12,"next_reset_timestamp_utc":"2026-11-20T00:00:00Z"}""");

        var window = CursorProvider.ParseGrokBotWindow(doc.RootElement);

        Assert.NotNull(window);
        Assert.Equal(12, window!.Window.UsedPercent);
    }

}
