using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using TaskbarQuota;
using TaskbarQuota.Services;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Claude (Anthropic) usage via the OAuth token Claude Code stores in ~/.claude/.credentials.json,
    /// querying https://api.anthropic.com/api/oauth/usage, with a claude.ai cookie fallback when
    /// the OAuth usage endpoint is temporarily rate limited.
    /// Ported from Win-CodexBar rust/src/providers/claude/oauth.rs.
    /// </summary>
    public sealed class ClaudeProvider : IUsageProvider
    {
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private const string RefreshUrl = "https://platform.claude.com/v1/oauth/token";
        private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        private const string RefreshScope = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
        private const string WebApiBaseUrl = "https://claude.ai/api";
        public const string PreferWebEnvironmentVariable = "TASKBARQUOTA_CLAUDE_PREFER_WEB";
        public const string ForceLoginEnvironmentVariable = "TASKBARQUOTA_CLAUDE_FORCE_LOGIN";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly object RateLimitLock = new();
        private static readonly DateTimeOffset ProcessStartedAt = DateTimeOffset.UtcNow;
        private static DateTimeOffset? _oauthRateLimitedUntil;

        public ProviderId Id => ProviderId.Claude;
        public string DisplayName => "Claude Code";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            if (ShouldForceLoginFlow())
            {
                var freshLogin = await ClaudeOAuth.GetValidTokensAsync(ct).ConfigureAwait(false);
                if (freshLogin != null && ClaudeOAuth.WasStoreWrittenAfter(ProcessStartedAt))
                    return await FetchWithOAuthTokenAsync(freshLogin, ct).ConfigureAwait(false);

                throw new ProviderException(ProviderErrorKind.AuthRequired, "Login with Claude required.");
            }

            if (ShouldPreferWebUsage() && await TryFetchWebUsageAsync(ct).ConfigureAwait(false) is { } preferredWeb)
                return preferredWeb;

            // An explicit "Login with Claude" wins over CLI/cookies, so the widget shows the
            // account you logged into (cookie-free, survives Chrome App-Bound Encryption).
            var oauth = await ClaudeOAuth.GetValidTokensAsync(ct).ConfigureAwait(false);
            if (oauth != null)
                return await FetchWithOAuthTokenAsync(oauth, ct).ConfigureAwait(false);

            Credentials creds;
            try
            {
                creds = LoadCredentials();
            }
            catch (ProviderException pe) when (pe.Kind is ProviderErrorKind.NotInstalled or ProviderErrorKind.NotRunning or ProviderErrorKind.AuthRequired)
            {
                // No CLI and no explicit login — fall back to claude.ai cookies (Edge/Firefox/Brave).
                if (await TryFetchWebUsageAsync(ct).ConfigureAwait(false) is { } webOnlyResult)
                    return webOnlyResult;

                // Nothing readable — prompt the one-click login (works on any browser, no CLI).
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Login with Claude required.");
            }

            if (IsOAuthRateLimited())
            {
                if (await TryFetchWebUsageAsync(ct).ConfigureAwait(false) is { } webResult)
                    return webResult;

                throw new ProviderException(ProviderErrorKind.RateLimited, "Claude API rate limited. Will retry in a few minutes.");
            }

            using var response = await SendUsageRequestAsync(creds.AccessToken, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var refreshed = await TryRefreshCredentialsAsync(creds, ct).ConfigureAwait(false);
                if (refreshed != null)
                {
                    using var retry = await SendUsageRequestAsync(refreshed.AccessToken, ct).ConfigureAwait(false);
                    return await HandleUsageResponseAsync(retry, refreshed, ct).ConfigureAwait(false);
                }

                throw new ProviderException(ProviderErrorKind.AuthRequired, "Claude OAuth token expired. Run `claude` to re-authenticate.");
            }

            return await HandleUsageResponseAsync(response, creds, ct).ConfigureAwait(false);
        }

        private static async Task<ProviderFetchResult> FetchWithOAuthTokenAsync(Services.ClaudeTokens oauth, CancellationToken ct)
        {
            var creds = new Credentials(oauth.AccessToken, oauth.SubscriptionType, oauth.RateLimitTier);
            using var resp = await SendUsageRequestAsync(oauth.AccessToken, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                Services.ClaudeOAuth.Logout(); // token dead and refresh already failed upstream
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Login with Claude required.");
            }
            return await HandleUsageResponseAsync(resp, creds, ct).ConfigureAwait(false);
        }

        private static async Task<HttpResponseMessage> SendUsageRequestAsync(string accessToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.0");
            return await Http.SendAsync(request, ct).ConfigureAwait(false);
        }

        private static async Task<ProviderFetchResult> HandleUsageResponseAsync(HttpResponseMessage response, Credentials creds, CancellationToken ct)
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                RecordOAuthRateLimit(response);
                if (await TryFetchWebUsageAsync(ct).ConfigureAwait(false) is { } webResult)
                    return webResult;

                throw new ProviderException(ProviderErrorKind.RateLimited,
                    $"Claude API rate limited ({(int)response.StatusCode}). Will retry in a few minutes.");
            }
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Claude API returned {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            ClearOAuthRateLimit();

            return BuildResult(doc.RootElement, creds);
        }

        private static ProviderFetchResult BuildResult(JsonElement json, Credentials creds)
        {
            var sessionWindow = ParseWindow(json, 300, "five_hour", "fiveHour");
            var secondary = ParseWindow(json, 10080, "seven_day", "sevenDay");
            var modelSpecific =
                ParseWindow(json, 10080, "seven_day_opus", "sevenDayOpus") is { } opus
                    ? WithLabel(opus, "Opus")
                    : ParseWindow(json, 10080, "seven_day_sonnet", "sevenDaySonnet") is { } sonnet
                        ? WithLabel(sonnet, "Sonnet")
                        : null;
            var fableWeekly = ParseScopedWeeklyLimit(json, "Fable");

            bool hasExtra = TryParseExtraUsage(json, out var cost, out var additional);
            string plan = ResolvePlan(creds.SubscriptionType, creds.RateLimitTier);

            // Claude Enterprise (and other org-billed plans) expose no session/weekly rate windows —
            // only an `extra_usage` spend cap. Promote that cap to the primary bar so it's a first-class,
            // widget-toggleable meter ("Spend limit") instead of a plain text line with no add option.
            // Mirrors CodexBar's `oauthSpendLimitWindow` / `treatAsSpendLimit`. See github issue #12.
            bool treatAsSpendLimit = sessionWindow == null && secondary == null && hasExtra;
            bool isSpendLimit = treatAsSpendLimit || string.Equals(plan, "Enterprise", StringComparison.OrdinalIgnoreCase);

            bool hasSpendLimitWindow = treatAsSpendLimit && cost is { Limit: > 0 };
            RateWindow primary;
            if (hasSpendLimitWindow && cost is { } spend)
                primary = BuildSpendLimitWindow(spend);
            else
                primary = sessionWindow ?? new RateWindow(0);

            var usage = new UsageSnapshot(primary)
            {
                HasPrimaryWindow = sessionWindow != null || hasSpendLimitWindow,
            };
            usage.Secondary = secondary;
            usage.ModelSpecific = modelSpecific;

            if (fableWeekly is { } fable)
                usage.ExtraRateWindows.Add(new NamedRateWindow("claude-fable", "Fable", fable));
            AddExtraWindow(usage, json, "claude-oauth-apps", "OAuth apps", "seven_day_oauth_apps", "seven_day_claude_oauth_apps", "oauth_apps", "oauth", "sevenDayOauthApps");
            AddExtraWindow(usage, json, "claude-design", "Claude Design", "seven_day_design", "seven_day_claude_design", "claude_design", "design", "seven_day_omelette", "omelette", "sevenDayDesign");
            AddExtraWindow(usage, json, "claude-routines", "Daily Routines", "seven_day_routines", "seven_day_claude_routines", "claude_routines", "routines", "routine", "seven_day_cowork", "cowork", "sevenDayRoutines");

            if (hasExtra && cost != null)
            {
                // CodexBar wording: spend-limit plans read "Spend limit", others "Monthly cap".
                usage.Cost = RelabelCostPeriod(cost, isSpendLimit ? "Spend limit" : "Monthly cap");
                usage.AdditionalUsage = additional;
            }

            usage.LoginMethod = plan;
            return new ProviderFetchResult(usage, "oauth");
        }

        private static CostSnapshot RelabelCostPeriod(CostSnapshot cost, string period)
        {
            var relabeled = new CostSnapshot(cost.Amount, cost.Currency, period);
            if (cost.Limit is { } limit) relabeled.WithLimit(limit);
            if (cost.ResetsAt is { } resetsAt) relabeled.WithResetsAt(resetsAt);
            return relabeled;
        }

        /// <summary>
        /// Builds the Enterprise "Spend limit" primary bar from the extra-usage cost: percent = used/limit.
        /// The dollar detail ($9.27 / $100.00) still renders below via <see cref="UsageSnapshot.Cost"/>.
        /// </summary>
        private static RateWindow BuildSpendLimitWindow(CostSnapshot spend)
        {
            double pct = spend.Limit is { } lim && lim > 0
                ? Math.Clamp(spend.Amount / lim * 100, 0, 100)
                : 0;
            return new RateWindow(pct, label: "Spend limit") { ShowCostValue = true };
        }

        private static RateWindow WithLabel(RateWindow window, string label)
            => new(window.UsedPercent, window.WindowMinutes, window.ResetAt, window.ResetDescription, label);

        internal static ProviderFetchResult BuildResultForTesting(JsonElement json, Credentials creds)
            => BuildResult(json, creds);

        private static string ResolvePlan(string? subscriptionType, string? rateLimitTier)
        {
            var subWords = NormalizedWords(subscriptionType);
            if (subWords.Count > 0)
            {
                if (subWords.Contains("max")) return "Max";
                if (subWords.Contains("pro")) return "Pro";
                if (subWords.Contains("team")) return "Team";
                if (subWords.Contains("enterprise")) return "Enterprise";
                if (subWords.Contains("ultra")) return "Ultra";
            }

            var tier = Normalized(rateLimitTier);
            if (tier.Contains("max")) return "Max";
            if (tier.Contains("pro")) return "Pro";
            if (tier.Contains("team")) return "Team";
            if (tier.Contains("enterprise")) return "Enterprise";

            return string.Empty;
        }

        private static string Normalized(string? text) =>
            (text ?? string.Empty).Trim().ToLowerInvariant();

        private static HashSet<string> NormalizedWords(string? text)
        {
            var result = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var word in Normalized(text).Split(WordSeps, StringSplitOptions.RemoveEmptyEntries))
                result.Add(word);
            return result;
        }

        private static readonly char[] WordSeps = " -_.".ToCharArray();

        private static void AddExtraWindow(UsageSnapshot usage, JsonElement root, string id, string title, params string[] names)
        {
            if (ParseWindow(root, 10080, names) is { } window)
                usage.ExtraRateWindows.Add(new NamedRateWindow(id, title, window));
        }

        private static RateWindow? ParseWindow(JsonElement root, int windowMinutes, params string[] names)
        {
            if (!TryGetFirstProperty(root, out var w, names) || w.ValueKind != JsonValueKind.Object) return null;
            if (!w.TryGetProperty("utilization", out var ut) || ut.ValueKind != JsonValueKind.Number) return null;

            double util = ut.GetDouble();

            DateTimeOffset? resetAt = null;
            string? resetDesc = null;
            if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ra.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                resetAt = dt;
                resetDesc = CodexProvider.FormatResetCountdown(dt);
            }

            return new RateWindow(util, windowMinutes, resetAt, resetDesc);
        }

        /// <summary>
        /// A model-scoped weekly limit from the <c>limits</c> array — a <c>kind: "weekly_scoped"</c> row
        /// whose <c>scope.model.display_name</c> names the model (e.g. "Fable"). Anthropic moved the
        /// per-model weekly windows off the legacy top-level <c>seven_day_&lt;model&gt;</c> keys (which now
        /// return null) into this array, so the Fable bucket is read by display name. <c>percent</c> is
        /// 0–100. Ported from openusage ClaudeUsageMapper.appendScopedWeeklyLimit (#814).
        /// </summary>
        private static RateWindow? ParseScopedWeeklyLimit(JsonElement root, string modelDisplayName)
        {
            if (!TryGetFirstProperty(root, out var limits, "limits") || limits.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var entry in limits.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(Str(entry, "kind"), "weekly_scoped", StringComparison.Ordinal)) continue;
                if (!entry.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) continue;
                if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(Str(model, "display_name"), modelDisplayName, StringComparison.OrdinalIgnoreCase)) continue;
                if (Num(entry, "percent") is not { } percent) continue;

                DateTimeOffset? resetAt = null;
                string? resetDesc = null;
                if (entry.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(ra.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                {
                    resetAt = dt;
                    resetDesc = CodexProvider.FormatResetCountdown(dt);
                }

                return new RateWindow(percent, 10080, resetAt, resetDesc);
            }

            return null;
        }

        private static bool TryGetFirstProperty(JsonElement root, out JsonElement value, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
                    return true;
            }

            value = default;
            return false;
        }

        private static bool TryParseExtraUsage(JsonElement root, out CostSnapshot? cost, out AdditionalUsageSnapshot? additional)
        {
            cost = null;
            additional = null;
            if (!TryGetFirstProperty(root, out var extra, "extra_usage", "extraUsage") || extra.ValueKind != JsonValueKind.Object)
                return false;

            var enabled = Bool(extra, "is_enabled", "isEnabled") ?? false;
            var usedCredits = Num(extra, "used_credits", "usedCredits");
            var limitCredits = Num(extra, "monthly_limit", "monthlyLimit", "monthly_credit_limit", "monthlyCreditLimit");
            var currency = Str(extra, "currency") ?? "USD";

            if (!enabled && usedCredits is null && limitCredits is null)
                return false;

            var used = (usedCredits ?? 0) / 100.0;
            var limit = limitCredits is { } lim ? lim / 100.0 : (double?)null;
            cost = new CostSnapshot(used, currency, "Monthly");
            if (limit is { } limitValue) cost.WithLimit(limitValue);
            additional = new AdditionalUsageSnapshot
            {
                Enabled = enabled,
                SpentUsd = used,
                BudgetUsd = limit,
            };
            return true;
        }

        private static double? Num(JsonElement obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                    return number;
            }

            return null;
        }

        private static string? Str(JsonElement obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }

            return null;
        }

        private static bool? Bool(JsonElement obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetProperty(key, out var value))
                {
                    if (value.ValueKind == JsonValueKind.True) return true;
                    if (value.ValueKind == JsonValueKind.False) return false;
                }
            }

            return null;
        }

        private static bool IsOAuthRateLimited()
        {
            lock (RateLimitLock)
            {
                if (_oauthRateLimitedUntil is not { } until)
                    return false;
                if (DateTimeOffset.Now < until)
                    return true;
                _oauthRateLimitedUntil = null;
                return false;
            }
        }

        private static void RecordOAuthRateLimit(HttpResponseMessage response)
        {
            var until = ParseRetryAfter(response) ?? DateTimeOffset.Now.AddMinutes(5);
            lock (RateLimitLock)
            {
                _oauthRateLimitedUntil = until;
            }
        }

        private static void ClearOAuthRateLimit()
        {
            lock (RateLimitLock)
            {
                _oauthRateLimitedUntil = null;
            }
        }

        internal static DateTimeOffset? ParseRetryAfter(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter is { } retryAfter)
            {
                if (retryAfter.Date is { } date)
                    return date;
                if (retryAfter.Delta is { } delta)
                    return DateTimeOffset.Now.Add(delta);
            }

            return null;
        }

        private static async Task<ProviderFetchResult?> TryFetchWebUsageAsync(CancellationToken ct)
        {
            try
            {
                var cookie = ResolveClaudeWebCookie();
                var headers = BuildWebHeaders(cookie);
                var orgId = await GetWebOrganizationId(cookie, headers, ct).ConfigureAwait(false);
                using var usageDoc = await GetWebJson($"{WebApiBaseUrl}/organizations/{orgId}/usage", headers, "Claude web usage", ct).ConfigureAwait(false);

                var result = BuildResult(usageDoc.RootElement, new Credentials(string.Empty, null, null));
                if (string.IsNullOrWhiteSpace(result.Usage.LoginMethod))
                    result.Usage.LoginMethod = "Claude";
                result.Usage.UsageDashboardUrl = "https://claude.ai/new#settings/usage";

                using var extraDoc = await TryGetWebJson($"{WebApiBaseUrl}/organizations/{orgId}/overage_spend_limit", headers, ct).ConfigureAwait(false);
                if (extraDoc != null && TryParseExtraUsage(extraDoc.RootElement, out var cost, out var additional))
                {
                    result.Usage.Cost = cost;
                    result.Usage.AdditionalUsage = additional;
                }

                using var accountDoc = await TryGetWebJson($"{WebApiBaseUrl}/account", headers, ct).ConfigureAwait(false);
                if (accountDoc != null)
                {
                    var account = accountDoc.RootElement;
                    result.Usage.Email = Str(account, "email_address", "email");
                    var plan = ResolvePlan(null, Str(account, "rate_limit_tier", "rateLimitTier"));
                    if (!string.IsNullOrWhiteSpace(plan))
                        result.Usage.LoginMethod = plan;
                }

                return new ProviderFetchResult(result.Usage, "web");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log.Warning(ex, "Claude OAuth usage was rate limited and the web usage fallback was unavailable.");
                return null;
            }
        }

        private static string ResolveClaudeWebCookie()
        {
            var sessionKey = Environment.GetEnvironmentVariable("CLAUDE_AI_SESSION_KEY") ?? Environment.GetEnvironmentVariable("CLAUDE_WEB_SESSION_KEY");
            if (!string.IsNullOrWhiteSpace(sessionKey))
            {
                var value = sessionKey.Trim();
                if (!value.StartsWith("sessionKey=", StringComparison.OrdinalIgnoreCase))
                    value = "sessionKey=" + value;
                return value;
            }

            return CookieHelper.Resolve(ProviderId.Claude, "claude.ai", "claude.com", "console.anthropic.com", "anthropic.com");
        }

        private static bool ShouldPreferWebUsage()
        {
            var value = Environment.GetEnvironmentVariable(PreferWebEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldForceLoginFlow()
        {
            var value = Environment.GetEnvironmentVariable(ForceLoginEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> BuildWebHeaders(string cookie) => new()
        {
            ["Cookie"] = cookie,
            ["Accept"] = "application/json",
            ["Origin"] = "https://claude.ai",
            ["Referer"] = "https://claude.ai/settings/usage",
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36",
            ["anthropic-client-platform"] = "web_claude_ai",
        };

        private static async Task<string> GetWebOrganizationId(string cookie, Dictionary<string, string> headers, CancellationToken ct)
        {
            if (CookieValue(cookie, "lastActiveOrg") is { } orgFromCookie)
                return orgFromCookie;

            using (var accountDoc = await TryGetWebJson($"{WebApiBaseUrl}/account", headers, ct).ConfigureAwait(false))
            {
                if (accountDoc != null && FindOrganizationId(accountDoc.RootElement) is { } orgFromAccount)
                    return orgFromAccount;
            }

            using var orgsDoc = await GetWebJson($"{WebApiBaseUrl}/organizations", headers, "Claude web organizations", ct).ConfigureAwait(false);
            if (orgsDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var org in orgsDoc.RootElement.EnumerateArray())
                {
                    if (Str(org, "uuid", "id") is { } id)
                        return id;
                }
            }

            throw new ProviderException(ProviderErrorKind.Parse, "Claude web API did not return an organization id.");
        }

        private static string? FindOrganizationId(JsonElement account)
        {
            if (!account.TryGetProperty("memberships", out var memberships) || memberships.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var membership in memberships.EnumerateArray())
            {
                if (membership.TryGetProperty("organization", out var org) && Str(org, "uuid", "id") is { } orgId)
                    return orgId;
                if (Str(membership, "uuid", "id") is { } membershipId)
                    return membershipId;
            }

            return null;
        }

        private static async Task<JsonDocument> GetWebJson(string url, Dictionary<string, string> headers, string label, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Claude web cookies expired.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"{label} returned {(int)response.StatusCode}");

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonDocument.Parse(text);
        }

        private static async Task<JsonDocument?> TryGetWebJson(string url, Dictionary<string, string> headers, CancellationToken ct)
        {
            try { return await GetWebJson(url, headers, "Claude web API", ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        private static string? CookieValue(string cookieHeader, string name)
        {
            foreach (var part in cookieHeader.Split(';'))
            {
                var pieces = part.Trim().Split('=', 2);
                if (pieces.Length == 2 && string.Equals(pieces[0].Trim(), name, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pieces[1]))
                    return pieces[1].Trim();
            }

            return null;
        }

        private static async Task<Credentials?> TryRefreshCredentialsAsync(Credentials creds, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(creds.RefreshToken))
                return null;

            using var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl);
            var body = new
            {
                grant_type = "refresh_token",
                refresh_token = creds.RefreshToken,
                client_id = ClientId,
                scope = RefreshScope,
            };
            request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diagnostics.Log.Warning($"Claude OAuth token refresh failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var accessEl) || accessEl.ValueKind != JsonValueKind.String)
                return null;

            var accessToken = accessEl.GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            var refreshToken = root.TryGetProperty("refresh_token", out var refreshEl) && refreshEl.ValueKind == JsonValueKind.String
                ? refreshEl.GetString()
                : creds.RefreshToken;
            var expiresAtMs = root.TryGetProperty("expires_in", out var expiresEl) && expiresEl.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.Now.AddSeconds(expiresEl.GetDouble()).ToUnixTimeMilliseconds()
                : (long?)null;

            var refreshed = creds with
            {
                AccessToken = accessToken!,
                RefreshToken = refreshToken,
                ExpiresAtMs = expiresAtMs,
            };
            PersistRefreshedCredentials(refreshed);
            Diagnostics.Log.Information("Claude OAuth token refreshed successfully.");
            return refreshed;
        }

        private static void PersistRefreshedCredentials(Credentials creds)
        {
            if (string.IsNullOrWhiteSpace(creds.CredentialsPath) || !File.Exists(creds.CredentialsPath))
                return;

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(creds.CredentialsPath)) as JsonObject ?? new JsonObject();
                var oauth = node["claudeAiOauth"] as JsonObject ?? new JsonObject();
                node["claudeAiOauth"] = oauth;
                oauth["accessToken"] = creds.AccessToken;
                if (!string.IsNullOrWhiteSpace(creds.RefreshToken))
                    oauth["refreshToken"] = creds.RefreshToken;
                if (creds.ExpiresAtMs is { } expiresAt)
                    oauth["expiresAt"] = expiresAt;
                if (!string.IsNullOrWhiteSpace(creds.SubscriptionType))
                    oauth["subscriptionType"] = creds.SubscriptionType;
                if (!string.IsNullOrWhiteSpace(creds.RateLimitTier))
                    oauth["rateLimitTier"] = creds.RateLimitTier;

                File.WriteAllText(creds.CredentialsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to persist refreshed Claude OAuth credentials.");
            }
        }

        internal sealed record Credentials(
            string AccessToken,
            string? SubscriptionType,
            string? RateLimitTier,
            string? RefreshToken = null,
            long? ExpiresAtMs = null,
            string? CredentialsPath = null);

        private static Credentials LoadCredentials()
        {
            // Environment override (matches Win-CodexBar's CODEXBAR_CLAUDE_OAUTH_TOKEN).
            var envToken = Environment.GetEnvironmentVariable("CODEXBAR_CLAUDE_OAUTH_TOKEN")?.Trim();
            if (!string.IsNullOrEmpty(envToken))
                return new Credentials(envToken!, null, null);

            // CLI store: ~/.claude/.credentials.json carries the token when the user ran `claude /login`.
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (TryReadCliCredentials(path) is { } cliCreds)
                return cliCreds with { CredentialsPath = path };

            // Desktop store: when the user signs in through the Claude desktop app instead, the CLI
            // file holds only metadata with an empty token — the live token lives in the desktop
            // app's encrypted config. Auto-detect it so local Claude works without an explicit login.
            if (ClaudeDesktopTokenReader.TryRead() is { } desktop)
                return new Credentials(desktop.AccessToken, desktop.SubscriptionType, desktop.RateLimitTier, desktop.RefreshToken, desktop.ExpiresAtMs);

            // Nothing usable on disk — surface the right state so the caller can fall back to web /
            // prompt the one-click login.
            if (!File.Exists(path) && !ClaudeDesktopTokenReader.IsInstalled)
            {
                if (!ProviderInstallDetector.IsInstalled(ProviderId.Claude))
                    throw new ProviderException(ProviderErrorKind.NotInstalled, ProviderInstallDetector.NotInstalledMessage(ProviderId.Claude));

                throw new ProviderException(ProviderErrorKind.NotRunning, ProviderInstallDetector.WaitingMessage(ProviderId.Claude));
            }

            throw new ProviderException(ProviderErrorKind.AuthRequired, "Claude OAuth access token missing. Run `claude` to authenticate.");
        }

        /// <summary>Reads the CLI credentials file; returns null when it is absent or holds an empty token.</summary>
        private static Credentials? TryReadCliCredentials(string path)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return ReadCredentials(doc.RootElement);
            }
            catch (ProviderException)
            {
                return null; // empty/missing accessToken — let the desktop store take over
            }
        }

        internal static Credentials ReadCredentials(JsonElement root)
        {
            JsonElement oauth = root.TryGetProperty("claudeAiOauth", out var o) ? o : root;

            string? access = oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(access))
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Claude OAuth access token missing. Run `claude` to authenticate.");

            string? tier = oauth.TryGetProperty("rateLimitTier", out var rt) ? rt.GetString() : null;
            string? subType = oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() : null;
            string? refresh = oauth.TryGetProperty("refreshToken", out var refreshEl) ? refreshEl.GetString() : null;
            long? expiresAt = null;
            if (oauth.TryGetProperty("expiresAt", out var expiresEl) && expiresEl.ValueKind == JsonValueKind.Number)
                expiresAt = expiresEl.TryGetInt64(out var raw) ? raw : (long?)expiresEl.GetDouble();
            return new Credentials(access!, subType, tier, refresh, expiresAt);
        }
    }
}
