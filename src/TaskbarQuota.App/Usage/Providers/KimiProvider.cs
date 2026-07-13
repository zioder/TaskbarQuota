using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using TaskbarQuota;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Kimi Code CLI usage. Ported from CodexBar Sources/CodexBarCore/Providers/Kimi/.
    ///
    /// Kimi Code is a terminal-only CLI (like Cline). Auth via KIMI_CODE_API_KEY env var
    /// or credentials.json. The Code API returns weekly usage and optional rate limits.
    /// An optional web API path also exists for subscription balance (monthly window).
    ///
    /// Install: irm https://code.kimi.com/kimi-code/install.ps1 | iex
    /// Binary:  %USERPROFILE%\.kimi-code\bin\kimi.exe
    /// </summary>
    public sealed class KimiProvider : IUsageProvider
    {
        private const string CodeAPIBaseUrl = "https://api.kimi.com";
        private const string CodeAPIUsagePath = "/coding/v1/usages";
        private const string WebUsageUrl = "https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages";
        private const string WebSubscriptionUrl = "https://www.kimi.com/apiv2/kimi.gateway.membership.v2.MembershipService/GetSubscriptionStats";
        private const string DashboardUrl = "https://www.kimi.com/code/console";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public ProviderId Id => ProviderId.Kimi;
        public string DisplayName => "Kimi";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            // Try API key first (Code API), then auth token (Web API)
            var apiKey = TryLoadApiKey();
            if (!string.IsNullOrEmpty(apiKey))
                return await FetchCodeAPIUsage(apiKey!, ct).ConfigureAwait(false);

            // The official CLI stores a short-lived OAuth token here after `kimi` + /login.
            // It is accepted by the same Code API and avoids requiring a duplicate manual key.
            var cliAccessToken = TryLoadCliAccessToken();
            if (!string.IsNullOrEmpty(cliAccessToken))
                return await FetchCodeAPIUsage(cliAccessToken!, ct, includeCliIdentity: true).ConfigureAwait(false);

            var authToken = TryLoadAuthToken();
            if (!string.IsNullOrEmpty(authToken))
                return await FetchWebUsage(authToken!, ct).ConfigureAwait(false);

            if (!ProviderInstallDetector.IsInstalled(ProviderId.Kimi))
                throw new ProviderException(ProviderErrorKind.NotInstalled,
                    ProviderInstallDetector.NotInstalledMessage(ProviderId.Kimi));

            throw new ProviderException(ProviderErrorKind.AuthRequired,
                "Kimi Code API key not found. Set KIMI_CODE_API_KEY or run `kimi` and /login.");
        }


        // --- Code API path (primary, uses API key) ------------------------------------

        private async Task<ProviderFetchResult> FetchCodeAPIUsage(string apiKey, CancellationToken ct, bool includeCliIdentity = false)
        {
            var url = $"{CodeAPIBaseUrl}{CodeAPIUsagePath}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.ParseAdd("application/json");
            if (includeCliIdentity)
            {
                request.Headers.TryAddWithoutValidation("X-Msh-Platform", "kimi_code_cli");
                request.Headers.TryAddWithoutValidation("X-Msh-Version", "WinCheck");
            }

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Kimi Code API key invalid or expired.");
            if ((int)response.StatusCode == 403)
                throw new ProviderException(ProviderErrorKind.Other, "Kimi Code API: permission or quota denied.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Kimi Code API returned {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return BuildCodeAPIResult(doc.RootElement);
        }

        internal static ProviderFetchResult BuildCodeAPIResult(JsonElement root)
        {
            // Code API returns: { "usage": { "limit", "used", "remaining", "resetTime" }, "limits": [...] }
            KimiUsageDetail? weekly = null;
            KimiUsageDetail? rateLimit = null;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                weekly = ParseUsageDetail(usage);

            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in limits.EnumerateArray())
                {
                    if (limit.TryGetProperty("window", out var window) && window.TryGetProperty("duration", out var dur))
                    {
                        int duration = dur.GetInt32();
                        // 5-hour rate limit window (300 minutes)
                        if (duration >= 280 && duration <= 320)
                        {
                            rateLimit = limit.TryGetProperty("detail", out var detail) ? ParseUsageDetail(detail) : null;
                        }
                    }
                }
            }

            return BuildSnapshot(weekly, rateLimit, sourceLabel: "api");
        }


        // --- Web API path (fallback, uses JWT auth token) -----------------------------

        private async Task<ProviderFetchResult> FetchWebUsage(string authToken, CancellationToken ct)
        {
            var usageJson = await PostWebJson(WebUsageUrl, authToken, ct).ConfigureAwait(false);
            var (weekly, rateLimit) = ParseWebUsageResponse(usageJson);

            // Try subscription stat for monthly window (non-fatal)
            KimiSubscriptionBalance? subscription = null;
            try
            {
                var subJson = await PostWebJson(WebSubscriptionUrl, authToken, ct).ConfigureAwait(false);
                subscription = ParseSubscriptionBalance(subJson);
            }
            catch { /* non-fatal */ }

            var snapshot = BuildSnapshot(weekly, rateLimit, sourceLabel: "web");

            // Add monthly as extra rate window if available
            if (subscription is { } sub && sub.AmountUsedRatio.HasValue)
            {
                var monthlyPercent = Math.Min(100, Math.Max(0, sub.AmountUsedRatio.Value * 100));
                var monthlyReset = ParseIsoDate(sub.ExpireTime);
                var monthlyWindow = new RateWindow(monthlyPercent, resetAt: monthlyReset, label: "Monthly");
                snapshot.Usage.ExtraRateWindows.Add(new NamedRateWindow("kimi-monthly", "Monthly", monthlyWindow));
            }

            return snapshot;
        }

        private static async Task<JsonElement> PostWebJson(string url, string authToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            var body = url == WebUsageUrl ? "{\"scope\":[\"FEATURE_CODING\"]}" : "{}";
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            request.Headers.Add("Cookie", $"kimi-auth={authToken}");
            request.Headers.Add("Origin", "https://www.kimi.com");
            request.Headers.Add("Referer", DashboardUrl);
            request.Headers.Add("connect-protocol-version", "1");
            request.Headers.Add("x-language", "en-US");
            request.Headers.Add("x-msh-platform", "web");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Kimi web API returned {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }


        // --- Parsing & snapshot building ----------------------------------------------

        internal static (KimiUsageDetail? weekly, KimiUsageDetail? rateLimit) ParseWebUsageResponse(JsonElement root)
        {
            KimiUsageDetail? weekly = null;
            KimiUsageDetail? rateLimit = null;

            if (root.TryGetProperty("usages", out var usages) && usages.ValueKind == JsonValueKind.Array)
            {
                foreach (var usage in usages.EnumerateArray())
                {
                    var scope = usage.TryGetProperty("scope", out var s) ? s.GetString() : "";
                    var detail = usage.TryGetProperty("detail", out var d) ? ParseUsageDetail(d) : null;
                    if (detail is null) continue;

                    if (scope is "FEATURE_CODING" or "WEEKLY")
                        weekly = detail;
                    else if (scope == "RATE_LIMIT")
                        rateLimit = detail;

                    // Also check limits array within each usage
                    if (usage.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var limit in limits.EnumerateArray())
                        {
                            if (limit.TryGetProperty("window", out var window) && window.TryGetProperty("duration", out var dur))
                            {
                                int duration = dur.GetInt32();
                                if (duration >= 280 && duration <= 320)
                                    rateLimit = limit.TryGetProperty("detail", out var det) ? ParseUsageDetail(det) : rateLimit;
                            }
                        }
                    }
                }
            }

            return (weekly, rateLimit);
        }

        internal static KimiUsageDetail? ParseUsageDetail(JsonElement el)
        {
            string? limit = GetStringOrNumber(el, "limit");
            if (string.IsNullOrEmpty(limit)) return null;
            string? used = GetStringOrNumber(el, "used");
            string? remaining = GetStringOrNumber(el, "remaining");
            string? resetTime = GetStringOrNumber(el, "resetTime")
                ?? GetStringOrNumber(el, "resetAt")
                ?? GetStringOrNumber(el, "reset_time")
                ?? GetStringOrNumber(el, "reset_at");
            return new KimiUsageDetail(limit!, used, remaining, resetTime);
        }

        internal static KimiSubscriptionBalance? ParseSubscriptionBalance(JsonElement root)
        {
            if (!root.TryGetProperty("subscriptionBalance", out var bal) || bal.ValueKind == JsonValueKind.Null)
                return null;
            return new KimiSubscriptionBalance(
                feature: bal.TryGetProperty("feature", out var f) ? f.GetString() : null,
                type: bal.TryGetProperty("type", out var t) ? t.GetString() : null,
                amountUsedRatio: bal.TryGetProperty("amountUsedRatio", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : (double?)null,
                expireTime: bal.TryGetProperty("expireTime", out var e) ? e.GetString() : null);
        }

        private static ProviderFetchResult BuildSnapshot(KimiUsageDetail? weekly, KimiUsageDetail? rateLimit, string sourceLabel)
        {
            // Kimi's rate limit is a rolling five-hour window, so it is the session-style
            // primary row. The coding-plan quota is the seven-day weekly secondary row.
            var weeklyWindow = weekly != null
                ? MakeRateWindow(weekly, resetDescription: $"{ParseInt(weekly.Used)}/{ParseInt(weekly.Limit)} requests")
                : new RateWindow(0, resetDescription: "No usage data");
            RateWindow? rateLimitWindow = null;
            if (rateLimit != null)
            {
                int rateUsed = ParseInt(rateLimit.Used);
                int rateLimitVal = ParseInt(rateLimit.Limit);
                rateLimitWindow = MakeRateWindow(rateLimit,
                    windowMinutes: 300,
                    resetDescription: $"Rate: {rateUsed}/{rateLimitVal} per 5 hours");
            }

            var usage = new UsageSnapshot(rateLimitWindow ?? weeklyWindow);
            if (rateLimitWindow != null)
                usage.Secondary = weeklyWindow;

            usage.UsageDashboardUrl = DashboardUrl;
            return new ProviderFetchResult(usage, sourceLabel);
        }

        private static RateWindow MakeRateWindow(KimiUsageDetail detail, int? windowMinutes = null, string? resetDescription = null)
        {
            int limit = ParseInt(detail.Limit);
            int remaining = ParseInt(detail.Remaining);
            int used = ParseInt(detail.Used);
            if (used == 0 && limit > 0 && remaining > 0)
                used = Math.Max(0, limit - remaining);
            double percent = limit > 0 ? Math.Min(100, Math.Max(0, (double)used / limit * 100)) : 0;
            var resetAt = ParseIsoDate(detail.ResetTime);
            return new RateWindow(percent, windowMinutes: windowMinutes, resetAt: resetAt, resetDescription: resetDescription);
        }


        // --- Helpers -------------------------------------------------------------------

        private static string? GetStringOrNumber(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString()?.Trim();
            if (v.ValueKind == JsonValueKind.Number) return v.GetInt64().ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static int ParseInt(string? s) => int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static DateTimeOffset? ParseIsoDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return null;
        }

        // --- Credential resolution ---------------------------------------------------

        internal static string? TryLoadApiKey()
        {
            var fromEnv = Environment.GetEnvironmentVariable("KIMI_CODE_API_KEY")?.Trim();
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
            var fromStore = CredentialStore.Instance.ApiKey(ProviderId.Kimi, "KIMI_CODE_API_KEY");
            return !string.IsNullOrWhiteSpace(fromStore) ? fromStore : null;
        }

        internal static string? TryLoadAuthToken()
        {
            var fromEnv = Environment.GetEnvironmentVariable("KIMI_AUTH_TOKEN")?.Trim()
                ?? Environment.GetEnvironmentVariable("kimi_auth_token")?.Trim();
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
            var fromStore = CredentialStore.Instance.For(ProviderId.Kimi).Extra;
            return !string.IsNullOrWhiteSpace(fromStore) ? fromStore : null;
        }

        internal static string? TryLoadCliAccessToken(string? homeOverride = null, DateTimeOffset? now = null)
        {
            var home = homeOverride
                ?? Environment.GetEnvironmentVariable("KIMI_CODE_HOME")?.Trim()
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
            var path = Path.Combine(home, "credentials", "kimi-code.json");
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (!root.TryGetProperty("access_token", out var tokenEl)) return null;
                var token = tokenEl.GetString()?.Trim();
                if (string.IsNullOrEmpty(token)) return null;
                if (!root.TryGetProperty("expires_at", out var expiryEl)) return null;
                double expiry = expiryEl.ValueKind switch
                {
                    JsonValueKind.Number => expiryEl.GetDouble(),
                    JsonValueKind.String when double.TryParse(expiryEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => 0,
                };
                var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
                return expiry > current + 60 ? token : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (JsonException) { return null; }
        }
    }

    // --- Kimi data models ----------------------------------------------------------

    internal sealed class KimiUsageDetail
    {
        public string Limit { get; }
        public string? Used { get; }
        public string? Remaining { get; }
        public string? ResetTime { get; }

        public KimiUsageDetail(string limit, string? used, string? remaining, string? resetTime)
        {
            Limit = limit;
            Used = used;
            Remaining = remaining;
            ResetTime = resetTime;
        }
    }

    internal sealed class KimiSubscriptionBalance
    {
        public string? Feature { get; }
        public string? Type { get; }
        public double? AmountUsedRatio { get; }
        public string? ExpireTime { get; }

        public KimiSubscriptionBalance(string? feature, string? type, double? amountUsedRatio, string? expireTime)
        {
            Feature = feature;
            Type = type;
            AmountUsedRatio = amountUsedRatio;
            ExpireTime = expireTime;
        }
    }
}
