using System;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota;

internal static class DashboardLayoutMetrics
{
    private const double CharWidth = 7.2;
    private const double MinContentWidth = 280;
    private const double MaxContentWidth = 520;
    private const double HorizontalPadding = 72;
    private const double MinContentHeight = 320;
    private const double HeaderHeight = 148;
    private const double StatusHeight = 28;
    private const double SectionSpacing = 16;
    private const double UsageBarHeight = 52;
    private const double ResetCreditsHeaderHeight = 24;
    private const double ResetCreditItemHeight = 44;
    private const double CardVerticalPadding = 32;
    private const double SetupCardHeight = 112;
    private const double ErrorCardHeight = 72;

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

        if (!string.IsNullOrEmpty(card.ResetCreditsCountText))
        {
            maxLine = Math.Max(maxLine,
                Estimate("Reset credits") + Estimate(card.ResetCreditsCountText) + 24);
            foreach (var resetCredit in card.ResetCreditItems)
            {
                maxLine = Math.Max(maxLine,
                    Math.Max(Estimate(resetCredit.TokenTitle), Estimate(resetCredit.ExpiresText)) +
                    48);
            }
        }

        if (!string.IsNullOrEmpty(card.SetupHint))
            maxLine = Math.Max(maxLine, Estimate(card.DisplayName) + Estimate(card.SetupHint) + 48);

        if (!string.IsNullOrEmpty(card.Error))
            maxLine = Math.Max(maxLine, Estimate(card.Error) + 48);

        return Math.Clamp(maxLine + HorizontalPadding, MinContentWidth, MaxContentWidth);
    }

    public static double EstimateDetailHeight(ProviderCardViewModel? card)
    {
        if (card is null)
            return MinContentHeight;

        double height = HeaderHeight + StatusHeight;
        int sections = 0;

        if (card.Bars.Count > 0)
        {
            sections++;
            height += CardVerticalPadding
                + (card.Bars.Count * UsageBarHeight)
                + (Math.Max(0, card.Bars.Count - 1) * 20);
        }

        if (!string.IsNullOrEmpty(card.CreditLeftText))
        {
            sections++;
            height += 104;
        }

        if (!string.IsNullOrEmpty(card.ResetCreditsCountText))
        {
            sections++;
            height += CardVerticalPadding
                + ResetCreditsHeaderHeight
                + 12
                + (card.ResetCreditItems.Count * ResetCreditItemHeight)
                + (Math.Max(0, card.ResetCreditItems.Count - 1) * 6);
        }

        if (!string.IsNullOrEmpty(card.AdditionalUsageStatusText))
        {
            sections++;
            height += 104;
        }

        if (card.TextMetrics.Count > 0)
        {
            sections++;
            height += 24 + (card.TextMetrics.Count * 32);
        }

        if (!string.IsNullOrEmpty(card.CostText))
        {
            sections++;
            height += 24;
        }

        if (!string.IsNullOrEmpty(card.SetupHint))
        {
            sections++;
            height += SetupCardHeight;
        }

        if (!string.IsNullOrEmpty(card.Error))
        {
            sections++;
            height += ErrorCardHeight;
        }

        height += Math.Max(0, sections - 1) * SectionSpacing;
        return Math.Max(MinContentHeight, height);
    }

    private static double Estimate(string? text)
        => string.IsNullOrEmpty(text) ? 0 : text.Length * CharWidth;
}
