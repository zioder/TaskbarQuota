using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests
{
    public sealed class UsageHistoryServiceTests
    {
        [Fact]
        public void CodexTokenEvents_AggregateIntoTodayAndModelBreakdown()
        {
            var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                "{\"timestamp\":\"2026-08-05T10:00:00Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-4o\"}}",
                "{\"timestamp\":\"2026-08-05T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":20,\"output_tokens\":30,\"total_tokens\":130}}}}",
            };

            var history = UsageHistoryService.BuildFromLines(ProviderId.Codex, lines, now);

            Assert.NotNull(history.Today);
            Assert.Equal(130UL, history.Today!.Tokens);
            var model = Assert.Single(history.Today.ModelBreakdown!.Models);
            Assert.Equal("gpt-4o", model.Model);
            Assert.True(history.Today.EstimatedCostUsd > 0);
        }

        [Fact]
        public void ClaudeUsageEvents_UseReportedCostWhenAvailable()
        {
            var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                "{\"timestamp\":\"2026-08-05T10:00:00Z\",\"message\":{\"model\":\"claude-3-7-sonnet\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}},\"costUSD\":0.42}",
            };

            var history = UsageHistoryService.BuildFromLines(ProviderId.Claude, lines, now);

            Assert.Equal(150UL, history.Today!.Tokens);
            Assert.Equal(0.42, history.Today.EstimatedCostUsd);
            Assert.Equal(0.42, history.Today.ModelBreakdown!.Models[0].CostUsd);
        }

        [Fact]
        public void CodexReasoning_IsReportedSeparatelyWithoutDoubleCountingOutput()
        {
            var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                "{\"timestamp\":\"2026-08-05T10:00:00Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5\"}}",
                "{\"timestamp\":\"2026-08-05T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":20,\"output_tokens\":30,\"reasoning_output_tokens\":10,\"total_tokens\":130}}}}",
            };

            var history = UsageHistoryService.BuildFromLines(ProviderId.Codex, lines, now);

            Assert.Equal(130UL, history.Today!.Tokens);
            Assert.Equal(30UL, history.Today.OutputTokens);
            Assert.Equal(10UL, history.Today.ReasoningTokens);
            Assert.NotNull(history.Last7Days);
            Assert.NotNull(history.Last90Days);
        }

        [Fact]
        public void ClaudeAssistantCopies_AreDeduplicatedAndCacheBucketsStayDisjoint()
        {
            var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
            const string line = "{\"type\":\"assistant\",\"timestamp\":\"2026-08-05T10:00:00Z\",\"sessionId\":\"s1\",\"requestId\":\"r1\",\"message\":{\"id\":\"m1\",\"model\":\"claude-3-7-sonnet\",\"usage\":{\"input_tokens\":100,\"cache_read_input_tokens\":20,\"cache_creation_input_tokens\":10,\"output_tokens\":50}},\"costUSD\":0.42}";

            var history = UsageHistoryService.BuildFromLines(ProviderId.Claude, new[] { line, line }, now);

            Assert.Equal(180UL, history.Today!.Tokens);
            Assert.Equal(100UL, history.Today.UncachedInputTokens);
            Assert.Equal(20UL, history.Today.CachedInputTokens);
            Assert.Equal(10UL, history.Today.CacheCreationTokens);
            Assert.Equal(50UL, history.Today.OutputTokens);
            Assert.Equal(1, history.Today.Records);
            Assert.Equal(1, history.Today.Sessions);
            Assert.Equal(0.42, history.Today.EstimatedCostUsd);
        }

        [Fact]
        public void GrokLogs_EstimateCostFromTheSupplement()
        {
            var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                "{\"timestamp\":\"2026-08-05T10:00:00Z\",\"msg\":\"shell.model_selected\",\"pid\":42,\"ctx\":{\"model\":\"grok-composer-2.5-fast\"}}",
                "{\"timestamp\":\"2026-08-05T10:01:00Z\",\"msg\":\"shell.turn.inference_done\",\"pid\":42,\"ctx\":{\"prompt_tokens\":1000,\"cached_prompt_tokens\":800,\"completion_tokens\":100,\"reasoning_tokens\":25}}",
            };

            var history = UsageHistoryService.BuildFromLines(ProviderId.Grok, lines, now);

            Assert.NotNull(history.Today);
            Assert.Equal(1100UL, history.Today!.Tokens);
            Assert.Equal(200UL, history.Today.UncachedInputTokens);
            Assert.Equal(800UL, history.Today.CachedInputTokens);
            // grok-composer-2.5-fast aliases to composer-2.5-fast ($3 in / $15 out / $0.5 cache read):
            // 200*3 + 800*0.5 + 100*15 = $0.0025.
            Assert.Equal(0.0025, history.Today.EstimatedCostUsd!.Value, 4);
            Assert.True(history.Today.EstimateComplete);
            Assert.Equal(0.0025, history.Today.ModelBreakdown!.Models[0].CostUsd!.Value, 4);
            Assert.Contains("estimated", history.Today.ModelBreakdown.SourceNote, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void OpenCodeDatabase_PreservesProviderSplitReportedCostAndReasoning()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var path = Path.Combine(directory, "opencode.db");
                var timestamp = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    connection.Open();
                    Execute(connection, "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT NOT NULL);");
                    InsertOpenCodeMessage(connection, "m1", "s1", timestamp, "opencode", "deepseek-v4-flash-free", 100, 20, 10, 30, 5, 0.40);
                    InsertOpenCodeMessage(connection, "m2", "s2", timestamp, "opencode-go", "kimi-k2.7-code", 200, 40, 0, 50, 10, 0.75);
                }

                var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
                var zen = UsageHistoryService.BuildFromFilesForTesting(ProviderId.OpenCode, new[] { path }, now);
                var go = UsageHistoryService.BuildFromFilesForTesting(ProviderId.OpenCodeGo, new[] { path }, now);

                Assert.Equal(165UL, zen.Today!.Tokens);
                Assert.Equal(30UL, zen.Today.OutputTokens);
                Assert.Equal(10UL, zen.Today.ReasoningTokens);
                Assert.Equal(0.40, zen.Today.EstimatedCostUsd!.Value, 6);
                Assert.False(zen.Today.CostEstimated);
                Assert.Equal("deepseek-v4-flash-free", zen.Today.ModelBreakdown!.Models[0].Model);
                Assert.Equal(300UL, go.Today!.Tokens);
                Assert.Equal(0.75, go.Today.EstimatedCostUsd!.Value, 6);
                Assert.Equal("kimi-k2.7-code", go.Today.ModelBreakdown!.Models[0].Model);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void OpenCodeGoDatabase_MergesProviderPrefixedModelIds()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var path = Path.Combine(directory, "opencode.db");
                var timestamp = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    connection.Open();
                    Execute(connection, "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT NOT NULL);");
                    InsertOpenCodeMessage(connection, "m1", "s1", timestamp, "opencode-go", "opencode-go/kimi-k2.7-code", 200, 40, 0, 50, 10, 0.50);
                    InsertOpenCodeMessage(connection, "m2", "s2", timestamp, "opencode-go", "kimi-k2.7-code", 100, 20, 0, 30, 5, 0.25);
                }

                var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
                var go = UsageHistoryService.BuildFromFilesForTesting(ProviderId.OpenCodeGo, new[] { path }, now);

                var model = Assert.Single(go.Today!.ModelBreakdown!.Models);
                Assert.Equal("kimi-k2.7-code", model.Model);
                Assert.Equal(455UL, model.TotalTokens);
                Assert.Equal(0.75, model.CostUsd!.Value, 6);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void OpenCodeGoDatabase_PrefixedModelWithoutReportedCost_UsesCatalogPricing()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var path = Path.Combine(directory, "opencode.db");
                var timestamp = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    connection.Open();
                    Execute(connection, "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT NOT NULL);");
                    var data = $$"""
                        {
                          "role":"assistant",
                          "providerID":"opencode-go",
                          "modelID":"opencode-go/claude-3-7-sonnet",
                          "time":{"created":{{timestamp}},"completed":{{timestamp}}},
                          "tokens":{"input":1000,"output":200,"reasoning":0,"cache":{"read":0,"write":0},"total":1200}
                        }
                        """;
                    using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO message VALUES ($id,$session,$time,$data);";
                    command.Parameters.AddWithValue("$id", "m1");
                    command.Parameters.AddWithValue("$session", "s1");
                    command.Parameters.AddWithValue("$time", timestamp);
                    command.Parameters.AddWithValue("$data", data);
                    command.ExecuteNonQuery();
                }

                var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
                var go = UsageHistoryService.BuildFromFilesForTesting(ProviderId.OpenCodeGo, new[] { path }, now);

                var model = Assert.Single(go.Today!.ModelBreakdown!.Models);
                Assert.Equal("claude-3-7-sonnet", model.Model);
                // claude-3-7-sonnet compatibility rates ($3/$15 per million): 1000 in + 200 out = $0.006.
                Assert.NotNull(model.CostUsd);
                Assert.InRange(model.CostUsd!.Value, 0.0059, 0.0061);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void CodexTranscript_OpenCodeGoModels_AreRoutedToOpenCodeGo()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var path = Path.Combine(directory, "t3-codex.jsonl");
                File.WriteAllText(path, string.Join(Environment.NewLine, new[]
                {
                    "{\"timestamp\":\"2026-08-05T10:00:00Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5\"}}",
                    "{\"timestamp\":\"2026-08-05T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"output_tokens\":30,\"total_tokens\":130}}}}",
                    "{\"timestamp\":\"2026-08-05T10:02:00Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"opencode-go/kimi-k2.7-code\"}}",
                    "{\"timestamp\":\"2026-08-05T10:03:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":200,\"output_tokens\":50,\"total_tokens\":250}}}}",
                }));

                var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
                var codex = UsageHistoryService.BuildFromFilesForTesting(ProviderId.Codex, new[] { path }, now);
                var go = UsageHistoryService.BuildFromFilesForTesting(ProviderId.OpenCodeGo, new[] { path }, now);

                var codexModel = Assert.Single(codex.Today!.ModelBreakdown!.Models);
                Assert.Equal("gpt-5", codexModel.Model);
                Assert.Equal(130UL, codexModel.TotalTokens);

                var goModel = Assert.Single(go.Today!.ModelBreakdown!.Models);
                Assert.Equal("kimi-k2.7-code", goModel.Model);
                Assert.Equal(250UL, goModel.TotalTokens);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void GrokEvents_ArePricedFromTheCatalog()
        {
            var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                "{\"ts\":\"2026-08-05T10:00:00Z\",\"pid\":100,\"msg\":\"model\",\"model\":\"grok-3\"}",
                "{\"ts\":\"2026-08-05T10:01:00Z\",\"pid\":100,\"msg\":\"shell.turn.inference_done\",\"ctx\":{\"prompt_tokens\":1000,\"cached_prompt_tokens\":0,\"completion_tokens\":200,\"reasoning_tokens\":0}}",
            };

            var history = UsageHistoryService.BuildFromLines(ProviderId.Grok, lines, now);

            var model = Assert.Single(history.Today!.ModelBreakdown!.Models);
            Assert.Equal("grok-3", model.Model);
            Assert.Equal(1200UL, model.TotalTokens);
            Assert.NotNull(model.CostUsd);
            Assert.InRange(model.CostUsd!.Value, 0.0059, 0.0061);
        }

        [Fact]
        public void ClineSessions_FollowCompanionFileAndPreserveSurfaceSplit()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var metadataPath = Path.Combine(directory, "session.json");
                var messagesPath = Path.Combine(directory, "session.messages.json");
                var timestamp = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                File.WriteAllText(metadataPath, """
                    {"provider":"cline-pass","session_id":"s1","model":"fallback","messages_path":"session.messages.json"}
                    """);
                File.WriteAllText(messagesPath, $$"""
                    [
                      {
                        "id":"m1",
                        "role":"assistant",
                        "ts":{{timestamp}},
                        "modelInfo":{"id":"cline-pass/kimi-k3","provider":"cline-pass"},
                        "metrics":{"inputTokens":100,"cacheReadTokens":30,"cacheWriteTokens":20,"outputTokens":40,"cost":0.25}
                      },
                      {
                        "id":"m1",
                        "role":"assistant",
                        "ts":{{timestamp}},
                        "modelInfo":{"id":"cline-pass/kimi-k3","provider":"cline-pass"},
                        "metrics":{"inputTokens":100,"cacheReadTokens":30,"cacheWriteTokens":20,"outputTokens":40,"cost":0.25}
                      }
                    ]
                    """);

                var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
                var pass = UsageHistoryService.BuildFromFilesForTesting(ProviderId.ClinePass, new[] { metadataPath }, now);
                var payAsYouGo = UsageHistoryService.BuildFromFilesForTesting(ProviderId.Cline, new[] { metadataPath }, now);

                Assert.Equal(190UL, pass.Today!.Tokens);
                Assert.Equal(30UL, pass.Today.CachedInputTokens);
                Assert.Equal(20UL, pass.Today.CacheCreationTokens);
                Assert.Equal(0.25, pass.Today.EstimatedCostUsd!.Value, 6);
                Assert.False(pass.Today.CostEstimated);
                Assert.Equal(1, pass.Today.Records);
                Assert.Equal("cline-pass/kimi-k3", pass.Today.ModelBreakdown!.Models[0].Model);
                Assert.Null(payAsYouGo.Last90Days);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void ZaiModelUsage_SubtractsCachedPromptAndCountsReasoningOnce()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var path = Path.Combine(directory, "db.sqlite");
                var timestamp = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    connection.Open();
                    Execute(connection, """
                        CREATE TABLE model_usage (
                            id TEXT PRIMARY KEY,
                            session_id TEXT,
                            completed_at INTEGER,
                            started_at INTEGER,
                            model_id TEXT,
                            input_tokens INTEGER,
                            output_tokens INTEGER,
                            reasoning_tokens INTEGER,
                            cache_creation_input_tokens INTEGER,
                            cache_read_input_tokens INTEGER,
                            status TEXT
                        );
                        """);
                    using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO model_usage VALUES
                        ('r1','s1',$time,$time,'GLM-5.2',100,20,5,10,40,'completed');
                        """;
                    insert.Parameters.AddWithValue("$time", timestamp);
                    insert.ExecuteNonQuery();
                }

                var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
                var history = UsageHistoryService.BuildFromFilesForTesting(ProviderId.Zai, new[] { path }, now);

                Assert.Equal(125UL, history.Today!.Tokens);
                Assert.Equal(50UL, history.Today.UncachedInputTokens);
                Assert.Equal(40UL, history.Today.CachedInputTokens);
                Assert.Equal(10UL, history.Today.CacheCreationTokens);
                Assert.Equal(25UL, history.Today.OutputTokens);
                Assert.Equal(5UL, history.Today.ReasoningTokens);
                Assert.True(history.Today.CostEstimated);
                Assert.Equal("GLM-5.2", history.Today.ModelBreakdown!.Models[0].Model);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), $"taskbarquota-history-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void InsertOpenCodeMessage(
            SqliteConnection connection,
            string id,
            string sessionId,
            long timestamp,
            string provider,
            string model,
            ulong input,
            ulong output,
            ulong reasoning,
            ulong cacheRead,
            ulong cacheWrite,
            double cost)
        {
            var data = $$"""
                {
                  "role":"assistant",
                  "providerID":"{{provider}}",
                  "modelID":"{{model}}",
                  "time":{"created":{{timestamp}},"completed":{{timestamp}}},
                  "tokens":{"input":{{input}},"output":{{output}},"reasoning":{{reasoning}},"cache":{"read":{{cacheRead}},"write":{{cacheWrite}}},"total":{{input + output + reasoning + cacheRead + cacheWrite}}},
                  "cost":{{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                }
                """;
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO message VALUES ($id,$session,$time,$data);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$time", timestamp);
            command.Parameters.AddWithValue("$data", data);
            command.ExecuteNonQuery();
        }
    }
}
