using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TaskbarQuota.Browser;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Cursor usage via cursor.com/api/usage-summary, authenticated with browser cookies
    /// (Edge/Chrome/Brave) or a manually pasted cookie header.
    /// Ported from Win-CodexBar rust/src/providers/cursor/api.rs.
    /// </summary>
    public sealed class CursorProvider : IUsageProvider
    {
        private const string BaseUrl = "https://cursor.com";
        private const string AppApiBaseUrl = "https://api2.cursor.sh";
        private const string CursorAuthClientId = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";
        private static readonly string[] CookieDomains = { "cursor.com", "cursor.sh" };
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public ProviderId Id => ProviderId.Cursor;
        public string DisplayName => "Cursor";
        public string SessionLabel => "Total usage";
        public string WeeklyLabel => "Auto + Composer Usage";
        public BillingKind Billing => BillingKind.Api;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var appAuth = LoadCursorAppAuth();
            if (appAuth?.AccessToken is { Length: > 0 })
            {
                try
                {
                    return await FetchUsageWithAppTokenAsync(appAuth, ct).ConfigureAwait(false);
                }
                catch (ProviderException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Debug($"Cursor app-token usage failed; falling back to web cookies: {ex.Message}");
                }
            }

            string cookie = ResolveCookieHeader();

            var summaryTask = GetJson($"{BaseUrl}/api/usage-summary", cookie, ct);
            var meTask = GetJson($"{BaseUrl}/api/auth/me", cookie, ct);
            JsonDocument summary;
            try { summary = await summaryTask.ConfigureAwait(false); }
            catch (ProviderException) { throw; }
            JsonDocument? me = null;
            try { me = await meTask.ConfigureAwait(false); } catch { }

            using (summary)
            using (me)
            {
                return Build(summary.RootElement, me?.RootElement);
            }
        }

        private static async Task<ProviderFetchResult> FetchUsageWithAppTokenAsync(CursorAppAuth auth, CancellationToken ct)
        {
            var token = auth.AccessToken ?? throw new ProviderException(ProviderErrorKind.AuthRequired, "Cursor app token missing");
            JsonDocument usage;
            try
            {
                usage = await PostAppJson("/aiserver.v1.DashboardService/GetCurrentPeriodUsage", token, "{}", ct).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (ex.Kind == ProviderErrorKind.AuthRequired && !string.IsNullOrWhiteSpace(auth.RefreshToken))
            {
                token = await RefreshAccessTokenAsync(auth.RefreshToken!, ct).ConfigureAwait(false);
                usage = await PostAppJson("/aiserver.v1.DashboardService/GetCurrentPeriodUsage", token, "{}", ct).ConfigureAwait(false);
            }

            using (usage)
            {
                var fetch = BuildFromAppUsage(usage.RootElement, auth);

                try
                {
                    using var profile = await GetAppJson("/auth/full_stripe_profile", token, ct).ConfigureAwait(false);
                    EnrichFromAppProfile(fetch.Usage, profile.RootElement);
                }
                catch { }

                return fetch;
            }
        }

        private string ResolveCookieHeader()
        {
            var manual = CredentialStore.Instance.ManualCookieHeader(Id);
            if (manual != null) return manual;
            foreach (var d in CookieDomains)
            {
                var header = CookieExtractor.GetCookieHeader(d);
                if (!string.IsNullOrEmpty(header)) return header!;
            }
            throw new ProviderException(ProviderErrorKind.AuthRequired,
                "No Cursor cookies found. Sign in via Edge/Chrome, or paste a cookie header in credentials.json.");
        }

        private static async Task<JsonDocument> GetJson(string url, string cookie, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Cursor cookies expired. Sign in again.");
            if (!resp.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Cursor API returned {(int)resp.StatusCode}");
            var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        }

        private static async Task<JsonDocument> GetAppJson(string path, string token, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AppApiBaseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Cursor app token expired. Sign in to Cursor again.");
            if (!resp.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Cursor app API returned {(int)resp.StatusCode}");
            var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        }

        private static async Task<JsonDocument> PostAppJson(string path, string token, string jsonBody, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, AppApiBaseUrl + path)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Cursor app token expired. Sign in to Cursor again.");
            if (!resp.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Cursor app API returned {(int)resp.StatusCode}");
            var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        }

        private static async Task<string> RefreshAccessTokenAsync(string refreshToken, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                client_id = CursorAuthClientId,
                refresh_token = refreshToken,
            });
            using var doc = await PostAppJson("/oauth/token", refreshToken, body, ct).ConfigureAwait(false);
            if (GetStr(doc.RootElement, "access_token") is { Length: > 0 } access)
                return access;
            throw new ProviderException(ProviderErrorKind.AuthRequired, "Cursor refresh token expired. Sign in to Cursor again.");
        }

        private static ProviderFetchResult BuildFromAppUsage(JsonElement root, CursorAppAuth auth)
        {
            if (!TryGetObject(root, "planUsage", out var plan))
                throw new ProviderException(ProviderErrorKind.Parse, "Cursor app usage response did not include planUsage.");

            var billingEnd = ParseCursorMillis(FindString(root, "billingCycleEnd"));
            var resetDescription = CodexProvider.FormatResetCountdown(billingEnd);
            var total = PercentFromPlan(plan, "totalPercentUsed", "totalSpend", "limit");
            var auto = GetNum(plan, "autoPercentUsed");
            var api = GetNum(plan, "apiPercentUsed");

            var usage = new UsageSnapshot(new RateWindow(total, null, billingEnd, resetDescription))
            {
                Secondary = auto is double autoPercent ? new RateWindow(NormalizePercent(autoPercent), null, billingEnd, resetDescription) : null,
                ModelSpecific = api is double apiPercent ? new RateWindow(NormalizePercent(apiPercent), null, billingEnd, resetDescription) : null,
                LoginMethod = PlanDisplay(auth.PlanType),
                Email = auth.Email,
            };

            if (TryGetObject(root, "spendLimitUsage", out var spendLimit))
                usage.Cost = AppCost(spendLimit, billingEnd);

            return new ProviderFetchResult(usage, "cursor app");
        }

        private static CostSnapshot? AppCost(JsonElement spendLimit, DateTimeOffset? billingEnd)
        {
            var used = GetNum(spendLimit, "used") ?? GetNum(spendLimit, "current") ?? GetNum(spendLimit, "spend") ?? 0;
            var limit = GetNum(spendLimit, "limit") ?? GetNum(spendLimit, "userLimit") ?? GetNum(spendLimit, "pooledLimit");
            if (used <= 0 && limit is not > 0) return null;

            var cost = new CostSnapshot(used / 100.0, "USD", "On-demand");
            if (limit is > 0) cost.WithLimit(limit.Value / 100.0);
            if (billingEnd is { } end) cost.WithResetsAt(end);
            return cost;
        }

        private static void EnrichFromAppProfile(UsageSnapshot usage, JsonElement profile)
        {
            if (FindString(profile, "membershipType", "individualMembershipType") is { Length: > 0 } plan)
                usage.LoginMethod = PlanDisplay(plan);
        }

        private static ProviderFetchResult Build(JsonElement summary, JsonElement? me)
        {
            DateTimeOffset? billingEnd = ParseDate(FindString(summary, "billingCycleEnd", "cycleEnd", "renewAt", "renewsAt"));
            var resetDescription = CodexProvider.FormatResetCountdown(billingEnd);

            double percent = 0; RateWindow? secondary = null, model = null; CostSnapshot? cost = null;

            if (TryGetPlanScope(summary, out var usageScope, out var plan))
            {
                double usedCents = GetNum(plan, "used") ?? 0;
                double limitCents = (TryGetObject(plan, "breakdown", out var bd) ? GetNum(bd, "total") : null)
                                    ?? GetNum(plan, "limit") ?? 0;
                percent = limitCents > 0 ? usedCents / limitCents * 100.0 : NormalizePercent(GetNum(plan, "totalPercentUsed") ?? 0);

                if (GetNum(plan, "autoPercentUsed") is double auto)
                    secondary = new RateWindow(NormalizePercent(auto), null, billingEnd, resetDescription);
                if (GetNum(plan, "apiPercentUsed") is double api)
                    model = new RateWindow(NormalizePercent(api), null, billingEnd, resetDescription);

                cost = OnDemandCost(usageScope, billingEnd)
                    ?? (TryGetObject(summary, "teamUsage", out var teamUsage) ? OnDemandCost(teamUsage, billingEnd) : null);
                if (cost == null)
                {
                    cost = new CostSnapshot(usedCents / 100.0, "USD", "Monthly");
                    if (limitCents > 0) cost.WithLimit(limitCents / 100.0);
                    if (billingEnd is { } be) cost.WithResetsAt(be);
                }
            }

            var primary = new RateWindow(percent, null, billingEnd, resetDescription);
            var usage = new UsageSnapshot(primary) { Secondary = secondary, ModelSpecific = model, Cost = cost };

            var planText = FindString(summary, "membershipType", "planType", "planName", "plan", "subscriptionPlan")
                ?? (me is { } meRoot ? FindString(meRoot, "membershipType", "planType", "planName", "plan", "subscriptionPlan") : null);
            usage.LoginMethod = planText is { } mt
                ? mt.ToLowerInvariant() switch
                {
                    "enterprise" => "Cursor Enterprise",
                    "pro" => "Cursor Pro",
                    "hobby" => "Cursor Hobby",
                    "team" => "Cursor Team",
                    _ => "Cursor " + char.ToUpperInvariant(mt[0]) + mt[1..],
                }
                : null;

            if (me is { } m && m.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String)
                usage.Email = em.GetString();

            return new ProviderFetchResult(usage, "web");
        }

        private static CursorAppAuth? LoadCursorAppAuth()
        {
            var db = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(db)) return null;

            try
            {
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Private");
                conn.Open();
                return new CursorAppAuth(
                    GetItem(conn, "cursorAuth/accessToken"),
                    GetItem(conn, "cursorAuth/refreshToken"),
                    GetItem(conn, "cursorAuth/cachedEmail"),
                    GetItem(conn, "cursorAuth/stripeMembershipType"));
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"Cursor app auth read failed: {ex.Message}");
                return null;
            }
        }

        private static string? GetItem(SqliteConnection conn, string key)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }

        private static CostSnapshot? OnDemandCost(JsonElement iu, DateTimeOffset? billingEnd)
        {
            if (!iu.TryGetProperty("onDemand", out var od) || od.ValueKind != JsonValueKind.Object) return null;
            if (od.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return null;
            double used = GetNum(od, "used") ?? 0;
            double limit = GetNum(od, "limit") ?? ((GetNum(od, "remaining") ?? 0) + used);
            if (used <= 0 && limit <= 0) return null;
            var c = new CostSnapshot(used / 100.0, "USD", "Monthly");
            if (limit > 0) c.WithLimit(limit / 100.0);
            if (billingEnd is { } be) c.WithResetsAt(be);
            return c;
        }

        private static double? GetNum(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            };
        }

        private static string? GetStr(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static DateTimeOffset? ParseDate(string? s)
            => s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

        private static double NormalizePercent(double value) => value is > 0 and <= 1 ? value * 100.0 : value;

        private static double PercentFromPlan(JsonElement plan, string percentName, string usedName, string limitName)
        {
            if (GetNum(plan, percentName) is double percent)
                return NormalizePercent(percent);
            var used = GetNum(plan, usedName) ?? 0;
            var limit = GetNum(plan, limitName) ?? 0;
            return limit > 0 ? used / limit * 100.0 : 0;
        }

        private static DateTimeOffset? ParseCursorMillis(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return ParseDate(s);
        }

        private static string? PlanDisplay(string? plan)
        {
            if (string.IsNullOrWhiteSpace(plan)) return null;
            return plan.ToLowerInvariant() switch
            {
                "enterprise" => "Enterprise",
                "pro" => "Pro",
                "hobby" => "Hobby",
                "team" => "Team",
                "ultra" => "Ultra",
                "free" => "Free",
                _ => char.ToUpperInvariant(plan[0]) + plan[1..],
            };
        }

        private static bool TryGetObject(JsonElement element, string name, out JsonElement obj)
        {
            if (element.TryGetProperty(name, out obj) && obj.ValueKind == JsonValueKind.Object)
                return true;
            obj = default;
            return false;
        }

        private static bool TryGetUsageScope(JsonElement summary, out JsonElement scope)
        {
            if (TryGetObject(summary, "individualUsage", out scope))
                return true;
            if (TryGetObject(summary, "teamUsage", out scope))
                return true;
            scope = default;
            return false;
        }

        private static bool TryGetPlanScope(JsonElement summary, out JsonElement scope, out JsonElement plan)
        {
            if (TryGetUsageScope(summary, out scope) && TryGetObject(scope, "plan", out plan))
                return true;
            if (TryGetObject(summary, "teamUsage", out scope) && TryGetObject(scope, "plan", out plan))
                return true;
            scope = default;
            plan = default;
            return false;
        }

        private static string? FindString(JsonElement element, params string[] names)
        {
            foreach (var name in names)
                if (GetStr(element, name) is { Length: > 0 } direct)
                    return direct;

            foreach (var child in EnumerateChildren(element))
                if (FindString(child, names) is { Length: > 0 } nested)
                    return nested;

            return null;
        }

        private static System.Collections.Generic.IEnumerable<JsonElement> EnumerateChildren(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                    yield return property.Value;
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    yield return item;
            }
        }

        private sealed record CursorAppAuth(string? AccessToken, string? RefreshToken, string? Email, string? PlanType);
    }
}
