using System;
using System.Collections.Generic;
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
    /// Claude (Anthropic) usage via the OAuth token Claude Code stores in ~/.claude/.credentials.json,
    /// querying https://api.anthropic.com/api/oauth/usage.
    /// Ported from Win-CodexBar rust/src/providers/claude/oauth.rs.
    /// </summary>
    public sealed class ClaudeProvider : IUsageProvider
    {
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public ProviderId Id => ProviderId.Claude;
        public string DisplayName => "Claude Code";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var creds = LoadCredentials();

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Claude OAuth token expired. Run `claude` to re-authenticate.");
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                throw new ProviderException(ProviderErrorKind.RateLimited,
                    $"Claude API rate limited ({(int)response.StatusCode}). Will retry in a few minutes.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Claude API returned {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            return BuildResult(doc.RootElement, creds);
        }

        private static ProviderFetchResult BuildResult(JsonElement json, Credentials creds)
        {
            var primary = ParseWindow(json, "five_hour", 300) ?? new RateWindow(0);
            var usage = new UsageSnapshot(primary);

            usage.Secondary = ParseWindow(json, "seven_day", 10080);
            usage.ModelSpecific = ParseWindow(json, "seven_day_opus", 10080) ?? ParseWindow(json, "seven_day_sonnet", 10080);
            usage.LoginMethod = ResolvePlan(creds.SubscriptionType, creds.RateLimitTier);
            return new ProviderFetchResult(usage, "oauth");
        }

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

        private static RateWindow? ParseWindow(JsonElement root, string name, int windowMinutes)
        {
            if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
            if (!w.TryGetProperty("utilization", out var ut) || ut.ValueKind != JsonValueKind.Number) return null;

            double util = ut.GetDouble();
            if (util > 0 && util <= 1.0) util *= 100.0; // some payloads use 0..1

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

        internal sealed record Credentials(string AccessToken, string? SubscriptionType, string? RateLimitTier);

        private static Credentials LoadCredentials()
        {
            // Environment override (matches Win-CodexBar's CODEXBAR_CLAUDE_OAUTH_TOKEN).
            var envToken = Environment.GetEnvironmentVariable("CODEXBAR_CLAUDE_OAUTH_TOKEN")?.Trim();
            if (!string.IsNullOrEmpty(envToken))
                return new Credentials(envToken!, null, null);

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (!File.Exists(path))
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Claude credentials not found. Run `claude` to authenticate.");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ReadCredentials(doc.RootElement);
        }

        internal static Credentials ReadCredentials(JsonElement root)
        {
            JsonElement oauth = root.TryGetProperty("claudeAiOauth", out var o) ? o : root;

            string? access = oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(access))
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Claude OAuth access token missing. Run `claude` to authenticate.");

            string? tier = oauth.TryGetProperty("rateLimitTier", out var rt) ? rt.GetString() : null;
            string? subType = oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() : null;
            return new Credentials(access!, subType, tier);
        }
    }
}
