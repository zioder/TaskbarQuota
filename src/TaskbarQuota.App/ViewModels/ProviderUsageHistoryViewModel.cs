using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
    public sealed class ProviderUsageHistoryViewModel
    {
        public static ProviderUsageHistoryViewModel Empty { get; } = Create(null, DateTime.Today);

        public bool HasData { get; }
        public bool HasTrend { get; }
        public string SourceNote { get; }
        public string TrendAutomationName { get; }
        public IReadOnlyList<ProviderUsagePeriodRowViewModel> Periods { get; }
        public IReadOnlyList<ProviderUsageTrendPointViewModel> TrendPoints { get; }

        private ProviderUsageHistoryViewModel(
            bool hasData,
            string sourceNote,
            IReadOnlyList<ProviderUsagePeriodRowViewModel> periods,
            IReadOnlyList<ProviderUsageTrendPointViewModel> trendPoints)
        {
            HasData = hasData;
            SourceNote = sourceNote;
            Periods = periods;
            TrendPoints = trendPoints;
            HasTrend = trendPoints.Any(point => point.Tokens > 0);

            var peak = trendPoints.OrderByDescending(point => point.Tokens).FirstOrDefault();
            TrendAutomationName = peak is null || peak.Tokens == 0
                ? "30-day usage trend. No daily token data."
                : $"30-day usage trend. Peak {FormatTokens(peak.Tokens)} on {peak.Date:MMM d}.";
        }

        public static ProviderUsageHistoryViewModel From(UsageHistory? history)
            => Create(history, DateTime.Today);

        internal static ProviderUsageHistoryViewModel CreateForTesting(UsageHistory? history, DateTime today)
            => Create(history, today.Date);

        private static ProviderUsageHistoryViewModel Create(UsageHistory? history, DateTime today)
        {
            if (history is null)
                return new ProviderUsageHistoryViewModel(false, string.Empty, Array.Empty<ProviderUsagePeriodRowViewModel>(), Array.Empty<ProviderUsageTrendPointViewModel>());

            var periods = new[]
            {
                new ProviderUsagePeriodRowViewModel("Today", history.Today),
                new ProviderUsagePeriodRowViewModel("Yesterday", history.Yesterday),
                new ProviderUsagePeriodRowViewModel("Last 30 Days", history.Last30Days),
            };
            var byDate = history.Daily
                .Select(usage => (Usage: usage, Parsed: ParseDate(usage.Date)))
                .Where(item => item.Parsed.HasValue)
                .GroupBy(item => item.Parsed!.Value.Date)
                .ToDictionary(group => group.Key, group => group.Last().Usage);
            var trend = new List<ProviderUsageTrendPointViewModel>(30);
            for (int offset = 29; offset >= 0; offset--)
            {
                var day = today.AddDays(-offset);
                byDate.TryGetValue(day, out var usage);
                trend.Add(new ProviderUsageTrendPointViewModel(day, usage));
            }

            var sourceNote = history.Today?.ModelBreakdown?.SourceNote
                ?? history.Last30Days?.ModelBreakdown?.SourceNote
                ?? history.Yesterday?.ModelBreakdown?.SourceNote
                ?? string.Empty;
            bool hasData = periods.Any(period => period.HasData) || trend.Any(point => point.Tokens > 0);
            return new ProviderUsageHistoryViewModel(hasData, sourceNote, periods, trend);
        }

        private static DateTime? ParseDate(string value)
            => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.Date
                : null;

        internal static string FormatTokens(ulong tokens)
        {
            if (tokens >= 1_000_000_000)
                return $"{tokens / 1_000_000_000d:0.##}B tokens";
            if (tokens >= 1_000_000)
                return $"{tokens / 1_000_000d:0.##}M tokens";
            if (tokens >= 1_000)
                return $"{tokens / 1_000d:0.##}K tokens";
            return $"{tokens:N0} tokens";
        }
    }

    public sealed class ProviderUsagePeriodRowViewModel
    {
        public string Label { get; }
        public bool HasData { get; }
        public string Reading { get; }
        public string TooltipText { get; }

        public ProviderUsagePeriodRowViewModel(string label, UsagePeriod? period)
        {
            Label = label;
            HasData = period is not null;
            if (period is null)
            {
                Reading = "No data";
                TooltipText = $"{label}: no local usage data";
                return;
            }

            var tokens = ProviderUsageHistoryViewModel.FormatTokens(period.Tokens);
            Reading = period.EstimatedCostUsd is { } cost
                ? $"${cost:N2} · {tokens}"
                : tokens;
            var figures = period.EstimatedCostUsd is { } exactCost
                ? $"${exactCost:N2} · {period.Tokens:N0} tokens"
                : $"{period.Tokens:N0} tokens · cost unavailable";
            TooltipText = period.CostEstimated && period.EstimatedCostUsd.HasValue
                ? $"{figures}\nAPI-equivalent estimate; subscription billing may differ"
                : figures;
        }
    }

    public sealed class ProviderUsageTrendPointViewModel
    {
        public DateTime Date { get; }
        public ulong Tokens { get; }
        public double? CostUsd { get; }
        public string TooltipText { get; }

        public ProviderUsageTrendPointViewModel(DateTime date, DailyUsage? usage)
        {
            Date = date.Date;
            Tokens = usage?.Tokens ?? 0;
            CostUsd = usage?.EstimatedCostUsd;
            TooltipText = CostUsd is { } cost
                ? $"{Date:dddd, MMM d}: {Tokens:N0} tokens · ${cost:N2}"
                : $"{Date:dddd, MMM d}: {Tokens:N0} tokens";
        }
    }
}
