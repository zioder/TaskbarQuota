using System;
using System.Collections.Generic;

namespace TaskbarQuota.Cost
{
    /// <summary>
    /// A single priced token-usage event read from a provider's local log. Token counts are raw
    /// (not billed): the whole point of the Cost feature is the <em>counterfactual API cost</em> —
    /// what these tokens would have cost at public pay-as-you-go list rates, regardless of the
    /// (subsidized) subscription the user actually paid. See <see cref="PricingCatalog"/>.
    /// </summary>
    public sealed class TokenUsageRecord
    {
        /// <summary>The originating provider (Claude Code, Codex, Grok, …).</summary>
        public required Usage.ProviderId Provider { get; init; }

        /// <summary>Provider-internal model id as written to the log (e.g. "claude-opus-4-8", "GLM-5.2").</summary>
        public required string RawModel { get; init; }

        /// <summary>UTC instant of the event; bucketed to a local day by the rollup.</summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>Input tokens excluding any cache read/write (billed at the model's input rate).</summary>
        public long InputTokens { get; init; }

        /// <summary>Output/completion tokens (includes reasoning tokens for providers that split them).</summary>
        public long OutputTokens { get; init; }

        /// <summary>Cache-read tokens (Anthropic ~0.1x input). Zero for providers that don't cache.</summary>
        public long CacheReadTokens { get; init; }

        /// <summary>Cache-creation/write tokens (Anthropic ~1.25x input).</summary>
        public long CacheWriteTokens { get; init; }

        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
    }

    /// <summary>Public API list price for one model, in USD <b>per single token</b> (LiteLLM schema).</summary>
    public sealed class ModelPrice
    {
        public double InputCostPerToken { get; init; }
        public double OutputCostPerToken { get; init; }
        /// <summary>Cache-read rate. Defaults to <see cref="InputCostPerToken"/> when a model omits it.</summary>
        public double CacheReadCostPerToken { get; init; }
        /// <summary>Cache-write/creation rate. Defaults to <see cref="InputCostPerToken"/> when omitted.</summary>
        public double CacheWriteCostPerToken { get; init; }

        public double CostOf(TokenUsageRecord r) =>
            r.InputTokens * InputCostPerToken
            + r.OutputTokens * OutputCostPerToken
            + r.CacheReadTokens * CacheReadCostPerToken
            + r.CacheWriteTokens * CacheWriteCostPerToken;
    }

    /// <summary>Per-provider cost total within a window, plus its share of unpriced tokens.</summary>
    public sealed class ProviderCost
    {
        public required Usage.ProviderId Provider { get; init; }
        public double CostUsd { get; set; }
        public long PricedTokens { get; set; }
        /// <summary>Tokens whose model had no public price → excluded from <see cref="CostUsd"/>.</summary>
        public long UnpricedTokens { get; set; }
    }

    /// <summary>Aggregated cost for one time window (Today / Yesterday / 7d / 30d).</summary>
    public sealed class CostWindow
    {
        public required CostRange Range { get; init; }
        public double TotalCostUsd { get; set; }
        public long UnpricedTokens { get; set; }
        public List<ProviderCost> Providers { get; } = new();
    }

    public enum CostRange
    {
        Today,
        Yesterday,
        Last7Days,
        Last30Days,
    }
}
