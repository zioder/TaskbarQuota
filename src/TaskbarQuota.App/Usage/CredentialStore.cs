using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TaskbarQuota;

namespace TaskbarQuota.Usage
{
    /// <summary>
    /// User-provided credentials (API keys, manual cookie headers) stored as JSON in
    /// %LOCALAPPDATA%\TaskbarQuota\credentials.json (migrated from WinCheck on first run).
    /// Falls back to environment variables for API keys.
    /// Keyed by lowercased provider id, e.g. {"zai":{"apiKey":"..."},"cursor":{"cookieHeader":"..."}}.
    /// </summary>
    public sealed class CredentialStore
    {
        public static CredentialStore Instance { get; } = new();

        private static readonly string Dir = AppStorage.AppDataDirectory;
        private static readonly string Path_ = Path.Combine(Dir, "credentials.json");

        private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public sealed class Entry
        {
            public string? ApiKey { get; set; }
            public string? CookieHeader { get; set; }
            public string? Extra { get; set; } // provider-specific (e.g. MiniMax group id)
        }

        private CredentialStore() => Load();

        public Entry For(ProviderId id)
        {
            var k = id.ToString().ToLowerInvariant();
            if (!_entries.TryGetValue(k, out var e)) { e = new Entry(); _entries[k] = e; }
            return e;
        }

        /// <summary>API key from the store, else the first non-empty environment variable.</summary>
        public string? ApiKey(ProviderId id, params string[] envNames)
        {
            var fromStore = For(id).ApiKey;
            if (!string.IsNullOrWhiteSpace(fromStore)) return fromStore!.Trim();
            foreach (var name in envNames)
            {
                var v = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
            }
            return null;
        }

        public string? Extra(ProviderId id, params string[] envNames)
        {
            var fromStore = For(id).Extra;
            if (!string.IsNullOrWhiteSpace(fromStore)) return fromStore!.Trim();
            foreach (var name in envNames)
            {
                var v = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
            }
            return null;
        }

        public string? ManualCookieHeader(ProviderId id)
        {
            var v = For(id).CookieHeader;
            return string.IsNullOrWhiteSpace(v) ? null : v!.Trim();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path_, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Diagnostics.Log.Warning(ex, "Failed to save credentials"); }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(Path_))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(Path_));
                    if (d != null) _entries = new Dictionary<string, Entry>(d, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) { Diagnostics.Log.Warning(ex, "Failed to load credentials"); }
        }
    }
}
