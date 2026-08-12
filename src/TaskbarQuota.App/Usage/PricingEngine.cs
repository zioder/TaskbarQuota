using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TaskbarQuota.Usage;

public sealed class ModelRates
{
    /// <summary>Default tier boundary for LiteLLM "*_above_200k_tokens" pricing fields.</summary>
    public const ulong DefaultLongContextThreshold = 200_000;

    public double InputPerMillion { get; }
    public double OutputPerMillion { get; }
    public double CacheWritePerMillion { get; }
    public double CacheReadPerMillion { get; }
    public double? InputAbove200kPerMillion { get; }
    public double? OutputAbove200kPerMillion { get; }
    public double? CacheWriteAbove200kPerMillion { get; }
    public double? CacheReadAbove200kPerMillion { get; }

    /// <summary>
    /// Per-model input-token boundary above which a request is billed at the
    /// "*_above_200k" tier. When set, the whole request switches tiers (OpenAI
    /// two-stage pricing) once input exceeds it. When null the model uses the
    /// default 200k boundary; because the catalog only ever carries one set of
    /// tier fields this keeps LiteLLM marginal-augment semantics but only
    /// matters when a tier differs from base.
    /// </summary>
    public ulong? LongContextThreshold { get; }

    public ModelRates(double inputPerMillion, double outputPerMillion, double? cacheWritePerMillion = null, double? cacheReadPerMillion = null,
        double? inputAbove200kPerMillion = null, double? outputAbove200kPerMillion = null,
        double? cacheWriteAbove200kPerMillion = null, double? cacheReadAbove200kPerMillion = null,
        ulong? longContextThreshold = null)
    {
        InputPerMillion = inputPerMillion; OutputPerMillion = outputPerMillion;
        CacheWritePerMillion = cacheWritePerMillion ?? inputPerMillion;
        CacheReadPerMillion = cacheReadPerMillion ?? inputPerMillion * 0.1;
        InputAbove200kPerMillion = inputAbove200kPerMillion; OutputAbove200kPerMillion = outputAbove200kPerMillion;
        CacheWriteAbove200kPerMillion = cacheWriteAbove200kPerMillion; CacheReadAbove200kPerMillion = cacheReadAbove200kPerMillion;
        LongContextThreshold = longContextThreshold;
    }

    internal ModelRates Scale(double factor) => new(
        InputPerMillion * factor, OutputPerMillion * factor,
        CacheWritePerMillion * factor, CacheReadPerMillion * factor,
        InputAbove200kPerMillion is { } ia ? ia * factor : null,
        OutputAbove200kPerMillion is { } oa ? oa * factor : null,
        CacheWriteAbove200kPerMillion is { } cwa ? cwa * factor : null,
        CacheReadAbove200kPerMillion is { } cra ? cra * factor : null,
        LongContextThreshold);

    public double CalculateCostDollars(TokenBreakdown tokens)
    {
        // A per-model long-context threshold bills the entire request at the
        // long-context tier once input exceeds it (OpenAI two-stage pricing).
        // Otherwise fall back to the default 200k boundary, still as a
        // whole-request switch given a single tier field set.
        var threshold = LongContextThreshold ?? DefaultLongContextThreshold;
        var longContext = tokens.PromptTokens > threshold;
        var input = longContext ? InputAbove200kPerMillion ?? InputPerMillion : InputPerMillion;
        var output = longContext ? OutputAbove200kPerMillion ?? OutputPerMillion : OutputPerMillion;
        var write = longContext ? CacheWriteAbove200kPerMillion ?? CacheWritePerMillion : CacheWritePerMillion;
        var read = longContext ? CacheReadAbove200kPerMillion ?? CacheReadPerMillion : CacheReadPerMillion;
        var cost = tokens.Input * input
            + tokens.Output * output
            + tokens.CacheWrite5m * write
            + tokens.CacheWrite1h * ((longContext ? InputAbove200kPerMillion ?? InputPerMillion : InputPerMillion) * 2)
            + tokens.CacheRead * read;
        return cost / 1_000_000d;
    }

    public double CalculateCacheSavingsDollars(TokenBreakdown tokens)
    {
        var threshold = LongContextThreshold ?? DefaultLongContextThreshold;
        var longContext = tokens.PromptTokens > threshold;
        var input = longContext ? InputAbove200kPerMillion ?? InputPerMillion : InputPerMillion;
        var read = longContext ? CacheReadAbove200kPerMillion ?? CacheReadPerMillion : CacheReadPerMillion;
        return tokens.CacheRead * Math.Max(0, input - read) / 1_000_000d;
    }

    public double CalculateCostDollars(ulong inputTokens, ulong outputTokens) => (inputTokens * InputPerMillion + outputTokens * OutputPerMillion) / 1_000_000d;
}

public static class PricingEngine
{
    /// <summary>Input-token boundary above which OpenAI bills a request at long-context rates (272K, not 200K).</summary>
    private const ulong OpenAiLongContextThreshold = 272_000;

    private static readonly PricingCatalogStore Store = new();
    private static readonly Dictionary<string, ModelRates> CompatibilityRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-3-7-sonnet"] = new(3, 15, 3.75, .3), ["claude-3-5-sonnet"] = new(3, 15, 3.75, .3),
        ["claude-3-5-haiku"] = new(.8, 4, 1, .08), ["gpt-4o"] = new(2.5, 10, 2.5, 1.25),
        ["o3-mini"] = new(1.1, 4.4, 1.1, .55), ["deepseek-r1"] = new(.55, 2.19, .55, .14),
        ["gemini-2.0-flash"] = new(.1, .4), ["grok-3"] = new(3, 15)
    };

    /// <summary>Model names whose two-stage tier starts at OpenAI's 272K input boundary rather than 200K.</summary>
    private static readonly string[] OpenAiLongContextModels = new[]
    {
        "gpt-5", "gpt-5.1", "gpt-5.2", "gpt-5.3", "gpt-5.4", "gpt-5.5",
        "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-4.1", "gpt-4o",
    };

    private static readonly Dictionary<string, ModelRates> OverrideRates = LoadOverrides();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelRates?> ResolveCache = new(StringComparer.OrdinalIgnoreCase);

    public static ModelRates? Resolve(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        var clean = modelName.Trim();
        // Cache whole resolutions (compat + alias + store + override) so repeated
        // per-entry calls across a history walk are O(1) after the first lookup.
        if (ResolveCache.TryGetValue(clean, out var cached)) return cached;
        ModelRates? result = ResolveUncached(clean);
        ResolveCache[clean] = result;
        return result;
    }

    private static ModelRates? ResolveUncached(string modelName)
    {
        if (CompatibilityRates.TryGetValue(modelName, out var compatibility)) return ApplyOpenAiThreshold(modelName, compatibility);
        if (modelName.Contains("claude-3-7-sonnet", StringComparison.OrdinalIgnoreCase)) return CompatibilityRates["claude-3-7-sonnet"];
        if (modelName.Contains("claude-haiku", StringComparison.OrdinalIgnoreCase)) return CompatibilityRates["claude-3-5-haiku"];
        if (modelName.Contains("xai-grok-3", StringComparison.OrdinalIgnoreCase)) return CompatibilityRates["grok-3"];
        var storeRates = Store.Current().Resolve(modelName);
        if (storeRates is null) return null;
        var withOverrides = ApplyOverrides(modelName, storeRates);
        return ApplyOpenAiThreshold(modelName, withOverrides);
    }

    private static ModelRates ApplyOverrides(string modelName, ModelRates rates)
        => OverrideRates.TryGetValue(modelName, out var overrideRates) ? overrideRates : rates;

    private static ModelRates ApplyOpenAiThreshold(string modelName, ModelRates rates)
    {
        if (rates.LongContextThreshold is not null) return rates;
        foreach (var prefix in OpenAiLongContextModels)
        {
            if (modelName.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                return WithThreshold(rates, OpenAiLongContextThreshold);
        }
        return rates;
    }

    private static ModelRates WithThreshold(ModelRates rates, ulong threshold)
        => new(rates.InputPerMillion, rates.OutputPerMillion, rates.CacheWritePerMillion, rates.CacheReadPerMillion,
            rates.InputAbove200kPerMillion, rates.OutputAbove200kPerMillion,
            rates.CacheWriteAbove200kPerMillion, rates.CacheReadAbove200kPerMillion,
            threshold);

    public static double? EstimateCostUsd(string modelName, TokenBreakdown tokens)
        => Resolve(modelName) is { } rates ? rates.CalculateCostDollars(tokens) * (tokens.IsFast ? FastMultiplier(modelName) : 1d) : null;

    public static double? EstimateCostUsd(string modelName, ulong inputTokens, ulong outputTokens)
        => Resolve(modelName) is { } rates ? rates.CalculateCostDollars(inputTokens, outputTokens) : null;

    public static double? EstimateCacheSavingsUsd(string modelName, TokenBreakdown tokens)
        => Resolve(modelName) is { } rates ? rates.CalculateCacheSavingsDollars(tokens) : null;

    private static double FastMultiplier(string modelName)
    {
        // The store already scales catalog fast entries. This fallback is for supplement-priced bases.
        return 1d;
    }

    private static Dictionary<string, ModelRates> LoadOverrides()
    {
        // Optional user-supplied overrides: <LocalAppData>/TaskbarQuota/pricing/overrides.json
        // {"model-name": {"input_per_million": 1.0, "output_per_million": 5.0, ...}}
        try
        {
            var path = Path.Combine(AppStorage.AppDataDirectory, "pricing", "overrides.json");
            if (!File.Exists(path)) return new Dictionary<string, ModelRates>(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, ModelRates>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (PricingCatalogStore.TryReadRateOverride(prop.Value) is { } rates)
                    result[prop.Name] = rates;
            }
            return result;
        }
        catch { return new Dictionary<string, ModelRates>(StringComparer.OrdinalIgnoreCase); }
    }
}
