using System;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
    public sealed class ModelUsageItemViewModel
    {
        public string ModelName { get; }
        public ulong TotalTokens { get; }
        public double? CostUsd { get; }
        public double RelativePercent { get; }

        public string TokensText => $"{TotalTokens:N0} tok";
        public string CostText => CostUsd.HasValue ? $"${CostUsd.Value:F2}" : "—";

        public ModelUsageItemViewModel(ModelUsageEntry entry, ulong maxTokens)
        {
            ModelName = entry.Model;
            TotalTokens = entry.TotalTokens;
            CostUsd = entry.CostUsd;
            RelativePercent = maxTokens > 0 ? (double)TotalTokens / maxTokens * 100.0 : 0;
        }
    }
}
