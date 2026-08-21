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

        private readonly string _directory;
        private readonly string _path;
        private readonly string _backupPath;

        private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public sealed class Entry
        {
            public string? ApiKey { get; set; }
            public string? CookieHeader { get; set; }
            public string? WorkspaceId { get; set; }
            public string? Extra { get; set; } // provider-specific (e.g. MiniMax group id)
        }

        private CredentialStore() : this(AppStorage.AppDataDirectory) { }

        internal CredentialStore(string directory)
        {
            _directory = directory;
            _path = Path.Combine(directory, "credentials.json");
            _backupPath = _path + ".bak";
            Load();
        }

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

        public string? WorkspaceId(ProviderId id)
        {
            var value = For(id).WorkspaceId;
            return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        public void Save()
        {
            string tempPath = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(_directory);
                File.WriteAllText(tempPath, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));

                if (File.Exists(_path))
                    File.Replace(tempPath, _path, _backupPath, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, _path);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to save credentials");
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private void Load()
        {
            if (TryLoad(_path))
                return;

            if (TryLoad(_backupPath))
                Diagnostics.Log.Warning("Recovered credentials from backup after the primary store could not be loaded.");
        }

        private bool TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (loaded is null)
                    return false;

                _entries = new Dictionary<string, Entry>(loaded, StringComparer.OrdinalIgnoreCase);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, $"Failed to load credential store '{Path.GetFileName(path)}'");
                return false;
            }
        }
    }
}
