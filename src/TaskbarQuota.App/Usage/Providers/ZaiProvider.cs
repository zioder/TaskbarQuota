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
        private const string SubscriptionPath = "api/biz/subscription/list";
        private static readonly TimeSpan SubscriptionMetadataTimeout = TimeSpan.FromSeconds(2);
        private const string DashboardUrl = "https://z.ai/manage-apikey/coding-plan/personal/my-plan";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        // Current Z.ai individual credit products published by ZCode's live client catalogue on
        // 2026-08-13. Tier names are deliberately not enough: legacy products reused Lite/Pro/Max.
        private static readonly HashSet<string> CurrentCreditProductIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "product-52c6b5", "product-448ee2", "product-7e6099", // monthly
            "product-1c613e", "product-9194ae", "product-7642bb", // quarterly
            "product-2d5858", "product-074f43", "product-1f179d", // annual
        };

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
            var subscriptionUrl = BuildSubscriptionUrl(quotaUrl);

            // The quota endpoint owns balances while the subscription endpoint owns product identity.
            // Start both together so pairing the responses does not add another network round trip.
            var quotaTask = FetchRequiredJsonAsync(quotaUrl, apiKey, ct);
            using var subscriptionTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            subscriptionTimeoutCts.CancelAfter(SubscriptionMetadataTimeout);
            var subscriptionTask = FetchOptionalJsonAsync(subscriptionUrl, apiKey, subscriptionTimeoutCts.Token, ct);
            await Task.WhenAll((Task)quotaTask, subscriptionTask).ConfigureAwait(false);
            using var quotaDoc = quotaTask.Result;
            using var subscriptionDoc = subscriptionTask.Result;
            return BuildResult(quotaDoc.RootElement, subscriptionDoc?.RootElement);
        }

        private static async Task<JsonDocument> FetchRequiredJsonAsync(string url, string apiKey, CancellationToken ct)
        {
            using var response = await SendAsync(url, apiKey, ct).ConfigureAwait(false);
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
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }

        private static async Task<JsonDocument?> FetchOptionalJsonAsync(
            string url,
            string apiKey,
            CancellationToken requestCt,
            CancellationToken callerCt)
        {
            try
            {
                using var response = await SendAsync(url, apiKey, requestCt).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                using var stream = await response.Content.ReadAsStreamAsync(requestCt).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: requestCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Metadata is optional and has its own short timeout; quota data remains usable when it expires.
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                // Subscription metadata enriches the quota response but must never make usage unavailable.
                return null;
            }
        }

        private static async Task<HttpResponseMessage> SendAsync(string url, string apiKey, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.ParseAdd("application/json");
            return await Http.SendAsync(request, ct).ConfigureAwait(false);
        }


        internal static ProviderFetchResult BuildResult(JsonElement root, JsonElement? subscriptionRoot = null)
        {
            if (!root.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
            {
                var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "Unknown error";
                throw new ProviderException(ProviderErrorKind.Other, $"z.ai API error: {msg}");
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                throw new ProviderException(ProviderErrorKind.Parse, "z.ai API returned no data.");
            var subscription = ParseCurrentSubscription(subscriptionRoot);
            string? planName = subscription?.ProductName;
            foreach (var key in new[] { "planName", "plan", "plan_type", "packageName" })
            {
                if (!string.IsNullOrWhiteSpace(planName)) break;
                if (data.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var raw = p.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(raw)) { planName = raw; break; }
                }
            }
            if (string.IsNullOrWhiteSpace(planName)
                && data.TryGetProperty("level", out var level)
                && level.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(level.GetString()))
                planName = $"GLM Coding {ToTitleCase(level.GetString()!)}";

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
            bool isCreditPlan = IsCurrentCreditPlan(subscription, tokenLimits);
            var pricing = ResolvePricing(isCreditPlan, subscription?.ProductName, DateTimeOffset.UtcNow);
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
            var primary = mainLimit.HasValue
                ? MakeRateWindow(mainLimit.Value)
                : new RateWindow(0, windowMinutes: null, resetAt: null, resetDescription: null);
            var usage = new UsageSnapshot(primary);
            usage.Pricing = pricing;
            if (sessionTokenLimit.HasValue && primaryLimit.HasValue)
                usage.Secondary = MakeRateWindow(primaryLimit.Value);
            if (timeLimit.HasValue)
                usage.ExtraRateWindows.Add(new NamedRateWindow("zai-mcp", "MCP", MakeRateWindow(timeLimit.Value, label: "MCP")));
            // Credit plans reuse the Copilot credits meter: the 5-hour pool is the dynamic rolling cap
            // (remaining in Cost.Amount, cap in Cost.Limit) and its reset becomes the card countdown.
            // The 7-day window stays as a percentage bar beside it.
            if (isCreditPlan && sessionTokenLimit is { } sessionLimit)
            {
                usage.HasPrimaryWindow = false;
                var credits = new CostSnapshot(sessionLimit.Remaining ?? 0, "credits", "Credits");
                if (sessionLimit.Usage is { } cap && cap > 0)
                    credits.Limit = cap;
                if (sessionLimit.NextResetTime is { } resetAt)
                    credits = credits.WithResetsAt(resetAt);
                usage.Cost = credits;
            }
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
                : null;
            return new RateWindow(percent, windowMinutes: windowMinutes, resetAt: entry.NextResetTime, resetDescription: resetDesc, label: label);
        }

        private static SubscriptionEntry? ParseCurrentSubscription(JsonElement? root)
        {
            if (root is not { } envelope
                || envelope.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
                || envelope.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
                    && code.TryGetInt32(out int codeValue) && codeValue is not 0 and not 200
                || !envelope.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return null;

            SubscriptionEntry? current = null;
            int currentPriority = 0;
            foreach (var item in data.EnumerateArray())
            {
                string? productId = StringProperty(item, "productId");
                string? productName = StringProperty(item, "productName");
                if (string.IsNullOrWhiteSpace(productId) && string.IsNullOrWhiteSpace(productName))
                    continue;

                bool inCurrentPeriod = BoolProperty(item, "inCurrentPeriod");
                bool valid = string.Equals(StringProperty(item, "status"), "VALID", StringComparison.OrdinalIgnoreCase);
                int priority = inCurrentPeriod && valid ? 3 : inCurrentPeriod ? 2 : valid ? 1 : 0;
                if (priority > currentPriority)
                {
                    current = new SubscriptionEntry(productId, productName);
                    currentPriority = priority;
                }
            }
            return current;
        }

        private static bool IsCurrentCreditPlan(SubscriptionEntry? subscription, IReadOnlyList<LimitEntry> tokenLimits)
        {
            if (subscription is { } value)
            {
                string identity = $"{value.ProductId} {value.ProductName}";
                if (identity.Contains("legacy", StringComparison.OrdinalIgnoreCase)
                    || identity.Contains("team edition", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (value.ProductId is { } productId && CurrentCreditProductIds.Contains(productId))
                    return true;
            }

            // New individual and team plans publish fixed credit allowances. This catches newly issued
            // product IDs without mistaking a legacy Lite/Pro/Max name for a current credit product.
            return tokenLimits.Any(static limit => limit is
            {
                Unit: 3,
                Number: 5,
                Usage: 2_000 or 12_000 or 15_000 or 28_000 or 35_000,
            }) && tokenLimits.Any(static limit => limit is
            {
                Unit: 6,
                Number: 1,
                Usage: 10_000 or 60_000 or 66_000 or 140_000 or 155_000,
            });
        }

        /// <summary>
        /// Resolves the current Z.ai coefficient in Singapore time (UTC+8, no DST). New credit plans
        /// charge 1x at peak and 0.5x off-peak; legacy V2/team plans charge 3x and 1x respectively.
        /// </summary>
        internal static UsagePricingSnapshot? ResolvePricing(bool isCreditPlan, string? productName, DateTimeOffset now)
        {
            double peakMultiplier;
            double offPeakMultiplier;
            if (isCreditPlan)
            {
                peakMultiplier = 1.0;
                offPeakMultiplier = 0.5;
            }
            else if (productName?.Contains("Legacy Plan V2", StringComparison.OrdinalIgnoreCase) == true
                || productName?.Contains("Team Edition", StringComparison.OrdinalIgnoreCase) == true)
            {
                peakMultiplier = 3.0;
                offPeakMultiplier = 1.0;
            }
            else
            {
                return null;
            }

            var singaporeTime = now.ToOffset(TimeSpan.FromHours(8));
            bool isWeekday = singaporeTime.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
            bool isPeak = isWeekday
                && singaporeTime.TimeOfDay >= TimeSpan.FromHours(14)
                && singaporeTime.TimeOfDay < TimeSpan.FromHours(18);
            return new UsagePricingSnapshot(isPeak ? "PEAK" : "OFF-PEAK", isPeak ? peakMultiplier : offPeakMultiplier);
        }

        private static string? StringProperty(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;

        private static bool BoolProperty(JsonElement element, string name)
            => element.TryGetProperty(name, out var value)
                && (value.ValueKind == JsonValueKind.True
                    || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number == 1);

        private static string ToTitleCase(string value)
            => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

        private readonly record struct SubscriptionEntry(string? ProductId, string? ProductName);


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

        internal static string BuildSubscriptionUrl(string quotaUrl)
        {
            var builder = new UriBuilder(quotaUrl)
            {
                Path = "/" + SubscriptionPath,
                Query = string.Empty,
            };
            return builder.Uri.ToString();
        }
    }
}
