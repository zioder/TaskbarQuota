using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TaskbarQuota;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Antigravity usage via its local language server (Connect/gRPC over self-signed TLS on 127.0.0.1).
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

        public ProviderId Id => ProviderId.Antigravity;
        public string DisplayName => "Antigravity";
        public string SessionLabel => "Gemini";
        public string WeeklyLabel => "Non-Gemini";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
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
            if (!doc.RootElement.TryGetProperty("response", out var resp) ||
                !resp.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return false;

            RateWindow? geminiWeekly = null, geminiFiveHour = null, otherWeekly = null, otherFiveHour = null;
            bool any = false;
            foreach (var g in groups.EnumerateArray())
            {
                string name = g.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                bool isGemini = name.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
                if (!g.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in buckets.EnumerateArray())
                {
                    var window = RateWindowFromQuota(b); // buckets carry remainingFraction + resetTime like quotaInfo
                    any = true;
                    if (IsFiveHourBucket(b))
                    {
                        if (isGemini) geminiFiveHour = window; else otherFiveHour = window;
                    }
                    else
                    {
                        if (isGemini) geminiWeekly = window; else otherWeekly = window;
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

        private static RateWindow RateWindowFromQuota(JsonElement quota)
        {
            double remaining = quota.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number ? rf.GetDouble() : 1.0;
            string? reset = quota.TryGetProperty("resetTime", out var rt) ? rt.GetString() : null;
            DateTimeOffset? resetAt = null;
            if (reset != null && DateTimeOffset.TryParse(reset, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                resetAt = dt;
            return new RateWindow((1.0 - remaining) * 100.0, null, resetAt, CodexProvider.FormatResetCountdown(resetAt));
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
    }
}
