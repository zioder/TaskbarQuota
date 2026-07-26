using TaskbarQuota.Controls;

namespace TaskbarQuota.Tests;

/// <summary>
/// The credits column reserves enough width that the value doesn't twitch as the used side grows, but no
/// more. It used to reserve a fixed "10,000/10,000" for every plan, padding tiles on small plans with tens
/// of pixels of dead space.
/// </summary>
public class WidgetCreditValueSampleTests
{
    [Theory]
    // The used side can never exceed the limit, so the limit's own width is the reserve.
    [InlineData("12/300", "000/300")]
    [InlineData("1,234/10,000", "000000/10,000")]
    [InlineData("0/50", "00/50")]
    public void ReservesTheLimitsWidthOnEachSide(string value, string expected)
        => Assert.Equal(expected, WidgetSummary.CreditValueSample(value));

    [Theory]
    // Nothing to derive a limit from: reserve exactly what is shown.
    [InlineData("300")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("500/")]
    public void FallsBackToTheValueWhenThereIsNoLimit(string value)
        => Assert.Equal(value, WidgetSummary.CreditValueSample(value));

    // The used side keeps growing, so the reserve has to cover the widest it can ever get — the limit —
    // even when today's value is narrower. Otherwise the column shifts as the number climbs.
    [Fact]
    public void ReserveCoversTheWidestTheValueCanEverGet()
    {
        Assert.Equal("10,000/10,000".Length, WidgetSummary.CreditValueSample("9,999/10,000").Length);
        Assert.Equal("300/300".Length, WidgetSummary.CreditValueSample("7/300").Length);
    }
}
