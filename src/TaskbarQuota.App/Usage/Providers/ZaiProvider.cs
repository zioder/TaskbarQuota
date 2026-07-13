using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using TaskbarQuota;

namespace TaskbarQuota.Usage.Providers
{
    public sealed class ZaiProvider : IUsageProvider
    {
        private const string DefaultGlobalBaseUrl = "https://api.z.ai";
        private const string QuotaPath = "api/monitor/usage/quota/limit";
        private const string DashboardUrl = "https://z.ai/manage-apikey/coding-plan/personal/my-plan";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public ProviderId Id => ProviderId.Zai;
        public string DisplayName => "Z.ai";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;


        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var apiKey = LoadApiKey();
            var baseUrl = ResolveBaseUrl();
            var quotaUrl = BuildQuotaUrl(baseUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, quotaUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "z.ai API key invalid or expired. Update your API key.");
            if ((int)response.StatusCode == 429)
                throw new ProviderException(ProviderErrorKind.RateLimited, "z.ai API rate limited. Try again later.");
            if (!response.IsSuccessStatusCode)
            {
                int code2 = (int)response.StatusCode;
                throw new ProviderException(ProviderErrorKind.Other, $"z.ai API returned {code2}");
            }
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return BuildResult(doc.RootElement);
        }


        internal static ProviderFetchResult BuildResult(JsonElement root)
        {
            if (!root.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
            {
                var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "Unknown error";
                throw new ProviderException(ProviderErrorKind.Other, $"z.ai API error: {msg}");
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                throw new ProviderException(ProviderErrorKind.Parse, "z.ai API returned no data.");
            string? planName = null;
            foreach (var key in new[] { "planName", "plan", "plan_type", "packageName" })
            {
                if (data.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var raw = p.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(raw)) { planName = raw; break; }
                }
            }
            var tokenLimits = new List<LimitEntry>();
            LimitEntry? timeLimit = null;
            if (data.TryGetProperty("limits", out var limitsArr) && limitsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in limitsArr.EnumerateArray())
                {
                    var entry = ParseLimitEntry(limit);
                    if (entry is null) continue;
                    if (entry.Value.Type == "TOKENS_LIMIT") tokenLimits.Add(entry.Value);
                    else if (entry.Value.Type == "TIME_LIMIT") timeLimit = entry;
                }
            }
            LimitEntry? primaryLimit;
            LimitEntry? sessionTokenLimit = null;
            if (tokenLimits.Count >= 2)
            {
                var sorted = tokenLimits.OrderBy(e => e.WindowMinutes ?? int.MaxValue).ToList();
                sessionTokenLimit = sorted[0];
                primaryLimit = sorted[^1];
            }
            else { primaryLimit = tokenLimits.Count > 0 ? tokenLimits[0] : (LimitEntry?)null; }
            // z.ai Coding Plan exposes a 5-hour prompt pool and a 7-day quota.
            // Put the shorter token window in the session-style primary row and the
            // longer token window in the weekly secondary row. TIME_LIMIT is the
            // separate monthly MCP/tool-call pool.
            var mainLimit = sessionTokenLimit ?? primaryLimit ?? timeLimit;
            var primary = mainLimit.HasValue ? MakeRateWindow(mainLimit.Value) : new RateWindow(0, windowMinutes: null, resetAt: null, resetDescription: null);
            var usage = new UsageSnapshot(primary);
            if (sessionTokenLimit.HasValue && primaryLimit.HasValue)
                usage.Secondary = MakeRateWindow(primaryLimit.Value);
            if (timeLimit.HasValue)
                usage.ExtraRateWindows.Add(new NamedRateWindow("zai-mcp", "MCP", MakeRateWindow(timeLimit.Value, label: "MCP")));
            if (!string.IsNullOrWhiteSpace(planName)) usage.LoginMethod = planName;
            usage.UsageDashboardUrl = DashboardUrl;
            return new ProviderFetchResult(usage, "api");
        }

        private static RateWindow MakeRateWindow(LimitEntry entry, string? label = null)
        {
            double percent = entry.UsedPercent;
            int? windowMinutes = entry.WindowMinutes;
            string? resetDesc = entry.NextResetTime.HasValue
                ? CodexProvider.FormatResetCountdown(entry.NextResetTime)
                : entry.WindowDescription != null ? $"{entry.WindowDescription} window" : null;
            return new RateWindow(percent, windowMinutes: windowMinutes, resetAt: entry.NextResetTime, resetDescription: resetDesc, label: label);
        }


        private static LimitEntry? ParseLimitEntry(JsonElement el)
        {
            if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) return null;
            var type = typeEl.GetString()!;
            if (type != "TOKENS_LIMIT" && type != "TIME_LIMIT") return null;
            int unit = el.TryGetProperty("unit", out var unitEl) ? unitEl.GetInt32() : 0;
            int number = el.TryGetProperty("number", out var numEl) ? numEl.GetInt32() : 0;
            int? usageVal = el.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Number ? usageEl.GetInt32() : null;
            int? currentValue = el.TryGetProperty("currentValue", out var cvEl) && cvEl.ValueKind == JsonValueKind.Number ? cvEl.GetInt32() : null;
            int? remaining = el.TryGetProperty("remaining", out var remEl) && remEl.ValueKind == JsonValueKind.Number ? remEl.GetInt32() : null;
            int percentage = el.TryGetProperty("percentage", out var pctEl) ? pctEl.GetInt32() : 0;
            DateTimeOffset? nextReset = null;
            if (el.TryGetProperty("nextResetTime", out var resetEl) && resetEl.ValueKind == JsonValueKind.Number)
            {
                long ts = resetEl.GetInt64();
                if (ts > 0) nextReset = DateTimeOffset.FromUnixTimeMilliseconds(ts);
            }
            return new LimitEntry(type, unit, number, usageVal, currentValue, remaining, percentage, nextReset);
        }


        private readonly record struct LimitEntry(string Type, int Unit, int Number, int? Usage, int? CurrentValue, int? Remaining, int Percentage, DateTimeOffset? NextResetTime)
        {
            public double UsedPercent => ComputedUsedPercent ?? Percentage;
            private double? ComputedUsedPercent
            {
                get
                {
                    if (Usage is not { } limit || limit <= 0) return null;
                    int? usedRaw;
                    if (Remaining is { } rem)
                    {
                        int u = limit - rem;
                        usedRaw = CurrentValue.HasValue ? Math.Max(u, CurrentValue.Value) : u;
                    }
                    else if (CurrentValue is { } cv) { usedRaw = cv; }
                    else { return null; }
                    int used = Math.Max(0, Math.Min(limit, usedRaw.Value));
                    return Math.Min(100, Math.Max(0, (double)used / limit * 100));
                }
            }
            public int? WindowMinutes => Number <= 0 ? null : Unit switch
            {
                5 => Number,
                3 => Number * 60,
                1 => Number * 24 * 60,
                6 => Number * 7 * 24 * 60,
                _ => null,
            };
            public string? WindowDescription
            {
                get
                {
                    if (Number <= 0) return null;
                    var u = Unit switch { 5 => "minute", 3 => "hour", 1 => "day", 6 => "week", _ => (string?)null };
                    if (u is null) return null;
                    var suffix = Number == 1 ? u : $"{u}s";
                    return $"{Number} {suffix}";
                }
            }
        }


        internal static string LoadApiKey()
        {
            var fromEnv = Environment.GetEnvironmentVariable("Z_AI_API_KEY")?.Trim();
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv!;
            var fromStore = CredentialStore.Instance.ApiKey(ProviderId.Zai, "Z_AI_API_KEY");
            if (!string.IsNullOrWhiteSpace(fromStore)) return fromStore!;
            var fromZCode = TryLoadApiKeyFromZCodeConfig();
            if (!string.IsNullOrWhiteSpace(fromZCode)) return fromZCode!;
            if (!ProviderInstallDetector.IsInstalled(ProviderId.Zai))
                throw new ProviderException(ProviderErrorKind.NotInstalled, ProviderInstallDetector.NotInstalledMessage(ProviderId.Zai));
            throw new ProviderException(ProviderErrorKind.AuthRequired, "z.ai API key not found. Set Z_AI_API_KEY or add it in Settings.");
        }

        internal static string? TryLoadApiKeyFromZCodeConfig(string? userProfileOverride = null)
        {
            var profile = userProfileOverride
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(profile, ".zcode", "v2", "config.json");
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("provider", out var providers)
                    || !providers.TryGetProperty("builtin:zai-coding-plan", out var provider)
                    || !provider.TryGetProperty("options", out var options)
                    || !options.TryGetProperty("apiKey", out var keyElement))
                    return null;
                var key = keyElement.GetString()?.Trim();
                return string.IsNullOrEmpty(key) ? null : key;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (JsonException) { return null; }
        }

        private static string ResolveBaseUrl()
        {
            var quotaUrl = Environment.GetEnvironmentVariable("Z_AI_QUOTA_URL")?.Trim();
            if (!string.IsNullOrEmpty(quotaUrl)) return quotaUrl!;
            var apiHost = Environment.GetEnvironmentVariable("Z_AI_API_HOST")?.Trim();
            if (!string.IsNullOrEmpty(apiHost)) return apiHost!.TrimEnd('/');
            return DefaultGlobalBaseUrl;
        }

        private static string BuildQuotaUrl(string baseUrl)
        {
            var trimmed = baseUrl.TrimEnd('/');
            if (trimmed.Contains(QuotaPath, StringComparison.OrdinalIgnoreCase)) return trimmed;
            return $"{trimmed}/{QuotaPath}";
        }
    }
}
