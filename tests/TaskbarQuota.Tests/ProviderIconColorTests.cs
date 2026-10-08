using System;
using TaskbarQuota;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests;

public class ProviderIconColorTests
{
    [Fact]
    public void Codex_DefaultsToThemeForeground_SoItStaysWhite()
    {
        Assert.Null(WidgetSettingsService.DefaultProviderIconColorHex(ProviderId.Codex));
    }

    [Fact]
    public void Claude_AndCodex_DefaultToDifferentIconColors()
    {
        Assert.NotNull(WidgetSettingsService.DefaultProviderIconColorHex(ProviderId.Claude));
        Assert.NotEqual(
            WidgetSettingsService.DefaultProviderIconColorHex(ProviderId.Claude),
            WidgetSettingsService.DefaultProviderIconColorHex(ProviderId.Codex));
    }

    [Fact]
    public void EveryDefaultIconColor_IsAValidHexColor()
    {
        foreach (var provider in Enum.GetValues<ProviderId>())
        {
            var hex = WidgetSettingsService.DefaultProviderIconColorHex(provider);
            if (hex is null)
                continue;

            Assert.True(WidgetSettingsService.TryNormalizeHexColor(hex, out var normalized), provider.ToString());
            Assert.Equal(hex, normalized);
        }
    }

    [Theory]
    [InlineData("#d97757", "#D97757")]
    [InlineData("D97757", "#D97757")]
    [InlineData("  #ffffff ", "#FFFFFF")]
    public void TryNormalizeHexColor_AcceptsSixDigitHex(string input, string expected)
    {
        Assert.True(WidgetSettingsService.TryNormalizeHexColor(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    public void TryNormalizeHexColor_RejectsInvalidInput(string? input)
    {
        Assert.False(WidgetSettingsService.TryNormalizeHexColor(input, out _));
    }
}
