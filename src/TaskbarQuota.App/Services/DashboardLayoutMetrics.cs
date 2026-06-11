using System;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota;

internal static class DashboardLayoutMetrics
{
    private const double CharWidth = 7.2;
    private const double MinContentWidth = 280;
    private const double MaxContentWidth = 520;
    private const double HorizontalPadding = 72;

    public static double EstimateDetailWidth(ProviderCardViewModel? card)
    {
        if (card is null)
            return MinContentWidth;

        double maxLine = 240;

        foreach (var bar in card.Bars)
        {
            maxLine = Math.Max(maxLine,
                Estimate(bar.Label) + Estimate(bar.PercentText) + Estimate(bar.ResetText) + 24);
        }

        foreach (var metric in card.TextMetrics)
            maxLine = Math.Max(maxLine, Estimate(metric.Label) + Estimate(metric.Value) + 24);

        if (!string.IsNullOrEmpty(card.CreditLeftText))
        {
            maxLine = Math.Max(maxLine,
                Estimate(card.CreditLeftText) + Estimate(card.CreditLimitText) + 24);
        }

        if (!string.IsNullOrEmpty(card.AdditionalUsageStatusText))
        {
            maxLine = Math.Max(maxLine,
                Estimate("Additional usage") + Estimate(card.AdditionalUsageStatusText) + 24);
            maxLine = Math.Max(maxLine,
                Estimate("Spend") + Estimate(card.AdditionalUsageSpendText) + 24);
        }

        if (!string.IsNullOrEmpty(card.SetupHint))
            maxLine = Math.Max(maxLine, Estimate(card.DisplayName) + Estimate(card.SetupHint) + 48);

        if (!string.IsNullOrEmpty(card.Error))
            maxLine = Math.Max(maxLine, Estimate(card.Error) + 48);

        return Math.Clamp(maxLine + HorizontalPadding, MinContentWidth, MaxContentWidth);
    }

    private static double Estimate(string? text)
        => string.IsNullOrEmpty(text) ? 0 : text.Length * CharWidth;
}