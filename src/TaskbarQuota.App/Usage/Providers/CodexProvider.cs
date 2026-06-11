using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using TaskbarQuota;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Codex (ChatGPT) usage via the OAuth token stored by the Codex CLI in ~/.codex/auth.json.
    /// Ported from Win-CodexBar rust/src/providers/codex/api.rs.
    /// </summary>
    public sealed class CodexProvider : IUsageProvider
    {
        private const string DefaultBaseUrl = "https://chatgpt.com/backend-api";
        private const string UsagePath = "/wham/usage";
        private static readonly Regex CodexModelPrefix = new(@"^GPT-[\d.]+-Codex-", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HttpClient Http = new(new HttpClientHandler())
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        public ProviderId Id => ProviderId.Codex;
        public string DisplayName => "Codex";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var creds = LoadCredentials();

            using var request = new HttpRequestMessage(HttpMethod.Get, ResolveBaseUrl() + UsagePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            request.Headers.UserAgent.ParseAdd("TaskbarQuota");
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrEmpty(creds.AccountId))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", creds.AccountId);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Codex token expired. Run `codex login`.");

            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Codex API returned {(int)response.StatusCode}");

            double? headerPrimary = TryHeaderF64(response, "x-codex-primary-used-percent");
            double? headerSecondary = TryHeaderF64(response, "x-codex-secondary-used-percent");
            double? headerCredits = TryHeaderF64(response, "x-codex-credits-balance");

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            return BuildResult(doc.RootElement, headerPrimary, headerSecondary, headerCredits);
        }

        internal static ProviderFetchResult BuildResult(JsonElement json, double? headerPrimary = null, double? headerSecondary = null, double? headerCredits = null)
        {
            var (primary, secondary, codeReview) = ExtractRateLimits(json);
            if (headerPrimary is double hp) primary = WithUsedPercent(primary, hp);
            if (headerSecondary is double hs)
            {
                secondary = secondary is null
                    ? new RateWindow(hs, 10080)
                    : WithUsedPercent(secondary, hs);
            }

            var usage = new UsageSnapshot(primary);
            if (secondary != null) usage.Secondary = secondary;
            if (codeReview != null) usage.ModelSpecific = codeReview;

            string? planType = json.TryGetProperty("plan_type", out var planEl) && planEl.ValueKind == JsonValueKind.String
                ? planEl.GetString()
                : null;
            AddAdditionalRateLimits(json, usage, planType);

            if (!string.IsNullOrWhiteSpace(planType))
                usage.LoginMethod = PlanDisplay(planType!);

            if (json.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String)
                usage.Email = emailEl.GetString();

            usage.Cost = ExtractCredits(json, headerCredits);

            return new ProviderFetchResult(usage, "oauth");
        }

        private static (RateWindow primary, RateWindow? secondary, RateWindow? codeReview) ExtractRateLimits(JsonElement json)
        {
            if (json.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                RateWindow? p = rl.TryGetProperty("primary_window", out var pw) && pw.ValueKind == JsonValueKind.Object ? ParseWindow(pw) : null;
                RateWindow? s = rl.TryGetProperty("secondary_window", out var sw) && sw.ValueKind == JsonValueKind.Object ? ParseWindow(sw) : null;
                RateWindow? cr = rl.TryGetProperty("code_review_window", out var cw) && cw.ValueKind == JsonValueKind.Object ? ParseWindow(cw) : null;
                if (cr is null &&
                    json.TryGetProperty("code_review_rate_limit", out var codeReview) &&
                    codeReview.ValueKind == JsonValueKind.Object &&
                    codeReview.TryGetProperty("primary_window", out var rw) &&
                    rw.ValueKind == JsonValueKind.Object)
                {
                    cr = ParseWindow(rw);
                }

                // Promote secondary to primary for weekly-only plans
                if (p == null && s != null) { p = s; s = null; }
                return (p ?? new RateWindow(0), s, cr);
            }

            return (new RateWindow(0), null, null);
        }

        private static void AddAdditionalRateLimits(JsonElement json, UsageSnapshot usage, string? planType)
        {
            if (!json.TryGetProperty("additional_rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                return;

            foreach (var entry in limits.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("rate_limit", out var rl) ||
                    rl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string rawName = entry.TryGetProperty("limit_name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;
                string shortName = CodexModelPrefix.Replace(rawName, string.Empty);
                if (string.IsNullOrWhiteSpace(shortName))
                    shortName = string.IsNullOrWhiteSpace(rawName) ? "Model" : rawName;
                if (IsSparkLimit(shortName) && !IsProPlan(planType))
                    continue;

                if (rl.TryGetProperty("primary_window", out var primary) && primary.ValueKind == JsonValueKind.Object)
                    usage.ExtraRateWindows.Add(new NamedRateWindow($"{shortName}-session", $"{shortName} Session", ParseWindow(primary)));
                if (rl.TryGetProperty("secondary_window", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
                    usage.ExtraRateWindows.Add(new NamedRateWindow($"{shortName}-weekly", $"{shortName} Weekly", ParseWindow(secondary)));
            }
        }

        private static bool IsSparkLimit(string shortName)
            => string.Equals(shortName.Trim(), "Spark", StringComparison.OrdinalIgnoreCase);

        private static bool IsProPlan(string? planType)
        {
            var normalized = NormalizePlanType(planType);
            return normalized is "pro" or "prolite" or "pro_lite" or "pro-lite";
        }

        private static RateWindow ParseWindow(JsonElement window)
        {
            double used = TryF64(window, "used_percent") ?? TryF64(window, "usage_percent") ?? 0;
            int? minutes = null;
            if (window.TryGetProperty("limit_window_seconds", out var lw) && lw.TryGetInt64(out var secs))
                minutes = (int)(secs / 60);

            DateTimeOffset? resetAt = null;
            if (window.TryGetProperty("reset_at", out var ra) && ra.TryGetInt64(out var ts))
                resetAt = DateTimeOffset.FromUnixTimeSeconds(ts);

            return new RateWindow(used, minutes, resetAt, FormatResetCountdown(resetAt));
        }

        private static RateWindow WithUsedPercent(RateWindow window, double usedPercent)
            => new(usedPercent, window.WindowMinutes, window.ResetAt, window.ResetDescription);

        private static CostSnapshot? ExtractCredits(JsonElement json, double? headerCredits)
        {
            double? balance = headerCredits;
            if (!json.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object)
                return balance is null ? null : new CostSnapshot(balance.Value, "credits", "Credits").WithLimit(1000);

            bool hasCredits = credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True;
            if (!hasCredits && balance is null) return null;
            if (credits.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True) return null;
            balance ??= TryF64(credits, "balance") ?? (hasCredits ? 0 : null);
            return balance is null ? null : new CostSnapshot(balance.Value, "credits", "Credits").WithLimit(1000);
        }

        private static double? TryHeaderF64(HttpResponseMessage response, string name)
        {
            if (!response.Headers.TryGetValues(name, out var values)) return null;
            foreach (var value in values)
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return null;
        }

        private static double? TryF64(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.String when double.TryParse(el.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
                _ => null,
            };
        }

        internal static string? FormatResetCountdown(DateTimeOffset? resetAt)
        {
            if (resetAt is not DateTimeOffset dt) return null;
            var diff = dt - DateTimeOffset.UtcNow;
            if (diff <= TimeSpan.Zero) return "now";
            int hours = (int)diff.TotalHours;
            int mins = diff.Minutes;
            if (hours >= 24)
            {
                int days = hours / 24, remH = hours % 24;
                return remH == 0 ? $"{days}d" : $"{days}d {remH}h";
            }
            if (hours > 0) return mins == 0 ? $"{hours}h" : $"{hours}h {mins}m";
            return $"{mins}m";
        }

        private static string PlanDisplay(string pt) => NormalizePlanType(pt) switch
        {
            "guest" => "Guest",
            "free" => "Free",
            "go" => "Go",
            "plus" => "Plus",
            "pro" => "Pro 20x",
            "pro_lite" or "prolite" or "pro-lite" => "Pro 5x",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "education" or "edu" => "Education",
            _ => Capitalize(pt),
        };

        private static string NormalizePlanType(string? pt) => (pt ?? string.Empty).Trim().ToLowerInvariant();

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

        private sealed record Credentials(string AccessToken, string? AccountId);

        private static Credentials LoadCredentials()
        {
            var authPath = GetAuthPath();
            if (!File.Exists(authPath))
            {
                if (!ProviderInstallDetector.IsInstalled(ProviderId.Codex))
                    throw new ProviderException(ProviderErrorKind.NotInstalled, ProviderInstallDetector.NotInstalledMessage(ProviderId.Codex));

                throw new ProviderException(ProviderErrorKind.AuthRequired, "Codex auth.json not found. Run `codex login`.");
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(authPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                string? access = tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                if (!string.IsNullOrEmpty(access))
                {
                    string? acct = tokens.TryGetProperty("account_id", out var ac) ? ac.GetString() : null;
                    return new Credentials(access!, acct);
                }
            }

            if (root.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind == JsonValueKind.String)
            {
                var k = key.GetString()?.Trim();
                if (!string.IsNullOrEmpty(k)) return new Credentials(k!, null);
            }

            throw new ProviderException(ProviderErrorKind.Parse, "Codex auth.json contains no usable token.");
        }

        private static string GetAuthPath()
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim();
            if (!string.IsNullOrEmpty(codexHome))
                return Path.Combine(codexHome, "auth.json");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        }

        private static string ResolveBaseUrl()
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim();
            var configPath = !string.IsNullOrEmpty(codexHome)
                ? Path.Combine(codexHome, "config.toml")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

            if (File.Exists(configPath))
            {
                foreach (var raw in File.ReadAllLines(configPath))
                {
                    var line = raw.Split('#')[0].Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line[..eq].Trim() != "chatgpt_base_url") continue;
                    var val = line[(eq + 1)..].Trim().Trim('"', '\'');
                    var normalized = NormalizeBaseUrl(val);
                    if (normalized.StartsWith("https://") || normalized.StartsWith("http://127.0.0.1") || normalized.StartsWith("http://localhost"))
                        return normalized;
                }
            }
            return DefaultBaseUrl;
        }

        private static string NormalizeBaseUrl(string url)
        {
            var trimmed = url.Trim().TrimEnd('/');
            if (trimmed.Length == 0) return DefaultBaseUrl;
            if ((trimmed.StartsWith("https://chatgpt.com") || trimmed.StartsWith("https://chat.openai.com")) && !trimmed.Contains("/backend-api"))
                trimmed += "/backend-api";
            return trimmed;
        }
    }
}
