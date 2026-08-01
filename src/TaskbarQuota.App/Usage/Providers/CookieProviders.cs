using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Browser;

namespace TaskbarQuota.Usage.Providers
{
    internal static class CookieHelper
    {
        public static string Resolve(ProviderId id, params string[] domains)
        {
            var manual = CredentialStore.Instance.ManualCookieHeader(id);
            if (manual != null) return manual;
            foreach (var d in domains)
            {
                var h = CookieExtractor.GetCookieHeader(d);
                if (!string.IsNullOrEmpty(h)) return h!;
            }
            throw new ProviderException(ProviderErrorKind.AuthRequired,
                $"No cookies found for {string.Join("/", domains)}. Sign in via Edge/Chrome or paste a cookie header in credentials.json.");
        }
    }

    /// <summary>
    /// OpenCode Zen / credits usage from opencode.ai. This is dollar-denominated billing, unlike
    /// OpenCode Go which exposes rolling, weekly and monthly subscription windows.
    /// </summary>
    public sealed class OpenCodeProvider : IUsageProvider
    {
        private const string ServerUrl = "https://opencode.ai/_server";
        private const string WorkspacesServerId = "def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f";
        private const string BillingServerId = "c83b78a614689c38ebee981f9b39a8b377716db85c1fd7dbab604adc02d3313d";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly Regex WorkspaceRe = new(@"wrk_[A-Za-z0-9_-]+", RegexOptions.Compiled);

        internal static string WorkspacePageUrl(string workspaceId, string? segment = null)
        {
            var baseUrl = $"https://opencode.ai/workspace/{workspaceId}";
            return string.IsNullOrEmpty(segment) ? baseUrl : $"{baseUrl}/{segment.TrimStart('/')}";
        }

        public ProviderId Id => ProviderId.OpenCode;
        public string DisplayName => "OpenCode Zen";
        public string SessionLabel => "Usage";
        public string WeeklyLabel => "Balance";
        public BillingKind Billing => BillingKind.Api;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var cookie = CookieHelper.Resolve(Id, "opencode.ai");

            string workspaceId = NormalizeWorkspaceId(CredentialStore.Instance.WorkspaceId(Id))
                ?? await FetchWorkspaceId(cookie, ct, "OpenCode").ConfigureAwait(false);

            var texts = new List<string>();
            var pageTasks = new[]
            {
                $"https://opencode.ai/workspace/{workspaceId}",
                $"https://opencode.ai/workspace/{workspaceId}/billing",
                $"https://opencode.ai/workspace/{workspaceId}/usage",
            }
                .Select(url => TryGetPageText(url, "https://opencode.ai", cookie, ct))
                .ToArray();

            foreach (var text in await Task.WhenAll(pageTasks).ConfigureAwait(false))
                if (!string.IsNullOrEmpty(text))
                    texts.Add(text);

            var billingText = await TryGetBillingText(workspaceId, cookie, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(billingText))
                texts.Add(billingText);

            var combined = string.Join("\n", texts);
            var monthlyUsage = FindMoneyValue(combined, "monthlyUsage", "monthly_usage", "currentUsage", "usage");
            var balance = ParseBillingBalance(combined)
                ?? FindMoneyValue(combined, "zenBalance", "currentBalance", "current_balance", "balance");
            var monthlyLimit = FindMoneyValue(combined, "monthlyLimit", "monthly_limit");

            if (monthlyUsage is null && balance is null)
                throw new ProviderException(ProviderErrorKind.Parse, "OpenCode: no dollar usage or balance found.");

            var primary = monthlyLimit is { } limit && limit > 0 && monthlyUsage is { } used
                ? new RateWindow(Math.Clamp(used / limit * 100.0, 0, 100), null, null, $"{used:0.00}/{limit:0.00} USD")
                : new RateWindow(0, null, null, monthlyUsage is { } usedOnly ? $"{usedOnly:0.00} USD used" : "Usage unavailable");

            var usage = new UsageSnapshot(primary)
            {
                Secondary = balance is { } bal ? new RateWindow(0, null, null, $"{bal:0.00} USD balance") : null,
                LoginMethod = "Zen",
                Cost = new CostSnapshot(monthlyUsage ?? balance ?? 0, "USD", monthlyUsage is null ? "Balance" : "Usage"),
                UsageDashboardUrl = WorkspacePageUrl(workspaceId, "usage"),
            };
            if (monthlyLimit is { } lim) usage.Cost.WithLimit(lim);
            return new ProviderFetchResult(usage, "web");
        }

        internal static string? NormalizeWorkspaceId(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var match = WorkspaceRe.Match(raw.Trim());
            return match.Success ? match.Value : null;
        }

        internal static IReadOnlyList<string> ParseWorkspaceIds(string text)
        {
            var results = new List<string>();
            foreach (Match match in WorkspaceRe.Matches(text))
                if (!results.Contains(match.Value, StringComparer.Ordinal))
                    results.Add(match.Value);
            return results;
        }

        internal static async Task<string> FetchWorkspaceId(string cookie, CancellationToken ct, string providerName)
        {
            var text = await GetText($"{ServerUrl}?id={WorkspacesServerId}", WorkspacesServerId,
                "https://opencode.ai", cookie, ct).ConfigureAwait(false);
            var ids = ParseWorkspaceIds(text);
            if (ids.Count > 0) return ids[0];

            text = await GetText(ServerUrl, WorkspacesServerId, "https://opencode.ai", cookie, ct,
                HttpMethod.Post, "[]").ConfigureAwait(false);
            ids = ParseWorkspaceIds(text);
            if (ids.Count > 0) return ids[0];
            throw new ProviderException(ProviderErrorKind.Parse, $"{providerName}: no workspace id found.");
        }

        private static async Task<string?> TryGetBillingText(string workspaceId, string cookie, CancellationToken ct)
        {
            try
            {
                var args = JsonSerializer.Serialize(new[] { workspaceId });
                return await GetText($"{ServerUrl}?id={BillingServerId}&args={Uri.EscapeDataString(args)}",
                    BillingServerId, WorkspacePageUrl(workspaceId), cookie, ct).ConfigureAwait(false);
            }
            catch { return null; }
        }

        internal static double? ParseBillingBalance(string text)
        {
            using var doc = TryJson(text);
            if (doc != null && TryFindBillingBalance(doc.RootElement, out var raw))
                return raw / 100_000_000d;

            if (!Regex.IsMatch(text, "(?:\\\"customerID\\\"|customerID)\\s*:\\s*(?:\\$R\\[\\d+\\]\\s*=\\s*)?\\\"[^\\\"]+\\\""))
                return null;
            var match = Regex.Match(text, "(?:\\\"balance\\\"|balance)\\s*:\\s*(?:\\$R\\[\\d+\\]\\s*=\\s*)?(-?[0-9]+(?:\\.[0-9]+)?)");
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out raw)
                ? raw / 100_000_000d
                : null;
        }

        private static bool TryFindBillingBalance(JsonElement element, out double balance)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("customerID", out var customer) && customer.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(customer.GetString()) && element.TryGetProperty("balance", out var value)
                    && TryJsonDouble(value, out balance))
                    return true;
                foreach (var property in element.EnumerateObject())
                    if (TryFindBillingBalance(property.Value, out balance)) return true;
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray())
                    if (TryFindBillingBalance(item, out balance)) return true;
            balance = 0;
            return false;
        }

        private static bool TryJsonDouble(JsonElement value, out double result)
        {
            if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out result);
            if (value.ValueKind == JsonValueKind.String)
                return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            result = 0;
            return false;
        }

        private static async Task<string> GetText(string url, string serverId, string referer, string cookie,
            CancellationToken ct, HttpMethod? method = null, string? body = null)
        {
            using var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("X-Server-Id", serverId);
            req.Headers.TryAddWithoutValidation("X-Server-Instance", $"server-fn:{Guid.NewGuid()}");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.TryAddWithoutValidation("Origin", "https://opencode.ai");
            req.Headers.TryAddWithoutValidation("Referer", referer);
            req.Headers.TryAddWithoutValidation("Accept", "text/javascript, application/json;q=0.9, */*;q=0.8");
            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "OpenCode cookies expired.");
            if (!resp.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"OpenCode API {(int)resp.StatusCode}");
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        private static async Task<string> GetPageText(string url, string referer, string cookie, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.TryAddWithoutValidation("Origin", "https://opencode.ai");
            req.Headers.TryAddWithoutValidation("Referer", referer);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,text/javascript,application/json;q=0.8,*/*;q=0.7");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || LooksSignedOut(text))
                throw new ProviderException(ProviderErrorKind.AuthRequired, "OpenCode cookies expired.");
            if (!resp.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"OpenCode API {(int)resp.StatusCode}");
            return text;
        }

        private static async Task<string?> TryGetPageText(string url, string referer, string cookie, CancellationToken ct)
        {
            try { return await GetPageText(url, referer, cookie, ct).ConfigureAwait(false); }
            catch { return null; }
        }

        internal static bool LooksSignedOut(string text)
        {
            var lower = text.ToLowerInvariant();
            return lower.Contains("auth/authorize") || lower.Contains("\"signin\"") || lower.Contains("please sign in") || lower.Contains("sign in");
        }

        internal static double? FindMoneyValue(string text, params string[] keys)
        {
            using var doc = TryJson(text);
            if (doc != null)
            {
                foreach (var key in keys)
                    if (FindMoneyValue(doc.RootElement, key) is { } value)
                        return value;
            }

            foreach (var key in keys)
            {
                var keyPattern = Regex.Escape(key).Replace("_", "[_-]?");
                var jsonMatch = Regex.Match(text, $@"(?i)""?{keyPattern}""?\s*[:=]\s*""?(-?[0-9][0-9,]*(?:\.[0-9]+)?)""?");
                if (jsonMatch.Success && double.TryParse(jsonMatch.Groups[1].Value.Replace(",", ""), out var raw))
                    return NormalizeMoney(raw);

                var label = key.Replace("_", " ");
                var dollarMatch = Regex.Match(text, $@"(?i){Regex.Escape(label)}[\s\S]{{0,120}}?\$\s*(-?[0-9][0-9,]*(?:\.[0-9]+)?)");
                if (dollarMatch.Success && double.TryParse(dollarMatch.Groups[1].Value.Replace(",", ""), out var dollars))
                    return dollars;
            }
            return null;
        }

        private static JsonDocument? TryJson(string text)
        {
            try { return JsonDocument.Parse(text); }
            catch { return null; }
        }

        private static double? FindMoneyValue(JsonElement el, string key)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number) return NormalizeMoney(prop.Value.GetDouble());
                        if (prop.Value.ValueKind == JsonValueKind.String && double.TryParse(prop.Value.GetString()?.Replace(",", ""), out var parsed))
                            return NormalizeMoney(parsed);
                    }
                    if (FindMoneyValue(prop.Value, key) is { } nested) return nested;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    if (FindMoneyValue(item, key) is { } nested) return nested;
            }
            return null;
        }

        private static double NormalizeMoney(double raw)
            => Math.Abs(raw) >= 1_000_000 ? raw / 100_000_000d : raw;

        private static (RateWindow? rolling, RateWindow? weekly, DateTimeOffset? renewsAt) ParseUsage(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return (FindWindow(doc.RootElement, 300, "rollingUsage", "rolling", "rolling_usage"),
                        FindWindow(doc.RootElement, 10080, "weeklyUsage", "weekly", "weekly_usage"),
                        FindDate(doc.RootElement, "renewAt", "renew_at"));
            }
            catch (JsonException)
            {
                return (ExtractWindow(text, 300, "rollingUsage", "rolling_usage", "rolling"),
                        ExtractWindow(text, 10080, "weeklyUsage", "weekly_usage", "weekly"),
                        ExtractRenewal(text));
            }
        }

        private static RateWindow? FindWindow(JsonElement el, int windowMinutes, params string[] keys)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in keys)
                    if (el.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object && ParseWindow(w, windowMinutes) is { } rw)
                        return rw;
                foreach (var prop in el.EnumerateObject())
                    if (FindWindow(prop.Value, windowMinutes, keys) is { } nested) return nested;
            }
            else if (el.ValueKind == JsonValueKind.Array)
                foreach (var item in el.EnumerateArray())
                    if (FindWindow(item, windowMinutes, keys) is { } nested) return nested;
            return null;
        }

        private static RateWindow? ParseWindow(JsonElement obj, int windowMinutes)
        {
            string[] pctKeys = { "usagePercent", "usedPercent", "percentUsed", "percent", "usage_percent", "used_percent", "utilization", "utilizationPercent", "utilization_percent", "usage" };
            double? pct = null;
            foreach (var k in pctKeys)
                if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number) { pct = v.GetDouble(); break; }
            if (pct is null)
            {
                double? used = Num(obj, "used") ?? Num(obj, "usage");
                double? limit = Num(obj, "limit") ?? Num(obj, "max");
                if (used is { } u && limit is { } l && l > 0) pct = u / l * 100.0;
            }
            if (pct is null) return null;
            double p = pct.Value <= 1.0 ? pct.Value * 100.0 : pct.Value;
            var resetAt = ResetAt(obj);
            return new RateWindow(Math.Clamp(p, 0, 100), windowMinutes, resetAt, resetAt is null ? null : FormatTimeUntil(resetAt.Value));
        }

        private static double? Num(JsonElement e, string n)
            => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        internal static RateWindow? ExtractWindow(string text, int windowMinutes, params string[] names)
        {
            foreach (var name in names)
            {
                string percentPattern = $@"{Regex.Escape(name)}[^}}]*?(?:usagePercent|usedPercent|percentUsed|percent|usage_percent|used_percent|utilization|utilizationPercent|utilization_percent|usage)\s*[:=]\s*([0-9]+(?:\.[0-9]+)?)";
                var pct = ExtractNumber(percentPattern, text);
                if (pct is null) continue;

                string resetPattern = $@"{Regex.Escape(name)}[^}}]*?(?:resetInSec|resetInSeconds|resetSeconds|reset_sec|reset_in_sec|resetsInSec|resetsInSeconds|resetIn|resetSec)\s*[:=]\s*([0-9]+)";
                var resetSeconds = ExtractNumber(resetPattern, text);
                DateTimeOffset? resetAt = resetSeconds is null ? null : DateTimeOffset.Now.AddSeconds(Math.Max(0, resetSeconds.Value));
                var p = pct.Value <= 1.0 ? pct.Value * 100.0 : pct.Value;
                return new RateWindow(Math.Clamp(p, 0, 100), windowMinutes, resetAt, resetAt is null ? null : FormatTimeUntil(resetAt.Value));
            }
            return null;
        }

        internal static double? ExtractNumber(string pattern, string text)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success && double.TryParse(match.Groups[1].Value, out var value) ? value : null;
        }

        internal static DateTimeOffset? ExtractRenewal(string text)
        {
            var match = Regex.Match(text, @"(?:""renewAt""|""renew_at""|renewAt|renew_at)\s*[:=]\s*""?([^"",}\s]+)""?", RegexOptions.IgnoreCase);
            return match.Success ? DateFromText(match.Groups[1].Value) : null;
        }

        private static DateTimeOffset? ResetAt(JsonElement obj)
        {
            string[] resetInKeys = { "resetInSec", "resetInSeconds", "resetSeconds", "reset_sec", "reset_in_sec", "resetsInSec", "resetsInSeconds", "resetIn", "resetSec" };
            foreach (var key in resetInKeys)
                if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                    return DateTimeOffset.Now.AddSeconds(Math.Max(0, v.GetDouble()));

            return FindDate(obj, "resetAt", "resetsAt", "reset_at", "resets_at", "nextReset", "next_reset", "renewAt", "renew_at");
        }

        private static DateTimeOffset? FindDate(JsonElement el, params string[] keys)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in keys)
                    if (el.TryGetProperty(key, out var v) && DateFromElement(v) is { } parsed)
                        return parsed;
                foreach (var prop in el.EnumerateObject())
                    if (FindDate(prop.Value, keys) is { } nested) return nested;
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    if (FindDate(item, keys) is { } nested) return nested;
            }
            return null;
        }

        private static DateTimeOffset? DateFromElement(JsonElement v)
            => v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out var i) => DateFromTimestamp(i),
                JsonValueKind.Number => DateFromTimestamp(v.GetDouble()),
                JsonValueKind.String => DateFromText(v.GetString() ?? ""),
                _ => null,
            };

        private static DateTimeOffset? DateFromText(string raw)
        {
            var text = raw.Trim();
            if (text.Length == 0) return null;
            if (double.TryParse(text, out var number)) return DateFromTimestamp(number);
            return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
        }

        private static DateTimeOffset? DateFromTimestamp(double number)
        {
            if (double.IsNaN(number) || double.IsInfinity(number) || number <= 0) return null;
            var seconds = number > 10_000_000_000d ? number / 1000d : number;
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }

        internal static string FormatTimeUntil(DateTimeOffset at)
        {
            var span = at - DateTimeOffset.Now;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalDays >= 2) return $"{span.TotalDays:0}d";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{Math.Max(0, (int)span.TotalMinutes)}m";
        }
    }

    /// <summary>OpenCode Go subscription usage from the workspace /go page: rolling, weekly and monthly windows.</summary>
    public sealed class OpenCodeGoProvider : IUsageProvider
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public ProviderId Id => ProviderId.OpenCodeGo;
        public string DisplayName => "OpenCode Go";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var cookie = CredentialStore.Instance.ManualCookieHeader(Id)
                ?? CredentialStore.Instance.ManualCookieHeader(ProviderId.OpenCode)
                ?? CookieHelper.Resolve(Id, "opencode.ai");
            string workspaceId = OpenCodeProvider.NormalizeWorkspaceId(CredentialStore.Instance.WorkspaceId(Id))
                ?? OpenCodeProvider.NormalizeWorkspaceId(CredentialStore.Instance.WorkspaceId(ProviderId.OpenCode))
                ?? await OpenCodeProvider.FetchWorkspaceId(cookie, ct, "OpenCode Go").ConfigureAwait(false);

            string usageText = await GetPageText(
                OpenCodeProvider.WorkspacePageUrl(workspaceId, "go"),
                cookie,
                ct).ConfigureAwait(false);
            if (OpenCodeProvider.LooksSignedOut(usageText)) throw new ProviderException(ProviderErrorKind.AuthRequired, "OpenCode Go cookies expired.");

            var rolling = OpenCodeProvider.ExtractWindow(usageText, 300, "rollingUsage", "rolling_usage", "rolling")
                ?? throw new ProviderException(ProviderErrorKind.Parse, "OpenCode Go: missing session usage.");
            var weekly = OpenCodeProvider.ExtractWindow(usageText, 10080, "weeklyUsage", "weekly_usage", "weekly");
            var monthly = OpenCodeProvider.ExtractWindow(usageText, 43200, "monthlyUsage", "monthly_usage", "monthly");

            var usage = new UsageSnapshot(rolling)
            {
                Secondary = weekly,
                Monthly = monthly,
                LoginMethod = "Go",
                UsageDashboardUrl = OpenCodeProvider.WorkspacePageUrl(workspaceId, "go"),
            };

            return new ProviderFetchResult(usage, "opencode");
        }

        private static async Task<string> GetPageText(string url, string cookie, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || OpenCodeProvider.LooksSignedOut(text))
                throw new ProviderException(ProviderErrorKind.AuthRequired, "OpenCode Go cookies expired.");
            if (!resp.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"OpenCode Go API {(int)resp.StatusCode}");
            return text;
        }

    }
}
