using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
    public sealed partial class TotalSpendViewModel : ObservableObject
    {
        [ObservableProperty] public partial string SelectedPeriod { get; set; } = "1";
        [ObservableProperty] public partial string SelectedMetric { get; set; } = "cost";
        [ObservableProperty] public partial string SelectedBreakdown { get; set; } = "model";

        [ObservableProperty] public partial bool HasSpendData { get; private set; }
        [ObservableProperty] public partial bool IsLoading { get; set; }
        [ObservableProperty] public partial Visibility IsLoadingVisibility { get; private set; } = Visibility.Collapsed;
        [ObservableProperty] public partial Visibility EmptyVisibility { get; private set; } = Visibility.Collapsed;
        [ObservableProperty] public partial string HeadlineLabel { get; private set; } = "Raw token cost";
        [ObservableProperty] public partial string FormattedCenterValue { get; private set; } = "$0.00";
        [ObservableProperty] public partial string HeadlineDetail { get; private set; } = "* if billed at full API rate";
        [ObservableProperty] public partial string RingCenterValue { get; private set; } = "$0.00";
        [ObservableProperty] public partial string RingCenterUnit { get; private set; } = "API estimate";
        [ObservableProperty] public partial string RingTooltip { get; private set; } = "Raw token cost: $0.00";
        [ObservableProperty] public partial string RingAutomationName { get; private set; } = "Raw token cost, $0.00";
        [ObservableProperty] public partial string PeriodSubtitle { get; private set; } = string.Empty;
        [ObservableProperty] public partial string ChartTitle { get; private set; } = "Daily cost";
        [ObservableProperty] public partial Visibility ModelBreakdownVisibility { get; private set; } = Visibility.Visible;
        [ObservableProperty] public partial Visibility DayBreakdownVisibility { get; private set; } = Visibility.Collapsed;

        [ObservableProperty] public partial string TotalTokensText { get; private set; } = "0";
        [ObservableProperty] public partial string TotalTokensDetail { get; private set; } = "0 per active day";
        [ObservableProperty] public partial string CachedInputText { get; private set; } = "0";
        [ObservableProperty] public partial string CachedInputDetail { get; private set; } = "0% of observed input";
        [ObservableProperty] public partial string UncachedInputText { get; private set; } = "0";
        [ObservableProperty] public partial string UncachedInputDetail { get; private set; } = "0 cache writes";
        [ObservableProperty] public partial string OutputText { get; private set; } = "0";
        [ObservableProperty] public partial string OutputDetail { get; private set; } = "includes 0 reasoning";
        [ObservableProperty] public partial string CacheSavingsText { get; private set; } = "$0.00";
        [ObservableProperty] public partial string CacheSavingsDetail { get; private set; } = "vs full input rates";
        [ObservableProperty] public partial IReadOnlyList<UsageChartDayViewModel> ChartDays { get; private set; } = Array.Empty<UsageChartDayViewModel>();

        public ObservableCollection<TotalSpendSliceViewModel> ProviderSlices { get; } = new();
        public ObservableCollection<CombinedModelUsageItemViewModel> ModelItems { get; } = new();
        public ObservableCollection<CombinedDayUsageItemViewModel> DayItems { get; } = new();

        private IReadOnlyList<UsageResult>? _lastResults;
        private int? _lastSnapshotSignature;

        partial void OnIsLoadingChanged(bool value) => UpdateSurfaceState();

        partial void OnHasSpendDataChanged(bool value) => UpdateSurfaceState();

        /// <summary>
        /// The cards only render once a complete history snapshot is available. While that snapshot is
        /// still being read show a loading state; if the scan completes with no data at all show an
        /// empty state instead of an empty shell.
        /// </summary>
        private void UpdateSurfaceState()
        {
            bool showLoading = IsLoading && !HasSpendData;
            IsLoadingVisibility = showLoading ? Visibility.Visible : Visibility.Collapsed;
            EmptyVisibility = !IsLoading && !HasSpendData ? Visibility.Visible : Visibility.Collapsed;
        }

        public void UpdateResults(IReadOnlyList<UsageResult> results, bool force = false)
        {
            _lastResults = results;
            var signature = BuildSnapshotSignature(results);
            if (!ShouldRefreshSnapshot(
                    _lastSnapshotSignature,
                    signature,
                    HasSpendData,
                    force))
                return;

            Recalculate();
            _lastSnapshotSignature = signature;
        }

        internal static bool ShouldRefreshSnapshot(
            int? previousSignature,
            int currentSignature,
            bool hasSpendData,
            bool force)
        {
            if (force)
                return true;
            if (previousSignature == currentSignature)
                return false;
            // Once populated, the usage page behaves as a snapshot. Live agent token events keep
            // updating the pending results but do not move the bars; a completed Refresh applies them.
            return !hasSpendData;
        }

        partial void OnSelectedPeriodChanged(string value) => Recalculate();
        partial void OnSelectedMetricChanged(string value) => Recalculate();

        partial void OnSelectedBreakdownChanged(string value)
        {
            ModelBreakdownVisibility = value == "model" ? Visibility.Visible : Visibility.Collapsed;
            DayBreakdownVisibility = value == "day" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Recalculate()
        {
            ProviderSlices.Clear();
            ModelItems.Clear();
            DayItems.Clear();

            var histories = (_lastResults ?? Array.Empty<UsageResult>())
                .Where(result => result.Fetch?.Usage.UsageHistory is not null)
                .Select(result => (Result: result, History: result.Fetch!.Usage.UsageHistory!))
                .ToArray();

            HasSpendData = histories.Any(item => item.History.Today is not null
                || item.History.Last7Days is not null
                || item.History.Last30Days is not null
                || item.History.Last90Days is not null);
            if (!HasSpendData)
            {
                ResetEmpty();
                return;
            }

            var days = DaysInPeriod(SelectedPeriod);
            var until = DateTime.Today;
            var since = until.AddDays(-(days - 1));
            PeriodSubtitle = days == 1 ? $"Today · {until:MMM d}" : $"{since:MMM d} to {until:MMM d}";

            var providerPeriods = new List<(UsageResult Result, UsagePeriod Period)>();
            foreach (var item in histories)
            {
                var period = SelectPeriod(item.History, SelectedPeriod);
                if (period is not null)
                    providerPeriods.Add((item.Result, period));
            }

            var totalTokens = providerPeriods.Aggregate<(UsageResult Result, UsagePeriod Period), ulong>(0, (sum, item) => sum + item.Period.Tokens);
            var totalCost = providerPeriods.Sum(item => item.Period.EstimatedCostUsd ?? 0);
            var totalBreakdown = SumTokens(providerPeriods.Select(item => item.Period.TokenBreakdown));
            var cacheSavings = providerPeriods.Sum(item => item.Period.CacheSavingsUsd);
            var sessions = providerPeriods.Sum(item => item.Period.Sessions);
            var dayLookup = BuildDayLookup(histories, since, until);
            var activeDays = dayLookup.Values.Count(values =>
                values.Aggregate<(UsageResult Result, DailyUsage Usage), ulong>(0, (sum, value) => sum + value.Usage.Tokens) > 0);
            var dailyAverage = activeDays == 0 ? 0 : totalTokens / (ulong)activeDays;
            var observedInput = totalBreakdown.Input + totalBreakdown.CacheRead;
            var cachedShare = observedInput == 0 ? 0 : (double)totalBreakdown.CacheRead / observedInput;

            HeadlineLabel = SelectedMetric == "tokens" ? "Processed tokens" : "Raw token cost";
            FormattedCenterValue = SelectedMetric == "tokens" ? $"{totalTokens:N0}" : $"${totalCost:F2}";
            HeadlineDetail = SelectedMetric == "tokens"
                ? $"Input, cache reads and output across {sessions:N0} sessions."
                : "API-equivalent estimate; subscription billing may differ";
            RingCenterValue = SelectedMetric == "tokens"
                ? FormatCompactValue(totalTokens)
                : FormatCompactCost(totalCost);
            RingCenterUnit = SelectedMetric == "tokens" ? "tokens" : "API estimate";
            RingTooltip = $"{HeadlineLabel}: {FormattedCenterValue}{Environment.NewLine}{HeadlineDetail}";
            RingAutomationName = $"{HeadlineLabel}, {FormattedCenterValue}. {PeriodSubtitle}.";
            ChartTitle = SelectedMetric == "tokens" ? "Daily processed tokens" : "Daily cost";

            TotalTokensText = $"{totalTokens:N0}";
            TotalTokensDetail = $"{dailyAverage:N0} per active day";
            CachedInputText = $"{totalBreakdown.CacheRead:N0}";
            CachedInputDetail = $"{cachedShare:P1} of observed input";
            UncachedInputText = $"{totalBreakdown.Input:N0}";
            UncachedInputDetail = $"{totalBreakdown.CacheWrite:N0} cache writes";
            OutputText = $"{totalBreakdown.Output:N0}";
            OutputDetail = $"includes {totalBreakdown.Reasoning:N0} reasoning";
            CacheSavingsText = $"${cacheSavings:F2}";
            CacheSavingsDetail = totalCost > 0
                ? $"{cacheSavings / totalCost:F1}x raw token cost"
                : "vs full input rates";

            BuildProviderSlices(providerPeriods, totalCost, totalTokens);
            BuildChart(dayLookup, since, until);
            BuildModelBreakdown(providerPeriods, totalCost);
            BuildDayBreakdown(dayLookup);
        }

        internal static int DaysInPeriod(string selectedPeriod) => selectedPeriod switch
        {
            "1" => 1,
            "7" => 7,
            "90" => 90,
            _ => 30,
        };

        internal static UsagePeriod? SelectPeriod(UsageHistory history, string selectedPeriod) => selectedPeriod switch
        {
            "1" => history.Today,
            "7" => history.Last7Days,
            "90" => history.Last90Days,
            _ => history.Last30Days,
        };

        private void BuildProviderSlices(
            IReadOnlyList<(UsageResult Result, UsagePeriod Period)> periods,
            double totalCost,
            ulong totalTokens)
        {
            foreach (var item in periods.OrderByDescending(item => SelectedMetric == "tokens" ? item.Period.Tokens : item.Period.EstimatedCostUsd ?? 0))
            {
                var cost = item.Period.EstimatedCostUsd;
                var share = SelectedMetric == "tokens"
                    ? (totalTokens == 0 ? 0 : (double)item.Period.Tokens / totalTokens * 100)
                    : (totalCost == 0 ? 0 : (cost ?? 0) / totalCost * 100);
                ProviderSlices.Add(new TotalSpendSliceViewModel(
                    item.Result.Id,
                    item.Result.DisplayName,
                    SelectedMetric == "tokens" ? item.Period.Tokens : cost ?? 0,
                    item.Period.Tokens,
                    cost,
                    share,
                    SelectedMetric));
            }
        }

        internal static string FormatCompactValue(double value)
        {
            if (value >= 1_000_000_000) return $"{value / 1_000_000_000:0.#}B";
            if (value >= 1_000_000) return $"{value / 1_000_000:0.#}M";
            if (value >= 1_000) return $"{value / 1_000:0.#}K";
            return $"{value:0}";
        }

        private static string FormatCompactCost(double value)
        {
            return value >= 1_000
                ? $"${FormatCompactValue(value)}"
                : $"${value:F2}";
        }

        private void BuildChart(
            IReadOnlyDictionary<DateTime, List<(UsageResult Result, DailyUsage Usage)>> lookup,
            DateTime since,
            DateTime until)
        {
            var points = new List<UsageChartDayViewModel>();
            for (var day = since; day <= until; day = day.AddDays(1))
            {
                lookup.TryGetValue(day, out var values);
                values ??= new List<(UsageResult Result, DailyUsage Usage)>();
                points.Add(new UsageChartDayViewModel(
                    day,
                    values.Select(value => new UsageChartProviderValueViewModel(
                        value.Result.Id,
                        value.Result.DisplayName,
                        value.Usage.Tokens,
                        value.Usage.EstimatedCostUsd)).ToArray()));
            }
            ChartDays = points;
        }

        private void BuildModelBreakdown(
            IReadOnlyList<(UsageResult Result, UsagePeriod Period)> periods,
            double totalCost)
        {
            var models = periods.SelectMany(item =>
                    (item.Period.ModelBreakdown?.Models ?? Array.Empty<ModelUsageEntry>())
                    .Select(model => (item.Result.DisplayName, Model: model)))
                .GroupBy(item => (item.DisplayName, item.Model.Model))
                .Select(group => new
                {
                    Provider = group.Key.DisplayName,
                    Model = group.Key.Model,
                    Tokens = group.Aggregate<(string DisplayName, ModelUsageEntry Model), ulong>(0, (sum, item) => sum + item.Model.TotalTokens),
                    Cost = group.All(item => item.Model.CostUsd.HasValue)
                        ? group.Sum(item => item.Model.CostUsd!.Value)
                        : (double?)null,
                })
                .OrderByDescending(item => item.Cost ?? 0)
                .ThenByDescending(item => item.Tokens);

            foreach (var model in models)
                ModelItems.Add(new CombinedModelUsageItemViewModel(model.Provider, model.Model, model.Tokens, model.Cost, totalCost));
        }

        private void BuildDayBreakdown(IReadOnlyDictionary<DateTime, List<(UsageResult Result, DailyUsage Usage)>> lookup)
        {
            foreach (var pair in lookup.OrderByDescending(pair => pair.Key).Take(8))
            {
                var tokens = pair.Value.Aggregate<(UsageResult Result, DailyUsage Usage), ulong>(0, (sum, value) => sum + value.Usage.Tokens);
                var cost = pair.Value.Sum(value => value.Usage.EstimatedCostUsd ?? 0);
                DayItems.Add(new CombinedDayUsageItemViewModel(pair.Key, tokens, cost));
            }
        }

        private static int BuildSnapshotSignature(IEnumerable<UsageResult> results)
        {
            var hash = new HashCode();
            foreach (var result in results.OrderBy(result => result.Id))
            {
                hash.Add(result.Id);
                var history = result.Fetch?.Usage.UsageHistory;
                if (history is null)
                    continue;

                foreach (var day in history.Daily)
                {
                    hash.Add(day.Date, StringComparer.Ordinal);
                    hash.Add(day.Tokens);
                    hash.Add(day.EstimatedCostUsd);
                    hash.Add(day.EstimateComplete);
                    foreach (var model in day.ModelBreakdown?.Models ?? Array.Empty<ModelUsageEntry>())
                    {
                        hash.Add(model.Model, StringComparer.OrdinalIgnoreCase);
                        hash.Add(model.TotalTokens);
                        hash.Add(model.CostUsd);
                    }
                }
            }
            return hash.ToHashCode();
        }

        private static Dictionary<DateTime, List<(UsageResult Result, DailyUsage Usage)>> BuildDayLookup(
            IEnumerable<(UsageResult Result, UsageHistory History)> histories,
            DateTime since,
            DateTime until)
        {
            var result = new Dictionary<DateTime, List<(UsageResult Result, DailyUsage Usage)>>();
            foreach (var item in histories)
            foreach (var usage in item.History.Daily)
            {
                if (!DateTime.TryParseExact(usage.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    || day < since
                    || day > until)
                    continue;
                if (!result.TryGetValue(day, out var values))
                    result[day] = values = new List<(UsageResult Result, DailyUsage Usage)>();
                values.Add((item.Result, usage));
            }
            return result;
        }

        private static TokenBreakdown SumTokens(IEnumerable<TokenBreakdown> values)
        {
            var total = new TokenBreakdown();
            foreach (var value in values)
                total = total.Add(value);
            return total;
        }

        private void ResetEmpty()
        {
            HeadlineLabel = "Raw token cost";
            FormattedCenterValue = "$0.00";
            HeadlineDetail = "No local provider usage history found";
            RingCenterValue = "$0.00";
            RingCenterUnit = "API estimate";
            RingTooltip = "Raw token cost: $0.00";
            RingAutomationName = "Raw token cost, $0.00. No local provider usage history found.";
            ChartDays = Array.Empty<UsageChartDayViewModel>();
        }
    }

    public sealed class UsageChartDayViewModel
    {
        public DateTime Date { get; }
        public IReadOnlyList<UsageChartProviderValueViewModel> Providers { get; }
        public ulong TotalTokens => Providers.Aggregate<UsageChartProviderValueViewModel, ulong>(0, (sum, provider) => sum + provider.Tokens);
        public double CostUsd => Providers.Sum(provider => provider.CostUsd ?? 0);
        public bool CostComplete => Providers.All(provider => provider.CostUsd.HasValue);
        public UsageChartDayViewModel(DateTime date, IReadOnlyList<UsageChartProviderValueViewModel> providers)
        {
            Date = date;
            Providers = providers;
        }
    }

    public sealed class UsageChartProviderValueViewModel
    {
        public ProviderId ProviderId { get; }
        public string ProviderName { get; }
        public ulong Tokens { get; }
        public double? CostUsd { get; }
        public UsageChartProviderValueViewModel(ProviderId providerId, string providerName, ulong tokens, double? costUsd)
        {
            ProviderId = providerId;
            ProviderName = providerName;
            Tokens = tokens;
            CostUsd = costUsd;
        }
    }

    public sealed class CombinedModelUsageItemViewModel
    {
        public string ProviderName { get; }
        public string ModelName { get; }
        public string CostText { get; }
        public string ShareText { get; }
        public string TokensText { get; }
        public CombinedModelUsageItemViewModel(string provider, string model, ulong tokens, double? cost, double totalCost)
        {
            ProviderName = provider;
            ModelName = model;
            CostText = cost.HasValue ? $"${cost.Value:F2}" : "Not priced";
            ShareText = cost.HasValue && totalCost > 0 ? $"{cost.Value / totalCost:P1}" : "—";
            TokensText = $"{tokens:N0}";
        }
    }

    public sealed class CombinedDayUsageItemViewModel
    {
        public string DayText { get; }
        public string CostText { get; }
        public string TokensText { get; }
        public CombinedDayUsageItemViewModel(DateTime day, ulong tokens, double cost)
        {
            DayText = day.ToString("MMM d", CultureInfo.CurrentCulture);
            CostText = $"${cost:F2}";
            TokensText = $"{tokens:N0}";
        }
    }
}
