using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota.Services
{
    public sealed record ClaudeTokens(
        string AccessToken,
        string? RefreshToken,
        string? SubscriptionType,
        string? RateLimitTier,
        DateTimeOffset? ExpiresAt)
    {
        public bool IsExpired => ExpiresAt is { } e && DateTimeOffset.UtcNow >= e - TimeSpan.FromMinutes(2);
    }

    public sealed record ClaudeLoginRequest(
        string AuthorizeUrl,
        string CodeVerifier,
        string State,
        string RedirectUri);

    /// <summary>
    /// "Login with Claude" via the public Claude Code OAuth client (PKCE). The Claude Code client
    /// is registered with Claude's platform callback, so the app opens Claude's auth page and asks
    /// the user to paste the returned code before exchanging it for a usage token.
    /// </summary>
    public static class ClaudeOAuth
    {
        private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        // Anthropic migrated the authorize endpoint from claude.ai/oauth/authorize to
        // claude.com/cai/oauth/authorize; the old host still serves the SPA but renders
        // "Authorization failed: Invalid request format". This URL matches what Claude Code emits.
        private const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
        private const string RedirectUri = "https://platform.claude.com/oauth/code/callback";
        private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
        private const string Scope = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public static bool IsLoggedIn => Load() is not null;

        public static bool WasStoreWrittenAfter(DateTimeOffset timestamp)
        {
            try
            {
                if (!File.Exists(StorePath))
                    return false;

                return File.GetLastWriteTimeUtc(StorePath) >= timestamp.UtcDateTime;
            }
            catch
            {
                return false;
            }
        }

        public static ClaudeLoginRequest CreateLoginRequest()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            // Claude Code's authorize flow uses a 43-character base64url state. Shorter
            // states can make Claude's SPA fail with "Invalid request format".
            var state = verifier;

            var query = new Dictionary<string, string>
            {
                ["code"] = "true",
                ["client_id"] = ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["scope"] = Scope,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
            };

            return new ClaudeLoginRequest(AuthorizeUrl + "?" + UrlEncode(query), verifier, state, RedirectUri);
        }

        public static void OpenLoginPage(ClaudeLoginRequest request)
        {
            try { Process.Start(new ProcessStartInfo(request.AuthorizeUrl) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not open browser for Claude login: {ex.Message}");
            }
        }

        public static async Task<ClaudeTokens> CompleteLoginAsync(ClaudeLoginRequest request, string callbackOrCode, CancellationToken ct = default)
        {
            var code = ExtractCode(callbackOrCode)
                ?? throw new InvalidOperationException("Paste the Claude authorization code or callback URL.");

            var body = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = request.RedirectUri,
                ["client_id"] = ClientId,
                ["code_verifier"] = request.CodeVerifier,
                ["state"] = request.State,
            };
            var tokens = await PostTokenAsync(body, fallbackRefresh: null, ct).ConfigureAwait(false);
            Save(tokens);
            return tokens;
        }

        /// <summary>Runs the interactive browser login. UI callers should prefer Create/Open/Complete.</summary>
        public static Task<ClaudeTokens> LoginAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Claude login requires pasting the authorization code shown in the browser.");

        /// <summary>Returns a valid (refreshed if needed) token, or null if not logged in / refresh failed.</summary>
        public static async Task<ClaudeTokens?> GetValidTokensAsync(CancellationToken ct = default)
        {
            var tokens = Load();
            if (tokens is null) return null;
            if (!tokens.IsExpired) return tokens;
            if (string.IsNullOrEmpty(tokens.RefreshToken)) return null;

            try
            {
                var body = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = ClientId,
                    ["refresh_token"] = tokens.RefreshToken!,
                    ["scope"] = Scope,
                };
                var refreshed = await PostTokenAsync(body, fallbackRefresh: tokens.RefreshToken, ct).ConfigureAwait(false);
                Save(refreshed);
                return refreshed;
            }
            catch (Exception ex)
            {
                Log.Debug($"[claude-oauth] refresh failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<ClaudeTokens> PostTokenAsync(Dictionary<string, string> body, string? fallbackRefresh, CancellationToken ct)
        {
            // Claude's token endpoint expects a JSON body (matches the Claude Code client and the
            // provider's own refresh path), not form-urlencoded.
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(TokenUrl, content, ct).ConfigureAwait(false);
            var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Claude token endpoint HTTP {(int)resp.StatusCode}: {Truncate(respBody, 200)}");

            using var doc = JsonDocument.Parse(respBody);
            var root = doc.RootElement;
            string access = root.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(access))
                throw new InvalidOperationException("Claude token endpoint returned no access_token.");
            string? refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : fallbackRefresh;
            DateTimeOffset? expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var secs)
                ? DateTimeOffset.UtcNow.AddSeconds(secs)
                : null;

            string? subType = null, tier = null;
            if (root.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object)
            {
                subType = Str(acct, "subscription_type", "subscriptionType");
                tier = Str(acct, "rate_limit_tier", "rateLimitTier");
            }
            subType ??= Str(root, "subscription_type", "subscriptionType");
            tier ??= Str(root, "rate_limit_tier", "rateLimitTier");

            return new ClaudeTokens(access, refresh, subType, tier, expires);
        }

        private static string? Str(JsonElement obj, params string[] keys)
        {
            foreach (var k in keys)
                if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }

        // --- token store -------------------------------------------------------

        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarQuota", "claude-oauth.json");

        public static ClaudeTokens? Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(StorePath));
                var r = doc.RootElement;
                string access = r.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(access)) return null;
                return new ClaudeTokens(
                    access,
                    r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                    r.TryGetProperty("subscription_type", out var st) ? st.GetString() : null,
                    r.TryGetProperty("rate_limit_tier", out var ti) ? ti.GetString() : null,
                    r.TryGetProperty("expires_at", out var ex) && ex.TryGetInt64(out var s)
                        ? DateTimeOffset.FromUnixTimeSeconds(s) : null);
            }
            catch (Exception ex) { Log.Debug($"[claude-oauth] load failed: {ex.Message}"); return null; }
        }

        public static void Logout()
        {
            try { if (File.Exists(StorePath)) File.Delete(StorePath); }
            catch (Exception ex) { Log.Debug($"[claude-oauth] logout failed: {ex.Message}"); }
        }

        private static void Save(ClaudeTokens t)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                using var stream = File.Create(StorePath);
                using var w = new Utf8JsonWriter(stream);
                w.WriteStartObject();
                w.WriteString("access_token", t.AccessToken);
                if (t.RefreshToken is not null) w.WriteString("refresh_token", t.RefreshToken);
                if (t.SubscriptionType is not null) w.WriteString("subscription_type", t.SubscriptionType);
                if (t.RateLimitTier is not null) w.WriteString("rate_limit_tier", t.RateLimitTier);
                if (t.ExpiresAt is { } e) w.WriteNumber("expires_at", e.ToUnixTimeSeconds());
                w.WriteEndObject();
            }
            catch (Exception ex) { Log.Debug($"[claude-oauth] save failed: {ex.Message}"); }
        }

        // --- helpers -----------------------------------------------------------

        private static int FreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) { map[Uri.UnescapeDataString(pair)] = ""; continue; }
                map[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return map;
        }

        public static string? ExtractCode(string input)
        {
            input = input.Trim();
            if (string.IsNullOrEmpty(input))
                return null;

            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                var query = ParseQuery(uri.Query);
                if (query.TryGetValue("code", out var codeFromUrl) && !string.IsNullOrWhiteSpace(codeFromUrl))
                    return codeFromUrl.Trim();
            }

            if (input.Contains("code=", StringComparison.OrdinalIgnoreCase))
            {
                var queryStart = input.IndexOf('?', StringComparison.Ordinal);
                var queryText = queryStart >= 0 ? input[(queryStart + 1)..] : input;
                var fragmentStart = queryText.IndexOf('#', StringComparison.Ordinal);
                if (fragmentStart >= 0)
                    queryText = queryText[..fragmentStart];

                var query = ParseQuery(queryText);
                if (query.TryGetValue("code", out var codeFromText) && !string.IsNullOrWhiteSpace(codeFromText))
                    return codeFromText.Trim();
            }

            var first = input.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
            var hash = first.IndexOf('#', StringComparison.Ordinal);
            return (hash > 0 ? first[..hash] : first).Trim();
        }

        private static void WriteResponse(HttpListenerContext ctx, string innerHtml)
        {
            try
            {
                var html = $"<!doctype html><html><head><meta charset=utf-8><title>TaskbarQuota</title></head>" +
                           $"<body style='font-family:Segoe UI,sans-serif;text-align:center;margin-top:15%'>{innerHtml}</body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }

        private static string UrlEncode(Dictionary<string, string> q)
        {
            var sb = new StringBuilder();
            foreach (var kv in q)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
            return sb.ToString();
        }

        private static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string Truncate(string s, int n) => s.Length > n ? s[..n] + "..." : s;
    }
}
