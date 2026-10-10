using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TaskbarQuota.Usage
{
    internal readonly record struct AntigravityRecordedUsage(
        DateTimeOffset Timestamp,
        string Model,
        TokenBreakdown Tokens,
        string SessionId,
        string DedupeKey);

    /// <summary>
    /// Reads the token counters Antigravity records in conversation SQLite protobuf blobs.
    /// The wire layout follows the same fields consumed by ccusage and CodexBar. Unknown or
    /// malformed rows are ignored rather than replaced with transcript-derived estimates.
    /// </summary>
    internal static class AntigravityUsageDatabaseReader
    {
        private const int MaxRowsPerTable = 10_000;
        private const int MaxBlobBytes = 16 * 1024 * 1024;
        private const string UnknownModel = "antigravity-unknown";

        private readonly record struct ProtoField(uint Number, ulong? Varint, byte[]? Bytes);

        private sealed class ParsedUsage
        {
            public ulong? ModelId { get; init; }
            public ulong Input { get; init; }
            public ulong TotalOutput { get; init; }
            public ulong CacheWrite { get; init; }
            public ulong CacheRead { get; init; }
            public ulong Reasoning { get; init; }
            public ulong VisibleOutput { get; init; }
            public string? MessageId { get; init; }
            public string? ResponseId { get; init; }
            public string? ProviderMessageId { get; init; }

            public bool HasTokens => Input > 0 || TotalOutput > 0 || CacheWrite > 0
                || CacheRead > 0 || Reasoning > 0 || VisibleOutput > 0;

            public string? Identity => FirstNonEmpty(ResponseId, ProviderMessageId, MessageId);
        }

        private sealed class ParsedRow
        {
            public string? Model { get; set; }
            public ulong? ModelId { get; set; }
            public DateTimeOffset? Timestamp { get; init; }
            public List<ParsedUsage> Usages { get; } = new();
        }

        public static IReadOnlyList<AntigravityRecordedUsage> Read(string path)
        {
            var sessionId = Path.GetFileNameWithoutExtension(path);
            var fallbackTimestamp = ReadTrajectoryTimestamp(path) ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var candidates = new List<AntigravityRecordedUsage>();

            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False");
            connection.Open();

            var steps = TableExists(connection, "steps")
                ? ReadRows(connection, "steps", "metadata", ParseStepRow)
                : new List<(ParsedRow Row, string Key)>();
            var generations = TableExists(connection, "gen_metadata")
                ? ReadGenerationRows(connection)
                : new List<(ParsedRow Row, string Key)>();

            // Steps usually carry only a numeric model id while generation rows also name the
            // model. Learn this file's id -> name pairs so ids missing from the built-in table
            // still resolve to a priceable model.
            var learned = new Dictionary<ulong, string>();
            foreach (var (row, _) in generations.Concat(steps))
            {
                if (row.ModelId is { } id && id != 0 && NormalizeModel(row.Model) is { } name && !IsUnresolved(name))
                    learned.TryAdd(id, name);
            }

            foreach (var (row, key) in steps)
                Append(row, ResolveModel(row.Model, row.ModelId, null, learned), sessionId, fallbackTimestamp, key, learned, candidates);
            string? currentModel = null;
            foreach (var (row, key) in generations)
            {
                currentModel = ResolveModel(row.Model, row.ModelId, null, learned) ?? currentModel;
                Append(row, currentModel, sessionId, fallbackTimestamp, key, learned, candidates);
            }

            // The same provider response may occur in both steps and gen_metadata. Stable response,
            // provider-message, and message identifiers win; rows without an identifier remain
            // distinct because there is no safe evidence that they are duplicates.
            var identified = new Dictionary<string, AntigravityRecordedUsage>(StringComparer.Ordinal);
            var anonymous = new List<AntigravityRecordedUsage>();
            foreach (var item in candidates)
            {
                if (!item.DedupeKey.StartsWith("identity:", StringComparison.Ordinal))
                {
                    anonymous.Add(item);
                    continue;
                }

                if (identified.TryGetValue(item.DedupeKey, out var existing))
                    identified[item.DedupeKey] = Merge(existing, item);
                else
                    identified[item.DedupeKey] = item;
            }
            anonymous.AddRange(identified.Values);
            return anonymous;
        }

        private static List<(ParsedRow Row, string Key)> ReadGenerationRows(SqliteConnection connection)
        {
            var rows = new List<(ParsedRow Row, string Key)>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT idx, data FROM gen_metadata ORDER BY idx ASC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", MaxRowsPerTable + 1);
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                if (++count > MaxRowsPerTable)
                    throw new InvalidDataException("Antigravity generation row limit exceeded.");
                if (reader.IsDBNull(1))
                    continue;
                var blob = (byte[])reader.GetValue(1);
                if (blob.Length == 0 || blob.Length > MaxBlobBytes || !TryParseGeneratorRow(blob, out var parsed))
                    continue;
                rows.Add((parsed, $"gen:{reader.GetInt64(0)}"));
            }
            return rows;
        }

        private static List<(ParsedRow Row, string Key)> ReadRows(
            SqliteConnection connection,
            string table,
            string column,
            Func<byte[], ParsedRow?> parser)
        {
            var rows = new List<(ParsedRow Row, string Key)>();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT idx, {column} FROM {table} WHERE {column} IS NOT NULL ORDER BY idx ASC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", MaxRowsPerTable + 1);
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                if (++count > MaxRowsPerTable)
                    throw new InvalidDataException($"Antigravity {table} row limit exceeded.");
                var blob = (byte[])reader.GetValue(1);
                if (blob.Length == 0 || blob.Length > MaxBlobBytes)
                    continue;
                var parsed = parser(blob);
                if (parsed is not null)
                    rows.Add((parsed, $"{table}:{reader.GetInt64(0)}"));
            }
            return rows;
        }

        private static void Append(
            ParsedRow row,
            string? rowModel,
            string sessionId,
            DateTimeOffset fallbackTimestamp,
            string rowKey,
            IReadOnlyDictionary<ulong, string> learned,
            List<AntigravityRecordedUsage> output)
        {
            for (var index = 0; index < row.Usages.Count; index++)
            {
                var usage = row.Usages[index];
                if (!usage.HasTokens)
                    continue;
                var totalOutput = Math.Max(usage.TotalOutput, SaturatingAdd(usage.VisibleOutput, usage.Reasoning));
                var model = ResolveModel(null, usage.ModelId, rowModel, learned) ?? UnknownModel;
                var identity = usage.Identity;
                var key = identity is { Length: > 0 }
                    ? $"identity:{sessionId}:{identity}"
                    : $"row:{sessionId}:{rowKey}:{index}";
                output.Add(new AntigravityRecordedUsage(
                    row.Timestamp ?? fallbackTimestamp,
                    model,
                    new TokenBreakdown
                    {
                        Input = usage.Input,
                        CacheWrite5m = usage.CacheWrite,
                        CacheRead = usage.CacheRead,
                        Output = totalOutput,
                        Reasoning = Math.Min(usage.Reasoning, totalOutput),
                    },
                    sessionId,
                    key));
            }
        }

        private static AntigravityRecordedUsage Merge(AntigravityRecordedUsage left, AntigravityRecordedUsage right)
        {
            var output = Math.Max(left.Tokens.Output, right.Tokens.Output);
            return new AntigravityRecordedUsage(
                left.Timestamp <= right.Timestamp ? left.Timestamp : right.Timestamp,
                left.Model == UnknownModel ? right.Model : left.Model,
                new TokenBreakdown
                {
                    Input = Math.Max(left.Tokens.Input, right.Tokens.Input),
                    CacheWrite5m = Math.Max(left.Tokens.CacheWrite, right.Tokens.CacheWrite),
                    CacheRead = Math.Max(left.Tokens.CacheRead, right.Tokens.CacheRead),
                    Output = output,
                    Reasoning = Math.Min(output, Math.Max(left.Tokens.Reasoning, right.Tokens.Reasoning)),
                },
                left.SessionId,
                left.DedupeKey);
        }

        private static ParsedRow? ParseStepRow(byte[] blob)
        {
            if (!TryDecode(blob, out var fields))
                return null;
            var row = new ParsedRow
            {
                Timestamp = ParseTimestamp(Bytes(fields, 8) ?? Bytes(fields, 1)),
            };
            if (TryDecode(Bytes(fields, 24), out var modelInfo))
            {
                row.Model = Text(modelInfo, 12) ?? Text(modelInfo, 8);
                row.ModelId = Varint(modelInfo, 1);
            }
            AddUsage(row, Bytes(fields, 9), modernLayout: true);
            foreach (var retry in AllBytes(fields, 28))
                if (TryDecode(retry, out var retryFields))
                    AddUsage(row, Bytes(retryFields, 2), modernLayout: true);
            return row;
        }

        private static bool TryParseGeneratorRow(byte[] blob, out ParsedRow row)
        {
            row = new ParsedRow();
            if (!TryDecode(blob, out var root) || !TryDecode(Bytes(root, 1), out var chat))
                return false;
            row = new ParsedRow
            {
                Model = Text(chat, 19) ?? Text(chat, 21),
                ModelId = Varint(chat, 3),
                Timestamp = ParseGenerationTimestamp(Bytes(chat, 9)),
            };
            AddUsage(row, Bytes(chat, 4), modernLayout: false);
            foreach (var retry in AllBytes(chat, 17))
                if (TryDecode(retry, out var retryFields))
                    AddUsage(row, Bytes(retryFields, 2), modernLayout: false);
            return true;
        }

        private static void AddUsage(ParsedRow row, byte[]? blob, bool modernLayout)
        {
            if (!TryDecode(blob, out var fields))
                return;
            // Antigravity currently writes the expanded counters in steps.metadata. Older
            // gen_metadata records use fields 1/2 for system/new input and 9/10 for visible
            // output/reasoning. Presence of modern-only counters safely disambiguates newer
            // generation records without interpreting a system-prompt count as a model id.
            modernLayout = modernLayout || fields.Any(field => field.Number is 3 or 4 or 6 or 12);
            var first = Varint(fields, 1) ?? 0;
            var second = Varint(fields, 2) ?? 0;
            var field9 = Varint(fields, 9) ?? 0;
            var field10 = Varint(fields, 10) ?? 0;
            row.Usages.Add(new ParsedUsage
            {
                ModelId = modernLayout ? first : null,
                Input = modernLayout ? second : SaturatingAdd(first, second),
                TotalOutput = modernLayout ? Varint(fields, 3) ?? 0 : SaturatingAdd(field9, field10),
                CacheWrite = modernLayout ? Varint(fields, 4) ?? 0 : 0,
                CacheRead = Varint(fields, 5) ?? 0,
                Reasoning = modernLayout ? field9 : field10,
                VisibleOutput = modernLayout ? field10 : field9,
                MessageId = Text(fields, 7),
                ResponseId = Text(fields, 11),
                ProviderMessageId = Text(fields, 12),
            });
        }

        private static DateTimeOffset? ReadTrajectoryTimestamp(string path)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False");
                connection.Open();
                if (!TableExists(connection, "trajectory_metadata_blob"))
                    return null;
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT data FROM trajectory_metadata_blob ORDER BY rowid ASC LIMIT 1";
                var blob = command.ExecuteScalar() as byte[];
                return TryDecode(blob, out var fields) ? ParseTimestamp(Bytes(fields, 2)) : null;
            }
            catch (SqliteException) { return null; }
        }

        private static DateTimeOffset? ParseGenerationTimestamp(byte[]? blob)
            => TryDecode(blob, out var fields) ? ParseTimestamp(Bytes(fields, 4)) : null;

        private static DateTimeOffset? ParseTimestamp(byte[]? blob)
        {
            if (!TryDecode(blob, out var fields) || Varint(fields, 1) is not { } seconds || seconds == 0)
                return null;
            var nanos = Math.Min(Varint(fields, 2) ?? 0, 999_999_999);
            if (seconds > long.MaxValue / 1000UL)
                return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000UL + nanos / 1_000_000)); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static bool TryDecode(byte[]? blob, out List<ProtoField> fields)
        {
            fields = new List<ProtoField>();
            if (blob is null)
                return false;
            var offset = 0;
            while (offset < blob.Length)
            {
                if (!TryReadVarint(blob, ref offset, out var tag) || tag >> 3 is 0 or > uint.MaxValue)
                    return false;
                var number = (uint)(tag >> 3);
                switch (tag & 7)
                {
                    case 0:
                        if (!TryReadVarint(blob, ref offset, out var value)) return false;
                        fields.Add(new ProtoField(number, value, null));
                        break;
                    case 1:
                        if (!TrySkip(blob, ref offset, 8)) return false;
                        fields.Add(new ProtoField(number, null, null));
                        break;
                    case 2:
                        if (!TryReadVarint(blob, ref offset, out var length) || length > int.MaxValue
                            || !TryTake(blob, ref offset, (int)length, out var bytes)) return false;
                        fields.Add(new ProtoField(number, null, bytes));
                        break;
                    case 5:
                        if (!TrySkip(blob, ref offset, 4)) return false;
                        fields.Add(new ProtoField(number, null, null));
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private static bool TryReadVarint(byte[] blob, ref int offset, out ulong value)
        {
            value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (offset >= blob.Length) return false;
                var current = blob[offset++];
                if (shift == 63 && (current & 0x7f) > 1) return false;
                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0) return true;
            }
            return false;
        }

        private static bool TrySkip(byte[] blob, ref int offset, int length)
        {
            if (length < 0 || offset > blob.Length - length) return false;
            offset += length;
            return true;
        }

        private static bool TryTake(byte[] blob, ref int offset, int length, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!TrySkip(blob, ref offset, length)) return false;
            value = blob.AsSpan(offset - length, length).ToArray();
            return true;
        }

        private static ulong? Varint(List<ProtoField> fields, uint number)
            => fields.LastOrDefault(field => field.Number == number && field.Varint.HasValue).Varint;

        private static byte[]? Bytes(List<ProtoField> fields, uint number)
            => fields.FirstOrDefault(field => field.Number == number && field.Bytes is not null).Bytes;

        private static IEnumerable<byte[]> AllBytes(List<ProtoField> fields, uint number)
            => fields.Where(field => field.Number == number && field.Bytes is not null).Select(field => field.Bytes!);

        private static string? Text(List<ProtoField> fields, uint number)
        {
            var bytes = fields.LastOrDefault(field => field.Number == number && field.Bytes is not null).Bytes;
            if (bytes is null) return null;
            var text = Encoding.UTF8.GetString(bytes).Trim();
            return text.Length == 0 ? null : text;
        }

        private static bool TableExists(SqliteConnection connection, string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
            command.Parameters.AddWithValue("$name", name);
            return command.ExecuteScalar() is not null;
        }

        /// <summary>
        /// Resolves a usage's model: a known numeric id wins (it is exact per request), then the
        /// row's model name, then an id learned from this file, then the row's model.
        /// Unknown models stay visible under their recorded name or id rather than guessed.
        /// </summary>
        private static string? ResolveModel(string? text, ulong? id, string? fallback, IReadOnlyDictionary<ulong, string> learned)
        {
            if (id is { } modelId && modelId != 0 && KnownModelName(modelId) is { } known)
                return known;
            var named = NormalizeModel(text);
            if (named is not null && !IsUnresolved(named))
                return named;
            if (id is { } learnedId && learnedId != 0 && learned.TryGetValue(learnedId, out var learnedName))
                return learnedName;
            return NormalizeModel(fallback)
                ?? named
                ?? (id is { } unknownId && unknownId != 0 ? $"antigravity-model-{unknownId}" : null);
        }

        private static bool IsUnresolved(string name)
            => name.StartsWith("antigravity-model-", StringComparison.Ordinal)
                || name.StartsWith("model_placeholder_", StringComparison.Ordinal)
                || CodenameModels.Contains(name);

        /// <summary>
        /// Antigravity's numeric model ids. Ids from 1000 up are the ones its UI and older logs
        /// show as <c>MODEL_PLACEHOLDER_M&lt;id - 1000&gt;</c>. Names are the LiteLLM keys the
        /// pricing table knows; effort and thinking variants bill at the base model's rates.
        /// </summary>
        private static string? KnownModelName(ulong id) => id switch
        {
            246 => "gemini-2.5-pro",
            312 or 313 or 329 => "gemini-2.5-flash",
            330 => "gemini-2.5-flash-lite",
            281 or 282 => "claude-sonnet-4-20250514",
            290 or 291 => "claude-opus-4-20250514",
            333 or 334 => "claude-sonnet-4-5",
            340 or 341 => "claude-haiku-4-5",
            342 => "gpt-oss-120b",
            1016 or 1036 or 1037 => "gemini-3.1-pro",
            1018 or 1047 or 1084 => "gemini-3-flash-preview",
            1020 or 1132 or 1133 or 1187 => "gemini-3.5-flash",
            1026 => "claude-opus-4-6",
            1035 => "claude-sonnet-4-6",
            1071 or 1072 or 1073 or 1196 => "gemini-3.6-flash",
            1299 or 1300 => "gemini-3.7-flash",
            >= 1318 and <= 1322 => "gemini-3.8-flash",
            1404 => "claude-sonnet-5-5",
            _ => null,
        };

        /// <summary>Internal routing names that do not identify a model on their own.</summary>
        private static readonly HashSet<string> CodenameModels = new(StringComparer.Ordinal)
        {
            "gemini-default", "gemini-3-flash-a", "gemini-3-flash-b",
        };

        private static readonly string[] VariantSuffixes =
        {
            "-tiered", "-thinking", "-extra-low", "-low", "-medium", "-high", "-minimal", "-n",
        };

        /// <summary>
        /// Normalizes Antigravity's display ("Gemini 3.6 Flash (Medium)", "Claude Sonnet 4.6
        /// (Thinking)") and internal ("gemini-3.8-flash-n", "claude-sonnet-5-5-medium") names to
        /// the base model's pricing key.
        /// </summary>
        private static string? NormalizeModel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = raw.Trim().ToLowerInvariant();
            var parenthesis = value.IndexOf('(');
            if (parenthesis >= 0) value = value[..parenthesis].Trim();
            if (value.Length == 0) return null;
            value = value.Replace(' ', '-');

            const string placeholderPrefix = "model_placeholder_m";
            if (value.StartsWith(placeholderPrefix, StringComparison.Ordinal)
                && ulong.TryParse(value[placeholderPrefix.Length..], out var placeholder))
                return KnownModelName(placeholder + 1000) ?? value;

            switch (value)
            {
                case "gemini-pro-default" or "gemini-pro-agent": return "gemini-3.1-pro";
                case "gemini-3-flash": return "gemini-3.6-flash";
            }

            for (var stripped = true; stripped;)
            {
                stripped = false;
                foreach (var suffix in VariantSuffixes)
                {
                    if (value.Length > suffix.Length && value.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        value = value[..^suffix.Length];
                        stripped = true;
                    }
                }
            }

            if (value.StartsWith("claude-", StringComparison.Ordinal))
            {
                // "claude-4.5-sonnet" -> "claude-sonnet-4-5": LiteLLM writes family first and
                // versions with dashes.
                value = ClaudeVersionFirst.Replace(value, "claude-$2-$1").Replace('.', '-');
            }
            return value;
        }

        private static readonly Regex ClaudeVersionFirst = new(@"^claude-(\d+(?:\.\d+)?)-(sonnet|opus|haiku)", RegexOptions.CultureInvariant);

        private static string? FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static ulong SaturatingAdd(ulong left, ulong right)
            => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    }
}
