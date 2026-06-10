using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Grok (xAI) usage. Detected from the Grok CLI (`irm https://x.ai/cli/install.ps1 | iex`), which
    /// stores an OIDC token in ~/.grok/auth.json. Credits come from the CLI proxy's JSON billing
    /// endpoint and the plan name from its settings endpoint (both need the X-XAI-Token-Auth header).
    /// Ported from the openusage grok plugin (plugins/grok/plugin.js), which is more accurate than
    /// CodexBar here: xAI returns the formatted plan name directly, so SuperGrok / SuperGrok Heavy /
    /// X Premium+ need no client-side tier guessing.
    /// </summary>
    public sealed class GrokProvider : IUsageProvider
    {
        private const string OidcScopePrefix = "https://auth.x.ai::";
        private const string LegacySessionScope = "https://accounts.x.ai/sign-in";
        private const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing";
        private const string SettingsUrl = "https://cli-chat-proxy.grok.com/v1/settings";
        private const string TokenAuthHeader = "xai-grok-cli";
        // The billing proxy frequently returns a transient "Timeout expired" on a cold call and
        // succeeds on the next try, so retry several times with a short backoff between attempts.
        private const int BillingRetries = 6;
        private static readonly TimeSpan BillingRetryDelay = TimeSpan.FromMilliseconds(500);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public ProviderId Id => ProviderId.Grok;
        public string DisplayName => "Grok";
        public string SessionLabel => "Credits";
        public string WeeklyLabel => "Monthly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var creds = LoadCredentials();

            var billing = await FetchBillingAsync(creds.AccessToken, ct).ConfigureAwait(false);

            var primary = new RateWindow(
                billing.UsedPercent,
                windowMinutes: null,
                resetAt: billing.ResetAt,
                resetDescription: CodexProvider.FormatResetCountdown(billing.ResetAt));

            var usage = new UsageSnapshot(primary)
            {
                Email = creds.Email,
                UsageDashboardUrl = "https://grok.com/?_s=usage",
            };

            // Surface credits the same way GitHub Copilot does: a "Credits" cost meter
            // (remaining against the monthly limit) instead of a plain percent bar.
            if (billing.LimitUnits > 0)
            {
                var cost = new CostSnapshot(billing.LimitUnits - billing.UsedUnits, "credits", "Credits")
                    .WithLimit(billing.LimitUnits);
                if (billing.ResetAt is { } resetsAt)
                    cost.WithResetsAt(resetsAt);
                usage.Cost = cost;
            }

            // Plan name is best-effort: xAI formats it for us, but a missing field must not fail the fetch.
            var plan = await TryFetchPlanAsync(creds, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(plan))
                usage.LoginMethod = plan;

            return new ProviderFetchResult(usage, "grok-cli");
        }

        // --- Credits (/v1/billing JSON) ----------------------------------------------------------

        internal sealed record BillingSnapshot(double UsedPercent, double UsedUnits, double LimitUnits, DateTimeOffset? ResetAt);

        private static async Task<BillingSnapshot> FetchBillingAsync(string accessToken, CancellationToken ct)
        {
            Exception? last = null;
            for (int attempt = 0; attempt < BillingRetries; attempt++)
            {
                try
                {
                    return await FetchBillingOnceAsync(accessToken, ct).ConfigureAwait(false);
                }
                catch (ProviderException pe) when (pe.Kind == ProviderErrorKind.Timeout && attempt < BillingRetries - 1)
                {
                    last = pe;
                    await Task.Delay(BillingRetryDelay, ct).ConfigureAwait(false);
                }
            }

            throw last ?? new ProviderException(ProviderErrorKind.Timeout, "Grok billing request timed out.");
        }

        private static async Task<BillingSnapshot> FetchBillingOnceAsync(string accessToken, CancellationToken ct)
        {
            using var request = NewGrokRequest(BillingUrl, accessToken);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Grok token expired. Run `grok login`.");

            var bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // xAI intermittently returns 400 { code:"The operation was cancelled", error:"Timeout expired" }.
            if (!response.IsSuccessStatusCode)
            {
                if (IsTransientBody(bodyText))
                    throw new ProviderException(ProviderErrorKind.Timeout, "Grok billing transient timeout.");
                throw new ProviderException(ProviderErrorKind.Other, $"Grok billing API returned {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(bodyText);
            return ParseBilling(doc.RootElement);
        }

        private static bool IsTransientBody(string body)
            => body.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || body.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            || body.Contains("deadline", StringComparison.OrdinalIgnoreCase);

        internal static BillingSnapshot ParseBilling(JsonElement root)
        {
            if (!root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
                throw new ProviderException(ProviderErrorKind.Parse, "Grok billing response changed.");

            double? used = UnitsValue(config, "used");
            double? limit = UnitsValue(config, "monthlyLimit");
            if (used is null || limit is null || limit <= 0)
                throw new ProviderException(ProviderErrorKind.Parse, "Grok billing response changed.");

            double percent = Math.Clamp(used.Value / limit.Value * 100.0, 0, 100);

            DateTimeOffset? reset = null;
            if (config.TryGetProperty("billingPeriodEnd", out var end) && end.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                reset = dt;
            }

            return new BillingSnapshot(percent, used.Value, limit.Value, reset);
        }

        private static double? UnitsValue(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object)
                return null;
            if (!obj.TryGetProperty("val", out var val))
                return null;
            return val.ValueKind switch
            {
                JsonValueKind.Number => val.GetDouble(),
                JsonValueKind.String when double.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) => v,
                _ => null,
            };
        }

        // --- Plan (/v1/settings JSON) ------------------------------------------------------------

        private static async Task<string?> TryFetchPlanAsync(Credentials creds, CancellationToken ct)
        {
            try
            {
                using var request = NewGrokRequest(SettingsUrl, creds.AccessToken);
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return PlanFromAuthMode(creds.AuthMode);

                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                return PlanFromSettings(doc.RootElement) ?? PlanFromAuthMode(creds.AuthMode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log.Warning(ex, "Grok settings lookup failed");
                return PlanFromAuthMode(creds.AuthMode);
            }
        }

        /// <summary>xAI returns a ready-to-display plan name (e.g. "SuperGrok", "SuperGrok Heavy", "X Premium+").</summary>
        internal static string? PlanFromSettings(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("subscription_tier_display", out var tier)
                && tier.ValueKind == JsonValueKind.String
                && tier.GetString()?.Trim() is { Length: > 0 } plan)
            {
                return plan;
            }
            return null;
        }

        private static string? PlanFromAuthMode(string? authMode) => authMode?.ToLowerInvariant() switch
        {
            "oidc" => "SuperGrok",
            "session" => "Session",
            _ => null,
        };

        private static HttpRequestMessage NewGrokRequest(string url, string accessToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", TokenAuthHeader);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("TaskbarQuota");
            return request;
        }

        // --- Credentials (~/.grok/auth.json) -----------------------------------------------------

        internal sealed record Credentials(string AccessToken, string? Email, string? TeamId, string? AuthMode);

        private static Credentials LoadCredentials()
        {
            var path = GetAuthPath();
            if (!File.Exists(path))
                throw new ProviderException(ProviderErrorKind.NotInstalled, "Grok auth.json not found. Run `grok login`.");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ReadCredentials(doc.RootElement);
        }

        internal static Credentials ReadCredentials(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new ProviderException(ProviderErrorKind.Parse, "Grok auth.json is not an object.");

            JsonElement? oidc = null, legacy = null;
            foreach (var entry in root.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (!entry.Value.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(key.GetString()))
                    continue;

                if (entry.Name.StartsWith(OidcScopePrefix, StringComparison.Ordinal))
                    oidc = entry.Value;
                else if (entry.Name == LegacySessionScope || entry.Name.Contains("/sign-in", StringComparison.Ordinal))
                    legacy = entry.Value;
            }

            var chosen = oidc ?? legacy;
            if (chosen is not { } e)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Grok auth.json contains no usable token. Run `grok login`.");

            string access = e.GetProperty("key").GetString()!;
            string? email = Str(e, "email");
            string? team = Str(e, "team_id");
            string? authMode = Str(e, "auth_mode");
            return new Credentials(access, email, team, authMode);
        }

        private static string? Str(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()?.Trim() is { Length: > 0 } s ? s : null
                : null;

        private static string GetAuthPath()
        {
            var grokHome = Environment.GetEnvironmentVariable("GROK_HOME")?.Trim();
            if (!string.IsNullOrEmpty(grokHome))
                return Path.Combine(grokHome, "auth.json");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "auth.json");
        }
    }
}
