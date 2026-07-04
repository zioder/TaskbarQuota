using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Shared access to the Cline account API for the two Cline surfaces (mirrors OpenCode Zen/Go):
    ///  - <see cref="ClinePassProvider"/> reads the ClinePass subscription (rolling 5h/weekly/monthly windows);
    ///  - <see cref="ClineProvider"/> reads the pay-as-you-go usage-billing credit balance.
    ///
    /// Auth lives in <c>~/.cline/data/settings/providers.json</c>, one WorkOS access/refresh pair per
    /// provider key ("cline-pass" = subscription, "cline" = usage-billing), plus a top-level
    /// <c>lastUsedProvider</c> that names the surface the CLI is currently drawing from. Balance and
    /// usage-limits are account-wide, so either key's token works; each provider prefers its own key but
    /// falls back to the other so a card still populates when the user has only configured one surface.
    ///
    /// The stored access token is short-lived; when expired we mint a fresh one in-memory via
    /// <c>POST /api/v1/auth/refresh</c> (Cline refresh tokens are reusable, so this does not disturb the
    /// CLI and we never write the file back). The API expects <c>Authorization: Bearer workos:&lt;jwt&gt;</c>.
    /// </summary>
    internal static class ClineAccount
    {
        public const string SubscriptionKey = "cline-pass";
        public const string UsageBillingKey = "cline";

        private const string ApiBaseUrl = "https://api.cline.bot";
        private const string WorkOsPrefix = "workos:";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // --- Config file --------------------------------------------------------------------------

        public static string ProvidersJsonPath()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cline", "data", "settings", "providers.json");

        // ActiveProviderKey is polled on the hot active-detection path (per cline candidate, every tick).
        // Cache the parsed value briefly so we don't read+parse providers.json on every call; the
        // ClineStateWatcher invalidates it immediately when the file actually changes.
        private static readonly object KeyCacheLock = new();
        private static string? _cachedKey;
        private static DateTime _cachedKeyAtUtc = DateTime.MinValue;
        private static readonly TimeSpan KeyCacheTtl = TimeSpan.FromSeconds(3);

        public static void InvalidateActiveProviderKeyCache()
        {
            lock (KeyCacheLock) _cachedKeyAtUtc = DateTime.MinValue;
        }

        /// <summary>The provider key the Cline CLI is currently drawing from ("cline-pass" or "cline"), or null.</summary>
        public static string? ActiveProviderKey()
        {
            lock (KeyCacheLock)
            {
                if (DateTime.UtcNow - _cachedKeyAtUtc < KeyCacheTtl)
                    return _cachedKey;
            }

            string? key = ReadActiveProviderKey();

            lock (KeyCacheLock)
            {
                _cachedKey = key;
                _cachedKeyAtUtc = DateTime.UtcNow;
            }
            return key;
        }

        private static string? ReadActiveProviderKey()
        {
            try
            {
                var path = ProvidersJsonPath();
                if (!File.Exists(path)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty("lastUsedProvider", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"Cline lastUsedProvider read failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Loads auth for <paramref name="preferredKey"/>, falling back to the other Cline key (same account).</summary>
        public static AuthTokens? LoadAuth(string preferredKey)
        {
            var path = ProvidersJsonPath();
            if (!File.Exists(path)) return null;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("providers", out var providers)
                    || providers.ValueKind != JsonValueKind.Object)
                    return null;

                var other = preferredKey == SubscriptionKey ? UsageBillingKey : SubscriptionKey;
                return ReadProviderAuth(providers, preferredKey) ?? ReadProviderAuth(providers, other);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to read Cline providers.json");
                return null;
            }
        }

        public static bool IsConfigured() => File.Exists(ProvidersJsonPath());

        internal static AuthTokens? ReadProviderAuth(JsonElement providers, string key)
        {
            if (!providers.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object
                || !settings.TryGetProperty("auth", out var auth) || auth.ValueKind != JsonValueKind.Object)
                return null;

            var accessToken = auth.TryGetProperty("accessToken", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            var refreshToken = auth.TryGetProperty("refreshToken", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString() : null;

            DateTimeOffset? expiresAt = null;
            if (auth.TryGetProperty("expiresAt", out var ea) && ea.ValueKind == JsonValueKind.Number
                && ea.TryGetInt64(out var ms) && ms > 0)
            {
                try { expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms); } catch { }
            }

            var accountId = auth.TryGetProperty("accountId", out var acc) && acc.ValueKind == JsonValueKind.String
                ? acc.GetString() : null;

            return new AuthTokens(accessToken!, refreshToken, expiresAt, ReadJwtEmail(accessToken!), accountId);
        }

        // --- HTTP ---------------------------------------------------------------------------------

        public static async Task<JsonElement> GetJsonAsync(string path, string bearer, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBaseUrl + path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired,
                    "Cline sign-in expired. Run `cline auth login` or sign in to the Cline app.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Cline API returned {(int)response.StatusCode}.");

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        /// <summary>Returns a valid Bearer value (with the <c>workos:</c> prefix), refreshing if the stored token expired.</summary>
        public static async Task<string> ResolveBearerAsync(AuthTokens auth, CancellationToken ct)
        {
            bool expired = auth.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow.AddSeconds(60);
            if (!expired && !string.IsNullOrEmpty(auth.AccessToken))
                return EnsureWorkOsPrefix(auth.AccessToken);

            if (string.IsNullOrEmpty(auth.RefreshToken))
                throw new ProviderException(ProviderErrorKind.AuthRequired,
                    "Cline sign-in expired. Run `cline auth login` or sign in to the Cline app.");

            var refreshed = await RefreshAsync(auth.RefreshToken, ct).ConfigureAwait(false);
            return EnsureWorkOsPrefix(refreshed);
        }

        private static async Task<string> RefreshAsync(string refreshToken, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new { refreshToken, grantType = "refresh_token" });
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiBaseUrl + "/api/v1/auth/refresh")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.AuthRequired,
                    "Cline sign-in expired. Run `cline auth login` or sign in to the Cline app.");

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("accessToken", out var at) && at.ValueKind == JsonValueKind.String
                && at.GetString() is { Length: > 0 } token)
            {
                return token;
            }

            throw new ProviderException(ProviderErrorKind.AuthRequired, "Cline token refresh returned no access token.");
        }

        private static string EnsureWorkOsPrefix(string token)
            => token.StartsWith(WorkOsPrefix, StringComparison.OrdinalIgnoreCase) ? token : WorkOsPrefix + token;

        // --- Account queries ----------------------------------------------------------------------

        public static async Task<string?> TryGetPlanNameAsync(string bearer, CancellationToken ct)
        {
            try
            {
                var plan = await GetJsonAsync("/api/v1/users/me/plan", bearer, ct).ConfigureAwait(false);
                if (plan.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                    && dn.GetString()?.Trim() is { Length: > 0 } name)
                {
                    return name;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log.Debug($"Cline plan name fetch failed: {ex.Message}");
            }
            return null;
        }

        public static async Task<double?> TryGetBalanceAsync(AuthTokens auth, string bearer, CancellationToken ct)
        {
            try
            {
                var userId = auth.AccountId;
                if (string.IsNullOrWhiteSpace(userId))
                {
                    var me = await GetJsonAsync("/api/v1/users/me", bearer, ct).ConfigureAwait(false);
                    userId = me.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                        && d.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() : null;
                }
                if (string.IsNullOrWhiteSpace(userId))
                    return null;

                var json = await GetJsonAsync($"/api/v1/users/{Uri.EscapeDataString(userId)}/balance", bearer, ct).ConfigureAwait(false);
                if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Number)
                {
                    return b.GetDouble();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log.Debug($"Cline balance fetch failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Parses the ClinePass usage-limits payload into 5h (primary) / weekly / monthly windows.</summary>
        internal static bool TryBuildUsage(JsonElement root, out UsageSnapshot usage)
        {
            usage = null!;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                return false;

            RateWindow? fiveHour = null, weekly = null, monthly = null;
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                double used = item.TryGetProperty("percentUsed", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
                var resetAt = ParseDate(item, "resetsAt");
                var window = new RateWindow(used, windowMinutes: null, resetAt: resetAt,
                    resetDescription: CodexProvider.FormatResetCountdown(resetAt));

                switch (type)
                {
                    case "five_hour": fiveHour = window; break;
                    case "weekly": weekly = window; break;
                    case "monthly": monthly = window; break;
                }
            }

            var primary = fiveHour ?? weekly ?? monthly;
            if (primary is null)
                return false;

            usage = new UsageSnapshot(primary);
            if (fiveHour is not null)
            {
                usage.Secondary = weekly;
                usage.Monthly = monthly;
            }
            else if (weekly is not null)
            {
                usage.Monthly = monthly;
            }
            return true;
        }

        private static DateTimeOffset? ParseDate(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
                return null;
            return DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
        }

        // --- Auth token ---------------------------------------------------------------------------

        internal sealed record AuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? Email, string? AccountId);

        /// <summary>Best-effort email from the WorkOS JWT payload (no signature verification — display only).</summary>
        internal static string? ReadJwtEmail(string accessToken)
        {
            try
            {
                var token = accessToken.StartsWith(WorkOsPrefix, StringComparison.OrdinalIgnoreCase)
                    ? accessToken[WorkOsPrefix.Length..]
                    : accessToken;
                var parts = token.Split('.');
                if (parts.Length < 2) return null;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
                return doc.RootElement.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        public static ProviderException NotConfigured()
        {
            if (!ProviderInstallDetector.IsInstalled(ProviderId.Cline))
                return new ProviderException(ProviderErrorKind.NotInstalled,
                    ProviderInstallDetector.NotInstalledMessage(ProviderId.Cline));

            return new ProviderException(ProviderErrorKind.AuthRequired,
                "Cline sign-in not found. Run `cline auth login` or sign in to the Cline app.");
        }
    }

    /// <summary>ClinePass subscription: rolling 5-hour / weekly / monthly usage windows (like OpenCode Go).</summary>
    public sealed class ClinePassProvider : IUsageProvider
    {
        private const string DashboardUrl = "https://app.cline.bot/dashboard/subscription";

        public ProviderId Id => ProviderId.ClinePass;
        public string DisplayName => "ClinePass";
        public string SessionLabel => "5h";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var auth = ClineAccount.LoadAuth(ClineAccount.SubscriptionKey) ?? throw ClineAccount.NotConfigured();
            var bearer = await ClineAccount.ResolveBearerAsync(auth, ct).ConfigureAwait(false);

            var limits = await ClineAccount.GetJsonAsync("/api/v1/users/me/plan/usage-limits", bearer, ct).ConfigureAwait(false);
            if (!ClineAccount.TryBuildUsage(limits, out var usage))
                throw new ProviderException(ProviderErrorKind.Parse, "No active ClinePass subscription.");

            usage.LoginMethod = await ClineAccount.TryGetPlanNameAsync(bearer, ct).ConfigureAwait(false);
            usage.Email = auth.Email;
            usage.UsageDashboardUrl = DashboardUrl;

            return new ProviderFetchResult(usage, "ClinePass");
        }
    }

    /// <summary>Cline usage-billing: pay-as-you-go credit balance (like OpenCode Zen).</summary>
    public sealed class ClineProvider : IUsageProvider
    {
        private const string DashboardUrl = "https://app.cline.bot/dashboard/credits";

        public ProviderId Id => ProviderId.Cline;
        public string DisplayName => "Cline Usage-Billing";
        public string SessionLabel => "Credits";
        public string WeeklyLabel => "Balance";
        public BillingKind Billing => BillingKind.Api;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var auth = ClineAccount.LoadAuth(ClineAccount.UsageBillingKey) ?? throw ClineAccount.NotConfigured();
            var bearer = await ClineAccount.ResolveBearerAsync(auth, ct).ConfigureAwait(false);

            double? balance = await ClineAccount.TryGetBalanceAsync(auth, bearer, ct).ConfigureAwait(false);
            if (balance is null)
                throw new ProviderException(ProviderErrorKind.Parse, "Cline credit balance unavailable. Try again later.");

            // Credits-only card: no percent windows, just the remaining pay-as-you-go balance.
            var usage = new UsageSnapshot(new RateWindow(0))
            {
                HasPrimaryWindow = false,
                LoginMethod = "Usage-Billing",
                Email = auth.Email,
                Cost = new CostSnapshot(balance.Value, "USD", "Credit Balance"),
                UsageDashboardUrl = DashboardUrl,
            };

            return new ProviderFetchResult(usage, "Cline");
        }
    }
}
