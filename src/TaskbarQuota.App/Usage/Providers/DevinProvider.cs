using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TaskbarQuota;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Devin (Cognition) usage. Cognition's Devin runs on Windsurf/Codeium infrastructure, so quota
    /// comes from the Codeium SeatManagementService GetUserStatus endpoint. Auth is read from two
    /// places, mirroring the openusage devin plugin (plugins/devin/plugin.js):
    ///  - the Devin CLI (`devin auth login`), which stores `windsurf_api_key` in credentials.toml; and
    ///  - the Devin desktop app (a VS Code fork), which stores `windsurfAuthStatus` in its state.vscdb.
    /// The weekly quota is the headline metric, the daily quota is secondary, and any prepaid overage
    /// balance is surfaced as an "Extra Usage" cost line.
    /// </summary>
    public sealed class DevinProvider : IUsageProvider
    {
        private const string CloudService = "exa.seat_management_pb.SeatManagementService";
        private const string DefaultApiServerUrl = "https://server.codeium.com";
        private const string CloudCompatVersion = "1.108.2";
        private const string UsageDashboardUrl = "https://app.devin.ai";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public ProviderId Id => ProviderId.Devin;
        public string DisplayName => "Devin";
        public string SessionLabel => "Weekly";
        public string WeeklyLabel => "Daily";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var candidates = LoadAuthCandidates();
            if (candidates.Count == 0)
            {
                if (!ProviderInstallDetector.IsInstalled(ProviderId.Devin))
                {
                    throw new ProviderException(ProviderErrorKind.NotInstalled,
                        ProviderInstallDetector.NotInstalledMessage(ProviderId.Devin));
                }

                throw new ProviderException(ProviderErrorKind.NotRunning,
                    ProviderInstallDetector.WaitingMessage(ProviderId.Devin));
            }

            bool sawAuthFailure = false;
            var tried = new HashSet<string>();
            foreach (var auth in candidates)
            {
                if (!tried.Add(auth.ApiKey + "\n" + auth.ApiServerUrl))
                    continue;

                var (userStatus, authFailed) = await CallCloudAsync(auth, ct).ConfigureAwait(false);
                if (authFailed) { sawAuthFailure = true; continue; }
                if (userStatus is not { } status) continue;

                if (TryBuildUsage(status, out var usage))
                {
                    usage.UsageDashboardUrl = UsageDashboardUrl;
                    return new ProviderFetchResult(usage, auth.Source);
                }
            }

            if (sawAuthFailure)
                throw new ProviderException(ProviderErrorKind.AuthRequired,
                    "Devin sign-in expired. Run `devin auth login` or sign in to the Devin app.");

            throw new ProviderException(ProviderErrorKind.Other, "Devin quota data unavailable. Try again later.");
        }

        // --- Cloud (GetUserStatus) ----------------------------------------------------------------

        private static async Task<(JsonElement? UserStatus, bool AuthFailed)> CallCloudAsync(AuthSource auth, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                metadata = new
                {
                    apiKey = auth.ApiKey,
                    ideName = "devin",
                    ideVersion = CloudCompatVersion,
                    extensionName = "devin",
                    extensionVersion = CloudCompatVersion,
                    locale = "en",
                },
            });

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    auth.ApiServerUrl + "/" + CloudService + "/GetUserStatus")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");

                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return (null, true);
                if (!response.IsSuccessStatusCode)
                {
                    Diagnostics.Log.Warning($"Devin GetUserStatus returned {(int)response.StatusCode} for {auth.Source}");
                    return (null, false);
                }

                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("userStatus", out var userStatus)
                    && userStatus.ValueKind == JsonValueKind.Object)
                {
                    return (userStatus.Clone(), false);
                }
                return (null, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log.Warning(ex, $"Devin cloud request failed for {auth.Source}");
                return (null, false);
            }
        }

        // --- Quota parsing ------------------------------------------------------------------------

        internal static bool TryBuildUsage(JsonElement userStatus, out UsageSnapshot usage)
        {
            usage = null!;
            if (!userStatus.TryGetProperty("planStatus", out var planStatus) || planStatus.ValueKind != JsonValueKind.Object)
                return false;

            var planInfo = planStatus.TryGetProperty("planInfo", out var pi) && pi.ValueKind == JsonValueKind.Object
                ? pi
                : default;
            bool hideDaily = planInfo.ValueKind == JsonValueKind.Object
                && planInfo.TryGetProperty("hideDailyQuota", out var hd) && hd.ValueKind == JsonValueKind.True;

            double? dailyRemaining = Number(planStatus, "dailyQuotaRemainingPercent");
            double? weeklyRemaining = Number(planStatus, "weeklyQuotaRemainingPercent");
            var dailyReset = UnixToOffset(Number(planStatus, "dailyQuotaResetAtUnix"));
            var weeklyReset = UnixToOffset(Number(planStatus, "weeklyQuotaResetAtUnix"));

            RateWindow? dailyWindow = !hideDaily && dailyRemaining is { } dr ? MakeWindow(dr, dailyReset) : null;

            RateWindow? weeklyWindow;
            if (weeklyRemaining is { } wr)
                weeklyWindow = MakeWindow(wr, weeklyReset);
            else if (hideDaily && dailyRemaining is { } hiddenDaily)
                // Devin reuses the hidden daily field as the weekly figure when no weekly percent is sent.
                weeklyWindow = MakeWindow(hiddenDaily, weeklyReset);
            else
                weeklyWindow = null;

            // Weekly is the headline window (matches the openusage primaryOrder); daily is secondary.
            var primary = weeklyWindow ?? dailyWindow;
            if (primary is null)
                return false;

            usage = new UsageSnapshot(primary);
            if (weeklyWindow is not null && dailyWindow is not null)
                usage.Secondary = dailyWindow;

            if (planInfo.ValueKind == JsonValueKind.Object
                && planInfo.TryGetProperty("planName", out var pn) && pn.ValueKind == JsonValueKind.String
                && pn.GetString()?.Trim() is { Length: > 0 } plan)
            {
                usage.LoginMethod = plan;
            }

            if (DollarsFromMicros(Number(planStatus, "overageBalanceMicros")) is { } balance && balance > 0)
                usage.Cost = new CostSnapshot(balance, "USD", "Extra Usage");

            return true;
        }

        private static RateWindow MakeWindow(double remainingPercent, DateTimeOffset? resetAt)
        {
            double used = Math.Clamp(100 - remainingPercent, 0, 100);
            return new RateWindow(used, windowMinutes: null, resetAt: resetAt,
                resetDescription: CodexProvider.FormatResetCountdown(resetAt));
        }

        private static double? Number(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            };
        }

        private static DateTimeOffset? UnixToOffset(double? seconds)
        {
            if (seconds is not { } s || !double.IsFinite(s) || s <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000)); }
            catch { return null; }
        }

        private static double? DollarsFromMicros(double? micros)
        {
            if (micros is not { } m || !double.IsFinite(m)) return null;
            return Math.Max(0, m) / 1_000_000.0;
        }

        // --- Auth sources -------------------------------------------------------------------------

        internal sealed record AuthSource(string ApiKey, string ApiServerUrl, string Source);

        private static List<AuthSource> LoadAuthCandidates()
        {
            var list = new List<AuthSource>();

            foreach (var cliPath in GetCredentialsPaths())
            {
                if (!File.Exists(cliPath)) continue;
                try
                {
                    var text = File.ReadAllText(cliPath);
                    if (ReadTomlString(text, "windsurf_api_key") is { Length: > 0 } apiKey)
                    {
                        var server = CleanApiServerUrl(ReadTomlString(text, "api_server_url")) ?? DefaultApiServerUrl;
                        list.Add(new AuthSource(apiKey, server, "devin-cli"));
                    }
                    else
                    {
                        Diagnostics.Log.Warning("Devin credentials.toml missing windsurf_api_key");
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Warning(ex, "Failed to read Devin credentials.toml");
                }
            }

            foreach (var (appName, label) in AppStateDbs())
            {
                if (ReadAppApiKey(appName) is { Length: > 0 } appKey)
                    list.Add(new AuthSource(appKey, DefaultApiServerUrl, label));
            }

            return list;
        }

        private static IEnumerable<(string AppName, string Label)> AppStateDbs()
        {
            yield return ("Devin", "Devin app");
            yield return ("Devin - Next", "Devin - Next app");
        }

        private static string? ReadAppApiKey(string appName)
        {
            var db = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                appName, "User", "globalStorage", "state.vscdb");
            if (!File.Exists(db)) return null;

            try
            {
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Private");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'windsurfAuthStatus' LIMIT 1";
                if (cmd.ExecuteScalar() is not string json || string.IsNullOrWhiteSpace(json))
                    return null;
                return ReadAppApiKeyFromJson(json);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"Devin {appName} auth read failed: {ex.Message}");
                return null;
            }
        }

        internal static string? ReadAppApiKeyFromJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("apiKey", out var key)
                    && key.ValueKind == JsonValueKind.String
                    && key.GetString()?.Trim() is { Length: > 0 } apiKey)
                {
                    return apiKey;
                }
            }
            catch { }
            return null;
        }

        internal static string? ReadTomlString(string text, string key)
        {
            foreach (var rawLine in (text ?? "").Split('\n'))
            {
                var line = rawLine.Trim();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(line[..eq].Trim(), key, StringComparison.Ordinal)) continue;

                var value = line[(eq + 1)..].Trim();
                if (value.Length == 0) return null;
                if (value[0] is '"' or '\'')
                {
                    var quote = value[0];
                    var end = value.IndexOf(quote, 1);
                    return end > 0 ? value[1..end] : null;
                }
                var comment = value.IndexOf('#');
                if (comment >= 0) value = value[..comment].Trim();
                return value.Length > 0 ? value : null;
            }
            return null;
        }

        private static string? CleanApiServerUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim().TrimEnd('/');
            return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
        }

        /// <summary>
        /// Ordered, de-duplicated credentials.toml locations. The Devin CLI (`devin auth login`) and the
        /// desktop app share this file, but write it to different roots per OS: %APPDATA%\Devin on Windows,
        /// and the XDG/`~/.local/share/devin` layout used by the openusage reference plugin elsewhere.
        /// </summary>
        private static IEnumerable<string> GetCredentialsPaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? Add(string? path)
                => !string.IsNullOrEmpty(path) && seen.Add(path) ? path : null;

            // Windows: the CLI and the app (a VS Code fork) both keep credentials.toml under %APPDATA%\Devin.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                if (Add(Path.Combine(appData, "Devin", "credentials.toml")) is { } a) yield return a;
                if (Add(Path.Combine(appData, "Devin - Next", "credentials.toml")) is { } b) yield return b;
            }

            // XDG_DATA_HOME / `~/.local/share/devin` (Linux, macOS) — matches the openusage plugin.
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")?.Trim();
            var root = !string.IsNullOrEmpty(dataHome)
                ? dataHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            if (Add(Path.Combine(root, "devin", "credentials.toml")) is { } c) yield return c;
        }
    }
}
