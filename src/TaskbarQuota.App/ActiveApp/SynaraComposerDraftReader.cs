using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Reads Synara's live renderer state from the Chromium localStorage LevelDB:
    /// <list type="bullet">
    /// <item><c>synara:recent-views:v1</c> — the MRU view list; its head thread is the focused thread
    /// (UI focus is not in the SQLite DB, so this is how we know which thread the user is looking at).</item>
    /// <item><c>synara:composer-drafts:v1</c> — <c>draftsByThreadId[id].activeProvider/model</c>, the
    /// in-flight dropdown selection. The persisted <c>projection_threads.model_selection_json</c> only
    /// updates when a turn runs, so the draft is the realtime source for the picked provider.</item>
    /// </list>
    ///
    /// We parse LevelDB directly so we never need SQLite or any other read on the hot path. A value lives in
    /// either the write-ahead <c>.log</c> (recent writes) or, after compaction, a snappy-compressed <c>.ldb</c>
    /// sstable — so we read both and pick the entry with the highest sequence number. The <c>.log</c> is tiny
    /// and re-read on every change; sstable results are cached and only refreshed when an <c>.ldb</c> changes
    /// (compaction is rare), keeping the per-tick cost to a small log parse.
    /// </summary>
    public static class SynaraComposerDraftReader
    {
        private const string DraftKey = "synara:composer-drafts:v1";
        private const string RecentViewsKey = "synara:recent-views:v1";
        private const int LevelDbBlockSize = 32 * 1024;
        private static readonly TimeSpan ProfileDiscoveryCacheTtl = TimeSpan.FromSeconds(1);
        private static readonly string[] LevelDbProfileNames =
        {
            // Current Synara desktop profiles from apps/desktop/src/desktopUserDataProfile.ts.
            "synara",
            "synara-dev",
            // Older/renamed builds seen in the wild and in Synara's profile seeding code.
            "synara-desktop",
            "dpcode",
            "dpcode-dev",
            "t3code",
            "t3code-dev",
            "DP Code (Alpha)",
            "DP Code (Dev)",
            "dp-code-desktop",
        };

        /// <summary>
        /// A composer selection: the active provider, that provider's model, and the full per-provider
        /// model map (<c>modelSelectionByProvider</c> / <c>stickyModelSelectionByProvider</c>). The map
        /// matters because Synara's effective provider (a thread/session lock) can differ from
        /// <see cref="ProviderLiteral"/>, and we then need that other provider's model.
        /// </summary>
        public sealed record DraftSelection(
            string ProviderLiteral,
            string? Model,
            IReadOnlyDictionary<string, string>? ModelByProvider = null)
        {
            /// <summary>The selected model for <paramref name="provider"/>, from the per-provider map.</summary>
            public string? ModelFor(string? provider) =>
                provider is { Length: > 0 } && ModelByProvider is { } m && m.TryGetValue(provider, out var model)
                    ? model
                    : null;
        }

        private sealed record Snapshot(
            Dictionary<string, DraftSelection> Drafts,
            DraftSelection? StickySelection,
            string? FocusedThreadId);

        // A value candidate tagged with its LevelDB sequence number so newer writes win across log + sstables.
        private readonly record struct Candidate(long Seq, byte[] Value);

        // Accumulated wanted-key values from .log files; only the appended tail is parsed on each rebuild.
        private sealed class LogScanCache
        {
            public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);
        }

        private static readonly object Gate = new();
        private static string? _cachedDir;
        private static long _cachedStamp;
        private static bool _forceRebuild;
        private static Snapshot? _cachedSnapshot;
        private static string? _logScanDir;
        private static LogScanCache? _logScanCache;
        private static DateTime _levelDbDirsCachedUntilUtc = DateTime.MinValue;
        private static string[]? _cachedLevelDbDirs;

        // sstable candidates change only on compaction; cache them keyed by the .ldb set's stamp.
        private static string? _cachedLdbDir;
        private static long _cachedLdbStamp = long.MinValue;
        private static Dictionary<string, Candidate>? _cachedLdbCandidates;

        /// <summary>The thread the user currently has open (MRU head), or null if unknown.</summary>
        public static string? GetFocusedThreadId() => GetSnapshot()?.FocusedThreadId;

        /// <summary>Resolve the focused thread and its draft from one cached snapshot read.</summary>
        public static DraftSelection? TryGetFocusedDraft(out string? threadId)
        {
            threadId = null;
            var snap = GetSnapshot();
            if (snap?.FocusedThreadId is not { Length: > 0 } focused)
                return null;

            threadId = focused;
            return snap.Drafts.TryGetValue(focused, out var sel) ? sel : null;
        }

        /// <summary>
        /// Off-thread pre-warm. Builds and caches the full snapshot (sstables + log) for the given
        /// directory so the first foreground refresh is a few-KB tail read, not a multi-MB cold parse.
        /// Safe to call repeatedly; the snapshot cache is keyed on the log dir stamp.
        /// </summary>
        public static void WarmSnapshot(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    return;

                var logStamp = DirectoryStamp(dir, "*.log");
                lock (Gate)
                {
                    if (!_forceRebuild && _cachedSnapshot != null && _cachedDir == dir
                        && _cachedStamp == logStamp)
                        return;
                }

                var snapshot = BuildSnapshot(dir);
                lock (Gate)
                {
                    _cachedDir = dir;
                    _cachedStamp = logStamp;
                    _forceRebuild = false;
                    _cachedSnapshot = snapshot;
                }
            }
            catch { /* fall back to lazy build on hot path */ }
        }

        /// <summary>The live draft selection for <paramref name="threadId"/>, or null if there is none.</summary>
        public static DraftSelection? TryGetDraft(string threadId)
        {
            if (string.IsNullOrEmpty(threadId))
                return null;
            var snap = GetSnapshot();
            return snap != null && snap.Drafts.TryGetValue(threadId, out var sel) ? sel : null;
        }

        /// <summary>
        /// Synara's globally sticky composer selection. The provider picker writes this alongside the
        /// per-thread draft, and it is the only persisted signal for the visible New Chat composer before
        /// a draft route has been recorded in recent views.
        /// </summary>
        public static DraftSelection? GetStickySelection() => GetSnapshot()?.StickySelection;

        /// <summary>
        /// Hot-path sticky read: parse only the composer-drafts log entry (no recent-views / full snapshot).
        /// Used when the file watcher signals a provider-picker change.
        /// </summary>
        internal static DraftSelection? TryGetStickySelectionFast()
        {
            foreach (var dir in GetLevelDbDirs())
            {
                try
                {
                    var logValues = ScanLogValues(dir);
                    if (!logValues.TryGetValue(DraftKey, out var draftValue))
                    {
                        lock (Gate)
                        {
                            if (string.Equals(_cachedLdbDir, dir, StringComparison.OrdinalIgnoreCase)
                                && _cachedLdbCandidates != null
                                && _cachedLdbCandidates.TryGetValue(DraftKey, out var candidate)
                                && candidate.Value.Length > 0)
                            {
                                draftValue = candidate.Value;
                            }
                        }
                    }

                    if (draftValue == null || draftValue.Length == 0)
                        continue;

                    var draftJson = DecodeValue(draftValue);
                    if (draftJson != null && ParseStickySelection(draftJson) is { } sticky)
                        return sticky;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Debug($"Synara sticky fast read failed ({dir}): {ex.Message}");
                }
            }
            return null;
        }

        private static Snapshot? GetSnapshot()
        {
            var dirs = GetLevelDbDirs();
            if (dirs.Count == 0)
                return null;

            Snapshot? firstSnapshot = null;
            foreach (var dir in dirs)
            {
                try
                {
                    var snapshot = GetSnapshotForDir(dir);
                    firstSnapshot ??= snapshot;
                    if (HasSnapshotSignal(snapshot))
                        return snapshot;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Debug($"Synara localStorage read failed ({dir}): {ex.Message}");
                }
            }

            return firstSnapshot;
        }

        private static Snapshot GetSnapshotForDir(string dir)
        {
            try
            {
                lock (Gate)
                {
                    if (!_forceRebuild && _cachedSnapshot != null && _cachedDir == dir
                        && _cachedStamp == DirectoryStamp(dir, "*.log"))
                        return _cachedSnapshot;
                }

                var snapshot = BuildSnapshot(dir);
                lock (Gate)
                {
                    _cachedDir = dir;
                    _cachedStamp = DirectoryStamp(dir, "*.log");
                    _forceRebuild = false;
                    _cachedSnapshot = snapshot;
                }
                return snapshot;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"Synara localStorage read failed: {ex.Message}");
                throw;
            }
        }

        internal static string? GetLevelDbDir()
        {
            return GetLevelDbDirs().FirstOrDefault();
        }

        internal static IReadOnlyList<string> GetLevelDbDirs()
        {
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                if (_cachedLevelDbDirs != null && now < _levelDbDirsCachedUntilUtc)
                    return _cachedLevelDbDirs;
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dirs = new List<string>();
            foreach (var profile in LevelDbProfileNames)
            {
                var dir = Path.Combine(appData, profile, "Local Storage", "leveldb");
                if (Directory.Exists(dir))
                    dirs.Add(dir);
            }

            dirs.Sort((left, right) => LevelDbActivityStamp(right).CompareTo(LevelDbActivityStamp(left)));
            var result = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            lock (Gate)
            {
                _cachedLevelDbDirs = result;
                _levelDbDirsCachedUntilUtc = now + ProfileDiscoveryCacheTtl;
            }
            return result;
        }

        private static bool HasSnapshotSignal(Snapshot? snapshot)
        {
            return snapshot != null
                && (snapshot.Drafts.Count > 0
                    || snapshot.StickySelection != null
                    || !string.IsNullOrEmpty(snapshot.FocusedThreadId));
        }

        private static long LevelDbActivityStamp(string dir)
        {
            return DirectoryStamp(dir, "*.log") * 31 + DirectoryStamp(dir, "*.ldb");
        }

        private static long DirectoryStamp(string dir, string pattern)
        {
            long stamp = 0;
            foreach (var file in Directory.EnumerateFiles(dir, pattern))
            {
                try
                {
                    var info = new FileInfo(file);
                    stamp = stamp * 31 + info.LastWriteTimeUtc.Ticks + info.Length;
                }
                catch { }
            }
            return stamp;
        }

        private static Snapshot BuildSnapshot(string dir)
        {
            // sstable candidates: cached, refreshed only when an .ldb changes (compaction).
            var ldbStamp = DirectoryStamp(dir, "*.ldb");
            Dictionary<string, Candidate> candidates;
            lock (Gate)
            {
                if (string.Equals(_cachedLdbDir, dir, StringComparison.OrdinalIgnoreCase)
                    && _cachedLdbCandidates != null
                    && _cachedLdbStamp == ldbStamp)
                {
                    candidates = new Dictionary<string, Candidate>(_cachedLdbCandidates);
                }
                else
                {
                    candidates = ScanSstables(dir);
                    _cachedLdbCandidates = new Dictionary<string, Candidate>(candidates);
                    _cachedLdbDir = dir;
                    _cachedLdbStamp = ldbStamp;
                }
            }

            // The .log holds writes newer than ANY sstable (leveldb flushes the log to sstables on
            // compaction), so a key present in the log is authoritative — it overrides sstable values
            // unconditionally, no cross-source sequence comparison.
            var logValues = ScanLogValues(dir);

            byte[]? draftValue = logValues.TryGetValue(DraftKey, out var lv) ? lv
                : candidates.TryGetValue(DraftKey, out var d) ? d.Value : null;
            byte[]? recentValue = logValues.TryGetValue(RecentViewsKey, out var rv) ? rv
                : candidates.TryGetValue(RecentViewsKey, out var r) ? r.Value : null;

            string? draftJson = draftValue != null ? DecodeValue(draftValue) : null;
            string? recentJson = recentValue != null ? DecodeValue(recentValue) : null;

            var drafts = draftJson != null ? ParseDrafts(draftJson) : null;
            var sticky = draftJson != null ? ParseStickySelection(draftJson) : null;
            var focused = recentJson != null ? ParseFocusedThreadId(recentJson) : null;
            return new Snapshot(
                drafts ?? new Dictionary<string, DraftSelection>(StringComparer.Ordinal),
                sticky,
                focused);
        }

        private static void Consider(Dictionary<string, Candidate> map, string key, long seq, byte[] value, byte type)
        {
            // type 1 = value, 0 = deletion. A deletion at a higher seq removes the value.
            if (!map.TryGetValue(key, out var existing) || seq >= existing.Seq)
                map[key] = new Candidate(seq, type == 0 ? Array.Empty<byte>() : value);
        }

        private static bool TryMatchWantedKey(byte[] userKey, out string wanted)
        {
            if (Contains(userKey, DraftKeyBytes)) { wanted = DraftKey; return true; }
            if (Contains(userKey, RecentViewsKeyBytes)) { wanted = RecentViewsKey; return true; }
            wanted = string.Empty;
            return false;
        }

        // --- LevelDB write-ahead log -------------------------------------------------------

        // Latest value (or deletion) per wanted key across the .log files, in write order.
        // Appends are parsed incrementally so provider switches don't rescan a multi-MB log.
        private static Dictionary<string, byte[]> ScanLogValues(string dir)
        {
            LogScanCache cache;
            lock (Gate)
            {
                if (_logScanCache == null || !string.Equals(_logScanDir, dir, StringComparison.OrdinalIgnoreCase))
                {
                    _logScanDir = dir;
                    _logScanCache = new LogScanCache();
                }
                cache = _logScanCache;
            }

            var logs = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(dir, "*.log"))
                logs.Add(new FileInfo(path));
            logs.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            lock (cache)
            {
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var log in logs)
                {
                    seenPaths.Add(log.FullName);
                    long currentSize;
                    try { currentSize = log.Length; }
                    catch { continue; }

                    cache.FileSizes.TryGetValue(log.FullName, out var lastSize);
                    if (lastSize == currentSize)
                        continue;

                    // Log rotation/truncation — rebuild accumulated state from scratch.
                    if (lastSize > currentSize)
                    {
                        cache.FileSizes.Clear();
                        cache.Values.Clear();
                        lastSize = 0;
                    }

                    byte[] bytes;
                    try
                    {
                        if (lastSize <= 0)
                            bytes = ReadAllBytesShared(log.FullName);
                        else
                        {
                            // Overlap one block so a record split across the previous tail boundary is not missed.
                            var readFrom = Math.Max(0, lastSize - LevelDbBlockSize);
                            bytes = ReadRangeShared(log.FullName, readFrom, currentSize - readFrom);
                        }
                    }
                    catch { continue; }

                    ApplyLogRecords(cache.Values, bytes);
                    cache.FileSizes[log.FullName] = currentSize;
                }

                // Drop entries for rotated-away log files.
                foreach (var stale in cache.FileSizes.Keys.Where(p => !seenPaths.Contains(p)).ToList())
                    cache.FileSizes.Remove(stale);

                return new Dictionary<string, byte[]>(cache.Values, StringComparer.Ordinal);
            }
        }

        private static void ApplyLogRecords(Dictionary<string, byte[]> result, byte[] bytes)
        {
            foreach (var record in ReadLogRecords(bytes))
            {
                ForEachPut(record, (tag, key, value) =>
                {
                    if (!TryMatchWantedKey(key, out var wanted))
                        return;
                    if (tag == 0)
                        result.Remove(wanted);
                    else
                        result[wanted] = value;
                });
            }
        }

        // Synara keeps the active .log open for writing, so a default read (FileShare.Read) hits a sharing
        // violation. Share ReadWrite|Delete to read the live log the way the writer permits.
        private static byte[] ReadAllBytesShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }

        private static byte[] ReadRangeShared(string path, long offset, long length)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[length];
            var read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            if (read == buf.Length)
                return buf;
            var trimmed = new byte[read];
            Array.Copy(buf, trimmed, read);
            return trimmed;
        }

        // Record header: crc(4) + length(2, little-endian) + type(1). Type: 1=FULL,2=FIRST,3=MIDDLE,4=LAST.
        private static IEnumerable<byte[]> ReadLogRecords(byte[] buf)
        {
            var pending = new List<byte>();
            int pos = 0;
            while (pos + 7 <= buf.Length)
            {
                int blockRemain = LevelDbBlockSize - (pos % LevelDbBlockSize);
                if (blockRemain < 7) { pos += blockRemain; continue; }

                int length = buf[pos + 4] | (buf[pos + 5] << 8);
                int type = buf[pos + 6];
                int dataStart = pos + 7;
                if (dataStart + length > buf.Length)
                    break;

                pos = dataStart + length;
                switch (type)
                {
                    case 1: yield return Slice(buf, dataStart, length); break;
                    case 2: pending.Clear(); AppendSlice(pending, buf, dataStart, length); break;
                    case 3: AppendSlice(pending, buf, dataStart, length); break;
                    case 4: AppendSlice(pending, buf, dataStart, length); yield return pending.ToArray(); pending.Clear(); break;
                    default: break;
                }
            }
        }

        // A log record is a WriteBatch: seq(8) + count(4), then entries.
        // Entry: tag(1) [0=delete,1=put], key(varint-len + bytes), and for put value(varint-len + bytes).
        private static void ForEachPut(byte[] rec, Action<byte, byte[], byte[]> onEntry)
        {
            if (rec.Length < 12)
                return;
            int p = 12;
            while (p < rec.Length)
            {
                byte tag = rec[p++];
                if (!TryReadLengthPrefixed(rec, ref p, out var key))
                    return;
                if (tag == 1)
                {
                    if (!TryReadLengthPrefixed(rec, ref p, out var value))
                        return;
                    onEntry(tag, key, value);
                }
                else if (tag == 0)
                {
                    onEntry(tag, key, Array.Empty<byte>());
                }
                else return;
            }
        }

        // --- LevelDB sstable (.ldb) --------------------------------------------------------

        private static Dictionary<string, Candidate> ScanSstables(string dir)
        {
            var map = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(dir, "*.ldb"))
            {
                try
                {
                    byte[] file = ReadAllBytesShared(path);
                    ScanSstable(file, map);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Debug($"Synara sstable parse failed ({Path.GetFileName(path)}): {ex.Message}");
                }
            }
            return map;
        }

        private static void ScanSstable(byte[] file, Dictionary<string, Candidate> map)
        {
            const int FooterSize = 48;
            if (file.Length < FooterSize)
                return;

            // Footer (last 48 bytes): metaindex handle, index handle, padding, 8-byte magic.
            int fp = file.Length - FooterSize;
            if (!TryReadBlockHandle(file, ref fp, out _, out _))
                return;
            if (!TryReadBlockHandle(file, ref fp, out long indexOffset, out long indexSize))
                return;

            byte[] indexBlock = ReadBlockContent(file, indexOffset, indexSize);
            // Index block: each entry value is a BlockHandle to a data block.
            IterateBlock(indexBlock, (key, value) =>
            {
                int vp = 0;
                if (!TryReadBlockHandle(value, ref vp, out long dataOffset, out long dataSize))
                    return;
                byte[] dataBlock;
                try { dataBlock = ReadBlockContent(file, dataOffset, dataSize); }
                catch { return; }

                IterateBlock(dataBlock, (ikey, ivalue) =>
                {
                    // Internal key = user key + 8-byte (seq<<8 | type) little-endian trailer.
                    if (ikey.Length < 8)
                        return;
                    int ukLen = ikey.Length - 8;
                    var userKey = Slice(ikey, 0, ukLen);
                    if (!TryMatchWantedKey(userKey, out var wanted))
                        return;
                    ulong trailer = BitConverter.ToUInt64(ikey, ukLen);
                    long seq = (long)(trailer >> 8);
                    byte type = (byte)(trailer & 0xff);
                    Consider(map, wanted, seq, ivalue, type);
                });
            });
        }

        private static byte[] ReadBlockContent(byte[] file, long offset, long size)
        {
            int o = checked((int)offset);
            int s = checked((int)size);
            if (o < 0 || s < 0 || o + s + 1 > file.Length)
                throw new InvalidDataException("block handle out of range");

            byte compression = file[o + s]; // 0 = none, 1 = snappy (crc follows, ignored)
            return compression == 1
                ? SnappyDecode(file, o, s)
                : Slice(file, o, s);
        }

        // Iterate a LevelDB block's key/value entries (prefix-compressed, restart array at the end).
        private static void IterateBlock(byte[] block, Action<byte[], byte[]> onEntry)
        {
            if (block.Length < 4)
                return;
            int numRestarts = ReadFixed32(block, block.Length - 4);
            int restartsBytes = numRestarts * 4 + 4;
            if (restartsBytes > block.Length)
                return;
            int limit = block.Length - restartsBytes;

            int p = 0;
            byte[] lastKey = Array.Empty<byte>();
            while (p < limit)
            {
                if (!TryReadVarint(block, ref p, out int shared)
                    || !TryReadVarint(block, ref p, out int nonShared)
                    || !TryReadVarint(block, ref p, out int valueLen))
                    return;
                if (shared > lastKey.Length || p + nonShared + valueLen > block.Length)
                    return;

                var key = new byte[shared + nonShared];
                Array.Copy(lastKey, 0, key, 0, shared);
                Array.Copy(block, p, key, shared, nonShared);
                p += nonShared;

                var value = Slice(block, p, valueLen);
                p += valueLen;

                onEntry(key, value);
                lastKey = key;
            }
        }

        // Minimal Snappy block decompressor (literals + back-references).
        private static byte[] SnappyDecode(byte[] src, int offset, int length)
        {
            int ip = offset;
            int end = offset + length;
            if (!TryReadVarint(src, ref ip, out int outLen))
                throw new InvalidDataException("bad snappy preamble");

            var outBuf = new byte[outLen];
            int op = 0;
            while (ip < end)
            {
                int c = src[ip++];
                int tag = c & 0x03;
                if (tag == 0) // literal
                {
                    int len = (c >> 2) + 1;
                    if (len > 60)
                    {
                        int n = len - 60;
                        len = ReadLittle(src, ip, n) + 1;
                        ip += n;
                    }
                    Array.Copy(src, ip, outBuf, op, len);
                    ip += len;
                    op += len;
                }
                else
                {
                    int len, copyOffset;
                    if (tag == 1)
                    {
                        len = ((c >> 2) & 0x07) + 4;
                        copyOffset = ((c >> 5) << 8) | src[ip++];
                    }
                    else if (tag == 2)
                    {
                        len = (c >> 2) + 1;
                        copyOffset = src[ip] | (src[ip + 1] << 8);
                        ip += 2;
                    }
                    else
                    {
                        len = (c >> 2) + 1;
                        copyOffset = ReadLittle(src, ip, 4);
                        ip += 4;
                    }
                    int from = op - copyOffset;
                    if (from < 0)
                        throw new InvalidDataException("bad snappy offset");
                    for (int i = 0; i < len; i++)
                        outBuf[op++] = outBuf[from++];
                }
            }
            return outBuf;
        }

        // --- value decode + helpers --------------------------------------------------------

        // Chromium localStorage value encoding: first byte 0 => UTF-16LE body, 1 => Latin-1 body.
        private static string? DecodeValue(byte[] value)
        {
            if (value.Length == 0)
                return null;
            return value[0] switch
            {
                0 => Encoding.Unicode.GetString(value, 1, value.Length - 1),
                1 => Encoding.Latin1.GetString(value, 1, value.Length - 1),
                _ => Encoding.UTF8.GetString(value),
            };
        }

        private static readonly byte[] DraftKeyBytes = Encoding.ASCII.GetBytes(DraftKey);
        private static readonly byte[] RecentViewsKeyBytes = Encoding.ASCII.GetBytes(RecentViewsKey);

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle.Length > haystack.Length)
                return false;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok)
                    return true;
            }
            return false;
        }

        private static bool TryReadBlockHandle(byte[] buf, ref int p, out long offset, out long size)
        {
            offset = 0; size = 0;
            return TryReadVarint64(buf, ref p, out offset) && TryReadVarint64(buf, ref p, out size);
        }

        private static bool TryReadLengthPrefixed(byte[] buf, ref int p, out byte[] result)
        {
            result = Array.Empty<byte>();
            if (!TryReadVarint(buf, ref p, out int len) || len < 0 || p + len > buf.Length)
                return false;
            result = Slice(buf, p, len);
            p += len;
            return true;
        }

        private static bool TryReadVarint(byte[] buf, ref int p, out int value)
        {
            bool ok = TryReadVarint64(buf, ref p, out long v);
            value = (int)v;
            return ok;
        }

        private static bool TryReadVarint64(byte[] buf, ref int p, out long value)
        {
            value = 0;
            int shift = 0;
            while (p < buf.Length && shift < 64)
            {
                byte b = buf[p++];
                value |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return true;
                shift += 7;
            }
            return false;
        }

        private static int ReadFixed32(byte[] buf, int p) =>
            buf[p] | (buf[p + 1] << 8) | (buf[p + 2] << 16) | (buf[p + 3] << 24);

        private static int ReadLittle(byte[] buf, int p, int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
                v |= buf[p + i] << (8 * i);
            return v;
        }

        private static byte[] Slice(byte[] buf, int start, int len)
        {
            var result = new byte[len];
            Array.Copy(buf, start, result, 0, len);
            return result;
        }

        private static void AppendSlice(List<byte> target, byte[] buf, int start, int len)
        {
            for (int i = 0; i < len; i++)
                target.Add(buf[start + i]);
        }

        // --- JSON shape parsing (pure, unit-tested) ----------------------------------------

        /// <summary>First <c>kind == "thread"</c> entry of the recent-views MRU list (the focused thread).</summary>
        internal static string? ParseFocusedThreadId(string recentViewsJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(recentViewsJson);
                if (!doc.RootElement.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
                    return null;
                if (!state.TryGetProperty("recentViews", out var views) || views.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var view in views.EnumerateArray())
                {
                    if (view.ValueKind != JsonValueKind.Object)
                        continue;
                    if (view.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                        && kind.GetString() == "thread"
                        && view.TryGetProperty("threadId", out var tid) && tid.ValueKind == JsonValueKind.String)
                    {
                        return tid.GetString();
                    }
                }
            }
            catch (JsonException) { }
            return null;
        }

        /// <summary>
        /// Parse the zustand composer-drafts blob into thread-id → selection. Pure for unit testing.
        /// Handles both the current <c>draftsByThreadId</c> and the legacy <c>draftsByThreadKey</c>
        /// (<c>projectId:threadId</c>) shapes.
        /// </summary>
        internal static Dictionary<string, DraftSelection>? ParseDrafts(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
                    return null;

                JsonElement threads;
                bool keyed;
                if (state.TryGetProperty("draftsByThreadId", out threads) && threads.ValueKind == JsonValueKind.Object)
                    keyed = false;
                else if (state.TryGetProperty("draftsByThreadKey", out threads) && threads.ValueKind == JsonValueKind.Object)
                    keyed = true;
                else
                    return null;

                var result = new Dictionary<string, DraftSelection>(StringComparer.Ordinal);
                foreach (var entry in threads.EnumerateObject())
                {
                    var threadId = keyed ? entry.Name[(entry.Name.LastIndexOf(':') + 1)..] : entry.Name;
                    if (string.IsNullOrEmpty(threadId) || entry.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!entry.Value.TryGetProperty("activeProvider", out var providerProp)
                        || providerProp.ValueKind != JsonValueKind.String)
                        continue;

                    var provider = providerProp.GetString();
                    if (string.IsNullOrEmpty(provider))
                        continue;

                    var modelByProvider = ParseModelByProvider(entry.Value, "modelSelectionByProvider");
                    modelByProvider.TryGetValue(provider, out var model);
                    result[threadId] = new DraftSelection(provider, model, modelByProvider);
                }

                return result;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parse a <c>{ provider: { model: "..." } }</c> object (the per-provider model map) into
        /// provider → model. Returns an empty map when the property is absent or malformed.
        /// </summary>
        private static Dictionary<string, string> ParseModelByProvider(JsonElement container, string propertyName)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (container.TryGetProperty(propertyName, out var byProvider) && byProvider.ValueKind == JsonValueKind.Object)
            {
                foreach (var prov in byProvider.EnumerateObject())
                {
                    if (prov.Value.ValueKind == JsonValueKind.Object
                        && prov.Value.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String
                        && m.GetString() is { Length: > 0 } model)
                    {
                        map[prov.Name] = model;
                    }
                }
            }
            return map;
        }

        internal static DraftSelection? ParseStickySelection(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
                    return null;
                if (!state.TryGetProperty("stickyActiveProvider", out var providerProp)
                    || providerProp.ValueKind != JsonValueKind.String)
                    return null;

                var provider = providerProp.GetString();
                if (string.IsNullOrEmpty(provider))
                    return null;

                var modelByProvider = ParseModelByProvider(state, "stickyModelSelectionByProvider");
                modelByProvider.TryGetValue(provider, out var model);
                return new DraftSelection(provider, model, modelByProvider);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Invalidate the combined snapshot only. The incremental log scan cache (<see cref="_logScanCache"/>)
        /// and the sstable candidate cache (<see cref="_cachedLdbCandidates"/>) are kept: their per-file
        /// size and sstable-stamp checks already detect new data correctly, and the size-delta path in
        /// <see cref="ScanLogValues"/> only re-reads the appended tail. Blowing them away on every FS event
        /// was the main reason a single refresh cost tens to hundreds of ms — the multi-MB active log was
        /// being re-parsed from byte 0 every time Chromium flushed a new write.
        /// </summary>
        internal static void Invalidate()
        {
            lock (Gate)
            {
                _cachedDir = null;
                _cachedStamp = 0;
                _cachedSnapshot = null;
                _forceRebuild = true;
            }
        }

        internal static void ResetCacheForTesting()
        {
            lock (Gate)
            {
                _cachedDir = null;
                _cachedStamp = 0;
                _cachedSnapshot = null;
                _logScanDir = null;
                _logScanCache = null;
                _cachedLdbDir = null;
                _cachedLdbStamp = long.MinValue;
                _cachedLdbCandidates = null;
                _cachedLevelDbDirs = null;
                _levelDbDirsCachedUntilUtc = DateTime.MinValue;
            }
        }
    }
}
