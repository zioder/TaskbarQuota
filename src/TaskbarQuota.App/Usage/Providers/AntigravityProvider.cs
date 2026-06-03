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
            int apiPort = await FindApiPort(info, ct).ConfigureAwait(false);

            var url = $"https://127.0.0.1:{apiPort}/exa.language_server_pb.LanguageServerService/GetUserStatus";
            var body = JsonSerializer.Serialize(new
            {
                metadata = new { ideName = "antigravity", extensionName = "antigravity", ideVersion = "unknown", locale = "en" }
            });

            string csrf = info.ExtensionServerCsrfToken ?? info.CsrfToken;
            var json = await PostStatus(url, body, csrf, ct).ConfigureAwait(false);
            if (json == null && info.ExtensionServerCsrfToken != null)
                json = await PostStatus(url, body, info.CsrfToken, ct).ConfigureAwait(false); // retry with LS token

            if (json == null)
                throw new ProviderException(ProviderErrorKind.Other, "Antigravity API request failed.");

            using var doc = JsonDocument.Parse(json);
            return new ProviderFetchResult(ParseUserStatus(doc.RootElement), "local");
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

            if (us.TryGetProperty("planStatus", out var ps) && ps.TryGetProperty("planInfo", out var pi))
            {
                string? plan = (pi.TryGetProperty("planDisplayName", out var pdn) ? pdn.GetString() : null)
                            ?? (pi.TryGetProperty("planName", out var pn) ? pn.GetString() : null);
                if (!string.IsNullOrEmpty(plan)) snapshot.LoginMethod = plan;
            }
            return snapshot;
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

        private sealed record ProcessInfo(string CsrfToken, string? ExtensionServerCsrfToken, int ExtensionPort, int? Pid);

        private static ProcessInfo DetectProcessInfo()
        {
            try
            {
                // Match both the older language_server_windows and the current language_server.exe.
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server%'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var cmd = mo["CommandLine"] as string;
                    if (cmd == null || !cmd.Contains("--csrf_token")) continue;

                    var csrf = CsrfRe.Match(cmd);
                    if (!csrf.Success) continue;

                    // --extension_server_port is optional in current builds (HTTPS port is random);
                    // we rely on the process's own listening ports in FindApiPort.
                    var port = PortRe.Match(cmd);
                    int extPort = port.Success ? int.Parse(port.Groups[1].Value) : 0;

                    var extCsrf = ExtCsrfRe.Match(cmd);
                    int? pid = int.TryParse(mo["ProcessId"]?.ToString(), out var p) ? p : null;
                    return new ProcessInfo(csrf.Groups[1].Value,
                        extCsrf.Success ? extCsrf.Groups[1].Value : null,
                        extPort, pid);
                }
            }
            catch (Exception ex)
            {
                throw new ProviderException(ProviderErrorKind.NotInstalled, $"Antigravity detection failed: {ex.Message}");
            }
            throw new ProviderException(ProviderErrorKind.NotInstalled, "Antigravity language server not running.");
        }

        private static async Task<int> FindApiPort(ProcessInfo info, CancellationToken ct)
        {
            var candidates = new List<int>();
            if (info.Pid is int pid) candidates.AddRange(ListeningPortsForPid(pid));
            if (info.ExtensionPort > 0)
                for (int off = 0; off < 20; off++) candidates.Add(info.ExtensionPort + off);
            candidates.AddRange(new[] { 53835, 53836, 53837, 53838, 53845, 53849 });

            var seen = new HashSet<int>();
            var ports = candidates.Where(port => seen.Add(port)).ToArray();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var probes = ports
                .Select(port => ProbePortResult(port, linkedCts.Token))
                .ToList();

            while (probes.Count > 0)
            {
                var completed = await Task.WhenAny(probes).ConfigureAwait(false);
                probes.Remove(completed);

                var result = await completed.ConfigureAwait(false);
                if (result.Available)
                {
                    linkedCts.Cancel();
                    _ = Task.WhenAll(probes).ContinueWith(_ => linkedCts.Dispose(), TaskScheduler.Default);
                    return result.Port;
                }
            }

            linkedCts.Dispose();
            throw new ProviderException(ProviderErrorKind.Other, "Could not find Antigravity API port.");
        }

        private static async Task<(int Port, bool Available)> ProbePortResult(int port, CancellationToken ct)
            => (port, await ProbePort(port, ct).ConfigureAwait(false));

        private static async Task<bool> ProbePort(int port, CancellationToken ct)
        {
            var url = $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUnleashData";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var resp = await Local.SendAsync(req, cts.Token).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                return code == 200 || code == 401;
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
