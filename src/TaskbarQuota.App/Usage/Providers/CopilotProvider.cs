using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarQuota.Usage.Providers
{
    public sealed class CopilotProvider : IUsageProvider
    {
        private const string UserEndpoint = "https://api.github.com/copilot_internal/user";

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        public ProviderId Id => ProviderId.Copilot;
        public string DisplayName => "Copilot";
        public string SessionLabel => "Credits";
        public string WeeklyLabel => "Chat";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            string token = ResolveToken();

            using var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("TaskbarQuota");
            request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.99.0");
            request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.27.0");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "GitHub token cannot read Copilot usage. Run `gh auth login` or add a GitHub token in Settings.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"GitHub Copilot API returned {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return BuildResult(doc.RootElement);
        }

        internal static ProviderFetchResult BuildResult(JsonElement root)
        {
            var reset = ParseReset(root);
            var resetDescription = CodexProvider.FormatResetCountdown(reset);

            if (TryParseAiCredits(root, out var credits))
            {
                double limit = credits.Limit ?? 0;
                double remaining = credits.Amount;
                double used = System.Math.Max(0, limit - remaining);
                double usedPercent = limit <= 0 ? 0 : System.Math.Clamp(used / limit * 100, 0, 100);

                if (reset is DateTimeOffset resetAt)
                    credits = credits.WithResetsAt(resetAt);

                var usage = new UsageSnapshot(new RateWindow(usedPercent, null, reset, resetDescription))
                {
                    Cost = credits,
                    AdditionalUsage = TryParseAdditionalUsage(root),
                    LoginMethod = ResolvePlanName(root),
                    Email = root.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String
                        ? login.GetString()
                        : null,
                };

                return new ProviderFetchResult(usage, "github");
            }

            var windows = ExtractWindows(root, reset);
            if (windows.Count == 0)
                windows.Add(new NamedRateWindow("premium_interactions", "Premium", new RateWindow(0)));

            var primaryNamed = windows.FirstOrDefault(w => w.Id == "premium_interactions") ?? windows[0];
            var usageLegacy = new UsageSnapshot(primaryNamed.Window);

            foreach (var window in windows)
            {
                if (ReferenceEquals(window, primaryNamed))
                    continue;
                if (usageLegacy.Secondary == null)
                    usageLegacy.Secondary = window.Window;
                else if (usageLegacy.ModelSpecific == null)
                    usageLegacy.ModelSpecific = window.Window;
                else if (usageLegacy.Monthly == null)
                    usageLegacy.Monthly = window.Window;
                else
                    usageLegacy.ExtraRateWindows.Add(window);
            }

            usageLegacy.LoginMethod = ResolvePlanName(root);
            usageLegacy.AdditionalUsage = TryParseAdditionalUsage(root);
            if (root.TryGetProperty("login", out var loginLegacy) && loginLegacy.ValueKind == JsonValueKind.String)
                usageLegacy.Email = loginLegacy.GetString();

            return new ProviderFetchResult(usageLegacy, "github");
        }

        internal static AdditionalUsageSnapshot? TryParseAdditionalUsage(JsonElement root)
        {
            if (!root.TryGetProperty("quota_snapshots", out var snapshots)
                || snapshots.ValueKind != JsonValueKind.Object
                || !snapshots.TryGetProperty("premium_interactions", out var premium)
                || premium.ValueKind != JsonValueKind.Object)
                return null;

            bool enabled = premium.TryGetProperty("overage_permitted", out var permitted)
                && permitted.ValueKind == JsonValueKind.True;
            double overageUnits = TryF64(premium, "overage_count") ?? 0;
            double unitUsd = UsesAiCredits(root) ? 0.01 : 0.04;
            double spentUsd = overageUnits * unitUsd;

            double? budgetUsd = enabled ? TryFindAdditionalBudgetUsd(root, premium, UsesAiCredits(root)) : 0;
            return new AdditionalUsageSnapshot
            {
                Enabled = enabled,
                SpentUsd = spentUsd,
                BudgetUsd = budgetUsd,
            };
        }

        private static double? TryFindAdditionalBudgetUsd(JsonElement root, JsonElement premium, bool tokenBasedBilling)
        {
            string[] usdNames =
            [
                "additional_usage_budget_usd",
                "overage_budget_usd",
                "metered_budget_usd",
                "budget_usd",
                "spend_limit_usd",
            ];

            foreach (var name in usdNames)
            {
                double? value = TryF64(root, name) ?? TryF64(premium, name);
                if (value is >= 0)
                    return value;
            }

            if (!tokenBasedBilling)
                return null;

            string[] creditBudgetNames = ["additional_usage_budget", "overage_budget", "metered_budget", "additional_spend_budget"];
            foreach (var name in creditBudgetNames)
            {
                double? value = TryF64(root, name) ?? TryF64(premium, name);
                if (value is > 0)
                    return value * 0.01;
            }

            return null;
        }

        internal static bool TryParseAiCredits(JsonElement root, out CostSnapshot credits)
        {
            credits = null!;
            if (!UsesAiCredits(root))
                return false;

            if (!root.TryGetProperty("quota_snapshots", out var snapshots)
                || snapshots.ValueKind != JsonValueKind.Object
                || !snapshots.TryGetProperty("premium_interactions", out var premium)
                || premium.ValueKind != JsonValueKind.Object)
                return false;

            bool unlimited = premium.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True;
            if (unlimited)
                return false;

            double? limit = TryF64(premium, "entitlement");
            double? remaining = TryF64(premium, "remaining") ?? TryF64(premium, "quota_remaining");
            if (limit is not > 0 || remaining is null)
                return false;

            credits = new CostSnapshot(remaining.Value, "credits", "Credits").WithLimit(limit.Value);
            return true;
        }

        private static bool UsesAiCredits(JsonElement root)
        {
            if (root.TryGetProperty("token_based_billing", out var rootBilling) && rootBilling.ValueKind == JsonValueKind.True)
                return true;

            return root.TryGetProperty("quota_snapshots", out var snapshots)
                && snapshots.TryGetProperty("premium_interactions", out var premium)
                && premium.TryGetProperty("token_based_billing", out var premiumBilling)
                && premiumBilling.ValueKind == JsonValueKind.True;
        }

        private static List<NamedRateWindow> ExtractWindows(JsonElement root, DateTimeOffset? reset)
        {
            var result = new List<NamedRateWindow>();
            if (!root.TryGetProperty("quota_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var prop in snapshots.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var window = ParseQuotaWindow(prop.Value, reset);
                result.Add(new NamedRateWindow(prop.Name, Title(prop.Name), window));
            }

            return result
                .OrderBy(w => w.Id == "premium_interactions" ? 0 : w.Id == "chat" ? 1 : w.Id == "completions" ? 2 : 3)
                .ToList();
        }

        private static RateWindow ParseQuotaWindow(JsonElement snapshot, DateTimeOffset? fallbackReset)
        {
            bool unlimited = snapshot.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True;
            double? limit = TryF64(snapshot, "entitlement");
            double? remaining = TryF64(snapshot, "remaining") ?? TryF64(snapshot, "quota_remaining");

            double used;
            if (unlimited)
                used = 0;
            else if (limit is > 0 && remaining is not null)
                used = System.Math.Clamp((limit.Value - remaining.Value) / limit.Value * 100, 0, 100);
            else
            {
                double percentRemaining = TryF64(snapshot, "percent_remaining") ?? 100;
                used = 100 - percentRemaining;
            }

            DateTimeOffset? resetAt = fallbackReset;
            if (snapshot.TryGetProperty("quota_reset_at", out var resetEl) && resetEl.TryGetInt64(out long ts) && ts > 0)
                resetAt = DateTimeOffset.FromUnixTimeSeconds(ts);

            return new RateWindow(used, null, resetAt, CodexProvider.FormatResetCountdown(resetAt));
        }

        private static DateTimeOffset? ParseReset(JsonElement root)
        {
            if (root.TryGetProperty("quota_reset_date_utc", out var resetUtc)
                && resetUtc.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(resetUtc.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;

            if (root.TryGetProperty("quota_reset_date", out var resetDate)
                && resetDate.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(resetDate.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
                return parsed;

            return null;
        }

        private static double? TryF64(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) => value,
                _ => null,
            };
        }

        private static string ResolveToken()
        {
            var manual = CredentialStore.Instance.ApiKey(ProviderId.Copilot, "GITHUB_TOKEN", "GH_TOKEN");
            if (!string.IsNullOrWhiteSpace(manual))
                return manual;

            try
            {
                var psi = new ProcessStartInfo("gh", "auth token")
                {
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var process = Process.Start(psi);
                if (process == null)
                    throw new InvalidOperationException("Could not start gh.");
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    return output;
            }
            catch
            {
                // Fall through to a provider-friendly setup message.
            }

            throw new ProviderException(ProviderErrorKind.AuthRequired, "GitHub auth not found. Run `gh auth login`, or paste a GitHub token in Settings.");
        }

        private static string ResolvePlanName(JsonElement root)
        {
            if (root.TryGetProperty("access_type_sku", out var sku) && sku.ValueKind == JsonValueKind.String)
            {
                var mapped = PlanDisplayFromSku(sku.GetString()!);
                if (!string.IsNullOrEmpty(mapped))
                    return mapped;
            }

            if (root.TryGetProperty("copilot_plan", out var plan) && plan.ValueKind == JsonValueKind.String)
                return PlanDisplay(plan.GetString()!);

            return string.Empty;
        }

        private static string Title(string id) => id switch
        {
            "premium_interactions" => "Premium",
            "chat" => "Chat",
            "completions" => "Completions",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(id.Replace('_', ' ')),
        };

        private static string PlanDisplayFromSku(string sku) => sku switch
        {
            "free_educational_quota" => "Student",
            "free_limited_copilot" or "free" => "Free",
            "individual_pro" or "plus_monthly_subscriber_quota" => "Pro",
            "individual_pro_plus" => "Pro+",
            "individual_max" => "Max",
            _ => string.Empty,
        };

        private static string PlanDisplay(string raw)
            => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').Replace('-', ' '));
    }
}
