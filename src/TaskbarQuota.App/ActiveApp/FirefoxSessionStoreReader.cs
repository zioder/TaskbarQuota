using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TaskbarQuota.ActiveApp
{
    internal static class FirefoxSessionStoreReader
    {
        private static readonly byte[] MozLz4Magic = [0x6D, 0x6F, 0x7A, 0x4C, 0x7A, 0x34, 0x30, 0x00]; // mozLz40\0
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(500);
        private static readonly object CacheLock = new();
        private static DateTime _cacheAtUtc = DateTime.MinValue;
        private static string? _cacheProcessName;
        private static string? _cacheUrl;

        internal static string? TryReadSelectedTabUrl(string? processName)
        {
            if (!IsSupportedFirefoxFamily(processName))
                return null;

            var normalized = NormalizeProcessName(processName!);
            lock (CacheLock)
            {
                if (_cacheProcessName == normalized && DateTime.UtcNow - _cacheAtUtc < CacheTtl)
                    return _cacheUrl;
            }

            var url = TryReadSelectedTabUrlCore(normalized);
            lock (CacheLock)
            {
                _cacheProcessName = normalized;
                _cacheUrl = url;
                _cacheAtUtc = DateTime.UtcNow;
            }
            return url;
        }

        private static bool IsSupportedFirefoxFamily(string? processName)
            => !string.IsNullOrWhiteSpace(processName)
            && NormalizeProcessName(processName) is "zen" or "firefox";

        private static string? TryReadSelectedTabUrlCore(string processName)
        {
            try
            {
                foreach (var sessionPath in EnumerateSessionFiles(processName))
                {
                    var json = TryReadMozLz4Json(sessionPath);
                    if (json is null)
                        continue;

                    if (TryExtractSelectedTabUrl(json) is { } url)
                        return url;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"[browser] Firefox session read failed: {ex.Message}");
            }

            return null;
        }

        private static IEnumerable<string> EnumerateSessionFiles(string processName)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var root = processName == "zen"
                ? Path.Combine(appData, "zen", "Profiles")
                : Path.Combine(appData, "Mozilla", "Firefox", "Profiles");

            if (!Directory.Exists(root))
                yield break;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "recovery.jsonlz4", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(root, "sessionstore.jsonlz4", SearchOption.AllDirectories))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(6)
                    .ToArray();
            }
            catch
            {
                yield break;
            }

            foreach (var file in files)
                yield return file;
        }

        internal static string? TryReadMozLz4Json(string path)
        {
            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length <= MozLz4Magic.Length || !bytes.AsSpan(0, MozLz4Magic.Length).SequenceEqual(MozLz4Magic))
                return null;

            var decompressed = DecompressLz4Block(bytes.AsSpan(MozLz4Magic.Length));
            return Encoding.UTF8.GetString(decompressed);
        }

        internal static byte[] DecompressLz4Block(ReadOnlySpan<byte> input)
        {
            var output = new List<byte>(input.Length * 3);
            var i = 0;
            while (i < input.Length)
            {
                var token = input[i++];
                var literalLength = token >> 4;
                if (literalLength == 15)
                    literalLength += ReadExtendedLength(input, ref i);

                if (i + literalLength > input.Length)
                    throw new InvalidDataException("Invalid LZ4 literal length.");
                for (var n = 0; n < literalLength; n++)
                    output.Add(input[i++]);

                if (i >= input.Length)
                    break;

                if (i + 2 > input.Length)
                    throw new InvalidDataException("Invalid LZ4 offset.");
                var offset = input[i] | (input[i + 1] << 8);
                i += 2;
                if (offset <= 0 || offset > output.Count)
                    throw new InvalidDataException("Invalid LZ4 match offset.");

                var matchLength = (token & 0x0F) + 4;
                if ((token & 0x0F) == 15)
                    matchLength += ReadExtendedLength(input, ref i);

                var start = output.Count - offset;
                for (var n = 0; n < matchLength; n++)
                    output.Add(output[start + n]);
            }

            return output.ToArray();
        }

        private static int ReadExtendedLength(ReadOnlySpan<byte> input, ref int i)
        {
            var length = 0;
            while (i < input.Length)
            {
                var b = input[i++];
                length += b;
                if (b != 255)
                    return length;
            }
            throw new InvalidDataException("Invalid LZ4 extended length.");
        }

        internal static string? TryExtractSelectedTabUrl(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() == 0)
                return null;

            var selectedWindowIndex = ReadOneBasedIndex(root, "selectedWindow", windows.GetArrayLength()) ?? 0;
            if (selectedWindowIndex < 0 || selectedWindowIndex >= windows.GetArrayLength())
                selectedWindowIndex = 0;

            return TryExtractSelectedTabUrl(windows[selectedWindowIndex]);
        }

        private static string? TryExtractSelectedTabUrl(JsonElement window)
        {
            if (!window.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array || tabs.GetArrayLength() == 0)
                return null;

            var selectedTabIndex = ReadOneBasedIndex(window, "selected", tabs.GetArrayLength()) ?? 0;
            if (selectedTabIndex < 0 || selectedTabIndex >= tabs.GetArrayLength())
                selectedTabIndex = 0;

            var tab = tabs[selectedTabIndex];
            if (!tab.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
                return null;

            var entryIndex = ReadOneBasedIndex(tab, "index", entries.GetArrayLength()) ?? entries.GetArrayLength() - 1;
            if (entryIndex < 0 || entryIndex >= entries.GetArrayLength())
                entryIndex = entries.GetArrayLength() - 1;

            var entry = entries[entryIndex];
            return entry.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : null;
        }

        private static int? ReadOneBasedIndex(JsonElement owner, string propertyName, int count)
        {
            if (!owner.TryGetProperty(propertyName, out var prop) || !prop.TryGetInt32(out var raw))
                return null;
            var index = raw - 1;
            return index >= 0 && index < count ? index : null;
        }

        private static string NormalizeProcessName(string processName)
            => processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4].ToLowerInvariant()
                : processName.ToLowerInvariant();
    }
}
