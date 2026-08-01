using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota.Browser
{
    /// <summary>
    /// Extracts cookies for a domain from Chromium-based browsers (Edge, Chrome, Brave) and
    /// Firefox-based browsers (Firefox, Zen, Waterfox, LibreWolf, Floorp) on Windows.
    ///
    /// Chromium: cookie DB is SQLite; values are AES-256-GCM encrypted with a key stored in
    /// "Local State" and protected by DPAPI. Chrome 127+ App-Bound Encryption breaks user-level
    /// DPAPI decryption — Edge is the reliable path on Windows.
    ///
    /// Firefox: cookie DB is SQLite (cookies.sqlite); values are stored in plaintext.
    ///
    /// A manual cookie-header fallback covers the rest.
    /// </summary>
    public static class CookieExtractor
    {
        private sealed record Browser(string Name, string UserDataDir);
        private sealed record FirefoxBrowser(string Name, string ProfilesDir);

        /// <summary>
        /// Cookies collected from one browser profile. Keeping profiles separate is important:
        /// combining cookies from different browsers can create a request made from a stale or
        /// mixed session when one Chromium profile cannot be decrypted.
        /// </summary>
        internal sealed record CookieSource(
            string BrowserName,
            string ProfileName,
            IReadOnlyList<(string Name, string Value)> Cookies)
        {
            public string Header => string.Join("; ", Cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }

        private static IEnumerable<Browser> ChromiumBrowsers()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return new Browser("Cursor", Path.Combine(roaming, "Cursor"));
            // Edge first — it generally still decrypts with the user DPAPI key.
            yield return new Browser("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"));
            yield return new Browser("Chrome", Path.Combine(local, "Google", "Chrome", "User Data"));
            yield return new Browser("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));
        }

        private static IEnumerable<FirefoxBrowser> FirefoxBrowsers()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return new FirefoxBrowser("Zen", Path.Combine(roaming, "zen"));
            yield return new FirefoxBrowser("Firefox", Path.Combine(roaming, "Mozilla", "Firefox"));
            yield return new FirefoxBrowser("Waterfox", Path.Combine(roaming, "Waterfox"));
            yield return new FirefoxBrowser("LibreWolf", Path.Combine(roaming, "LibreWolf"));
            yield return new FirefoxBrowser("Floorp", Path.Combine(roaming, "Floorp"));
        }

        /// <summary>Returns a "name=value; name2=value2" Cookie header for the domain, or null if none found.</summary>
        public static string? GetCookieHeader(string domain)
        {
            var jar = new Dictionary<string, string>();

            foreach (var browser in ChromiumBrowsers())
            {
                if (!Directory.Exists(browser.UserDataDir)) continue;
                try
                {
                    foreach (var (name, value) in ExtractFromChromium(browser, domain))
                        jar[name] = value;
                }
                catch (Exception ex)
                {
                    Log.Debug($"Cookie extract failed for {browser.Name}/{domain}: {ex.Message}");
                }
            }

            foreach (var browser in FirefoxBrowsers())
            {
                if (!Directory.Exists(browser.ProfilesDir)) continue;
                try
                {
                    foreach (var (name, value) in ExtractFromFirefox(browser, domain))
                        jar[name] = value;
                }
                catch (Exception ex)
                {
                    Log.Debug($"Cookie extract failed for {browser.Name}/{domain}: {ex.Message}");
                }
            }

            if (jar.Count == 0) return null;
            return string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        /// <summary>
        /// Returns cookie jars separately for each browser profile. Unlike <see cref="GetCookieHeader"/>,
        /// this method never combines cookies from different profiles and preserves Firefox cookie
        /// chunks. OpenCode uses it to choose a complete, decryptable session instead of accepting
        /// a hybrid made from stale and current browser cookies.
        /// </summary>
        internal static IReadOnlyList<CookieSource> GetCookieSources(params string[] domains)
        {
            var requestedDomains = domains
                .Where(domain => !string.IsNullOrWhiteSpace(domain))
                .Select(domain => domain.Trim().TrimStart('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (requestedDomains.Length == 0)
                return Array.Empty<CookieSource>();

            var sources = new List<CookieSource>();

            foreach (var browser in ChromiumBrowsers())
            {
                if (!Directory.Exists(browser.UserDataDir)) continue;

                byte[] key;
                try { key = GetEncryptionKey(Path.Combine(browser.UserDataDir, "Local State")); }
                catch (Exception ex)
                {
                    Log.Debug($"{browser.Name} key error: {ex.Message}");
                    continue;
                }

                try
                {
                    foreach (var profile in EnumerateChromiumProfiles(browser.UserDataDir)
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var cookiesDb = FindChromiumCookiesDb(profile);
                        if (cookiesDb == null) continue;

                        var jar = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var domain in requestedDomains)
                        {
                            foreach (var (name, value) in ReadChromiumCookies(cookiesDb, key, domain))
                                jar[name] = value;
                        }

                        if (jar.Count > 0)
                        {
                            sources.Add(new CookieSource(
                                browser.Name,
                                Path.GetFileName(profile),
                                jar.Select(cookie => (cookie.Key, cookie.Value)).ToList()));
                        }
                    }
                }
                catch (Exception ex) { Log.Debug($"Cookie source scan failed for {browser.Name}: {ex.Message}"); }
            }

            foreach (var browser in FirefoxBrowsers())
            {
                if (!Directory.Exists(browser.ProfilesDir)) continue;

                try
                {
                    foreach (var profile in EnumerateFirefoxProfiles(browser.ProfilesDir)
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var cookiesDb = Path.Combine(profile, "cookies.sqlite");
                        if (!File.Exists(cookiesDb)) continue;

                        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var domain in requestedDomains)
                        {
                            foreach (var (name, value) in ReadFirefoxCookiesRaw(cookiesDb, domain))
                                cookies[name] = value;
                        }

                        if (cookies.Count > 0)
                        {
                            sources.Add(new CookieSource(
                                browser.Name,
                                Path.GetFileName(profile),
                                cookies.Select(cookie => (cookie.Key, cookie.Value)).ToList()));
                        }
                    }
                }
                catch (Exception ex) { Log.Debug($"Cookie source scan failed for {browser.Name}: {ex.Message}"); }
            }

            return sources;
        }

        /// <summary>
        /// Returns raw (name, value) cookie pairs for the domain WITHOUT recombining Firefox's
        /// chunked cookies. Needed for browser-cookie based requests: chunked cookies (e.g.
        /// NextAuth's __Secure-next-auth.session-token.0/.1) must stay separate because each
        /// chunk fits Chromium's ~4 KB per-cookie limit and the server reassembles them.
        /// </summary>
        public static List<(string name, string value)> GetCookiePairs(string domain)
        {
            var jar = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var browser in ChromiumBrowsers())
            {
                if (!Directory.Exists(browser.UserDataDir)) continue;
                try
                {
                    foreach (var (name, value) in ExtractFromChromium(browser, domain))
                        jar[name] = value;
                }
                catch (Exception ex) { Log.Debug($"Cookie pairs failed for {browser.Name}/{domain}: {ex.Message}"); }
            }

            foreach (var browser in FirefoxBrowsers())
            {
                if (!Directory.Exists(browser.ProfilesDir)) continue;
                try
                {
                    foreach (var profileDir in EnumerateFirefoxProfiles(browser.ProfilesDir))
                    {
                        var cookiesDb = Path.Combine(profileDir, "cookies.sqlite");
                        if (!File.Exists(cookiesDb)) continue;
                        foreach (var (name, value) in ReadFirefoxCookiesRaw(cookiesDb, domain))
                            jar[name] = value;
                    }
                }
                catch (Exception ex) { Log.Debug($"Cookie pairs failed for {browser.Name}/{domain}: {ex.Message}"); }
            }

            return jar.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        private static IEnumerable<(string name, string value)> ExtractFromChromium(Browser browser, string domain)
        {
            var localStatePath = Path.Combine(browser.UserDataDir, "Local State");
            if (!File.Exists(localStatePath)) yield break;

            byte[] key;
            try { key = GetEncryptionKey(localStatePath); }
            catch (Exception ex) { Log.Debug($"{browser.Name} key error: {ex.Message}"); yield break; }

            foreach (var profile in EnumerateChromiumProfiles(browser.UserDataDir))
            {
                var cookiesDb = FindChromiumCookiesDb(profile);
                if (cookiesDb == null) continue;

                foreach (var c in ReadChromiumCookies(cookiesDb, key, domain))
                    yield return c;
            }
        }

        private static string? FindChromiumCookiesDb(string profile)
        {
            var networkCookies = Path.Combine(profile, "Network", "Cookies");
            if (File.Exists(networkCookies)) return networkCookies;

            var legacyCookies = Path.Combine(profile, "Cookies");
            return File.Exists(legacyCookies) ? legacyCookies : null;
        }

        private static IEnumerable<(string name, string value)> ExtractFromFirefox(FirefoxBrowser browser, string domain)
        {
            foreach (var profileDir in EnumerateFirefoxProfiles(browser.ProfilesDir))
            {
                var cookiesDb = Path.Combine(profileDir, "cookies.sqlite");
                if (!File.Exists(cookiesDb)) continue;

                foreach (var c in ReadFirefoxCookies(cookiesDb, domain))
                    yield return c;
            }
        }

        private static IEnumerable<string> EnumerateChromiumProfiles(string userDataDir)
        {
            if (File.Exists(Path.Combine(userDataDir, "Network", "Cookies")) ||
                File.Exists(Path.Combine(userDataDir, "Cookies")))
                yield return userDataDir;

            var def = Path.Combine(userDataDir, "Default");
            if (Directory.Exists(def)) yield return def;
            foreach (var dir in Directory.GetDirectories(userDataDir, "Profile *"))
                yield return dir;
        }

        private static IEnumerable<string> EnumerateFirefoxProfiles(string profilesDir)
        {
            var profilesIni = Path.Combine(profilesDir, "profiles.ini");
            if (File.Exists(profilesIni))
            {
                foreach (var line in File.ReadAllLines(profilesIni))
                {
                    if (!line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = line["Path=".Length..].Trim();
                    var absolute = Path.Combine(profilesDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(absolute)) yield return absolute;
                }
            }

            var profilesRoot = Path.Combine(profilesDir, "Profiles");
            if (Directory.Exists(profilesRoot))
            {
                foreach (var dir in Directory.GetDirectories(profilesRoot))
                    yield return dir;
            }
        }

        private static List<(string name, string value)> ReadChromiumCookies(string cookiesDb, byte[] key, string domain)
        {
            var results = new List<(string, string)>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    // Read the live browser database without copying it to %TEMP%; the copy operation
                    // created TaskbarQuota_* SQLite/journal files and could race the browser's writes.
                    // SQLite permits a read-only connection while the browser owns the write connection.
                    using var conn = OpenReadOnlyConnection(cookiesDb);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "SELECT name, encrypted_value, host_key FROM cookies " +
                        "WHERE host_key = $exact OR host_key = $subdomain OR host_key LIKE $suffix";
                    cmd.Parameters.AddWithValue("$exact", domain);
                    cmd.Parameters.AddWithValue("$subdomain", "." + domain);
                    cmd.Parameters.AddWithValue("$suffix", "%." + domain);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string name = reader.GetString(0);
                        byte[] enc = (byte[])reader[1];
                        var val = DecryptCookie(enc, key);
                        if (val != null) results.Add((name, val));
                    }
                    return results;
                }
                catch (Exception ex) when (attempt == 0 && IsTransientSqliteLock(ex))
                {
                    System.Threading.Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Log.Debug($"chromium cookie db read failed: {ex.Message}");
                    break;
                }
            }
            return results;
        }

        private static List<(string name, string value)> ReadFirefoxCookiesRaw(string cookiesDb, string domain)
        {
            var raw = new List<(string name, string value)>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    // Use the browser's database in read-only mode; never create a temp SQLite copy.
                    using var conn = OpenReadOnlyConnection(cookiesDb);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "SELECT name, value, host FROM moz_cookies " +
                        "WHERE host = $exact OR host = $subdomain OR host LIKE $suffix";
                    cmd.Parameters.AddWithValue("$exact", domain);
                    cmd.Parameters.AddWithValue("$subdomain", "." + domain);
                    cmd.Parameters.AddWithValue("$suffix", "%." + domain);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string name = reader.GetString(0);
                        string value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        if (!string.IsNullOrEmpty(name))
                            raw.Add((name, value));
                    }
                    return raw;
                }
                catch (Exception ex) when (attempt == 0 && IsTransientSqliteLock(ex))
                {
                    System.Threading.Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Log.Debug($"firefox cookie db read failed: {ex.Message}");
                    break;
                }
            }
            return raw;
        }

        private static bool IsTransientSqliteLock(Exception ex)
        {
            var message = ex.Message;
            return message.Contains("locked", StringComparison.OrdinalIgnoreCase)
                || message.Contains("busy", StringComparison.OrdinalIgnoreCase);
        }

        private static List<(string name, string value)> ReadFirefoxCookies(string cookiesDb, string domain)
        {
            var raw = ReadFirefoxCookiesRaw(cookiesDb, domain);

            // Firefox splits cookies >4KB into .0, .1, .N chunks; recombine them.
            var grouped = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
            foreach (var (name, value) in raw)
            {
                var dotIdx = name.LastIndexOf('.');
                if (dotIdx > 0 && int.TryParse(name[(dotIdx + 1)..], out var chunk))
                {
                    var baseName = name[..dotIdx];
                    if (!grouped.TryGetValue(baseName, out var chunks))
                    {
                        chunks = new SortedDictionary<int, string>();
                        grouped[baseName] = chunks;
                    }
                    chunks[chunk] = value;
                }
                else
                {
                    if (!grouped.TryGetValue(name, out var chunks))
                    {
                        chunks = new SortedDictionary<int, string>();
                        grouped[name] = chunks;
                    }
                    chunks[-1] = value; // single-chunk cookie uses sentinel key
                }
            }

            var results = new List<(string name, string value)>(grouped.Count);
            foreach (var (baseName, chunks) in grouped)
            {
                if (chunks.Count == 1 && chunks.ContainsKey(-1))
                {
                    // Single-chunk cookie, return as-is
                    results.Add((baseName, chunks[-1]));
                }
                else
                {
                    // Multi-chunk cookie: concatenate values in order (.0, .1, ...)
                    var combined = new System.Text.StringBuilder();
                    foreach (var kv in chunks)
                        combined.Append(kv.Value);
                    results.Add((baseName, combined.ToString()));
                }
            }
            return results;
        }

        private static SqliteConnection OpenReadOnlyConnection(string databasePath)
        {
            return new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
        }

        private static byte[] GetEncryptionKey(string localStatePath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
            var b64 = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()
                      ?? throw new InvalidOperationException("no encrypted_key");
            var encrypted = Convert.FromBase64String(b64);
            if (encrypted.Length < 5 || Encoding.ASCII.GetString(encrypted, 0, 5) != "DPAPI")
                throw new InvalidOperationException("invalid key format");
            var withoutPrefix = encrypted[5..];
            return ProtectedData.Unprotect(withoutPrefix, null, DataProtectionScope.CurrentUser);
        }

        private static string? DecryptCookie(byte[] enc, byte[] key)
        {
            if (enc.Length == 0) return string.Empty;
            // v10/v11: 3-byte prefix + 12-byte nonce + ciphertext + 16-byte tag
            if (enc.Length >= 31 && (enc[0] == 'v' && enc[1] == '1' && (enc[2] == '0' || enc[2] == '1')))
            {
                try
                {
                    var nonce = enc[3..15];
                    var tag = enc[^16..];
                    var ciphertext = enc[15..^16];
                    var plaintext = new byte[ciphertext.Length];
                    using var gcm = new AesGcm(key, 16);
                    gcm.Decrypt(nonce, ciphertext, tag, plaintext);
                    return Encoding.UTF8.GetString(plaintext);
                }
                catch { return null; } // ABE / wrong key
            }
            // Chromium's newer app-bound formats (for example v20) require browser-process
            // mediation and must not be mistaken for the legacy DPAPI format below.
            if (enc.Length >= 3
                && enc[0] == 'v'
                && enc[1] >= '0' && enc[1] <= '9'
                && enc[2] >= '0' && enc[2] <= '9'
                && !(enc[1] == '1' && (enc[2] == '0' || enc[2] == '1')))
                return null;
            // Legacy DPAPI-encrypted value (pre-v10)
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
    }
}
