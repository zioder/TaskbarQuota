using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TaskbarQuota;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Antigravity quota via Google OAuth from Windows keyring, with a local language-server fallback.
    /// The local server uses Connect/gRPC over self-signed TLS on 127.0.0.1.
    /// Finds the running language_server_windows process, extracts the CSRF token + extension port from
    /// its command line, probes for the real API port, then calls GetUserStatus.
    /// Ported from Win-CodexBar rust/src/providers/antigravity/mod.rs.
    /// </summary>
    public sealed class AntigravityProvider : IUsageProvider
    {
        private static readonly Regex CsrfRe = new(@"--csrf_token\s+([a-f0-9-]+)", RegexOptions.Compiled);
        private static readonly Regex ExtCsrfRe = new(@"--extension_server_csrf_token\s+([a-f0-9-]+)", RegexOptions.Compiled);
        private static readonly Regex PortRe = new(@"--extension_server_port\s+(\d+)", RegexOptions.Compiled);

        // Local language server uses a self-signed cert scoped to 127.0.0.1.
        private static readonly HttpClient Local = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = false,
        })
        { Timeout = TimeSpan.FromSeconds(8) };

        private const string GoogleQuotaUrl = "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
        private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
        private const string AntigravityHubUserAgent = "antigravity/hub/2.24.0";
        private const string KeyringTargetName = "gemini:antigravity";
        private const string GoKeyringBase64Prefix = "go-keyring-base64:";

        private static readonly HttpClient Cloud = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static string? _cachedAccessToken;
        private static DateTimeOffset _cachedAccessTokenExpiry = DateTimeOffset.MinValue;

        public ProviderId Id => ProviderId.Antigravity;
        public string DisplayName => "Antigravity";
        public string SessionLabel => "Gemini";
        public string WeeklyLabel => "Non-Gemini";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var oauthResult = await TryFetchViaGoogleOAuthAsync(ct).ConfigureAwait(false);
            if (oauthResult != null)
                return oauthResult;

            var info = DetectProcessInfo();
            var endpoint = await FindApiEndpoint(info, ct).ConfigureAwait(false);

            var baseUrl = $"{endpoint.Scheme}://127.0.0.1:{endpoint.Port}/exa.language_server_pb.LanguageServerService";
            var meta = JsonSerializer.Serialize(new
            {
                metadata = new { ideName = "antigravity", extensionName = "antigravity", ideVersion = "unknown", locale = "en" }
            });

            // GetUserStatus carries the plan name + email; RetrieveUserQuotaSummary carries the
            // plan-aware quota groups (weekly, plus a 5-hour limit on Plus/Pro/Ultra tiers).
            var statusJson = await CallWithCsrf($"{baseUrl}/GetUserStatus", meta, info, ct).ConfigureAwait(false);
            if (statusJson == null)
                throw new ProviderException(ProviderErrorKind.Other, "Antigravity API request failed.");

            var summaryJson = await CallWithCsrf($"{baseUrl}/RetrieveUserQuotaSummary", "{}", info, ct).ConfigureAwait(false);

            using var statusDoc = JsonDocument.Parse(statusJson);
            var snapshot = (summaryJson != null && TryParseQuotaSummary(summaryJson, out var fromSummary))
                ? fromSummary
                : ParseUserStatus(statusDoc.RootElement); // legacy per-model fallback
            ApplyPlanInfo(statusDoc.RootElement, snapshot);
            return new ProviderFetchResult(snapshot, info.IsCli ? "local-cli" : "local");
        }

        // Prefer the extension-server token, falling back to the language-server token on rejection.
        private static async Task<string?> CallWithCsrf(string url, string body, ProcessInfo info, CancellationToken ct)
        {
            var json = await PostStatus(url, body, info.ExtensionServerCsrfToken ?? info.CsrfToken, ct).ConfigureAwait(false);
            if (json == null && info.ExtensionServerCsrfToken != null)
                json = await PostStatus(url, body, info.CsrfToken, ct).ConfigureAwait(false);
            return json;
        }

        private static async Task<string?> PostStatus(string url, string body, string csrf, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            req.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrf);
            try
            {
                using var resp = await Local.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch { return null; }
        }

        private static UsageSnapshot ParseUserStatus(JsonElement root)
        {
            if (!root.TryGetProperty("userStatus", out var us) || us.ValueKind != JsonValueKind.Object)
                throw new ProviderException(ProviderErrorKind.Parse, "Antigravity: missing userStatus.");

            var configs = new List<(string label, RateWindow window)>();
            if (us.TryGetProperty("cascadeModelConfigData", out var d) &&
                d.TryGetProperty("clientModelConfigs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    string label = c.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    if (!c.TryGetProperty("quotaInfo", out var q) || q.ValueKind != JsonValueKind.Object) continue;
                    configs.Add((label, RateWindowFromQuota(q)));
                }
            }

            // Aggregate into Gemini and Non-Gemini groups (max used-percent in each).
            RateWindow? gemini = null, nonGemini = null;
            DateTimeOffset? geminiReset = null, nonGeminiReset = null;
            foreach (var (label, window) in configs)
            {
                if (IsGemini(label))
                {
                    if (gemini == null || window.UsedPercent > gemini.UsedPercent)
                    {
                        gemini = window;
                        geminiReset = window.ResetAt;
                    }
                }
                else
                {
                    if (nonGemini == null || window.UsedPercent > nonGemini.UsedPercent)
                    {
                        nonGemini = window;
                        nonGeminiReset = window.ResetAt;
                    }
                }
            }

            gemini ??= configs.Count > 0 ? configs[0].window : new RateWindow(0);
            nonGemini ??= new RateWindow(0);

            var snapshot = new UsageSnapshot(gemini) { Secondary = nonGemini };

            // Keep individual models as extra rate windows for the flyout.
            int idx = 0;
            foreach (var (label, window) in configs)
            {
                var title = Regex.Replace(label.Trim().Replace('_', ' '), "  +", " ");
                if (title.Length > 0) snapshot.ExtraRateWindows.Add(new NamedRateWindow($"model-{idx}", title, window));
                idx++;
            }

            return snapshot;
        }

        /// <summary>
        /// Parse RetrieveUserQuotaSummary into Gemini / non-Gemini weekly (+ 5-hour) windows.
        /// Free / starter tiers expose only a weekly bucket per group; Plus/Pro/Ultra add a 5-hour bucket.
        /// </summary>
        internal static bool TryParseQuotaSummary(string json, out UsageSnapshot snapshot)
        {
            snapshot = null!;
            using var doc = JsonDocument.Parse(json);
            JsonElement groups;
            if (doc.RootElement.TryGetProperty("response", out var resp) &&
                resp.ValueKind == JsonValueKind.Object &&
                resp.TryGetProperty("groups", out groups) && groups.ValueKind == JsonValueKind.Array)
            {
                // Wrapped Connect/gRPC response format
            }
            else if (doc.RootElement.TryGetProperty("groups", out groups) && groups.ValueKind == JsonValueKind.Array)
            {
                // Direct Cloud Code REST response format
            }
            else
            {
                return false;
            }

            RateWindow? geminiWeekly = null, geminiFiveHour = null, otherWeekly = null, otherFiveHour = null;
            bool any = false;
            foreach (var g in groups.EnumerateArray())
            {
                string name = g.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                bool isGemini = name.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
                if (!g.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in buckets.EnumerateArray())
                {
                    if (b.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
                        continue;
                    // Bucket IDs are stable even when display names are localized.
                    string id = b.TryGetProperty("bucketId", out var bucketId) ? bucketId.GetString() ?? "" : "";
                    bool bucketIsGemini = id.StartsWith("gemini-", StringComparison.Ordinal) ||
                        (!id.StartsWith("3p-", StringComparison.Ordinal) && isGemini);
                    bool fiveHour = IsFiveHourBucket(b);
                    var window = RateWindowFromQuota(b, fiveHour ? QuotaWindowLength.FiveHours : QuotaWindowLength.Weekly); // buckets carry remainingFraction + resetTime like quotaInfo
                    any = true;
                    if (fiveHour)
                    {
                        if (bucketIsGemini) geminiFiveHour = window; else otherFiveHour = window;
                    }
                    else
                    {
                        if (bucketIsGemini) geminiWeekly = window; else otherWeekly = window;
                    }
                }
            }

            if (!any) return false;

            snapshot = new UsageSnapshot(geminiWeekly ?? new RateWindow(0))
            {
                ModelSpecific = geminiFiveHour,
                Secondary = otherWeekly,
                Monthly = otherFiveHour,
            };
            return true;
        }

        private static bool IsFiveHourBucket(JsonElement bucket)
        {
            string window = bucket.TryGetProperty("window", out var w) ? w.GetString() ?? "" : "";
            string id = bucket.TryGetProperty("bucketId", out var i) ? i.GetString() ?? "" : "";
            string display = bucket.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "";
            var s = $"{window} {id} {display}".ToLowerInvariant();
            return s.Contains("hour") || s.Contains("5h") || s.Contains("five");
        }

        private static void ApplyPlanInfo(JsonElement root, UsageSnapshot snapshot)
        {
            if (!root.TryGetProperty("userStatus", out var us) || us.ValueKind != JsonValueKind.Object)
                return;

            if (us.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String)
                snapshot.Email = em.GetString();

            // userTier.name is the real account plan ("Antigravity Starter Quota", "Google AI Pro/Plus/Ultra").
            // planInfo only carries the underlying Codeium teams tier ("Pro") and is a last-resort fallback.
            string? plan = us.TryGetProperty("userTier", out var ut) && ut.ValueKind == JsonValueKind.Object
                && ut.TryGetProperty("name", out var tn) ? tn.GetString() : null;

            if (string.IsNullOrEmpty(plan)
                && us.TryGetProperty("planStatus", out var ps) && ps.TryGetProperty("planInfo", out var pi))
            {
                plan = (pi.TryGetProperty("planDisplayName", out var pdn) ? pdn.GetString() : null)
                    ?? (pi.TryGetProperty("planName", out var pn) ? pn.GetString() : null);
            }

            if (!string.IsNullOrEmpty(plan)) snapshot.LoginMethod = plan;
        }

        private static bool IsGemini(string label)
        {
            var s = label.ToLowerInvariant();
            return (s.Contains("gemini") || s.Contains("flash")) && !s.Contains("claude") && !s.Contains("gpt");
        }

        private static RateWindow RateWindowFromQuota(JsonElement quota, int? windowMinutes = null)
        {
            double remaining = quota.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number ? rf.GetDouble() : 1.0;
            string? reset = quota.TryGetProperty("resetTime", out var rt) ? rt.GetString() : null;
            DateTimeOffset? resetAt = null;
            if (reset != null && DateTimeOffset.TryParse(reset, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                resetAt = dt;
            return new RateWindow((1.0 - remaining) * 100.0, windowMinutes, resetAt, CodexProvider.FormatResetCountdown(resetAt));
        }

        internal sealed record ProcessInfo(
            string CsrfToken,
            string? ExtensionServerCsrfToken,
            int ExtensionPort,
            int? Pid,
            bool IsCli);

        private readonly record struct ApiEndpoint(string Scheme, int Port);

        internal static ProcessInfo DetectProcessInfo()
        {
            try
            {
                ProcessInfo? tokenlessIde = null;
                // Match the IDE language server and the terminal-only agy CLI server.
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server%' OR Name = 'agy.exe' OR Name = 'antigravity-cli.exe' OR Name = 'antigravity_cli.exe'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var name = mo["Name"] as string ?? "";
                    var cmd = mo["CommandLine"] as string;
                    if (string.IsNullOrWhiteSpace(cmd)) continue;

                    var kind = ClassifyProcess(name, cmd);
                    if (kind == ProcessKind.None) continue;

                    var csrf = CsrfRe.Match(cmd);
                    if (!csrf.Success && kind == ProcessKind.Ide)
                    {
                        tokenlessIde ??= new ProcessInfo("", null, 0, TryReadPid(mo), false);
                        continue;
                    }

                    var port = PortRe.Match(cmd);
                    int extPort = port.Success ? int.Parse(port.Groups[1].Value) : 0;

                    var extCsrf = ExtCsrfRe.Match(cmd);
                    return new ProcessInfo(
                        csrf.Success ? csrf.Groups[1].Value : "",
                        extCsrf.Success ? extCsrf.Groups[1].Value : null,
                        extPort,
                        TryReadPid(mo),
                        kind == ProcessKind.Cli);
                }

                if (tokenlessIde != null)
                    throw new ProviderException(ProviderErrorKind.NotRunning, "Antigravity language server is missing its CSRF token. Restart Antigravity and retry.");
            }
            catch (ProviderException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw InstallStateException($"Antigravity detection failed: {ex.Message}");
            }

            throw InstallStateException("Antigravity language server not running.");
        }

        private enum ProcessKind { None, Ide, Cli }

        internal static bool IsAntigravityCliProcess(string name, string commandLine)
            => ClassifyProcess(name, commandLine) == ProcessKind.Cli;

        private static ProcessKind ClassifyProcess(string name, string commandLine)
        {
            var lowerName = name.ToLowerInvariant();
            var lower = commandLine.ToLowerInvariant();
            if (lowerName.Contains("language_server") &&
                ((lower.Contains("--app_data_dir") && lower.Contains("antigravity")) ||
                 lower.Contains("\\antigravity\\") ||
                 lower.Contains("/antigravity/")))
            {
                return ProcessKind.Ide;
            }

            if (lowerName is "agy.exe" or "antigravity-cli.exe" or "antigravity_cli.exe")
                return ProcessKind.Cli;

            if (Regex.IsMatch(lower, @"(^|[\\/])(?:agy|antigravity-cli|antigravity_cli)(?:\.exe)?(\s|$)", RegexOptions.IgnoreCase))
                return ProcessKind.Cli;

            return ProcessKind.None;
        }

        private static int? TryReadPid(ManagementObject mo)
            => int.TryParse(mo["ProcessId"]?.ToString(), out var p) ? p : null;

        private static ProviderException InstallStateException(string runtimeDetail)
        {
            if (ProviderInstallDetector.IsInstalled(ProviderId.Antigravity))
                return new ProviderException(ProviderErrorKind.NotRunning, ProviderInstallDetector.WaitingMessage(ProviderId.Antigravity));

            return new ProviderException(ProviderErrorKind.NotInstalled, ProviderInstallDetector.NotInstalledMessage(ProviderId.Antigravity));
        }

        private static async Task<ApiEndpoint> FindApiEndpoint(ProcessInfo info, CancellationToken ct)
        {
            var candidates = new List<int>();
            if (info.Pid is int pid) candidates.AddRange(ListeningPortsForPid(pid));
            if (!info.IsCli && info.ExtensionPort > 0)
                for (int off = 0; off < 20; off++) candidates.Add(info.ExtensionPort + off);
            if (!info.IsCli)
                candidates.AddRange(new[] { 53835, 53836, 53837, 53838, 53845, 53849 });

            var seen = new HashSet<int>();
            var ports = candidates.Where(port => seen.Add(port)).ToArray();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var probes = ports
                .Select(port => ProbeEndpointResult(port, info.CsrfToken, linkedCts.Token))
                .ToList();

            while (probes.Count > 0)
            {
                var completed = await Task.WhenAny(probes).ConfigureAwait(false);
                probes.Remove(completed);

                var result = await completed.ConfigureAwait(false);
                if (result is ApiEndpoint found)
                {
                    linkedCts.Cancel();
                    _ = Task.WhenAll(probes).ContinueWith(_ => linkedCts.Dispose(), TaskScheduler.Default);
                    return found;
                }
            }

            linkedCts.Dispose();
            throw new ProviderException(ProviderErrorKind.Other, "Could not find Antigravity API port.");
        }

        private static async Task<ApiEndpoint?> ProbeEndpointResult(int port, string csrf, CancellationToken ct)
        {
            if (await ProbePort("https", port, csrf, ct).ConfigureAwait(false))
                return new ApiEndpoint("https", port);
            if (await ProbePort("http", port, csrf, ct).ConfigureAwait(false))
                return new ApiEndpoint("http", port);
            return null;
        }

        private static async Task<bool> ProbePort(string scheme, int port, string csrf, CancellationToken ct)
        {
            var url = $"{scheme}://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUnleashData";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            req.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrf);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var resp = await Local.SendAsync(req, cts.Token).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                return code == 200 || code == 400 || code == 401 || code == 403;
            }
            catch { return false; }
        }

        private static List<int> ListeningPortsForPid(int pid)
        {
            // IP Helper would be cleaner, but matching the original's Get-NetTCPConnection keeps parity.
            var ports = new List<int>();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    $"-ExecutionPolicy Bypass -Command \"Get-NetTCPConnection -OwningProcess {pid} -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return ports;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                foreach (var line in output.Split('\n'))
                    if (int.TryParse(line.Trim(), out var port)) ports.Add(port);
            }
            catch { }
            return ports;
        }

        internal sealed record KeyringOAuthCreds(
            string? AccessToken,
            string? RefreshToken,
            string? ClientId,
            string? ClientSecret,
            DateTimeOffset? Expiry);

        private static async Task<ProviderFetchResult?> TryFetchViaGoogleOAuthAsync(CancellationToken ct)
        {
            var creds = ReadKeyringOAuthCreds();
            if (creds == null) return null;

            string? token = (!string.IsNullOrWhiteSpace(_cachedAccessToken) && _cachedAccessTokenExpiry > DateTimeOffset.UtcNow.AddMinutes(1))
                ? _cachedAccessToken
                : creds.AccessToken;

            if (string.IsNullOrWhiteSpace(token) || (creds.Expiry.HasValue && creds.Expiry.Value <= DateTimeOffset.UtcNow.AddMinutes(1) && token == creds.AccessToken))
            {
                token = await RefreshGoogleTokenAsync(creds, ct).ConfigureAwait(false) ?? token;
            }

            if (string.IsNullOrWhiteSpace(token)) return null;

            var (status, json) = await PostGoogleQuotaSummaryAsync(token, ct).ConfigureAwait(false);
            if (status == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(creds.RefreshToken))
            {
                var refreshed = await RefreshGoogleTokenAsync(creds, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(refreshed))
                    (status, json) = await PostGoogleQuotaSummaryAsync(refreshed, ct).ConfigureAwait(false);
            }

            if (status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(json))
                return null;

            if (!TryParseQuotaSummary(json, out var snapshot))
                return null;

            return new ProviderFetchResult(snapshot, "oauth");
        }

        private static async Task<(HttpStatusCode Status, string? Body)> PostGoogleQuotaSummaryAsync(string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, GoogleQuotaUrl)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.TryAddWithoutValidation("User-Agent", AntigravityHubUserAgent);
            try
            {
                using var resp = await Cloud.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (resp.StatusCode, body);
            }
            catch
            {
                return (0, null);
            }
        }

        // The Go token record omits its native OAuth client registration. Resolve the
        // verified registration from the installed app, keeping its secret out of source.
        // Fingerprints identify the registration, not a version-specific executable.
        private static readonly Lazy<(string Id, string Secret)?> InstalledOAuthClient = new(ReadInstalledOAuthClient);

        private static (string Id, string Secret)? ReadInstalledOAuthClient()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity"),
            };
            foreach (var root in roots)
            {
                var path = Path.Combine(root, "resources", "bin", "language_server.exe");
                try
                {
                    if (!File.Exists(path)) continue;
                    using var stream = File.OpenRead(path);
                    var buffer = new byte[64 * 1024];
                    string tail = "";
                    string? id = null, secret = null;
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var content = tail + Encoding.ASCII.GetString(buffer, 0, count);
                        string? MatchFingerprint(string pattern, string fingerprint)
                        {
                            foreach (Match match in Regex.Matches(content, pattern, RegexOptions.CultureInvariant))
                                if (Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(match.Value))) == fingerprint)
                                    return match.Value;
                            return null;
                        }
                        id ??= MatchFingerprint(@"\d{10,13}-[a-z0-9]{32}\.apps\.googleusercontent\.com", "BF00C418024BA6BF606CCDC37120976E41BC429DD1D46ECF16A729AA532626EA");
                        secret ??= MatchFingerprint(@"GOCSPX-[A-Za-z0-9_-]{28}", "1D2F041093FD95AA8995A038C711D50A7960DA09A505381C09A745D6AD0ECC60");
                        if (id != null && secret != null) return (id, secret);
                        // Keep matches spanning chunk boundaries without retaining the binary.
                        tail = content.Length > 128 ? content[^128..] : content;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        internal static KeyringOAuthCreds WithInstalledOAuthClient(
            KeyringOAuthCreds creds, (string Id, string Secret)? client)
        {
            if (client == null) return creds;
            if (!string.IsNullOrWhiteSpace(creds.ClientId) && creds.ClientId != client.Value.Id)
                return creds;
            return creds with
            {
                ClientId = string.IsNullOrWhiteSpace(creds.ClientId) ? client.Value.Id : creds.ClientId,
                ClientSecret = string.IsNullOrWhiteSpace(creds.ClientSecret) ? client.Value.Secret : creds.ClientSecret,
            };
        }

        private static async Task<string?> RefreshGoogleTokenAsync(KeyringOAuthCreds creds, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(creds.RefreshToken)) return null;
            if (string.IsNullOrWhiteSpace(creds.ClientId) || string.IsNullOrWhiteSpace(creds.ClientSecret))
                creds = WithInstalledOAuthClient(creds, InstalledOAuthClient.Value);
            if (string.IsNullOrWhiteSpace(creds.ClientId)) return null;

            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = creds.RefreshToken!,
                ["client_id"] = creds.ClientId!,
            };
            if (!string.IsNullOrWhiteSpace(creds.ClientSecret))
                fields["client_secret"] = creds.ClientSecret;

            using var req = new HttpRequestMessage(HttpMethod.Post, GoogleTokenUrl)
            {
                Content = new FormUrlEncodedContent(fields),
            };
            try
            {
                using var resp = await Cloud.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
                    return null;
                var newToken = at.GetString();
                if (string.IsNullOrWhiteSpace(newToken)) return null;
                int expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetInt32() : 3600;
                _cachedAccessToken = newToken;
                _cachedAccessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
                return newToken;
            }
            catch
            {
                return null;
            }
        }

        private static KeyringOAuthCreds? ReadKeyringOAuthCreds()
        {
            var bytes = ReadGenericCredential(KeyringTargetName);
            return bytes == null ? null : ParseKeyringOAuthCreds(bytes);
        }

        internal static KeyringOAuthCreds? ParseKeyringOAuthCreds(byte[] bytes)
        {
            // Go keyring writes UTF-8; other Windows keyring writers use UTF-16LE.
            string raw = bytes.Length >= 2 && (bytes[1] == 0 || (bytes[0] == 0xff && bytes[1] == 0xfe))
                ? Encoding.Unicode.GetString(bytes)
                : Encoding.UTF8.GetString(bytes);
            string payload = raw.TrimEnd('\0').TrimStart('\uFEFF').Trim();
            if (payload.StartsWith(GoKeyringBase64Prefix, StringComparison.Ordinal))
            {
                try
                {
                    var decoded = Convert.FromBase64String(payload[GoKeyringBase64Prefix.Length..]);
                    payload = Encoding.UTF8.GetString(decoded);
                }
                catch
                {
                    return null;
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                var token = root.TryGetProperty("token", out var nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested : root;

                static string? GetStr(JsonElement el, params string[] names)
                {
                    foreach (var n in names)
                        if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString();
                    return null;
                }

                DateTimeOffset? expiry = null;
                var expiryStr = GetStr(token, "expiry", "expires_at", "expiry_date");
                if (!string.IsNullOrWhiteSpace(expiryStr) && DateTimeOffset.TryParse(expiryStr, out var dt))
                    expiry = dt;

                return new KeyringOAuthCreds(
                    GetStr(token, "access_token", "accessToken"),
                    GetStr(token, "refresh_token", "refreshToken"),
                    GetStr(token, "client_id", "clientId") ?? GetStr(root, "client_id", "clientId"),
                    GetStr(token, "client_secret", "clientSecret") ?? GetStr(root, "client_secret", "clientSecret"),
                    expiry);
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? ReadGenericCredential(string targetName)
        {
            if (!CredReadW(targetName, 1, 0, out var credPtr) || credPtr == IntPtr.Zero)
                return null;

            try
            {
                var cred = Marshal.PtrToStructure<NATIVE_CREDENTIAL>(credPtr);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize <= 0)
                    return null;

                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NATIVE_CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr cred);
    }
}
