using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota.Usage
{
    /// <summary>
    /// Reads local usage histories by parsing provider-owned local records, aggregating by local
    /// calendar day, and keeping the estimate explicitly marked.
    /// A missing or malformed local source is treated as no history, never as fake usage.
    /// </summary>
    internal static class UsageHistoryService
    {
        private sealed record HistoryCacheEntry(
            DateTime LocalDay,
            int FileCount,
            long TotalLength,
            long LatestWriteTicks,
            int PathHash,
            UsageHistory History);

        private static readonly object CacheLock = new();
        private static readonly Dictionary<ProviderId, HistoryCacheEntry> Cache = new();

        internal readonly record struct FileVersion(long Length, long WriteTicks, long WalLength, long WalWriteTicks);

        private sealed record FileEventsEntry(DateTime LocalDay, FileVersion Version, UsageEvent[] Events);

        private static readonly Dictionary<(ProviderId Provider, string Path), FileEventsEntry> FileEvents = new();
        private static readonly Dictionary<ProviderId, object> ProviderLocks =
            Enum.GetValues<ProviderId>().ToDictionary(id => id, _ => new object());

        private readonly record struct UsageEvent(
            DateTimeOffset Timestamp,
            string Model,
            TokenBreakdown Tokens,
            double? ReportedCostUsd,
            string SessionId,
            string? DedupeKey);

        public static bool TryLoad(ProviderId providerId, out UsageHistory history)
        {
            lock (ProviderLocks[providerId])
                return TryLoadCore(providerId, out history);
        }

        private static bool TryLoadCore(ProviderId providerId, out UsageHistory history)
        {
            var files = DiscoverFiles(providerId).ToArray();
            if (files.Length == 0)
            {
                PruneFileEvents(providerId, files);
                history = new UsageHistory();
                return false;
            }

            var fingerprint = Fingerprint(files);
            lock (CacheLock)
            {
                if (Cache.TryGetValue(providerId, out var cached)
                    && cached.LocalDay == DateTime.Today
                    && cached.FileCount == fingerprint.FileCount
                    && cached.TotalLength == fingerprint.TotalLength
                    && cached.LatestWriteTicks == fingerprint.LatestWriteTicks
                    && cached.PathHash == fingerprint.PathHash)
                {
                    history = cached.History;
                    return history.Last90Days is not null;
                }
            }

            var now = DateTimeOffset.Now;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var events = new List<UsageEvent>();
            foreach (var file in files)
            {
                try
                {
                    events.AddRange(ParseFileCached(providerId, file, now));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
                catch (SqliteException) { }
                catch (InvalidOperationException) { }
            }
            PruneFileEvents(providerId, files);

            history = Aggregate(DropSupersededEvents(providerId, events), now, SourceNote(providerId), providerId);
            var loaded = history.Last90Days is not null;
            lock (CacheLock)
                Cache[providerId] = new HistoryCacheEntry(DateTime.Today, fingerprint.FileCount, fingerprint.TotalLength, fingerprint.LatestWriteTicks, fingerprint.PathHash, history);
            Log.Information($"[history] provider={providerId} files={files.Length} events={events.Count} today={history.Today?.Tokens ?? 0} last90={history.Last90Days?.Tokens ?? 0} loaded={loaded} elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
            return loaded;
        }

        private static UsageEvent[] ParseFileCached(ProviderId providerId, string path, DateTimeOffset now)
        {
            // Codex and OpenCode Go read the same session logs, so both share one unfiltered parse.
            bool codexLog = providerId is ProviderId.Codex or ProviderId.OpenCodeGo
                && Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase);
            var events = ReadFileEventsCached(codexLog ? ProviderId.Codex : providerId, path, now, codexLog);
            if (!codexLog)
                return events;

            bool openCodeGo = providerId == ProviderId.OpenCodeGo;
            return events.Where(item => IsOpenCodeGoModel(item.Model) == openCodeGo).ToArray();
        }

        private static UsageEvent[] ReadFileEventsCached(ProviderId cacheProvider, string path, DateTimeOffset now, bool codexLog)
        {
            var version = ReadFileVersion(path);
            var key = (cacheProvider, path);
            lock (CacheLock)
            {
                if (FileEvents.TryGetValue(key, out var cached)
                    && cached.LocalDay == DateTime.Today
                    && cached.Version == version)
                {
                    return cached.Events;
                }
            }

            var events = codexLog
                ? ParseCodex(ReadSharedLines(path)).ToArray()
                : ParseFile(cacheProvider, path, now).ToArray();
            lock (CacheLock)
                FileEvents[key] = new FileEventsEntry(DateTime.Today, version, events);
            return events;
        }

        internal static FileVersion ReadFileVersion(string path)
        {
            var info = new FileInfo(path);
            // SQLite in WAL mode appends to the -wal companion and leaves the main file unchanged until a checkpoint.
            var wal = new FileInfo(path + "-wal");
            return new FileVersion(
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                wal.Exists ? wal.Length : 0,
                wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0);
        }

        private static void PruneFileEvents(ProviderId providerId, IReadOnlyCollection<string> files)
        {
            var current = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            lock (CacheLock)
            {
                foreach (var key in FileEvents.Keys.Where(k => k.Provider == providerId && !current.Contains(k.Path)).ToList())
                    FileEvents.Remove(key);

                // OpenCode Go parses Codex-style sessions into the shared Codex cache. It may be
                // the only provider queried, so its file discovery must also evict deleted logs.
                if (providerId == ProviderId.OpenCodeGo)
                {
                    foreach (var key in FileEvents.Keys.Where(k => k.Provider == ProviderId.Codex
                        && Path.GetExtension(k.Path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                        && !current.Contains(k.Path)).ToList())
                        FileEvents.Remove(key);
                }
            }
        }

        private static (int FileCount, long TotalLength, long LatestWriteTicks, int PathHash) Fingerprint(IEnumerable<string> files)
        {
            var count = 0;
            long length = 0;
            long latest = 0;
            var pathHash = new HashCode();
            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    count++;
                    length += info.Length;
                    latest = Math.Max(latest, info.LastWriteTimeUtc.Ticks);
                    pathHash.Add(file, StringComparer.OrdinalIgnoreCase);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return (count, length, latest, pathHash.ToHashCode());
        }

        internal static UsageHistory BuildFromLines(
            ProviderId providerId,
            IEnumerable<string> lines,
            DateTimeOffset now)
        {
            var events = ParseEvents(providerId, lines);

            return Aggregate(events, now, SourceNote(providerId), providerId);
        }

        internal static UsageHistory BuildFromFilesForTesting(
            ProviderId providerId,
            IEnumerable<string> files,
            DateTimeOffset now)
        {
            var events = files.SelectMany(file => ParseFile(providerId, file, now)).ToList();
            return Aggregate(DropSupersededEvents(providerId, events), now, SourceNote(providerId), providerId);
        }

        /// <summary>
        /// Grok Build's per-session <c>updates.jsonl</c> reports billed usage per turn; the legacy
        /// shell log (<c>logs/unified.jsonl</c>) recorded per-inference token counts for the same
        /// sessions. Where a session has session-log usage, its legacy events are dropped.
        /// </summary>
        private static List<UsageEvent> DropSupersededEvents(ProviderId providerId, List<UsageEvent> events)
        {
            if (providerId != ProviderId.Grok)
                return events;
            var reported = events
                .Where(item => item.DedupeKey?.StartsWith(GrokSessionKeyPrefix, StringComparison.Ordinal) == true)
                .Select(item => item.SessionId)
                .ToHashSet(StringComparer.Ordinal);
            return reported.Count == 0
                ? events
                : events.Where(item => item.DedupeKey?.StartsWith(GrokSessionKeyPrefix, StringComparison.Ordinal) == true
                    || !reported.Contains(item.SessionId)).ToList();
        }

        internal static UsageHistory BuildFromCursorDashboardEvents(
            IEnumerable<CursorDashboardUsageEvent> dashboardEvents,
            DateTimeOffset now)
        {
            var index = 0;
            var events = dashboardEvents.Where(item => item.Tokens.TotalTokens > 0).Select(item => new UsageEvent(
                item.Timestamp,
                string.IsNullOrWhiteSpace(item.Model) ? "cursor-unknown" : item.Model,
                item.Tokens,
                item.ReportedCostUsd,
                item.SessionId ?? string.Empty,
                $"cursor-dashboard:{index++}"));
            return Aggregate(events, now, "Cursor dashboard reported tokens and API-rate costs", ProviderId.Cursor);
        }

        private static IEnumerable<UsageEvent> ParseFile(ProviderId providerId, string path, DateTimeOffset now)
            => providerId switch
            {
                ProviderId.Antigravity when Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase)
                    => AntigravityUsageDatabaseReader.Read(path).Select(item => new UsageEvent(
                        item.Timestamp, item.Model, item.Tokens, null, item.SessionId, item.DedupeKey)),
                ProviderId.OpenCode or ProviderId.OpenCodeGo when Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase)
                    => ParseOpenCodeDatabase(path, providerId, now),
                ProviderId.OpenCodeGo when Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                    => ParseCodex(ReadSharedLines(path)).Where(item => IsOpenCodeGoModel(item.Model)),
                ProviderId.Cline or ProviderId.ClinePass when path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith(".messages.json", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith(".compaction.json", StringComparison.OrdinalIgnoreCase)
                    => ParseClineSession(path, providerId),
                ProviderId.Zai when Path.GetExtension(path).Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
                    || providerId == ProviderId.Zai && Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase)
                    => ParseZaiDatabase(path, now),
                ProviderId.Codex when Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                    => ParseCodex(ReadSharedLines(path)).Where(item => !IsOpenCodeGoModel(item.Model)),
                ProviderId.Grok when Path.GetFileName(path).Equals("updates.jsonl", StringComparison.OrdinalIgnoreCase)
                    => ParseGrokSessionUpdates(ReadSharedLines(path)),
                ProviderId.Claude or ProviderId.Grok
                    => ParseEvents(providerId, ReadSharedLines(path)),
                _ => Array.Empty<UsageEvent>(),
            };

        private static List<UsageEvent> ParseEvents(ProviderId providerId, IEnumerable<string> lines)
            => providerId switch
            {
                ProviderId.Codex => ParseCodex(lines),
                ProviderId.Claude => ParseClaude(lines),
                ProviderId.Grok => ParseGrok(lines),
                _ => new List<UsageEvent>(),
            };

        private static IEnumerable<string> DiscoverFiles(ProviderId providerId)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                yield break;

            if (providerId == ProviderId.Cursor)
            {
                // Cursor history is account-wide and comes from its authenticated dashboard API.
                // A context-window snapshot in state.vscdb is not cumulative token usage.
                yield break;
            }

            if (providerId == ProviderId.Antigravity)
            {
                var antigravitySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in AntigravityConversationRoots(home))
                {
                    if (!Directory.Exists(root))
                        continue;

                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(root, "*.db", SearchOption.TopDirectoryOnly); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }

                    foreach (var file in files
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        if (antigravitySeen.Add(file))
                        {
                            yield return file;
                            foreach (var companion in SqliteCompanions(file).Where(File.Exists))
                                if (antigravitySeen.Add(companion))
                                    yield return companion;
                        }
                    }
                }
                yield break;
            }

            if (providerId is ProviderId.OpenCode or ProviderId.OpenCodeGo)
            {
                var openCodeSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in OpenCodeDatabaseCandidates(home).Where(File.Exists))
                {
                    if (!openCodeSeen.Add(path))
                        continue;
                    yield return path;
                    foreach (var companion in SqliteCompanions(path).Where(File.Exists))
                        if (openCodeSeen.Add(companion))
                            yield return companion;
                }

                if (providerId == ProviderId.OpenCodeGo)
                {
                    // T3 Code runs Open Code Go sessions inside a Codex-style harness and writes them
                    // to the Codex session store. Route those transcripts in here so Go usage is counted
                    // against Open Code Go, not against Codex (Codex excludes the opencode-go models).
                    foreach (var file in CodexSessionFiles(home, openCodeSeen))
                        yield return file;
                }
                yield break;
            }

            if (providerId == ProviderId.Zai)
            {
                var configured = Environment.GetEnvironmentVariable("ZCODE_DB_PATH");
                var path = string.IsNullOrWhiteSpace(configured)
                    ? Path.Combine(home, ".zcode", "cli", "db", "db.sqlite")
                    : configured.Trim();
                if (File.Exists(path))
                {
                    yield return path;
                    foreach (var companion in SqliteCompanions(path).Where(File.Exists))
                        yield return companion;
                }
                yield break;
            }

            if (providerId is ProviderId.Cline or ProviderId.ClinePass)
            {
                var configured = Environment.GetEnvironmentVariable("CLINE_DATA_DIR");
                var root = string.IsNullOrWhiteSpace(configured)
                    ? Path.Combine(home, ".cline", "data")
                    : configured.Trim();
                var sessions = Path.Combine(root, "sessions");
                if (!Directory.Exists(sessions))
                    yield break;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(sessions, "*.json", SearchOption.AllDirectories); }
                catch (IOException) { yield break; }
                catch (UnauthorizedAccessException) { yield break; }
                var oldestUsefulWrite = DateTime.UtcNow.AddDays(-91);
                foreach (var file in files
                    .Where(path => LastWriteUtc(path) >= oldestUsefulWrite)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    yield return file;
                yield break;
            }

            var roots = providerId switch
            {
                ProviderId.Codex => CodexRoots(home),
                ProviderId.Claude => new[]
                {
                    Path.Combine(home, ".claude", "projects"),
                    Path.Combine(home, ".config", "claude", "projects"),
                },
                ProviderId.Grok => new[] { Path.Combine(GrokHome(home), "logs") },
                _ => Array.Empty<string>(),
            };

            if (providerId == ProviderId.Grok)
            {
                // Grok Build writes one updates.jsonl per session under sessions/<cwd>/<id>/. The
                // sibling chat_history and events logs are large and never carry usage.
                var sessions = Path.Combine(GrokHome(home), "sessions");
                if (Directory.Exists(sessions))
                {
                    IEnumerable<string> updates;
                    try { updates = Directory.EnumerateFiles(sessions, "updates.jsonl", SearchOption.AllDirectories).ToList(); }
                    catch (IOException) { updates = Array.Empty<string>(); }
                    catch (UnauthorizedAccessException) { updates = Array.Empty<string>(); }
                    var oldestUsefulWrite = DateTime.UtcNow.AddDays(-91);
                    foreach (var file in updates
                        .Where(path => LastWriteUtc(path) >= oldestUsefulWrite)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                        yield return file;
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (File.Exists(root) && seen.Add(root))
                {
                    yield return root;
                    continue;
                }

                if (!Directory.Exists(root))
                    continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var oldestUsefulWrite = DateTime.UtcNow.AddDays(-91);
                foreach (var file in files
                    .Where(path => LastWriteUtc(path) >= oldestUsefulWrite)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (seen.Add(file))
                        yield return file;
                }
            }

            if (providerId == ProviderId.Grok)
            {
                var unified = Path.Combine(GrokHome(home), "logs", "unified.jsonl");
                if (File.Exists(unified) && seen.Add(unified))
                    yield return unified;
            }
        }

        private static IEnumerable<string> OpenCodeDatabaseCandidates(string home)
        {
            var configured = Environment.GetEnvironmentVariable("OPENCODE_DB_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
                yield return configured.Trim();

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            yield return Path.Combine(localAppData, "opencode", "opencode.db");
            yield return Path.Combine(appData, "opencode", "opencode.db");
            yield return Path.Combine(home, ".local", "share", "opencode", "opencode.db");
            if (!string.IsNullOrWhiteSpace(xdgData))
                yield return Path.Combine(xdgData.Trim(), "opencode", "opencode.db");
        }

        private static IEnumerable<string> CodexSessionFiles(string home, HashSet<string> seen)
        {
            foreach (var root in CodexRoots(home))
            {
                if (File.Exists(root) && seen.Add(root))
                {
                    yield return root;
                    continue;
                }

                if (!Directory.Exists(root))
                    continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var oldestUsefulWrite = DateTime.UtcNow.AddDays(-91);
                foreach (var file in files
                    .Where(path => LastWriteUtc(path) >= oldestUsefulWrite)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (seen.Add(file))
                        yield return file;
                }
            }
        }

        private static IEnumerable<string> SqliteCompanions(string path)
        {
            yield return path + "-wal";
            yield return path + "-shm";
        }

        private static DateTime LastWriteUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (IOException) { return DateTime.MinValue; }
            catch (UnauthorizedAccessException) { return DateTime.MinValue; }
        }

        private static string[] CodexRoots(string home)
        {
            var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var codexHome = string.IsNullOrWhiteSpace(configuredHome)
                ? Path.Combine(home, ".codex")
                : configuredHome.Trim();
            return new[]
            {
                Path.Combine(codexHome, "sessions"),
                Path.Combine(codexHome, "archived_sessions"),
            };
        }

        private static IEnumerable<string> AntigravityConversationRoots(string home)
        {
            var guiHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_GUI_HOME");
            var guiRoot = string.IsNullOrWhiteSpace(guiHome)
                ? Path.Combine(home, ".gemini", "antigravity")
                : guiHome.Trim();
            yield return guiRoot;
            yield return Path.Combine(guiRoot, "conversations");

            var cliHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_HOME");
            var cliRoot = string.IsNullOrWhiteSpace(cliHome)
                ? Path.Combine(home, ".gemini", "antigravity-cli")
                : cliHome.Trim();
            yield return cliRoot;
            yield return Path.Combine(cliRoot, "conversations");
        }

        private static bool SqliteTableExists(SqliteConnection connection, string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
            command.Parameters.AddWithValue("$name", name);
            return command.ExecuteScalar() is not null;
        }

        private static ulong ReadJsonUInt64(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetUInt64(out var number))
                    return number;
                if (value.TryGetDouble(out var real) && real > 0)
                    return (ulong)real;
            }
            if (value.ValueKind == JsonValueKind.String
                && ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0;
        }

        // Usage records are under 200 KB; longer lines carry embedded images and can reach tens of MB.
        internal const int MaxHistoryLineBytes = 1024 * 1024;

        private static IEnumerable<string> ReadSharedLines(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            foreach (var line in ReadBoundedLines(stream, MaxHistoryLineBytes))
                yield return line;
        }

        internal static IEnumerable<string> ReadBoundedLines(Stream stream, int maxLineBytes)
        {
            var buffer = new byte[64 * 1024];
            var line = new MemoryStream();
            bool skipping = false;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                        continue;

                    if (!skipping && line.Length + (i - start) <= maxLineBytes)
                    {
                        line.Write(buffer, start, i - start);
                        yield return DecodeLine(line);
                    }
                    line.SetLength(0);
                    skipping = false;
                    start = i + 1;
                }

                if (skipping)
                    continue;
                if (line.Length + (read - start) > maxLineBytes)
                {
                    line.SetLength(0);
                    skipping = true;
                }
                else
                {
                    line.Write(buffer, start, read - start);
                }
            }

            if (!skipping && line.Length > 0)
                yield return DecodeLine(line);
        }

        private static string DecodeLine(MemoryStream line)
        {
            var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
            if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                bytes = bytes[..^1];
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                bytes = bytes[3..];
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        private static string ReadSharedText(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static List<UsageEvent> ParseCodex(IEnumerable<string> lines)
        {
            var events = new List<UsageEvent>();
            string? currentModel = null;
            string sessionId = string.Empty;
            string? previousSignature = null;

            foreach (var line in lines)
            {
                if (!line.Contains("token_count", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("turn_context", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("session_meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryDocument(line, out var root))
                    continue;

                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "session_meta"
                    && root.TryGetProperty("payload", out var sessionPayload))
                {
                    sessionId = TryFindString(sessionPayload, "id")
                        ?? TryFindString(sessionPayload, "session_id")
                        ?? sessionId;
                    continue;
                }

                if (root.TryGetProperty("type", out type)
                    && type.GetString() == "turn_context"
                    && TryFindModel(root, out var contextModel))
                {
                    currentModel = contextModel;
                    continue;
                }

                if (!root.TryGetProperty("type", out type)
                    || type.GetString() != "event_msg"
                    || !root.TryGetProperty("timestamp", out var timestampValue)
                    || timestampValue.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(
                        timestampValue.GetString()?.Trim(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var timestamp)
                    || !root.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object
                    || !payload.TryGetProperty("type", out var payloadType)
                    || payloadType.GetString() != "token_count"
                    || !payload.TryGetProperty("info", out var info))
                    continue;

                if (!TryFindProperty(info, "last_token_usage", out var last))
                    continue;

                var signature = last.GetRawText();
                if (signature == previousSignature)
                    continue;
                previousSignature = signature;

                var usage = ReadUsage(last);
                if (usage.EffectiveTotal == 0)
                    continue;

                if (TryFindModel(payload, out var model))
                    currentModel = model;
                events.Add(new UsageEvent(
                    timestamp,
                    currentModel ?? "gpt-5",
                    usage.ToTokens(inputIncludesCache: true),
                    null,
                    sessionId,
                    null));
            }

            return events;
        }

        private static List<UsageEvent> ParseClaude(IEnumerable<string> lines)
        {
            var events = new List<UsageEvent>();
            foreach (var line in lines)
            {
                if (!line.Contains("\"usage\"", StringComparison.OrdinalIgnoreCase)
                    || !TryDocument(line, out var root)
                    || !TryFindTimestamp(root, out var timestamp)
                    || !TryFindProperty(root, "message", out var message)
                    || !TryFindProperty(message, "usage", out var usage))
                    continue;

                var recordType = TryFindString(root, "type");
                if (recordType is not null && recordType != "assistant")
                    continue;

                var tokens = ReadUsage(usage).ToTokens(inputIncludesCache: false);
                if (tokens.TotalTokens == 0)
                    continue;

                var model = TryFindModel(message, out var parsedModel) ? parsedModel : "claude-unknown";
                double? reportedCost = TryFindNumber(root, "costUSD", out var cost) ? cost : null;
                var messageId = TryFindString(message, "id");
                var requestId = TryFindString(root, "requestId");
                var dedupeKey = messageId is null && requestId is null
                    ? null
                    : $"{messageId ?? string.Empty}:{requestId ?? string.Empty}";
                events.Add(new UsageEvent(
                    timestamp,
                    model,
                    tokens,
                    reportedCost,
                    TryFindString(root, "sessionId") ?? string.Empty,
                    dedupeKey));
            }

            return events;
        }

        private const string GrokSessionKeyPrefix = "grok-session:";

        /// <summary>Grok reports cost in integer ticks: 1 USD = 10^10 ticks.</summary>
        private const double GrokCostTicksPerDollar = 10_000_000_000d;

        private static string GrokHome(string home)
        {
            var configured = Environment.GetEnvironmentVariable("GROK_HOME");
            return string.IsNullOrWhiteSpace(configured) ? Path.Combine(home, ".grok") : configured.Trim();
        }

        /// <summary>
        /// Parses a Grok Build session log (<c>sessions/&lt;cwd&gt;/&lt;id&gt;/updates.jsonl</c>). Usage
        /// lands on <c>turn_completed</c> updates with the billed cost in <c>costUsdTicks</c>;
        /// <c>usage.modelUsage</c> splits a turn by model. Models without their own ticks share
        /// the turn's remaining cost by token count.
        /// </summary>
        private static List<UsageEvent> ParseGrokSessionUpdates(IEnumerable<string> lines)
        {
            var events = new List<UsageEvent>();
            foreach (var line in lines)
            {
                if (!line.Contains("\"turn_completed\"", StringComparison.Ordinal)
                    || !TryDocument(line, out var root)
                    || !root.TryGetProperty("params", out var parameters)
                    || parameters.ValueKind != JsonValueKind.Object
                    || !parameters.TryGetProperty("update", out var update)
                    || update.ValueKind != JsonValueKind.Object
                    || ReadDirectString(update, "sessionUpdate") != "turn_completed"
                    || !update.TryGetProperty("usage", out var usage)
                    || usage.ValueKind != JsonValueKind.Object)
                    continue;

                DateTimeOffset timestamp;
                if (parameters.TryGetProperty("_meta", out var meta) && ReadDirectInt64(meta, "agentTimestampMs") is { } agentMs)
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(agentMs);
                else if (ReadDirectInt64(root, "timestamp") is { } seconds)
                    timestamp = seconds > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(seconds) : DateTimeOffset.FromUnixTimeSeconds(seconds);
                else
                    continue;

                var sessionId = ReadDirectString(parameters, "sessionId") ?? string.Empty;
                var turnKey = ReadDirectString(update, "prompt_id") ?? timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                var turnCost = GrokCostUsd(usage);

                var models = new List<(string Model, TokenBreakdown Tokens, double? Cost)>();
                if (usage.TryGetProperty("modelUsage", out var modelUsage) && modelUsage.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in modelUsage.EnumerateObject())
                    {
                        if (entry.Name.Length == 0 || entry.Value.ValueKind != JsonValueKind.Object)
                            continue;
                        var tokens = GrokTokens(entry.Value);
                        if (tokens.TotalTokens > 0)
                            models.Add((entry.Name, tokens, GrokCostUsd(entry.Value)));
                    }
                }

                if (models.Count == 0)
                {
                    var tokens = GrokTokens(usage);
                    if (tokens.TotalTokens > 0)
                        events.Add(new UsageEvent(timestamp, "grok", tokens, turnCost, sessionId, $"{GrokSessionKeyPrefix}{sessionId}:{turnKey}:grok"));
                    continue;
                }

                var remaining = turnCost is { } total
                    ? Math.Max(0, total - models.Sum(item => item.Cost ?? 0))
                    : (double?)null;
                var untickedTokens = (double)models.Where(item => item.Cost is null).Sum(item => (decimal)item.Tokens.TotalTokens);
                foreach (var (model, tokens, cost) in models)
                {
                    var reported = cost ?? (remaining is { } share && untickedTokens > 0 ? share * tokens.TotalTokens / untickedTokens : null);
                    events.Add(new UsageEvent(timestamp, model, tokens, reported, sessionId, $"{GrokSessionKeyPrefix}{sessionId}:{turnKey}:{model}"));
                }
            }
            return events;
        }

        /// <summary>Grok Build counts cached reads and cache writes inside <c>inputTokens</c>.</summary>
        private static TokenBreakdown GrokTokens(JsonElement usage)
        {
            var input = ReadDirectUInt64(usage, "inputTokens");
            var cacheRead = ReadDirectUInt64(usage, "cachedReadTokens");
            var cacheWrite = ReadDirectUInt64(usage, "cacheCreationTokens");
            var output = ReadDirectUInt64(usage, "outputTokens");
            return new TokenBreakdown
            {
                Input = input > cacheRead + cacheWrite ? input - cacheRead - cacheWrite : 0,
                CacheRead = cacheRead,
                CacheWrite5m = cacheWrite,
                Output = output,
                Reasoning = Math.Min(output, ReadDirectUInt64(usage, "reasoningTokens")),
            };
        }

        private static double? GrokCostUsd(JsonElement usage)
            => usage.TryGetProperty("costUsdTicks", out var ticks) && ticks.TryGetDouble(out var value) && value >= 0
                ? value / GrokCostTicksPerDollar
                : null;

        private static List<UsageEvent> ParseGrok(IEnumerable<string> lines)
        {
            var events = new List<UsageEvent>();
            var modelByProcess = new Dictionary<long, string>();
            foreach (var line in lines)
            {
                if (!TryDocument(line, out var root) || !TryFindTimestamp(root, out var timestamp))
                    continue;

                var message = TryFindString(root, "msg") ?? string.Empty;
                var processId = TryFindInt64(root, "pid");
                if (message.Contains("model", StringComparison.OrdinalIgnoreCase)
                    && TryFindModel(root, out var model)
                    && processId is { } pid)
                {
                    modelByProcess[pid] = model;
                }

                if (!message.Equals("shell.turn.inference_done", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryFindProperty(root, "ctx", out var context))
                    continue;
                var prompt = ReadUInt64(context, "prompt_tokens");
                var cached = Math.Min(prompt, ReadUInt64(context, "cached_prompt_tokens"));
                var tokens = new TokenBreakdown
                {
                    Input = prompt - cached,
                    CacheRead = cached,
                    Output = ReadUInt64(context, "completion_tokens"),
                    Reasoning = ReadUInt64(context, "reasoning_tokens"),
                };
                if (tokens.TotalTokens == 0)
                    continue;

                var eventModel = processId is { } modelPid && modelByProcess.TryGetValue(modelPid, out var known)
                    ? known
                    : "grok-unknown";
                // "sid" is the Grok Build session id, which lets session-log usage supersede these events.
                var sessionId = TryFindString(root, "sid") ?? processId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                events.Add(new UsageEvent(timestamp, eventModel, tokens, null, sessionId, null));
            }

            return events;
        }

        private static IReadOnlyList<UsageEvent> ParseOpenCodeDatabase(string path, ProviderId providerId, DateTimeOffset now)
        {
            var events = new List<UsageEvent>();
            using var connection = OpenReadOnlyDatabase(path);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, session_id, time_created, data
                FROM message
                WHERE time_created >= $cutoff
                ORDER BY time_created;
                """;
            command.Parameters.AddWithValue("$cutoff", now.AddDays(-91).ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            var expectedProvider = providerId == ProviderId.OpenCodeGo ? "opencode-go" : "opencode";
            while (reader.Read())
            {
                var data = reader.GetString(3);
                if (!TryDocument(data, out var root)
                    || !string.Equals(ReadDirectString(root, "role"), "assistant", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(ReadDirectString(root, "providerID"), expectedProvider, StringComparison.OrdinalIgnoreCase)
                    || !root.TryGetProperty("tokens", out var usage)
                    || usage.ValueKind != JsonValueKind.Object)
                    continue;

                var tokens = ReadOpenCodeTokens(usage);
                if (tokens.TotalTokens == 0)
                    continue;

                var timestampMs = ReadNestedInt64(root, "time", "completed")
                    ?? ReadNestedInt64(root, "time", "created")
                    ?? (reader.IsDBNull(2) ? null : reader.GetInt64(2));
                if (!TryUnixTimestamp(timestampMs, out var timestamp))
                    continue;

                var model = ReadDirectString(root, "modelID") ?? "opencode-unknown";
                double? reportedCost = ReadDirectDouble(root, "cost");
                events.Add(new UsageEvent(
                    timestamp,
                    model,
                    tokens,
                    reportedCost,
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(0) ? null : reader.GetString(0)));
            }
            return events;
        }

        private static IReadOnlyList<UsageEvent> ParseClineSession(string metadataPath, ProviderId providerId)
        {
            using var metadataDocument = JsonDocument.Parse(ReadSharedText(metadataPath));
            var metadata = metadataDocument.RootElement;
            var expectedProvider = providerId == ProviderId.ClinePass ? "cline-pass" : "cline";
            if (!string.Equals(ReadDirectString(metadata, "provider"), expectedProvider, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<UsageEvent>();

            var sessionId = ReadDirectString(metadata, "session_id")
                ?? ReadDirectString(metadata, "sessionId")
                ?? Path.GetFileNameWithoutExtension(metadataPath);
            var fallbackModel = ReadDirectString(metadata, "model") ?? $"{expectedProvider}-unknown";
            var messagesPath = ReadDirectString(metadata, "messages_path")
                ?? ReadDirectString(metadata, "messagesPath");
            if (string.IsNullOrWhiteSpace(messagesPath))
            {
                messagesPath = Path.Combine(
                    Path.GetDirectoryName(metadataPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(metadataPath) + ".messages.json");
            }
            else if (!Path.IsPathRooted(messagesPath))
            {
                messagesPath = Path.Combine(Path.GetDirectoryName(metadataPath) ?? string.Empty, messagesPath);
            }

            JsonElement messages;
            JsonDocument? messagesDocument = null;
            try
            {
                if (metadata.TryGetProperty("messages", out var inlineMessages)
                    && inlineMessages.ValueKind == JsonValueKind.Array)
                {
                    messages = inlineMessages;
                }
                else
                {
                    if (!File.Exists(messagesPath))
                        return Array.Empty<UsageEvent>();
                    messagesDocument = JsonDocument.Parse(ReadSharedText(messagesPath));
                    messages = messagesDocument.RootElement.ValueKind == JsonValueKind.Array
                        ? messagesDocument.RootElement
                        : messagesDocument.RootElement.TryGetProperty("messages", out var nested)
                            && nested.ValueKind == JsonValueKind.Array
                            ? nested
                            : default;
                }

                if (messages.ValueKind != JsonValueKind.Array)
                    return Array.Empty<UsageEvent>();

                var events = new List<UsageEvent>();
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind != JsonValueKind.Object
                        || !message.TryGetProperty("metrics", out var metrics)
                        || metrics.ValueKind != JsonValueKind.Object
                        || !TryUnixTimestamp(ReadDirectInt64(message, "ts"), out var timestamp))
                        continue;

                    var tokens = new TokenBreakdown
                    {
                        Input = ReadDirectUInt64(metrics, "inputTokens"),
                        CacheRead = ReadDirectUInt64(metrics, "cacheReadTokens"),
                        CacheWrite5m = ReadDirectUInt64(metrics, "cacheWriteTokens"),
                        Output = ReadDirectUInt64(metrics, "outputTokens"),
                    };
                    if (tokens.TotalTokens == 0)
                        continue;

                    var model = message.TryGetProperty("modelInfo", out var modelInfo)
                        && modelInfo.ValueKind == JsonValueKind.Object
                        ? ReadDirectString(modelInfo, "id") ?? fallbackModel
                        : fallbackModel;
                    events.Add(new UsageEvent(
                        timestamp,
                        model,
                        tokens,
                        ReadDirectDouble(metrics, "cost"),
                        sessionId,
                        ReadDirectString(message, "id") is { Length: > 0 } id ? $"{sessionId}:{id}" : null));
                }
                return events;
            }
            finally
            {
                messagesDocument?.Dispose();
            }
        }

        private static IReadOnlyList<UsageEvent> ParseZaiDatabase(string path, DateTimeOffset now)
        {
            var events = new List<UsageEvent>();
            using var connection = OpenReadOnlyDatabase(path);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, session_id, COALESCE(completed_at, started_at), model_id,
                       input_tokens, output_tokens, reasoning_tokens,
                       cache_creation_input_tokens, cache_read_input_tokens
                FROM model_usage
                WHERE COALESCE(completed_at, started_at) >= $cutoff
                  AND status <> 'running'
                ORDER BY COALESCE(completed_at, started_at);
                """;
            command.Parameters.AddWithValue("$cutoff", now.AddDays(-91).ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryUnixTimestamp(reader.IsDBNull(2) ? null : reader.GetInt64(2), out var timestamp))
                    continue;

                var input = ReadDatabaseUInt64(reader, 4);
                var cacheWrite = ReadDatabaseUInt64(reader, 7);
                var cacheRead = ReadDatabaseUInt64(reader, 8);
                var reasoning = ReadDatabaseUInt64(reader, 6);
                var tokens = new TokenBreakdown
                {
                    Input = input - Math.Min(input, AddSaturated(cacheRead, cacheWrite)),
                    CacheRead = cacheRead,
                    CacheWrite5m = cacheWrite,
                    Output = AddSaturated(ReadDatabaseUInt64(reader, 5), reasoning),
                    Reasoning = reasoning,
                };
                if (tokens.TotalTokens == 0)
                    continue;

                events.Add(new UsageEvent(
                    timestamp,
                    reader.IsDBNull(3) ? "zai-unknown" : reader.GetString(3),
                    tokens,
                    null,
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(0) ? null : reader.GetString(0)));
            }
            return events;
        }

        private static SqliteConnection OpenReadOnlyDatabase(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        private static TokenBreakdown ReadOpenCodeTokens(JsonElement usage)
        {
            var reasoning = ReadDirectUInt64(usage, "reasoning");
            var cache = usage.TryGetProperty("cache", out var value) && value.ValueKind == JsonValueKind.Object
                ? value
                : default;
            return new TokenBreakdown
            {
                Input = ReadDirectUInt64(usage, "input"),
                CacheRead = cache.ValueKind == JsonValueKind.Object ? ReadDirectUInt64(cache, "read") : 0,
                CacheWrite5m = cache.ValueKind == JsonValueKind.Object ? ReadDirectUInt64(cache, "write") : 0,
                Output = AddSaturated(ReadDirectUInt64(usage, "output"), reasoning),
                Reasoning = reasoning,
            };
        }

        private static UsageHistory Aggregate(
            IEnumerable<UsageEvent> events,
            DateTimeOffset now,
            string sourceNote,
            ProviderId providerId)
        {
            var today = now.LocalDateTime.Date;
            var yesterday = today.AddDays(-1);
            var cutoff = today.AddDays(-89);
            var buckets = new Dictionary<DateTime, List<UsageEvent>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in events)
            {
                if (item.DedupeKey is { Length: > 0 } key && !seen.Add(key))
                    continue;
                var date = item.Timestamp.LocalDateTime.Date;
                if (date < cutoff || date > today)
                    continue;
                if (!buckets.TryGetValue(date, out var bucket))
                    buckets[date] = bucket = new List<UsageEvent>();
                bucket.Add(item);
            }

            var history = new UsageHistory
            {
                Today = BuildPeriod(buckets.GetValueOrDefault(today), sourceNote, providerId),
                Yesterday = BuildPeriod(buckets.GetValueOrDefault(yesterday), sourceNote, providerId),
                Last7Days = BuildPeriod(EventsSince(buckets, today.AddDays(-6)), sourceNote, providerId),
                Last30Days = BuildPeriod(EventsSince(buckets, today.AddDays(-29)), sourceNote, providerId),
                Last90Days = BuildPeriod(EventsSince(buckets, cutoff), sourceNote, providerId),
                Daily = buckets.OrderBy(pair => pair.Key)
                    .Select(pair => BuildDaily(pair.Key, pair.Value, sourceNote, providerId))
                    .ToArray(),
            };
            return history;
        }

        private static IReadOnlyCollection<UsageEvent> EventsSince(
            IReadOnlyDictionary<DateTime, List<UsageEvent>> buckets,
            DateTime since)
            => buckets.Where(pair => pair.Key >= since)
                .SelectMany(pair => pair.Value)
                .ToArray();

        private static DailyUsage BuildDaily(
            DateTime date,
            IReadOnlyCollection<UsageEvent> events,
            string sourceNote,
            ProviderId providerId)
        {
            var period = BuildPeriod(events, sourceNote, providerId)!;
            return new DailyUsage(
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                period.Tokens,
                period.EstimatedCostUsd,
                period.EstimateComplete,
                period.TokenBreakdown,
                period.CacheSavingsUsd,
                period.Records,
                period.Sessions,
                period.ModelBreakdown);
        }

        private static UsagePeriod? BuildPeriod(
            IReadOnlyCollection<UsageEvent>? events,
            string sourceNote,
            ProviderId providerId)
        {
            if (events is null || events.Count == 0)
                return null;

            var entries = events.GroupBy(item => NormalizeModelName(item.Model), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var costs = group.Select(item => Cost(item)).ToArray();
                    var tokens = SumTokens(group.Select(item => item.Tokens));
                    return new ModelUsageEntry(
                        group.Key,
                        tokens,
                        costs.All(cost => cost.HasValue) ? costs.Sum(cost => cost!.Value) : null,
                        group.Sum(item => CacheSavings(item)),
                        group.Count(),
                        group.Select(item => item.SessionId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Count());
                })
                .OrderByDescending(entry => entry.TotalTokens)
                .ToArray();
            var allTokens = SumTokens(events.Select(item => item.Tokens));
            var pricedEntries = entries.Where(item => item.CostUsd.HasValue).ToArray();
            return new UsagePeriod(
                allTokens.TotalTokens,
                pricedEntries.Length == 0 ? null : pricedEntries.Sum(item => item.CostUsd!.Value),
                costEstimated: events.Any(item => !item.ReportedCostUsd.HasValue),
                estimateComplete: entries.All(item => item.CostUsd.HasValue),
                modelBreakdown: new ModelUsageBreakdown(entries, sourceNote),
                tokenBreakdown: allTokens,
                cacheSavingsUsd: entries.Sum(item => item.CacheSavingsUsd),
                records: events.Count,
                sessions: events.Select(item => item.SessionId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Count());
        }

        private static TokenBreakdown SumTokens(IEnumerable<TokenBreakdown> values)
        {
            var total = new TokenBreakdown();
            foreach (var value in values)
                total = total.Add(value);
            return total;
        }

        private static double? Cost(UsageEvent item)
            // Grok's local log carries no billed cost, so it is estimated from the bundled pricing
            // supplement (grok-4.5, composer-*, grok-build aliases) rather than left unpriced.
            => item.ReportedCostUsd
                ?? PricingEngine.EstimateCostUsd(NormalizeModelName(item.Model), item.Tokens, item.Timestamp);

        /// <summary>
        /// T3 Code / Synara-style transcripts prefix the model id with the underlying provider id
        /// ("opencode-go/deepseek-v4-flash"). Strip that prefix so OpenCode Go models are counted and
        /// displayed under their plain model name alongside identical unprefixed entries.
        /// </summary>
        private static string NormalizeModelName(string model)
            => IsOpenCodeGoModel(model) ? model.Substring("opencode-go/".Length) : model;

        /// <summary>True for models written by the T3 Code harness as "opencode-go/&lt;model&gt;".</summary>
        private static bool IsOpenCodeGoModel(string model)
            => model.StartsWith("opencode-go/", StringComparison.OrdinalIgnoreCase);

        private static double CacheSavings(UsageEvent item)
            => PricingEngine.EstimateCacheSavingsUsd(NormalizeModelName(item.Model), item.Tokens, item.Timestamp) ?? 0;

        private static string SourceNote(ProviderId providerId) => providerId switch
        {
            ProviderId.Grok => "From your Grok Build logs (reported cost; older shell logs estimated)",
            ProviderId.OpenCode or ProviderId.OpenCodeGo => $"From your {DisplayName(providerId)} database (reported cost)",
            ProviderId.Cline or ProviderId.ClinePass => $"From your {DisplayName(providerId)} sessions (reported cost)",
            ProviderId.Zai => "From your Z.ai model usage database (estimated cost)",
            ProviderId.Cursor => "Cursor dashboard reported tokens and API-rate costs",
            ProviderId.Antigravity => "Antigravity locally recorded token counters (API-rate cost estimate)",
            _ => $"From your {DisplayName(providerId)} logs (estimated)",
        };

        private static string DisplayName(ProviderId id) => id switch
        {
            ProviderId.Codex => "Codex",
            ProviderId.Claude => "Claude",
            ProviderId.Grok => "Grok",
            ProviderId.Cursor => "Cursor",
            ProviderId.Antigravity => "Antigravity",
            ProviderId.OpenCode => "OpenCode",
            ProviderId.OpenCodeGo => "OpenCode Go",
            ProviderId.Cline => "Cline",
            ProviderId.ClinePass => "Cline Pass",
            ProviderId.Zai => "Z.ai",
            _ => id.ToString(),
        };

        private static bool TryDocument(string line, out JsonElement root)
        {
            root = default;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
                return root.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException) { return false; }
        }

        private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
                return true;
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && TryFindProperty(property.Value, name, out value))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    if (TryFindProperty(item, name, out value))
                        return true;
            }
            value = default;
            return false;
        }

        private static bool TryFindModel(JsonElement element, out string model)
        {
            var direct = TryFindString(element, "model");
            var named = TryFindString(element, "model_name");
            var id = TryFindString(element, "modelId");
            var upperId = TryFindString(element, "modelID");
            model = direct ?? named ?? id ?? upperId ?? string.Empty;
            if (model.Length > 0)
                return true;
            return false;
        }

        private static string? TryFindString(JsonElement element, string name)
            => TryFindProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;

        private static bool TryFindTimestamp(JsonElement element, out DateTimeOffset timestamp)
        {
            foreach (var name in new[] { "timestamp", "ts", "created_at", "createdAt" })
            {
                var value = TryFindString(element, name);
                if (value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp))
                    return true;
            }
            timestamp = default;
            return false;
        }

        private static bool TryFindNumber(JsonElement element, string name, out double value)
        {
            if (TryFindProperty(element, name, out var property) && property.TryGetDouble(out value))
                return true;
            value = 0;
            return false;
        }

        private static long? TryFindInt64(JsonElement element, string name)
            => TryFindProperty(element, name, out var value) && value.TryGetInt64(out var number) ? number : null;

        private static string? ReadDirectString(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()?.Trim()
                    : null;

        private static long? ReadDirectInt64(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var number))
                return null;
            return number;
        }

        private static long? ReadNestedInt64(JsonElement element, string objectName, string valueName)
            => element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(objectName, out var nested)
                ? ReadDirectInt64(nested, valueName)
                : null;

        private static double? ReadDirectDouble(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.TryGetDouble(out var number)
                    ? number
                    : null;

        private static ulong ReadDirectUInt64(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.TryGetUInt64(out var number)
                    ? number
                    : 0;

        private static ulong ReadDatabaseUInt64(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
                return 0;
            var value = reader.GetInt64(ordinal);
            return value > 0 ? (ulong)value : 0;
        }

        private static bool TryUnixTimestamp(long? value, out DateTimeOffset timestamp)
        {
            timestamp = default;
            if (value is not > 0)
                return false;
            try
            {
                timestamp = value < 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeSeconds(value.Value)
                    : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static ulong AddSaturated(ulong left, ulong right)
            => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        private static ulong ReadUInt64(JsonElement element, string name)
            => TryFindProperty(element, name, out var value) && value.TryGetUInt64(out var number) ? number : 0;

        private static RawUsage ReadUsage(JsonElement value)
        {
            var combinedCacheWrite = ReadUInt64(value, "cache_creation_input_tokens", "cache_write_input_tokens");
            var cacheWrite5m = combinedCacheWrite > 0
                ? combinedCacheWrite
                : ReadUInt64(value, "ephemeral_5m_input_tokens");
            var cacheWrite1h = combinedCacheWrite > 0
                ? 0
                : ReadUInt64(value, "ephemeral_1h_input_tokens");
            return new RawUsage(
                ReadUInt64(value, "input_tokens", "prompt_tokens", "input"),
                ReadUInt64(value, "cached_input_tokens", "cache_read_input_tokens", "cached_tokens"),
                cacheWrite5m,
                cacheWrite1h,
                ReadUInt64(value, "output_tokens", "completion_tokens", "output"),
                ReadUInt64(value, "reasoning_output_tokens", "reasoning_tokens"),
                ReadUInt64(value, "total_tokens"));
        }

        private static ulong ReadUInt64(JsonElement element, params string[] names)
        {
            foreach (var name in names)
                if (TryFindProperty(element, name, out var value) && value.TryGetUInt64(out var number))
                    return number;
            return 0;
        }

        private readonly record struct RawUsage(
            ulong Input,
            ulong Cached,
            ulong CacheWrite5m,
            ulong CacheWrite1h,
            ulong Output,
            ulong Reasoning,
            ulong Total)
        {
            public ulong EffectiveTotal => Total > 0
                ? Total
                : Input + Cached + CacheWrite5m + CacheWrite1h + Output;

            public TokenBreakdown ToTokens(bool inputIncludesCache) => new()
            {
                Input = inputIncludesCache
                    ? Input - Math.Min(Input, Cached + CacheWrite5m + CacheWrite1h)
                    : Input,
                CacheRead = Cached,
                CacheWrite5m = CacheWrite5m,
                CacheWrite1h = CacheWrite1h,
                Output = Output,
                Reasoning = Math.Min(Output, Reasoning),
            };
        }
    }
}
