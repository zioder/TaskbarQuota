using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota.Cost
{
    /// <summary>
    /// Canonical public API list prices, keyed by the canonical model ids <see cref="ModelResolver"/>
    /// produces. Loaded once from <c>model_prices.json</c> shipped beside the exe (LiteLLM per-token
    /// schema), with a compiled-in fallback so pricing works even if the file is missing. A user
    /// override at <c>%LOCALAPPDATA%\TaskbarQuota\model_prices.json</c> wins when present, so prices
    /// can be refreshed without an app update.
    /// </summary>
    public sealed class PricingCatalog
    {
        private readonly IReadOnlyDictionary<string, ModelPrice> _prices;

        private PricingCatalog(IReadOnlyDictionary<string, ModelPrice> prices) => _prices = prices;

        private static PricingCatalog? _instance;
        public static PricingCatalog Instance => _instance ??= Load();

        /// <summary>Price a record at API-equivalent list rates. Returns null when the model is unpriced.</summary>
        public double? CostOf(TokenUsageRecord record)
        {
            // Exact raw-id match wins (case-insensitive) — the catalog keys are real model ids, so a
            // logged "claude-opus-4-8" is priced directly, no heuristic. Fall back to the family
            // resolver only for aliases / unknown ids.
            if (!string.IsNullOrWhiteSpace(record.RawModel) && _prices.TryGetValue(record.RawModel.Trim(), out var exact))
                return exact.CostOf(record);

            string? key = ModelResolver.Resolve(record.RawModel);
            if (key is null || !_prices.TryGetValue(key, out var price))
            {
                if (!string.IsNullOrWhiteSpace(record.RawModel))
                    ModelResolver.NoteUnresolved(record.RawModel);
                return null;
            }
            return price.CostOf(record);
        }

        public bool TryGet(string canonicalKey, out ModelPrice price) => _prices.TryGetValue(canonicalKey, out price!);

        private static PricingCatalog Load()
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var parsed = Parse(File.ReadAllText(path));
                    if (parsed.Count > 0)
                    {
                        Log.Information($"PricingCatalog loaded {parsed.Count} models from {path}");
                        return new PricingCatalog(parsed);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"PricingCatalog failed to load {path}");
                }
            }

            Log.Warning("PricingCatalog using compiled-in fallback prices");
            return new PricingCatalog(Fallback());
        }

        private static IEnumerable<string> CandidatePaths()
        {
            // 1. User override, refreshable without reinstall.
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "TaskbarQuota", "model_prices.json");
            // 2. Shipped beside the exe.
            yield return Path.Combine(AppContext.BaseDirectory, "Cost", "model_prices.json");
            yield return Path.Combine(AppContext.BaseDirectory, "model_prices.json");
        }

        private static Dictionary<string, ModelPrice> Parse(string json)
        {
            var result = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue; // skip "_comment"
                double input = GetD(prop.Value, "input_cost_per_token");
                double output = GetD(prop.Value, "output_cost_per_token");
                if (input <= 0 && output <= 0) continue;
                result[prop.Name] = new ModelPrice
                {
                    InputCostPerToken = input,
                    OutputCostPerToken = output,
                    CacheReadCostPerToken = GetD(prop.Value, "cache_read_input_token_cost", input),
                    CacheWriteCostPerToken = GetD(prop.Value, "cache_creation_input_token_cost", input),
                };
            }
            return result;
        }

        private static double GetD(JsonElement obj, string name, double fallback = 0) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

        // Compiled-in safety net (rates derived vs ccusage) so a missing JSON never zeroes the feature.
        private static Dictionary<string, ModelPrice> Fallback() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-4-8"] = new() { InputCostPerToken = 5e-6, OutputCostPerToken = 2.5e-5, CacheReadCostPerToken = 5e-7, CacheWriteCostPerToken = 1e-5 },
            ["claude-fable-5"] = new() { InputCostPerToken = 1e-5, OutputCostPerToken = 5e-5, CacheReadCostPerToken = 1e-6, CacheWriteCostPerToken = 2e-5 },
            ["claude-sonnet-5"] = new() { InputCostPerToken = 3e-6, OutputCostPerToken = 1.5e-5, CacheReadCostPerToken = 3e-7, CacheWriteCostPerToken = 3.75e-6 },
            ["claude-sonnet-4-6"] = new() { InputCostPerToken = 2.13e-6, OutputCostPerToken = 1.065e-5, CacheReadCostPerToken = 2.13e-7, CacheWriteCostPerToken = 4.26e-6 },
            ["claude-haiku-4-5"] = new() { InputCostPerToken = 8e-7, OutputCostPerToken = 4e-6, CacheReadCostPerToken = 8e-8, CacheWriteCostPerToken = 1.5e-6 },
            ["gpt-5.5"] = new() { InputCostPerToken = 5e-6, OutputCostPerToken = 2.5e-5, CacheReadCostPerToken = 5e-7, CacheWriteCostPerToken = 5e-6 },
            ["gpt-5.3-codex"] = new() { InputCostPerToken = 1.75e-6, OutputCostPerToken = 1.4e-5, CacheReadCostPerToken = 1.75e-7, CacheWriteCostPerToken = 1.75e-6 },
            ["gpt-5.4-mini"] = new() { InputCostPerToken = 7.3e-7, OutputCostPerToken = 5.85e-6, CacheReadCostPerToken = 7.3e-8, CacheWriteCostPerToken = 7.3e-7 },
            ["glm-5.2"] = new() { InputCostPerToken = 1.53e-6, OutputCostPerToken = 4.47e-6, CacheReadCostPerToken = 2.5e-7, CacheWriteCostPerToken = 1.53e-6 },
            ["glm-4.6"] = new() { InputCostPerToken = 6e-7, OutputCostPerToken = 2.2e-6, CacheReadCostPerToken = 1.1e-7, CacheWriteCostPerToken = 6e-7 },
            ["kimi-k2.7-code"] = new() { InputCostPerToken = 9.5e-7, OutputCostPerToken = 4e-6, CacheReadCostPerToken = 1.9e-7, CacheWriteCostPerToken = 9.5e-7 },
            ["grok-4"] = new() { InputCostPerToken = 3e-6, OutputCostPerToken = 1.5e-5, CacheReadCostPerToken = 7.5e-7, CacheWriteCostPerToken = 3e-6 },
            ["grok-code-fast-1"] = new() { InputCostPerToken = 2e-7, OutputCostPerToken = 1.5e-6, CacheReadCostPerToken = 2e-8, CacheWriteCostPerToken = 2e-7 },
            ["gemini-3-pro"] = new() { InputCostPerToken = 1.25e-6, OutputCostPerToken = 1e-5, CacheReadCostPerToken = 3.125e-7, CacheWriteCostPerToken = 1.25e-6 },
            ["gemini-3-flash"] = new() { InputCostPerToken = 3e-7, OutputCostPerToken = 2.5e-6, CacheReadCostPerToken = 7.5e-8, CacheWriteCostPerToken = 3e-7 },
        };
    }
}
