using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// stores an OIDC token in ~/.grok/auth.json. The credit window comes from the same gRPC-web call
    /// the grok.com usage page makes (GetGrokCreditsConfig); the plan comes from /rest/subscriptions.
    /// Ported from CodexBar Sources/CodexBarCore/Providers/Grok.
    /// </summary>
    public sealed class GrokProvider : IUsageProvider
    {
        private const string OidcScopePrefix = "https://auth.x.ai::";
        private const string LegacySessionScope = "https://accounts.x.ai/sign-in";
        private const string CreditsEndpoint = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";
        private const string SubscriptionsEndpoint = "https://grok.com/rest/subscriptions";
        private const int CreditsRetries = 3;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public ProviderId Id => ProviderId.Grok;
        public string DisplayName => "Grok";
        public string SessionLabel => "Credits";
        public string WeeklyLabel => "Monthly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var creds = LoadCredentials();

            var credits = await FetchCreditsAsync(creds.AccessToken, ct).ConfigureAwait(false);

            var primary = new RateWindow(
                credits.UsedPercent,
                windowMinutes: null,
                resetAt: credits.ResetAt,
                resetDescription: CodexProvider.FormatResetCountdown(credits.ResetAt));

            var usage = new UsageSnapshot(primary)
            {
                Email = creds.Email,
                UsageDashboardUrl = "https://grok.com/?_s=usage",
            };

            // Plan name is best-effort: a missing/changed subscriptions endpoint must not fail the fetch.
            var plan = await TryFetchPlanAsync(creds, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(plan))
                usage.LoginMethod = plan;

            return new ProviderFetchResult(usage, "grok-cli");
        }

        // --- Credits (gRPC-web GetGrokCreditsConfig) ---------------------------------------------

        internal sealed record CreditsSnapshot(double UsedPercent, DateTimeOffset? ResetAt);

        private static async Task<CreditsSnapshot> FetchCreditsAsync(string accessToken, CancellationToken ct)
        {
            Exception? last = null;
            for (int attempt = 0; attempt < CreditsRetries; attempt++)
            {
                try
                {
                    return await FetchCreditsOnceAsync(accessToken, ct).ConfigureAwait(false);
                }
                catch (ProviderException pe) when (pe.Kind == ProviderErrorKind.Timeout && attempt < CreditsRetries - 1)
                {
                    last = pe;
                }
            }

            throw last ?? new ProviderException(ProviderErrorKind.Timeout, "Grok credits request timed out.");
        }

        private static async Task<CreditsSnapshot> FetchCreditsOnceAsync(string accessToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CreditsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Origin", "https://grok.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://grok.com/?_s=usage");
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
            request.Headers.TryAddWithoutValidation("x-user-agent", "connect-es/2.1.1");
            request.Headers.UserAgent.ParseAdd("TaskbarQuota");
            // Empty gRPC-web message frame: 1 flag byte + 4-byte big-endian length (0).
            request.Content = new ByteArrayContent(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 });
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Grok token expired. Run `grok login`.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Grok credits API returned {(int)response.StatusCode}");

            var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // gRPC status can arrive as a response header or as a trailer frame in the body.
            var headerStatus = response.Headers.TryGetValues("grpc-status", out var hv) ? hv.FirstOrDefault() : null;
            var headerMessage = response.Headers.TryGetValues("grpc-message", out var mv) ? mv.FirstOrDefault() : null;
            EnsureGrpcOk(headerStatus, headerMessage);

            var trailers = GrpcWebTrailerFields(body);
            trailers.TryGetValue("grpc-status", out var trailerStatus);
            trailers.TryGetValue("grpc-message", out var trailerMessage);
            EnsureGrpcOk(trailerStatus, trailerMessage);

            return ParseCreditsResponse(body, DateTimeOffset.UtcNow);
        }

        private static void EnsureGrpcOk(string? status, string? message)
        {
            if (string.IsNullOrEmpty(status) || status == "0")
                return;

            var text = Uri.UnescapeDataString(message ?? string.Empty);
            // Status 1 (CANCELLED) / 4 (DEADLINE_EXCEEDED) with a timeout message is a transient xAI hiccup.
            bool transient = status is "1" or "4"
                && (text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("deadline", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("expired", StringComparison.OrdinalIgnoreCase));
            if (transient)
                throw new ProviderException(ProviderErrorKind.Timeout, $"Grok credits RPC transient error: {text}");

            if (status is "16")
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Grok token expired. Run `grok login`.");

            throw new ProviderException(ProviderErrorKind.Other, $"Grok credits RPC failed (status {status}): {text}");
        }

        internal static CreditsSnapshot ParseCreditsResponse(byte[] data, DateTimeOffset now)
        {
            var frames = GrpcWebDataFrames(data);
            if (frames.Count == 0)
                throw new ProviderException(ProviderErrorKind.Parse, "Grok credits returned no payload.");

            var scan = new ProtobufScan();
            foreach (var frame in frames)
                ScanProtobuf(frame, 0, Array.Empty<ulong>(), scan);

            double? percent = scan.Fixed32
                .Where(f => f.Path.Length > 0 && f.Path[^1] == 1 && !float.IsNaN(f.Value) && !float.IsInfinity(f.Value) && f.Value >= 0 && f.Value <= 100)
                .OrderBy(f => f.Path.Length)
                .ThenBy(f => f.Order)
                .Select(f => (double?)f.Value)
                .FirstOrDefault();

            var resets = scan.Varint
                .Where(f => f.Value >= 1_700_000_000 && f.Value <= 2_100_000_000)
                .Select(f => (f.Path, Date: DateTimeOffset.FromUnixTimeSeconds((long)f.Value)))
                .Where(f => f.Date > now)
                .ToList();

            DateTimeOffset? reset = resets
                .Where(f => PathEquals(f.Path, 1, 5, 1))
                .Select(f => (DateTimeOffset?)f.Date)
                .DefaultIfEmpty(null)
                .Min()
                ?? resets.Select(f => (DateTimeOffset?)f.Date).DefaultIfEmpty(null).Min();

            // Brand-new subscription: no usage floats yet, but a reset and a usage submessage exist.
            bool noUsageYet = percent is null
                && scan.Fixed32.Count == 0
                && reset != null
                && scan.Varint.Any(f => f.Path.Length >= 2 && f.Path[0] == 1 && f.Path[1] == 6);

            if (percent is null && noUsageYet)
                percent = 0;
            if (percent is null)
                throw new ProviderException(ProviderErrorKind.Parse, "Could not parse Grok credit usage.");

            return new CreditsSnapshot(Math.Clamp(percent.Value, 0, 100), reset);
        }

        // --- Plan (REST subscriptions) -----------------------------------------------------------

        private static async Task<string?> TryFetchPlanAsync(Credentials creds, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, SubscriptionsEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd("TaskbarQuota");

                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return PlanFromAuthMode(creds.AuthMode);

                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                return PlanFromSubscriptions(doc.RootElement) ?? PlanFromAuthMode(creds.AuthMode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log.Warning(ex, "Grok subscriptions lookup failed");
                return PlanFromAuthMode(creds.AuthMode);
            }
        }

        internal static string? PlanFromSubscriptions(JsonElement root)
        {
            if (!root.TryGetProperty("subscriptions", out var subs) || subs.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var sub in subs.EnumerateArray())
            {
                string? tier = sub.TryGetProperty("tier", out var tr) && tr.ValueKind == JsonValueKind.String
                    ? tr.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(tier))
                    continue;

                string plan = PlanFromTier(tier!);
                bool trial = sub.TryGetProperty("activeOffer", out var offer)
                    && offer.ValueKind == JsonValueKind.Object
                    && offer.TryGetProperty("type", out var ot)
                    && (ot.GetString() ?? string.Empty).Contains("FREE_TRIAL", StringComparison.OrdinalIgnoreCase);

                if (trial) plan += " (Trial)";
                return plan;
            }

            return null;
        }

        // xAI does not document the tier enum. Only SUBSCRIPTION_TIER_GROK_PRO is verified (a real
        // SuperGrok account); the rest are inferred by substring and any unknown tier is title-cased
        // verbatim so it is never silently mislabeled. CodexBar does not read tier at all (it labels
        // every OIDC login "SuperGrok"), so this is intentionally more granular than the reference.
        private static string PlanFromTier(string tier)
        {
            var upper = tier.ToUpperInvariant();
            if (upper.Contains("HEAVY")) return "SuperGrok Heavy";          // inferred
            if (upper.Contains("PRO") || upper.Contains("SUPER")) return "SuperGrok"; // PRO verified
            if (upper.Contains("PREMIUM"))                                  // inferred (X Premium / Premium+)
                return upper.Contains("PLUS") ? "X Premium+" : "X Premium";

            // Unknown tier: strip SUBSCRIPTION_TIER_[GROK_] prefix, title-case the remainder verbatim.
            var trimmed = upper
                .Replace("SUBSCRIPTION_TIER_", string.Empty, StringComparison.Ordinal)
                .Replace("GROK_", string.Empty, StringComparison.Ordinal)
                .Replace('_', ' ')
                .Trim();
            if (trimmed.Length == 0) return "Grok";
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
        }

        private static string? PlanFromAuthMode(string? authMode) => authMode?.ToLowerInvariant() switch
        {
            "oidc" => "SuperGrok",
            "session" => "Session",
            _ => null,
        };

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

        // --- gRPC-web framing + protobuf scan ----------------------------------------------------

        internal static List<byte[]> GrpcWebDataFrames(byte[] data)
        {
            var frames = new List<byte[]>();
            int i = 0;
            while (i + 5 <= data.Length)
            {
                byte flags = data[i];
                int length = (data[i + 1] << 24) | (data[i + 2] << 16) | (data[i + 3] << 8) | data[i + 4];
                int start = i + 5;
                int end = start + length;
                if (length < 0 || end > data.Length) break;
                if ((flags & 0x80) == 0)
                    frames.Add(data[start..end]);
                i = end;
            }
            return frames;
        }

        private static Dictionary<string, string> GrpcWebTrailerFields(byte[] data)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i + 5 <= data.Length)
            {
                byte flags = data[i];
                int length = (data[i + 1] << 24) | (data[i + 2] << 16) | (data[i + 3] << 8) | data[i + 4];
                int start = i + 5;
                int end = start + length;
                if (length < 0 || end > data.Length) break;
                if ((flags & 0x80) != 0)
                {
                    var text = System.Text.Encoding.UTF8.GetString(data, start, length);
                    foreach (var line in text.Split('\n', '\r'))
                    {
                        var sep = line.IndexOf(':');
                        if (sep <= 0) continue;
                        var k = line[..sep].Trim().ToLowerInvariant();
                        var val = line[(sep + 1)..].Trim();
                        if (k.Length > 0) fields[k] = val;
                    }
                }
                i = end;
            }
            return fields;
        }

        private static bool PathEquals(ulong[] path, params ulong[] expected)
            => path.Length == expected.Length && path.SequenceEqual(expected);

        private sealed class ProtobufScan
        {
            public readonly List<(ulong[] Path, float Value, int Order)> Fixed32 = new();
            public readonly List<(ulong[] Path, ulong Value)> Varint = new();
            public int NextOrder;
        }

        private static void ScanProtobuf(byte[] data, int depth, ulong[] path, ProtobufScan scan)
        {
            int i = 0;
            while (i < data.Length)
            {
                int fieldStart = i;
                if (!TryReadVarint(data, ref i, out ulong key) || key == 0)
                {
                    i = fieldStart + 1;
                    continue;
                }

                ulong fieldNumber = key >> 3;
                ulong wireType = key & 0x07;
                var fieldPath = Append(path, fieldNumber);

                switch (wireType)
                {
                    case 0: // varint
                        if (TryReadVarint(data, ref i, out ulong v))
                            scan.Varint.Add((fieldPath, v));
                        else
                            i = fieldStart + 1;
                        break;
                    case 1: // 64-bit
                        if (i + 8 > data.Length) return;
                        i += 8;
                        break;
                    case 2: // length-delimited
                        if (!TryReadVarint(data, ref i, out ulong len) || len > (ulong)(data.Length - i))
                        {
                            i = fieldStart + 1;
                            continue;
                        }
                        int start = i;
                        int end = i + (int)len;
                        if (depth < 4)
                            ScanProtobuf(data[start..end], depth + 1, fieldPath, scan);
                        i = end;
                        break;
                    case 5: // 32-bit
                        if (i + 4 > data.Length) return;
                        uint bits = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
                        scan.Fixed32.Add((fieldPath, BitConverter.Int32BitsToSingle((int)bits), scan.NextOrder++));
                        i += 4;
                        break;
                    default:
                        i = fieldStart + 1;
                        break;
                }
            }
        }

        private static ulong[] Append(ulong[] path, ulong value)
        {
            var next = new ulong[path.Length + 1];
            Array.Copy(path, next, path.Length);
            next[^1] = value;
            return next;
        }

        private static bool TryReadVarint(byte[] data, ref int index, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (index < data.Length && shift < 64)
            {
                byte b = data[index++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }
    }
}
