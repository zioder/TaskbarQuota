using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
    public sealed class TotalSpendSliceViewModel
    {
        public ProviderId ProviderId { get; }
        public string ProviderName { get; }
        public double Value { get; }
        public ulong Tokens { get; }
        public double? CostUsd { get; }

        /// <summary>This provider's share (0-100) of the combined value across all slices.</summary>
        public double SharePercent { get; }

        public string TokensText => $"{Tokens:N0} tok";
        public string CostText => CostUsd.HasValue ? $"${CostUsd.Value:F2}" : "—";
        public string SummaryValueText { get; }
        public string ShareText => $"{SharePercent:0.#}%";

        /// <summary>Distinct brand-ish dot color so each provider is scannable in the breakdown.</summary>
        public Brush DotBrush { get; }

        public TotalSpendSliceViewModel(
            ProviderId providerId,
            string providerName,
            double value,
            ulong tokens,
            double? costUsd,
            double sharePercent,
            string selectedMetric)
        {
            ProviderId = providerId;
            ProviderName = providerName;
            Value = value;
            Tokens = tokens;
            CostUsd = costUsd;
            SharePercent = sharePercent;
            SummaryValueText = selectedMetric == "tokens" ? $"{tokens:N0} tokens" : CostText;
            DotBrush = new SolidColorBrush(ProviderColor(providerId));
        }

        internal static Color ProviderColor(ProviderId id) => id switch
        {
            ProviderId.Claude => Color.FromArgb(255, 217, 119, 87),
            ProviderId.Codex => Color.FromArgb(255, 16, 163, 127),
            ProviderId.Cursor => Color.FromArgb(255, 169, 112, 255),
            ProviderId.Antigravity => Color.FromArgb(255, 66, 133, 244),
            ProviderId.OpenCode => Color.FromArgb(255, 124, 58, 237),
            ProviderId.OpenCodeGo => Color.FromArgb(255, 167, 139, 250),
            ProviderId.Copilot => Color.FromArgb(255, 137, 87, 229),
            ProviderId.Grok => Color.FromArgb(255, 226, 192, 141),
            ProviderId.Devin => Color.FromArgb(255, 61, 90, 254),
            ProviderId.Cline => Color.FromArgb(255, 236, 105, 93),
            ProviderId.ClinePass => Color.FromArgb(255, 241, 138, 84),
            ProviderId.Zai => Color.FromArgb(255, 45, 212, 191),
            ProviderId.Kimi => Color.FromArgb(255, 47, 84, 235),
            _ => Color.FromArgb(255, 128, 128, 128),
        };
    }
}
