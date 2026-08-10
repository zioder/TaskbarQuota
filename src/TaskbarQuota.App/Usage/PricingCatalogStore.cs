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
/// Supplement-compatible pricing store: the bundled supplement overrides LiteLLM, which overrides
/// models.dev.
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
        var supplement = ReadSupplement(File.Exists(Path.Combine(cacheDir, "supplement.json")) ? Path.Combine(cacheDir, "supplement.json") : Path.Combine(baseDir, "pricing_supplement.json"));
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
        // models.dev's nested cost object is already expressed in USD per million tokens.
        // Only LiteLLM's explicit *_cost_per_token fields need the 1,000,000 conversion.
        var input = Number(value, "i") ?? Number(value, "input_per_million") ?? Number(value, "input_cost_per_token") * 1_000_000 ?? (value.TryGetProperty("cost", out var cost) ? Number(cost, "input") : null);
        var output = Number(value, "o") ?? Number(value, "output_per_million") ?? Number(value, "output_cost_per_token") * 1_000_000 ?? (value.TryGetProperty("cost", out var cost2) ? Number(cost2, "output") : null);
        if (input is null || output is null) return null;
        var cacheRead = Number(value, "cr") ?? Number(value, "cache_read_per_million") ?? Number(value, "cache_read_input_token_cost") * 1_000_000 ?? (value.TryGetProperty("cost", out var c) ? Number(c, "cache_read") : null);
        var cacheWrite = Number(value, "cw") ?? Number(value, "cache_write_per_million") ?? Number(value, "cache_creation_input_token_cost") * 1_000_000 ?? (value.TryGetProperty("cost", out var c2) ? Number(c2, "cache_write") : null);
        var threshold = Number(value, "long_context_threshold") ?? Number(value, "ctx_threshold") ?? Number(value, "long_context_threshold_tokens");
        return new ModelRates(input.Value, output.Value, cacheWrite ?? input, cacheRead ?? input * 0.1,
            Number(value, "ia") ?? Number(value, "input_above_200k_per_million"),
            Number(value, "oa") ?? Number(value, "output_above_200k_per_million"),
            Number(value, "cwa") ?? Number(value, "cache_write_above_200k_per_million"),
            Number(value, "cra") ?? Number(value, "cache_read_above_200k_per_million"),
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
            if (Supplement.Rates.TryGetValue(model, out var rate)) return rate;
            if (Primary.TryGetValue(model, out rate)) return ApplyFast(model, rate);
            if (model.EndsWith("-fast", StringComparison.OrdinalIgnoreCase) && Supplement.Rates.TryGetValue(model[..^5], out rate))
                return rate.Scale(Supplement.Fast.TryGetValue(model[..^5], out var supplementMultiplier) ? supplementMultiplier : 1d);
            if (model.EndsWith("-fast", StringComparison.OrdinalIgnoreCase) && Primary.TryGetValue(model[..^5], out rate)) return ApplyFast(model, rate);
            if (Secondary.TryGetValue(model, out rate)) return ApplyFast(model, rate);
            var match = Primary.FirstOrDefault(p => p.Key.EndsWith(model, StringComparison.OrdinalIgnoreCase));
            return match.Key is null ? null : ApplyFast(model, match.Value);
        }
        private ModelRates ApplyFast(string name, ModelRates rate) => name.EndsWith("-fast", StringComparison.OrdinalIgnoreCase) && Supplement.Fast.TryGetValue(name[..^5], out var m) ? rate.Scale(m) : rate;
    }

    internal sealed record Supplement(Dictionary<string, ModelRates> Rates, List<(Regex Pattern, string Canonical)> Aliases, Dictionary<string, double> Fast);
}
