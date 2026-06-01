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

        private static IEnumerable<(string name, string value)> ExtractFromChromium(Browser browser, string domain)
        {
            var localStatePath = Path.Combine(browser.UserDataDir, "Local State");
            if (!File.Exists(localStatePath)) yield break;

            byte[] key;
            try { key = GetEncryptionKey(localStatePath); }
            catch (Exception ex) { Log.Debug($"{browser.Name} key error: {ex.Message}"); yield break; }

            foreach (var profile in EnumerateChromiumProfiles(browser.UserDataDir))
            {
                var cookiesDb = File.Exists(Path.Combine(profile, "Network", "Cookies"))
                    ? Path.Combine(profile, "Network", "Cookies")
                    : Path.Combine(profile, "Cookies");
                if (!File.Exists(cookiesDb)) continue;

                foreach (var c in ReadChromiumCookies(cookiesDb, key, domain))
                    yield return c;
            }
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
            // Browser keeps the DB locked; copy to temp first.
            string temp = Path.Combine(Path.GetTempPath(), $"TaskbarQuota_cookies_{Guid.NewGuid():N}.db");
            try
            {
                CopyPossiblyLockedFile(cookiesDb, temp);
                using var conn = new SqliteConnection($"Data Source={temp};Mode=ReadOnly;Cache=Private");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT name, encrypted_value, host_key FROM cookies WHERE host_key LIKE $a OR host_key LIKE $b";
                cmd.Parameters.AddWithValue("$a", "%" + domain);
                cmd.Parameters.AddWithValue("$b", "." + domain);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(0);
                    byte[] enc = (byte[])reader[1];
                    var val = DecryptCookie(enc, key);
                    if (val != null) results.Add((name, val));
                }
            }
            catch (Exception ex) { Log.Debug($"chromium cookie db read failed: {ex.Message}"); }
            finally { try { File.Delete(temp); } catch { } }
            return results;
        }

        private static List<(string name, string value)> ReadFirefoxCookies(string cookiesDb, string domain)
        {
            var results = new List<(string, string)>();
            string temp = Path.Combine(Path.GetTempPath(), $"TaskbarQuota_ff_cookies_{Guid.NewGuid():N}.db");
            try
            {
                CopyPossiblyLockedFile(cookiesDb, temp);
                using var conn = new SqliteConnection($"Data Source={temp};Mode=ReadOnly;Cache=Private");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT name, value, host FROM moz_cookies WHERE host LIKE $a OR host LIKE $b OR host LIKE $c";
                cmd.Parameters.AddWithValue("$a", domain);
                cmd.Parameters.AddWithValue("$b", "." + domain);
                cmd.Parameters.AddWithValue("$c", "%" + domain);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(0);
                    string value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (!string.IsNullOrEmpty(name))
                        results.Add((name, value));
                }
            }
            catch (Exception ex) { Log.Debug($"firefox cookie db read failed: {ex.Message}"); }
            finally { try { File.Delete(temp); } catch { } }
            return results;
        }

        private static void CopyPossiblyLockedFile(string source, string destination)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
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
            // Legacy DPAPI-encrypted value (pre-v10)
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
    }
}
