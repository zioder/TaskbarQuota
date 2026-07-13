using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
    public sealed partial class DashboardViewModel : ObservableObject, IDisposable
    {
        private readonly DispatcherQueue _dispatcher;
        private IReadOnlyList<UsageResult> _lastResults = Array.Empty<UsageResult>();
        private ProviderId? _lastActive;
        private ProviderId? _lastTopProvider;
        private CancellationTokenSource? _loadCts;
        private readonly Dictionary<ProviderId, string> _cardSignatures = new();

        public ObservableCollection<ProviderCardViewModel> Cards { get; } = new();
        public ObservableCollection<ProviderCardViewModel> AvailableCards { get; } = new();

        [ObservableProperty] public partial ProviderCardViewModel? SelectedCard { get; set; }
        [ObservableProperty] public partial double DetailContentWidth { get; private set; }
        [ObservableProperty] public partial double DetailContentHeight { get; private set; }
        [ObservableProperty] public partial Visibility AvailableCardsVisibility { get; private set; }
        [ObservableProperty] public partial Visibility OnboardingVisibility { get; private set; }

        /// <summary>Raised when the first card changes so the view can scroll back to the top.</summary>
        public event Action? ScrollToTopRequested;
        public event Action<ProviderCardViewModel?>? SelectedCardChanged;
        public event Action<double>? DetailContentWidthChanged;
        public event Action<double>? DetailContentHeightChanged;

        [ObservableProperty] public partial bool IsRefreshing { get; set; }
        [ObservableProperty] public partial string StatusText { get; set; }

        public DashboardViewModel(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
            StatusText = "";
            OnboardingVisibility = OnboardingStateService.IsDismissed()
                ? Visibility.Collapsed
                : Visibility.Visible;
            WidgetSettingsService.PercentageModeChanged += OnPercentageModeChanged;
            WidgetSettingsService.Changed += OnWidgetSettingsChanged;
            WidgetSettingsService.DashboardCompositionChanged += OnDashboardCompositionChanged;
            UsageCoordinator.Instance.StateChanged += OnCoordinatorStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged += OnActiveProviderChanged;
        }

        partial void OnSelectedCardChanged(ProviderCardViewModel? value)
        {
            UpdateDetailContentMetrics(value);
            SelectedCardChanged?.Invoke(value);
        }

        partial void OnDetailContentWidthChanged(double value)
            => DetailContentWidthChanged?.Invoke(value);

        partial void OnDetailContentHeightChanged(double value)
            => DetailContentHeightChanged?.Invoke(value);

        public void ReportMeasuredDetailHeight(double height)
        {
            // The tray flyout intentionally keeps a stable height. Let the inner dashboard scroll
            // instead of resizing the native popup, which makes provider switching feel laggy.
        }

        public void SelectProvider(ProviderId id)
        {
            foreach (var card in Cards)
            {
                if (card.ProviderId != id)
                    continue;

                if (!ReferenceEquals(SelectedCard, card))
                    SelectedCard = card;
                return;
            }

            foreach (var card in AvailableCards)
            {
                if (card.ProviderId != id)
                    continue;

                EnableAvailableProvider(id);
                return;
            }
        }

        public void EnableAvailableProvider(ProviderId id)
        {
            ProviderDiscoveryService.EnableProvider(id);
            _ = RefreshAsync();
        }

        public void DismissOnboarding()
        {
            OnboardingStateService.Dismiss();
            OnboardingVisibility = Visibility.Collapsed;
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            StatusText = "Refreshing...";
            try
            {
                await LoadProgressiveAsync(force: true);
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        /// <summary>Initial load using cached values (no forced network).</summary>
        public Task LoadAsync() => LoadProgressiveAsync(force: false);

        private void OnPercentageModeChanged(object? sender, EventArgs e)
            => _dispatcher.TryEnqueue(() => UpdateCards(_lastResults, _lastActive, force: true));

        private void OnWidgetSettingsChanged(object? sender, EventArgs e)
            => _dispatcher.TryEnqueue(RefreshCardsVisibility);

        private void OnDashboardCompositionChanged(object? sender, EventArgs e)
            => _dispatcher.TryEnqueue(() => UpdateCards(_lastResults, _lastActive, force: true));

        private void RefreshCardsVisibility()
        {
            foreach (var card in Cards)
                card.RefreshVisibility();
        }

        private async Task LoadProgressiveAsync(bool force)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            var active = UsageCoordinator.Instance.ActiveProvider;
            var results = await Task.Run(() => BuildDashboardResults(active)).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                UpdateCards(results, active);
                StatusText = force ? "Refreshing..." : "Loading...";
            });

            try
            {
                await UsageCoordinator.Instance.FetchAllProgressiveAsync(
                    force,
                    result =>
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        var index = results.FindIndex(r => r.Id == result.Id);
                        if (index >= 0)
                            results[index] = result;
                        else
                            results.Add(result);

                        var current = UsageCoordinator.Instance.ActiveProvider;
                        var snapshot = results.ToList();
                        _ = Task.Run(() =>
                        {
                            var merged = BuildDashboardResults(current, snapshot);
                            _dispatcher.TryEnqueue(() => UpdateCards(merged, current));
                        });
                    },
                    ct);

                if (!ct.IsCancellationRequested)
                {
                    var finalActive = UsageCoordinator.Instance.ActiveProvider;
                    var finalResults = results.ToList();
                    var merged = await Task.Run(() => BuildDashboardResults(finalActive, finalResults)).ConfigureAwait(false);
                    _dispatcher.TryEnqueue(() =>
                    {
                        UpdateCards(merged, finalActive);
                        StatusText = $"Updated at {DateTime.Now:HH:mm:ss}";
                    });
                }
            }
            catch (OperationCanceledException) { }
        }

        private void UpdateCards(IReadOnlyList<UsageResult> results, ProviderId? active, bool force = false)
        {
            Views.DashboardPage.SetSuppressWidgetEvents(true);
            try
            {
                UpdateCardsCore(results, active, force);
            }
            finally
            {
                _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    Views.DashboardPage.SetSuppressWidgetEvents(false);
                });
            }
        }

        private void UpdateCardsCore(IReadOnlyList<UsageResult> results, ProviderId? active, bool force)
        {
            var selectedProviderId = SelectedCard?.ProviderId;

            results = BuildDashboardResults(active, results);
            _lastResults = results.ToArray();
            _lastActive = active;

            var dashboardResults = results
                .Where(r => ProviderDiscoveryService.ShouldShowInDashboard(r, active))
                .ToList();
            var availableResults = results
                .Where(r => ProviderDiscoveryService.ShouldShowInAvailable(r, active))
                .ToList();

            SyncCardCollection(Cards, dashboardResults, active, force);
            SyncCardCollection(AvailableCards, availableResults, active: null, force: force);

            AvailableCardsVisibility = AvailableCards.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            RestoreSelectedCard(selectedProviderId);

            UpdateDetailContentMetrics(SelectedCard);

            var topProvider = dashboardResults.Count > 0 ? dashboardResults[0].Id : (ProviderId?)null;
            if (topProvider != _lastTopProvider)
            {
                _lastTopProvider = topProvider;
                ScrollToTopRequested?.Invoke();
            }
        }

        private void SyncCardCollection(
            ObservableCollection<ProviderCardViewModel> collection,
            IReadOnlyList<UsageResult> results,
            ProviderId? active,
            bool force)
        {
            var ids = results.Select(r => r.Id).ToHashSet();
            for (int i = collection.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(collection[i].ProviderId))
                {
                    var removedId = collection[i].ProviderId;
                    _cardSignatures.Remove(removedId);
                    collection.RemoveAt(i);
                }
            }

            for (int targetIndex = 0; targetIndex < results.Count; targetIndex++)
            {
                var result = results[targetIndex];
                var signature = BuildCardSignature(result, result.Id == active);
                var existingIndex = IndexOfCard(collection, result.Id);

                if (existingIndex < 0)
                {
                    var card = new ProviderCardViewModel(result, result.Id == active);
                    collection.Insert(targetIndex, card);
                    if (ReferenceEquals(collection, Cards) && SelectedCard is null)
                        SelectedCard = card;
                    _cardSignatures[result.Id] = signature;
                    continue;
                }

                if (existingIndex != targetIndex)
                    collection.Move(existingIndex, targetIndex);

                if (force || !_cardSignatures.TryGetValue(result.Id, out var previous) || previous != signature)
                {
                    var wasSelected = SelectedCard?.ProviderId == result.Id;
                    var card = new ProviderCardViewModel(result, result.Id == active);
                    collection[targetIndex] = card;
                    if (ReferenceEquals(collection, Cards) && wasSelected)
                        SelectedCard = card;
                    _cardSignatures[result.Id] = signature;
                }
            }
        }

        private void RestoreSelectedCard(ProviderId? selectedProviderId)
        {
            if (selectedProviderId is { } preservedId)
            {
                foreach (var card in Cards)
                {
                    if (card.ProviderId == preservedId)
                    {
                        SelectedCard = card;
                        return;
                    }
                }
            }

            if (SelectedCard is null || !Cards.Any(c => c.ProviderId == SelectedCard.ProviderId))
                SelectedCard = Cards.Count > 0 ? Cards[0] : null;
        }

        private void UpdateDetailContentMetrics(ProviderCardViewModel? card)
        {
            // Option A: fixed flyout. Width stays at FlyoutLayout.BaseLogicalWidth regardless of how
            // many providers are installed; the strip only pushes the window wider once its icons
            // overflow that base. Content is laid out inside the resulting width.
            int flyoutWidth = FlyoutLayout.ComputeLogicalWidth(Cards.Count, 0);
            DetailContentWidth = flyoutWidth - FlyoutLayout.DetailContentPadding;

            DetailContentHeight = FlyoutLayout.FixedLogicalContentHeight;
        }

        private void OnCoordinatorStateChanged(UsageResult result)
        {
            _dispatcher.TryEnqueue(() =>
            {
                var results = _lastResults.ToList();
                var index = results.FindIndex(r => r.Id == result.Id);
                if (index >= 0)
                    results[index] = result;
                else
                    results.Add(result);

                UpdateCards(results, UsageCoordinator.Instance.ActiveProvider);
            });
        }

        private void OnActiveProviderChanged(ProviderId? _)
            => _dispatcher.TryEnqueue(() => UpdateCards(_lastResults, UsageCoordinator.Instance.ActiveProvider));

        private static IReadOnlyList<UsageResult> OrderResults(IReadOnlyList<UsageResult> results, ProviderId? active)
            => UsageCoordinator.SortByRecentActivity(results, UsageCoordinator.Instance.RecentProviders, active);

        private static List<UsageResult> BuildDashboardResults(ProviderId? active, IReadOnlyList<UsageResult>? live = null)
        {
            var merged = new Dictionary<ProviderId, UsageResult>();
            if (live is not null)
            {
                foreach (var result in live)
                    merged[result.Id] = result;
            }

            var service = UsageCoordinator.Instance.Service;
            foreach (var provider in service.All)
            {
                if (merged.ContainsKey(provider.Id))
                    continue;

                if (ProviderDiscoveryService.IsProbed(provider.Id) && service.TryGetCached(provider.Id, out var cached))
                {
                    merged[provider.Id] = cached;
                    continue;
                }

                if (ProviderDiscoveryService.ShouldFetch(provider.Id, active))
                {
                    merged[provider.Id] = UsageResult.Pending(provider.Id, provider,
                        active == provider.Id ? "Loading active provider..." : "Loading...");
                    continue;
                }
            }

            return OrderResults(merged.Values.ToArray(), active).ToList();
        }

        private static int IndexOfCard(ObservableCollection<ProviderCardViewModel> collection, ProviderId id)
        {
            for (int i = 0; i < collection.Count; i++)
            {
                if (collection[i].ProviderId == id)
                    return i;
            }

            return -1;
        }

        private static string BuildCardSignature(UsageResult r, bool isActive)
        {
            var sb = new StringBuilder()
                .Append(r.Id).Append('|')
                .Append(isActive).Append('|')
                .Append(r.Error).Append('|')
                .Append(r.ErrorKind).Append('|')
                .Append(r.Source.Kind).Append('|')
                .Append(r.Source.DisplayName);

            if (r.Fetch is not { } fetch)
                return sb.ToString();

            var usage = fetch.Usage;
            AppendRateWindow(sb, usage.Primary);
            AppendRateWindow(sb, usage.Secondary);
            AppendRateWindow(sb, usage.ModelSpecific);
            AppendRateWindow(sb, usage.Monthly);
            foreach (var extra in usage.ExtraRateWindows)
            {
                sb.Append('|').Append(extra.Id).Append('|').Append(extra.Title);
                AppendRateWindow(sb, extra.Window);
            }

            sb.Append('|').Append(usage.LoginMethod)
              .Append('|').Append(usage.Email)
              .Append('|').Append(usage.Cost?.Label)
              .Append('|').Append(usage.Cost?.Display)
              .Append('|').Append(fetch.SourceLabel);

            if (usage.AdditionalUsage is { } additional)
            {
                sb.Append('|').Append(additional.Enabled)
                  .Append('|').Append(additional.SpentUsd)
                  .Append('|').Append(additional.BudgetUsd)
                  .Append('|').Append(additional.IsCredits);
            }

            if (usage.ResetCredits is { } resetCredits)
            {
                sb.Append('|').Append(resetCredits.AvailableCount);
                foreach (var credit in resetCredits.Credits)
                {
                    sb.Append('|').Append(credit.Status)
                      .Append('|').Append(credit.GrantedAt)
                      .Append('|').Append(credit.ExpiresAt);
                }
            }

            return sb.ToString();
        }

        private static void AppendRateWindow(StringBuilder sb, RateWindow? window)
        {
            if (window is null)
            {
                sb.Append("|null");
                return;
            }

            sb.Append('|').Append(window.UsedPercent)
              .Append('|').Append(window.ResetDescription)
              .Append('|').Append(window.WindowMinutes)
              .Append('|').Append(window.ResetAt);
        }

        public void Dispose()
        {
            WidgetSettingsService.PercentageModeChanged -= OnPercentageModeChanged;
            WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
            WidgetSettingsService.DashboardCompositionChanged -= OnDashboardCompositionChanged;
            UsageCoordinator.Instance.StateChanged -= OnCoordinatorStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged -= OnActiveProviderChanged;
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
    }
}
