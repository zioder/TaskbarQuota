using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using TaskbarQuota.Cost;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
    /// <summary>One provider's slice of a cost window: color, name, dollars, and share-of-total.</summary>
    public sealed partial class CostSegmentViewModel : ObservableObject
    {
        public required ProviderId Provider { get; init; }
        public required string DisplayName { get; init; }
        public required Color Color { get; init; }
        public double CostUsd { get; init; }
        /// <summary>0..1 share of the window total; drives the donut arc sweep.</summary>
        public double Fraction { get; set; }

        public SolidColorBrush Brush => new(Color);
        public string CostText => CostFormatting.Money(CostUsd);
    }

    /// <summary>
    /// Backs the Cost page: the API-equivalent spend (what tokens would cost at public list rates)
    /// per provider, across Today / Yesterday / Last 7 / Last 30 day windows. Purely presentational —
    /// all numbers come from <see cref="CostService"/>.
    /// </summary>
    public sealed partial class CostViewModel : ObservableObject
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly CostService _service;
        private IReadOnlyDictionary<CostRange, CostWindow>? _windows;

        public CostViewModel(DispatcherQueue dispatcher, CostService? service = null)
        {
            _dispatcher = dispatcher;
            _service = service ?? CostService.Instance;
        }

        public ObservableCollection<CostSegmentViewModel> Segments { get; } = new();

        [ObservableProperty] private CostRange selectedRange = CostRange.Today;
        [ObservableProperty] private string totalText = "$0.00";
        [ObservableProperty] private string subtitleText = "API-equivalent cost";
        [ObservableProperty] private bool isLoading;
        [ObservableProperty] private bool isEmpty;
        [ObservableProperty] private string? unpricedNote;
        [ObservableProperty] private Visibility unpricedNoteVisibility = Visibility.Collapsed;

        public IReadOnlyList<(CostRange Range, string Label)> RangeOptions { get; } = new (CostRange, string)[]
        {
            (CostRange.Today, "Today"),
            (CostRange.Yesterday, "Yesterday"),
            (CostRange.Last7Days, "7 Days"),
            (CostRange.Last30Days, "30 Days"),
        };

        partial void OnSelectedRangeChanged(CostRange value) => Project();

        public async Task LoadAsync()
        {
            IsLoading = true;
            try
            {
                var windows = await _service.ComputeAsync().ConfigureAwait(false);
                _dispatcher.TryEnqueue(() =>
                {
                    _windows = windows;
                    Project();
                    IsLoading = false;
                });
            }
            catch
            {
                _dispatcher.TryEnqueue(() => { IsLoading = false; IsEmpty = true; });
            }
        }

        private void Project()
        {
            Segments.Clear();
            if (_windows is null || !_windows.TryGetValue(SelectedRange, out var window) || window.TotalCostUsd <= 0)
            {
                TotalText = "$0.00";
                UnpricedNote = null;
                IsEmpty = _windows is not null;
                return;
            }

            IsEmpty = false;
            double total = window.TotalCostUsd;
            TotalText = CostFormatting.MoneyCompact(total);

            int i = 0;
            foreach (var pc in window.Providers.Where(p => p.CostUsd > 0))
            {
                Segments.Add(new CostSegmentViewModel
                {
                    Provider = pc.Provider,
                    DisplayName = ProviderPalette.Name(pc.Provider),
                    Color = ProviderPalette.Of(pc.Provider, i++),
                    CostUsd = pc.CostUsd,
                    Fraction = pc.CostUsd / total,
                });
            }

            UnpricedNote = window.UnpricedTokens > 0
                ? $"{CostFormatting.Tokens(window.UnpricedTokens)} tokens from unpriced models excluded"
                : null;
            UnpricedNoteVisibility = UnpricedNote is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Stable per-provider colors for the donut + legend, with a fallback palette by index.</summary>
    internal static class ProviderPalette
    {
        private static readonly Dictionary<ProviderId, Color> Map = new()
        {
            [ProviderId.Claude] = Color.FromArgb(255, 0xD9, 0x77, 0x57),  // Anthropic clay
            [ProviderId.Codex] = Color.FromArgb(255, 0x10, 0xA3, 0x7F),   // OpenAI green
            [ProviderId.Grok] = Color.FromArgb(255, 0x20, 0x20, 0x20),    // xAI near-black
            [ProviderId.Zai] = Color.FromArgb(255, 0x3B, 0x82, 0xF6),     // GLM blue
            [ProviderId.Cline] = Color.FromArgb(255, 0x8B, 0x5C, 0xF6),   // violet
            [ProviderId.Cursor] = Color.FromArgb(255, 0x6B, 0x72, 0x80),  // graphite
            [ProviderId.Kimi] = Color.FromArgb(255, 0xEC, 0x48, 0x99),    // magenta
        };

        private static readonly Color[] Fallback =
        {
            Color.FromArgb(255, 0x0E, 0x9F, 0x6E),
            Color.FromArgb(255, 0x3B, 0x82, 0xF6),
            Color.FromArgb(255, 0xF5, 0x9E, 0x0B),
            Color.FromArgb(255, 0xEF, 0x44, 0x44),
            Color.FromArgb(255, 0x8B, 0x5C, 0xF6),
            Color.FromArgb(255, 0x14, 0xB8, 0xA6),
        };

        public static Color Of(ProviderId id, int index) =>
            Map.TryGetValue(id, out var c) ? c : Fallback[index % Fallback.Length];

        public static string Name(ProviderId id) => id switch
        {
            ProviderId.Claude => "Claude Code",
            ProviderId.Codex => "Codex",
            ProviderId.Grok => "Grok",
            ProviderId.Zai => "Z.ai",
            ProviderId.Cline => "Cline",
            ProviderId.ClinePass => "ClinePass",
            ProviderId.Cursor => "Cursor",
            ProviderId.Kimi => "Kimi",
            ProviderId.Copilot => "Copilot",
            ProviderId.Devin => "Devin",
            ProviderId.Antigravity => "Antigravity",
            _ => id.ToString(),
        };
    }

    internal static class CostFormatting
    {
        public static string Money(double v) => v >= 0 ? $"${v:N2}" : $"-${-v:N2}";

        /// <summary>Compact center label: $1.2K / $12.3K for big totals, plain dollars otherwise.</summary>
        public static string MoneyCompact(double v)
        {
            if (v >= 1000) return $"${v / 1000:0.#}K";
            return $"${v:0.00}";
        }

        public static string Tokens(long t)
        {
            if (t >= 1_000_000) return $"{t / 1_000_000.0:0.#}M";
            if (t >= 1_000) return $"{t / 1_000.0:0.#}K";
            return t.ToString();
        }
    }
}
