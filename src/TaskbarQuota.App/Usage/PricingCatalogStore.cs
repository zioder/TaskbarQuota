using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TaskbarQuota.Usage;

/// <summary>
/// Pricing store: LiteLLM (refreshed at runtime, the table T3 Code and ccusage price against) wins,
/// the bundled supplement fills models LiteLLM has not published yet, and models.dev comes last.
/// Bundled snapshots make the estimator useful offline; cached refreshed feeds are preferred at runtime.
/// </summary>
internal sealed class PricingCatalogStore
{
    private const string LiteLlmUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    private const string ModelsDevUrl = "https://models.dev/api.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);
    private readonly object _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private Catalog _catalog;
    private bool _refreshStarted;

    public PricingCatalogStore() => _catalog = LoadCatalog();

    public Catalog Current()
    {
        lock (_gate)
        {
            if (!_refreshStarted && RefreshDue())
            {
                _refreshStarted = true;
                _ = Task.Run(RefreshAsync);
            }
            return _catalog;
        }
    }

    private bool RefreshDue()
    {
        var statePath = Path.Combine(AppStorage.AppDataDirectory, "pricing", "state.json");
        try
        {
            if (!File.Exists(statePath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            var last = doc.RootElement.TryGetProperty("lastAttemptUtc", out var p) && p.TryGetDateTimeOffset(out var dt) ? dt : DateTimeOffset.MinValue;
            return DateTimeOffset.UtcNow - last >= RefreshInterval;
        }
        catch { return true; }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var dir = Path.Combine(AppStorage.AppDataDirectory, "pricing");
            Directory.CreateDirectory(dir);
            var next = _catalog;
            foreach (var source in new[] { ("litellm", LiteLlmUrl), ("modelsdev", ModelsDevUrl) })
            {
                var path = Path.Combine(dir, source.Item1 + ".json");
                var etagPath = path + ".etag";
                using var request = new HttpRequestMessage(HttpMethod.Get, source.Item2);
                if (File.Exists(etagPath)) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(File.ReadAllText(etagPath).Trim()));
                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified || !response.IsSuccessStatusCode) continue;
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var validated = JsonDocument.Parse(json);
                WriteAtomic(path, json);
                if (response.Headers.ETag is not null) WriteAtomic(etagPath, response.Headers.ETag.Tag);
            }
            lock (_gate) _catalog = LoadCatalog();
            WriteAtomic(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(new { lastAttemptUtc = DateTimeOffset.UtcNow }));
        }
        catch
        {
            try
            {
                var dir = Path.Combine(AppStorage.AppDataDirectory, "pricing");
                Directory.CreateDirectory(dir);
                WriteAtomic(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(new { lastAttemptUtc = DateTimeOffset.UtcNow.Subtract(RefreshInterval - RetryInterval) }));
            }
            catch { }
        }
    }

    private Catalog LoadCatalog()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Pricing");
        var cacheDir = Path.Combine(AppStorage.AppDataDirectory, "pricing");
        var supplement = ReadSupplement(Path.Combine(baseDir, "pricing_supplement.json"));
        var primary = ReadCatalog(File.Exists(Path.Combine(cacheDir, "litellm.json")) ? Path.Combine(cacheDir, "litellm.json") : Path.Combine(baseDir, "pricing_litellm_snapshot.json"));
        var secondary = ReadCatalog(File.Exists(Path.Combine(cacheDir, "modelsdev.json")) ? Path.Combine(cacheDir, "modelsdev.json") : Path.Combine(baseDir, "pricing_models_dev_snapshot.json"));
        return new Catalog(supplement, primary, secondary);
    }

    private static Supplement ReadSupplement(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var rates = new Dictionary<string, ModelRates>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("pricing", out var pricing)) foreach (var p in pricing.EnumerateObject()) if (ReadRate(p.Value) is { } r) rates[p.Name] = r;
            var aliases = new List<(Regex Pattern, string Canonical)>();
            if (root.TryGetProperty("alias_rules", out var rules)) foreach (var rule in rules.EnumerateArray())
                try { aliases.Add((new Regex(rule.GetProperty("pattern").GetString()!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), rule.GetProperty("canonical").GetString()!)); } catch { }
            var fast = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("fast_multipliers", out var fastJson)) foreach (var p in fastJson.EnumerateObject()) if (p.Value.TryGetDouble(out var value)) fast[p.Name] = value;
            return new Supplement(rates, aliases, fast);
        }
        catch { return new Supplement(new(StringComparer.OrdinalIgnoreCase), new(), new(StringComparer.OrdinalIgnoreCase)); }
    }

    private static Dictionary<string, ModelRates> ReadCatalog(string path)
    {
        var result = new Dictionary<string, ModelRates>(StringComparer.OrdinalIgnoreCase);
        try { using var doc = JsonDocument.Parse(File.ReadAllText(path)); Walk(doc.RootElement, result, null); } catch { }
        return result;
    }

    private static void Walk(JsonElement element, Dictionary<string, ModelRates> result, string? key)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        if (key is not null && ReadRate(element) is { } direct) result[key] = direct;
        foreach (var p in element.EnumerateObject())
        {
            if (p.NameEquals("models") && p.Value.ValueKind == JsonValueKind.Object)
                foreach (var model in p.Value.EnumerateObject()) Walk(model.Value, result, model.Name);
            else if (p.Value.ValueKind == JsonValueKind.Object) Walk(p.Value, result, p.Name);
        }
    }

    private static ModelRates? ReadRate(JsonElement value)
    {
        var rates = ReadTier(value, "");
        if (rates is null) return null;
        // LiteLLM publishes faster service either as "*_priority" rates (OpenAI priority tier)
        // or as a provider_specific_entry.fast multiple (Claude fast mode). The compact
        // snapshot carries the same data as "f*" rates and a "fast" multiple.
        var fast = ReadTier(value, "_priority", rates);
        if (fast is null && (Number(value, "fast")
            ?? (value.TryGetProperty("provider_specific_entry", out var specific) ? Number(specific, "fast") : null)) is { } multiple && multiple > 0)
            fast = rates.Scale(multiple);
        return fast is null ? rates : new ModelRates(rates.InputPerMillion, rates.OutputPerMillion, rates.CacheWritePerMillion, rates.CacheReadPerMillion,
            rates.InputAbove200kPerMillion, rates.OutputAbove200kPerMillion, rates.CacheWriteAbove200kPerMillion, rates.CacheReadAbove200kPerMillion,
            rates.LongContextThreshold) { Fast = fast };
    }

    /// <summary>
    /// Reads one rate set. <paramref name="suffix"/> selects a LiteLLM tier such as <c>_priority</c>
    /// (compact form: an "f" prefix). A faster tier that omits cache rates keeps the standard tier's
    /// cache-to-input ratio.
    /// </summary>
    private static ModelRates? ReadTier(JsonElement value, string suffix, ModelRates? standard = null)
    {
        var compact = suffix.Length == 0 ? "" : "f";
        // models.dev's nested cost object is already expressed in USD per million tokens.
        // Only LiteLLM's explicit *_cost_per_token fields need the 1,000,000 conversion.
        double? Rate(string compactKey, string perMillionKey, string liteLlmKey, string modelsDevKey)
            => Number(value, compact + compactKey)
                ?? (suffix.Length == 0 ? Number(value, perMillionKey) : null)
                ?? Number(value, liteLlmKey + suffix) * 1_000_000
                ?? (suffix.Length == 0 && value.TryGetProperty("cost", out var cost) ? Number(cost, modelsDevKey) : null);
        var input = Rate("i", "input_per_million", "input_cost_per_token", "input");
        var output = Rate("o", "output_per_million", "output_cost_per_token", "output");
        if (input is null || output is null) return null;
        var cacheRead = Rate("cr", "cache_read_per_million", "cache_read_input_token_cost", "cache_read");
        var cacheWrite = Rate("cw", "cache_write_per_million", "cache_creation_input_token_cost", "cache_write");
        if (standard is not null && standard.InputPerMillion > 0)
        {
            cacheRead ??= standard.CacheReadPerMillion / standard.InputPerMillion * input;
            cacheWrite ??= standard.CacheWritePerMillion / standard.InputPerMillion * input;
        }

        // LiteLLM names the long-context tier by its boundary: Anthropic/Gemini at 200K,
        // OpenAI at 272K. Either switches the whole request once input crosses it.
        var tier = Number(value, "input_cost_per_token_above_272k_tokens" + suffix) is not null ? "272k" : "200k";
        double? Above(string compactKey, string perMillionKey, string liteLlmKey)
            => Number(value, compact + compactKey)
                ?? (suffix.Length == 0 ? Number(value, perMillionKey) : null)
                ?? Number(value, $"{liteLlmKey}_above_{tier}_tokens{suffix}") * 1_000_000;
        var threshold = Number(value, "ctx") ?? Number(value, "long_context_threshold") ?? Number(value, "ctx_threshold") ?? Number(value, "long_context_threshold_tokens")
            ?? (tier == "272k" ? 272_000 : null);
        return new ModelRates(input.Value, output.Value, cacheWrite ?? input, cacheRead ?? input * 0.1,
            Above("ia", "input_above_200k_per_million", "input_cost_per_token"),
            Above("oa", "output_above_200k_per_million", "output_cost_per_token"),
            Above("cwa", "cache_write_above_200k_per_million", "cache_creation_input_token_cost"),
            Above("cra", "cache_read_above_200k_per_million", "cache_read_input_token_cost"),
            threshold is { } t && t > 0 ? (ulong)t : null);
    }

    /// <summary>Parses a user-supplied pricing override entry (<c>overrides.json</c>).</summary>
    internal static ModelRates? TryReadRateOverride(JsonElement value) => ReadRate(value);

    private static double? Number(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : null;

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp"; File.WriteAllText(temp, content); File.Move(temp, path, true);
    }

    internal sealed record Catalog(Supplement Supplement, Dictionary<string, ModelRates> Primary, Dictionary<string, ModelRates> Secondary)
    {
        public ModelRates? Resolve(string model)
        {
            var canonical = Supplement.Aliases.FirstOrDefault(a => a.Pattern.IsMatch(model)).Canonical;
            if (!string.IsNullOrEmpty(canonical)) model = canonical;
            if (Primary.TryGetValue(model, out var rate)) return rate;
            if (Supplement.Rates.TryGetValue(model, out rate)) return rate;
            if (model.EndsWith("-fast", StringComparison.OrdinalIgnoreCase))
            {
                // A "-fast" model bills at its base model's fast tier: LiteLLM's published
                // priority rates first, else the supplement's multiple for models it prices.
                var baseName = model[..^5];
                if ((Primary.TryGetValue(baseName, out rate) || Supplement.Rates.TryGetValue(baseName, out rate)
                    || Secondary.TryGetValue(baseName, out rate)) && rate is not null)
                    return rate.Fast ?? rate.Scale(Supplement.Fast.TryGetValue(baseName, out var multiple) ? multiple : 1d);
            }
            if (Secondary.TryGetValue(model, out rate)) return rate;
            var match = Primary.FirstOrDefault(p => p.Key.EndsWith(model, StringComparison.OrdinalIgnoreCase));
            return match.Key is null ? null : match.Value;
        }
    }

    internal sealed record Supplement(Dictionary<string, ModelRates> Rates, List<(Regex Pattern, string Canonical)> Aliases, Dictionary<string, double> Fast);
}
