using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota.Services
{
    public sealed record ClaudeDesktopTokens(
        string AccessToken,
        string? RefreshToken,
        string? SubscriptionType,
        string? RateLimitTier,
        long? ExpiresAtMs);

    /// <summary>
    /// Reads the live Claude Code OAuth token from the Claude **desktop** app, which on Windows
    /// stores it in %AppData%\Claude\config.json under the "oauth:tokenCache" key — a Chromium
    /// OSCrypt (v10, AES-256-GCM) blob whose key lives in the sibling "Local State" file, DPAPI
    /// wrapped (same App-Bound scheme as browser cookies).
    ///
    /// This is the source of truth when the user signs in through the desktop app rather than the
    /// CLI's `claude /login`: in that case ~/.claude/.credentials.json keeps only metadata
    /// (subscriptionType, scopes) with an empty accessToken, so the CLI file reader finds nothing.
    /// </summary>
    public static class ClaudeDesktopTokenReader
    {
        // The Claude Code public OAuth client — same id the app uses for "Login with Claude".
        private const string ClaudeCodeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        private const string ClaudeCodeScope = "user:sessions:claude_code";

        private static string ConfigDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

        public static bool IsInstalled => File.Exists(Path.Combine(ConfigDir, "config.json"));

        /// <summary>Returns the Claude Code token from the desktop app, or null if unavailable.</summary>
        public static ClaudeDesktopTokens? TryRead()
        {
            try
            {
                var configPath = Path.Combine(ConfigDir, "config.json");
                var localStatePath = Path.Combine(ConfigDir, "Local State");
                if (!File.Exists(configPath) || !File.Exists(localStatePath))
                    return null;

                string? cacheB64;
                using (var cfg = JsonDocument.Parse(File.ReadAllText(configPath)))
                {
                    cacheB64 = cfg.RootElement.TryGetProperty("oauth:tokenCache", out var tc) ? tc.GetString() : null;
                }
                if (string.IsNullOrEmpty(cacheB64))
                    return null;

                var key = GetEncryptionKey(localStatePath);
                var json = DecryptV10(Convert.FromBase64String(cacheB64), key);
                if (json is null)
                    return null;

                using var doc = JsonDocument.Parse(json);
                return SelectClaudeCodeEntry(doc.RootElement);
            }
            catch (Exception ex)
            {
                Log.Debug($"[claude-desktop] token read failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The cache is keyed by "&lt;clientId&gt;:&lt;accountId&gt;:&lt;audience&gt;:&lt;scopes&gt;". Prefer the
        /// Claude Code client with the claude_code session scope; fall back to any entry from that
        /// client.
        /// </summary>
        private static ClaudeDesktopTokens? SelectClaudeCodeEntry(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            ClaudeDesktopTokens? fallback = null;
            foreach (var entry in root.EnumerateObject())
            {
                var entryKey = entry.Name;
                if (!entryKey.StartsWith(ClaudeCodeClientId, StringComparison.Ordinal))
                    continue;
                if (Parse(entry.Value) is not { } tokens)
                    continue;

                if (entryKey.Contains(ClaudeCodeScope, StringComparison.Ordinal))
                    return tokens;
                fallback ??= tokens;
            }
            return fallback;
        }

        private static ClaudeDesktopTokens? Parse(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Object)
                return null;
            string? token = v.TryGetProperty("token", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
                return null;

            string? refresh = v.TryGetProperty("refreshToken", out var r) ? r.GetString() : null;
            string? sub = v.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            string? tier = v.TryGetProperty("rateLimitTier", out var rl) && rl.ValueKind == JsonValueKind.String ? rl.GetString() : null;
            long? expires = v.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms)
                ? ms : (long?)null;
            return new ClaudeDesktopTokens(token!, refresh, sub, tier, expires);
        }

        // --- App-Bound (OSCrypt) decryption — mirrors CookieExtractor's browser-cookie path ---

        private static byte[] GetEncryptionKey(string localStatePath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
            var b64 = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()
                      ?? throw new InvalidOperationException("no encrypted_key");
            var encrypted = Convert.FromBase64String(b64);
            if (encrypted.Length < 5 || Encoding.ASCII.GetString(encrypted, 0, 5) != "DPAPI")
                throw new InvalidOperationException("invalid key format");
            return ProtectedData.Unprotect(encrypted[5..], null, DataProtectionScope.CurrentUser);
        }

        private static string? DecryptV10(byte[] blob, byte[] key)
        {
            // v10/v11: 3-byte prefix + 12-byte nonce + ciphertext + 16-byte tag
            if (blob.Length < 31 || blob[0] != 'v' || blob[1] != '1' || (blob[2] != '0' && blob[2] != '1'))
                return null;
            try
            {
                var nonce = blob[3..15];
                var tag = blob[^16..];
                var ciphertext = blob[15..^16];
                var plaintext = new byte[ciphertext.Length];
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(nonce, ciphertext, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch { return null; }
        }
    }
}
