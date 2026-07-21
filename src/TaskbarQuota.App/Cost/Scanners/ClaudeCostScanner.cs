using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Cost.Scanners
{
    /// <summary>
    /// Reads Claude Code session logs at <c>~/.claude/projects/**/*.jsonl</c>. Each assistant line
    /// carries <c>message.usage.{input_tokens, output_tokens, cache_creation_input_tokens,
    /// cache_read_input_tokens}</c> and <c>message.model</c>. Lines are de-duplicated by
    /// <c>message.id</c>+<c>requestId</c> (Claude Code emits a request twice — streaming + final —
    /// with identical counts), matching how <c>ccusage</c> avoids double-counting.
    /// </summary>
    public sealed class ClaudeCostScanner : ICostScanner
    {
        public ProviderId Provider => ProviderId.Claude;

        private readonly string _root;

        public ClaudeCostScanner(string? root = null)
        {
            _root = root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
        }

        public IEnumerable<TokenUsageRecord> Scan(DateTimeOffset sinceUtc)
        {
            if (!Directory.Exists(_root))
                yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in EnumerateLogs())
            {
                // A file untouched since before the window can't contain in-window rows.
                DateTime lastWrite;
                try { lastWrite = File.GetLastWriteTimeUtc(file); }
                catch { continue; }
                if (lastWrite < sinceUtc.UtcDateTime.AddMinutes(-5))
                    continue;

                foreach (var record in ScanFile(file, sinceUtc, seen))
                    yield return record;
            }
        }

        private IEnumerable<string> EnumerateLogs()
        {
            IEnumerator<string> e;
            try { e = Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).GetEnumerator(); }
            catch (Exception ex) { Log.Warning(ex, "ClaudeCostScanner enumerate failed"); yield break; }
            using (e)
            {
                while (true)
                {
                    try { if (!e.MoveNext()) break; }
                    catch { break; }
                    yield return e.Current;
                }
            }
        }

        private IEnumerable<TokenUsageRecord> ScanFile(string file, DateTimeOffset sinceUtc, HashSet<string> seen)
        {
            IEnumerable<string> lines;
            try { lines = ReadLines(file); }
            catch (Exception ex) { Log.Warning(ex, $"ClaudeCostScanner open {file}"); yield break; }

            foreach (var line in lines)
            {
                if (line.Length < 2) continue;
                // Cheap pre-filter: only assistant usage lines matter.
                if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;

                TokenUsageRecord? record = TryParse(line, sinceUtc, seen);
                if (record is not null)
                    yield return record;
            }
        }

        private static IEnumerable<string> ReadLines(string file)
        {
            // Share-read: the CLI holds the file open and appends while we read.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                yield return line;
        }

        private static TokenUsageRecord? TryParse(string line, DateTimeOffset sinceUtc, HashSet<string> seen)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("usage", out var usage) ||
                    usage.ValueKind != JsonValueKind.Object)
                    return null;

                if (!root.TryGetProperty("timestamp", out var tsEl) ||
                    !DateTimeOffset.TryParse(tsEl.GetString(), out var ts))
                    return null;
                if (ts < sinceUtc)
                    return null;

                // Dedup identical retries/streaming duplicates.
                string msgId = message.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                string reqId = root.TryGetProperty("requestId", out var rEl) ? rEl.GetString() ?? "" : "";
                if (msgId.Length > 0 || reqId.Length > 0)
                {
                    if (!seen.Add(msgId + ":" + reqId))
                        return null;
                }

                string model = message.TryGetProperty("model", out var mEl) ? mEl.GetString() ?? "" : "";

                return new TokenUsageRecord
                {
                    Provider = ProviderId.Claude,
                    RawModel = model,
                    Timestamp = ts,
                    InputTokens = GetLong(usage, "input_tokens"),
                    OutputTokens = GetLong(usage, "output_tokens"),
                    CacheReadTokens = GetLong(usage, "cache_read_input_tokens"),
                    CacheWriteTokens = GetLong(usage, "cache_creation_input_tokens"),
                };
            }
            catch (JsonException)
            {
                return null; // partial line at EOF while the CLI is mid-write
            }
        }

        private static long GetLong(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }
}
