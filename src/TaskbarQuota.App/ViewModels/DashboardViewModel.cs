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

        [ObservableProperty] public partial ProviderCardViewModel? SelectedCard { get; set; }

        /// <summary>Raised when the first card changes so the view can scroll back to the top.</summary>
        public event Action? ScrollToTopRequested;
        public event Action<ProviderCardViewModel?>? SelectedCardChanged;

        [ObservableProperty] public partial bool IsRefreshing { get; set; }
        [ObservableProperty] public partial string StatusText { get; set; }

        public DashboardViewModel(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
            StatusText = "";
            WidgetSettingsService.PercentageModeChanged += OnPercentageModeChanged;
            WidgetSettingsService.Changed += OnWidgetSettingsChanged;
            UsageCoordinator.Instance.StateChanged += OnCoordinatorStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged += OnActiveProviderChanged;
        }

        partial void OnSelectedCardChanged(ProviderCardViewModel? value)
            => SelectedCardChanged?.Invoke(value);

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
            => _dispatcher.TryEnqueue(() => UpdateCards(_lastResults, _lastActive, force: true));

        private async Task LoadProgressiveAsync(bool force)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            var active = UsageCoordinator.Instance.ActiveProvider;
            var results = OrderResults(UsageCoordinator.Instance.Service.Snapshot(active), active).ToList();
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
                        _dispatcher.TryEnqueue(() =>
                        {
                            UpdateCards(results, current);
                        });
                    },
                    ct);

                if (!ct.IsCancellationRequested)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
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
            results = OrderResults(results, active);
            _lastResults = results.ToArray();
            _lastActive = active;

            var ids = results.Select(r => r.Id).ToHashSet();
            for (int i = Cards.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(Cards[i].ProviderId))
                {
                    var removedId = Cards[i].ProviderId;
                    _cardSignatures.Remove(removedId);
                    Cards.RemoveAt(i);
                    if (SelectedCard?.ProviderId == removedId)
                        SelectedCard = Cards.Count > 0 ? Cards[0] : null;
                }
            }

            for (int targetIndex = 0; targetIndex < results.Count; targetIndex++)
            {
                var result = results[targetIndex];
                var signature = BuildCardSignature(result, result.Id == active);
                var existingIndex = IndexOfCard(result.Id);

                if (existingIndex < 0)
                {
                    var card = new ProviderCardViewModel(result, result.Id == active);
                    Cards.Insert(targetIndex, card);
                    if (SelectedCard is null || result.Id == active)
                        SelectedCard = card;
                    _cardSignatures[result.Id] = signature;
                    continue;
                }

                if (existingIndex != targetIndex)
                    Cards.Move(existingIndex, targetIndex);

                if (force || !_cardSignatures.TryGetValue(result.Id, out var previous) || previous != signature)
                {
                    var wasSelected = SelectedCard?.ProviderId == result.Id;
                    var card = new ProviderCardViewModel(result, result.Id == active);
                    Cards[targetIndex] = card;
                    if (wasSelected || SelectedCard is null || result.Id == active)
                        SelectedCard = card;
                    _cardSignatures[result.Id] = signature;
                }
            }

            if (SelectedCard is null && Cards.Count > 0)
                SelectedCard = Cards[0];

            var topProvider = results.Count > 0 ? results[0].Id : (ProviderId?)null;
            if (topProvider != _lastTopProvider)
            {
                _lastTopProvider = topProvider;
                ScrollToTopRequested?.Invoke();
            }
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

        private int IndexOfCard(ProviderId id)
        {
            for (int i = 0; i < Cards.Count; i++)
            {
                if (Cards[i].ProviderId == id)
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
                .Append(WidgetSettingsService.IsProviderVisible(r.Id)).Append('|')
                .Append(WidgetSettingsService.RowVisibilitySignature(r.Id));

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
                  .Append('|').Append(additional.BudgetUsd);
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
            UsageCoordinator.Instance.StateChanged -= OnCoordinatorStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged -= OnActiveProviderChanged;
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
    }
}
