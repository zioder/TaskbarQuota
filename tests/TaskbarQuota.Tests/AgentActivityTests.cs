using System;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using TaskbarQuota.AgentActivity;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests;

public sealed class AgentActivityTests
{
    [Fact]
    public void PrimaryPrefersLiveAgentOverNewerCompletedItem()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new AgentActivitySnapshot(new[]
        {
            new AgentActivityItem("done", ProviderId.Claude, "Auth", "Finished", AgentActivityStatus.Completed, now.AddMinutes(-2), now),
            new AgentActivityItem("live", ProviderId.Codex, "Tests", "Running tests", AgentActivityStatus.Working, now.AddMinutes(-1), now.AddSeconds(-1)),
        });

        Assert.Equal("live", snapshot.Primary?.Id);
        Assert.True(snapshot.HasUnreadCompletions);
    }

    [Fact]
    public void WaitingAgentIsLiveButFailureIsNot()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new AgentActivitySnapshot(new[]
        {
            new AgentActivityItem("waiting", ProviderId.Cursor, "Review", "Waiting for approval", AgentActivityStatus.Waiting, now, now),
            new AgentActivityItem("failed", ProviderId.Cline, "Build", "Build failed", AgentActivityStatus.Failed, now, now),
        });

        Assert.True(snapshot.HasLiveItems);
        Assert.Equal("waiting", snapshot.Primary?.Id);
    }

    [Fact]
    public void ClineSession_ExtractsTitleToolActionAndWorkingState()
    {
        var root = Path.Combine(Path.GetTempPath(), "taskbarquota-cline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var metadataPath = Path.Combine(root, "cline-1.json");
            var messagesPath = Path.Combine(root, "cline-1.messages.json");
            File.WriteAllText(metadataPath, $$"""
                {
                  "session_id": "cline-1",
                  "started_at": "{{now:O}}",
                  "status": "running",
                  "model": "cline/kimi-k3",
                  "prompt": "<user_input mode=\"act\">Run the build checks</user_input>",
                  "metadata": { "title": "Run the build checks" }
                }
                """);
            File.WriteAllText(messagesPath, $$"""
                {
                  "messages": [
                    { "role": "user", "content": [{ "type": "text", "text": "Run the build checks" }], "ts": {{now.ToUnixTimeMilliseconds()}} },
                    { "role": "assistant", "content": [{ "type": "thinking", "thinking": "Checking the project" }], "ts": {{now.ToUnixTimeMilliseconds()}} },
                    { "role": "assistant", "content": [{ "type": "tool_use", "name": "shell_command", "input": { "command": "dotnet test" } }], "ts": {{now.ToUnixTimeMilliseconds()}} }
                  ]
                }
                """);

            var item = AgentActivityScanner.ReadClineForTesting(metadataPath);

            Assert.NotNull(item);
            Assert.Equal(ProviderId.Cline, item.Provider);
            Assert.Equal("Run the build checks", item.Title);
            Assert.Equal("Ran tests", item.Step);
            Assert.Equal(AgentActivityStatus.Working, item.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KimiSession_ExtractsTitleToolActionAndSubagentCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "taskbarquota-kimi-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "session_kimi-1");
        var main = Path.Combine(session, "agents", "main");
        Directory.CreateDirectory(main);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var statePath = Path.Combine(session, "state.json");
            File.WriteAllText(statePath, $$"""
                {
                  "createdAt": "{{now:O}}",
                  "updatedAt": "{{now:O}}",
                  "title": "New Session",
                  "agents": {
                    "main": { "type": "main" },
                    "worker": { "type": "subagent" }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(main, "wire.jsonl"), $$"""
                {"type":"user_input","text":"Inspect the build" ,"time":{{now.ToUnixTimeMilliseconds()}}}
                {"type":"tool_call","name":"shell","arguments":{"command":"dotnet test"},"time":{{now.ToUnixTimeMilliseconds()}}}
                """);

            var item = AgentActivityScanner.ReadKimiForTesting(statePath);

            Assert.NotNull(item);
            Assert.Equal(ProviderId.Kimi, item.Provider);
            Assert.Equal("Inspect the build", item.Title);
            Assert.Equal("Ran tests", item.Step);
            Assert.Equal(AgentActivityStatus.Working, item.Status);
            Assert.Equal(1, item.SubagentCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyKimiSession_ExtractsDirectWireLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "taskbarquota-kimi-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var statePath = Path.Combine(root, "state.json");
            File.WriteAllText(statePath, $$"""
                { "created_at": "{{now:O}}", "title": "Legacy session" }
                """);
            File.WriteAllText(Path.Combine(root, "wire.jsonl"), $$"""
                {"type":"user","text":"Review the legacy session","timestamp":{{now.ToUnixTimeMilliseconds()}}}
                {"type":"tool_call","name":"Read","timestamp":{{now.ToUnixTimeMilliseconds()}}}
                """);

            var item = AgentActivityScanner.ReadKimiForTesting(statePath);

            Assert.NotNull(item);
            Assert.Equal(ProviderId.Kimi, item.Provider);
            Assert.Equal("Legacy session", item.Title);
            Assert.Equal("Inspected files", item.Step);
            Assert.Equal(AgentActivityStatus.Working, item.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClaudeTranscript_ExtractsPromptModelAndToolAction()
    {
        var transcript = string.Join('\n',
            "{\"type\":\"user\",\"sessionId\":\"claude-thread\",\"timestamp\":\"2026-08-01T12:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"Refactor the activity scanner and add tests\"}}",
            "{\"type\":\"assistant\",\"sessionId\":\"claude-thread\",\"timestamp\":\"2026-08-01T12:00:02Z\",\"message\":{\"model\":\"claude-sonnet-4\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"AgentActivityScanner.cs\"}}]}}" );

        var info = AgentActivityScanner.ParseTranscriptForTesting(ProviderId.Claude, transcript);

        Assert.Equal("claude-thread", info.ThreadId);
        Assert.Equal("Refactor the activity scanner and add tests", info.Prompt);
        Assert.Equal("claude-sonnet-4", info.Model);
        Assert.Equal("Edited code", info.Step);
        Assert.Equal(AgentActivityScanner.TranscriptState.Action, info.State);
    }

    [Fact]
    public void ClaudeTranscript_AskUserToolBecomesWaiting()
    {
        var transcript = "{\"type\":\"assistant\",\"sessionId\":\"claude-thread\",\"timestamp\":\"2026-08-01T12:00:02Z\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"AskUserQuestion\",\"input\":{}}]}}";

        var info = AgentActivityScanner.ParseTranscriptForTesting(ProviderId.Claude, transcript);

        Assert.Equal("Waiting for input", info.Step);
        Assert.Equal(AgentActivityScanner.TranscriptState.Waiting, info.State);
    }

    [Theory]
    [InlineData("t3code_desktop", "T3 Code")]
    [InlineData("synara", "Synara")]
    public void ClaudeTranscript_ExtractsHost(string originator, string expectedHost)
    {
        var transcript = $"{{\"type\":\"user\",\"originator\":\"{originator}\",\"sessionId\":\"claude-thread\",\"timestamp\":\"2026-08-01T12:00:00Z\",\"message\":{{\"content\":\"Identify the host\"}}}}";

        var info = AgentActivityScanner.ParseTranscriptForTesting(ProviderId.Claude, transcript);

        Assert.Equal(expectedHost, info.Host);
    }

    [Fact]
    public void CodexTranscript_ExtractsPromptAndShellAction()
    {
        var transcript = string.Join('\n',
            "{\"timestamp\":\"2026-08-01T11:59:59Z\",\"payload\":{\"type\":\"session_meta\",\"id\":\"codex-thread\"}}",
            "{\"timestamp\":\"2026-08-01T12:00:00Z\",\"payload\":{\"type\":\"user_message\",\"id\":\"codex-thread\",\"message\":\"Run the tests for the scanner\"}}",
            "{\"timestamp\":\"2026-08-01T12:00:02Z\",\"payload\":{\"type\":\"custom_tool_call\",\"id\":\"tool-1\",\"name\":\"shell_command\",\"arguments\":\"dotnet test\"}}" );

        var info = AgentActivityScanner.ParseTranscriptForTesting(ProviderId.Codex, transcript);

        Assert.Equal("codex-thread", info.ThreadId);
        Assert.Equal("Run the tests for the scanner", info.Prompt);
        Assert.Equal("Ran tests", info.Step);
        Assert.Equal(AgentActivityScanner.TranscriptState.Action, info.State);
    }

    [Fact]
    public void OpenCodeDatabase_ExtractsSessionPromptModelAndRunningTool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskbarquota-opencode-{Guid.NewGuid():N}.db");
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, title TEXT, model TEXT, time_created INTEGER, time_updated INTEGER);
                    CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, data TEXT, time_created INTEGER, time_updated INTEGER);
                    CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT, session_id TEXT, data TEXT, time_created INTEGER, time_updated INTEGER);
                    """;
                command.ExecuteNonQuery();

                command.Parameters.Clear();
                command.CommandText = "INSERT INTO session VALUES ($id, NULL, $title, $model, $created, $updated);";
                command.Parameters.AddWithValue("$id", "ses_1");
                command.Parameters.AddWithValue("$title", "Refactor activity scanner");
                command.Parameters.AddWithValue("$model", "{\"id\":\"gpt-5\",\"providerID\":\"openai\"}");
                command.Parameters.AddWithValue("$created", now - 5_000);
                command.Parameters.AddWithValue("$updated", now);
                command.ExecuteNonQuery();

                command.Parameters.Clear();
                command.CommandText = "INSERT INTO message VALUES ($id, 'ses_1', $data, $created, $updated);";
                command.Parameters.AddWithValue("$id", "msg_user");
                command.Parameters.AddWithValue("$data", $"{{\"role\":\"user\",\"time\":{{\"created\":{now - 2_000}}}}}");
                command.Parameters.AddWithValue("$created", now - 2_000);
                command.Parameters.AddWithValue("$updated", now - 2_000);
                command.ExecuteNonQuery();

                command.Parameters.Clear();
                command.CommandText = "INSERT INTO message VALUES ($id, 'ses_1', $data, $created, $updated);";
                command.Parameters.AddWithValue("$id", "msg_assistant");
                command.Parameters.AddWithValue("$data", $"{{\"role\":\"assistant\",\"time\":{{\"created\":{now - 1_000}}},\"finish\":\"tool-calls\",\"modelID\":\"gpt-5\",\"providerID\":\"openai\"}}");
                command.Parameters.AddWithValue("$created", now - 1_000);
                command.Parameters.AddWithValue("$updated", now - 1_000);
                command.ExecuteNonQuery();

                command.Parameters.Clear();
                command.CommandText = "INSERT INTO part VALUES ($id, 'msg_user', 'ses_1', $data, $created, $updated);";
                command.Parameters.AddWithValue("$id", "part_prompt");
                command.Parameters.AddWithValue("$data", "{\"type\":\"text\",\"text\":\"Run the tests for the scanner\"}");
                command.Parameters.AddWithValue("$created", now - 2_000);
                command.Parameters.AddWithValue("$updated", now - 2_000);
                command.ExecuteNonQuery();

                command.Parameters.Clear();
                command.CommandText = "INSERT INTO part VALUES ($id, 'msg_assistant', 'ses_1', $data, $created, $updated);";
                command.Parameters.AddWithValue("$id", "part_tool");
                command.Parameters.AddWithValue("$data", $"{{\"type\":\"tool\",\"tool\":\"bash\",\"state\":{{\"status\":\"running\",\"input\":{{\"command\":\"dotnet test\"}},\"time\":{{\"start\":{now - 500}}}}}}}");
                command.Parameters.AddWithValue("$created", now - 500);
                command.Parameters.AddWithValue("$updated", now - 500);
                command.ExecuteNonQuery();
            }

            var item = Assert.Single(AgentActivityScanner.ReadOpenCodeForTesting(path));

            Assert.Equal(ProviderId.OpenCode, item.Provider);
            Assert.Equal("Refactor activity scanner", item.Title);
            Assert.Equal("Run the tests for the scanner", item.Detail);
            Assert.Equal("gpt-5", item.Model);
            Assert.Equal("Ran tests", item.Step);
            Assert.Equal(AgentActivityStatus.Working, item.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void GrokSession_ExtractsPromptModelAndRunningTool()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"taskbarquota-grok-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            File.WriteAllText(Path.Combine(directory, "summary.json"), $$"""
                {
                  "info": { "id": "grok-session-1", "cwd": "C:\\work" },
                  "session_summary": "",
                  "created_at": "{{now}}",
                  "updated_at": "{{now}}",
                  "current_model_id": "grok-4.5",
                  "agent_name": "grok-build"
                }
                """);
            File.WriteAllText(Path.Combine(directory, "chat_history.jsonl"), $$"""
                {"type":"system","content":"system prompt"}
                {"type":"user","content":[{"type":"text","text":"Refactor Grok activity tracking"}]}
                {"type":"assistant","content":"","tool_calls":[{"id":"call-1","name":"shell_command","arguments":"dotnet test"}]}
                """);
            File.WriteAllText(Path.Combine(directory, "events.jsonl"), $$"""
                {"type":"turn_started","ts":"{{now}}","session_id":"grok-session-1"}
                {"type":"tool_started","ts":"{{now}}","tool_name":"shell_command"}
                """);

            var item = Assert.Single(AgentActivityScanner.ReadGrokForTesting(
                Path.Combine(directory, "summary.json")));

            Assert.Equal(ProviderId.Grok, item.Provider);
            Assert.Equal("grok-session-1", item.ThreadId);
            Assert.Equal("Refactor Grok activity tracking", item.Title);
            Assert.Equal("Refactor Grok activity tracking", item.Detail);
            Assert.Equal("grok-4.5", item.Model);
            Assert.Equal("Running command", item.Step);
            Assert.Equal(AgentActivityStatus.Working, item.Status);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AntigravityMetadata_ExtractsPreviewAndWorkspace()
    {
        var metadata = $$"""
            {
              "conversations": {
                "agy-1": {
                  "summary": {
                    "ID": "agy-1",
                    "Title": "",
                    "Preview": "Refactor Antigravity activity tracking",
                    "WorkspaceURIs": ["file:///c:/work/TaskbarQuota"]
                  }
                }
              }
            }
            """;

        var parsed = AgentActivityScanner.ParseAntigravityMetadataForTesting(metadata, "agy-1");

        Assert.NotNull(parsed);
        Assert.Equal("Refactor Antigravity activity tracking", parsed!.Value.Preview);
        Assert.Equal("C:\\work\\TaskbarQuota", parsed.Value.Workspace);
    }

    [Fact]
    public void AntigravityDatabase_ExtractsPromptModelToolAndSubagent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskbarquota-antigravity-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE steps (
                        idx INTEGER PRIMARY KEY,
                        step_type INTEGER NOT NULL DEFAULT 0,
                        status INTEGER NOT NULL DEFAULT 0,
                        has_subtrajectory NUMERIC NOT NULL DEFAULT false,
                        metadata BLOB,
                        error_details BLOB,
                        permissions BLOB,
                        task_details BLOB,
                        render_info BLOB,
                        step_payload BLOB,
                        step_format INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                command.ExecuteNonQuery();

                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO steps (idx, step_type, status, has_subtrajectory, step_payload)
                    VALUES (1, 5, 3, 1, $payload);
                    """;
                command.Parameters.AddWithValue("$payload", Encoding.UTF8.GetBytes("""
                    {"prompt":"Refactor Antigravity activity tracking","model":"gemini-3","toolName":"grep_search","toolSummary":"Grep for activation","toolAction":"Searching for activation"}
                    """));
                command.ExecuteNonQuery();
            }

            var item = Assert.Single(AgentActivityScanner.ReadAntigravityForTesting(path));

            Assert.Equal(ProviderId.Antigravity, item.Provider);
            Assert.Equal("Refactor Antigravity activity tracking", item.Title);
            Assert.Equal("Refactor Antigravity activity tracking", item.Detail);
            Assert.Equal("gemini-3", item.Model);
            Assert.Equal("Grep for activation", item.Step);
            Assert.Equal(1, item.SubagentCount);
            Assert.Equal(AgentActivityStatus.Working, item.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void AntigravityGuiTranscript_ExtractsPromptAndToolAction()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"taskbarquota-antigravity-gui-{Guid.NewGuid():N}",
            ".system_generated", "logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "transcript.jsonl");
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            File.WriteAllText(path, $$$"""
                {"step_index":0,"source":"user","type":"USER_INPUT","status":"SUCCESS","created_at":"{{{now}}}","content":"Refactor Antigravity GUI activity tracking"}
                {"step_index":1,"source":"agent","type":"PLANNER_RESPONSE","status":"SUCCESS","created_at":"{{{now}}}","tool_calls":[{"name":"grep_search","args":{"Query":"activation","toolAction":"Searching for activation","toolSummary":"Grep for activation"}}]}
                {"step_index":2,"source":"tool","type":"GREP_SEARCH","status":"SUCCESS","created_at":"{{{now}}}","content":"matches"}
                {"step_index":3,"source":"system","type":"CHECKPOINT","status":"DONE","created_at":"{{{now}}}","content":"# USER Objective:\nSystem Connectivity Test\n"}
                """);

            var item = Assert.Single(AgentActivityScanner.ReadAntigravityGuiForTesting(path));

            Assert.Equal(ProviderId.Antigravity, item.Provider);
            Assert.Equal("System Connectivity Test", item.Title);
            Assert.Equal("Refactor Antigravity GUI activity tracking", item.Detail);
            Assert.Equal("Inspected files", item.Step);
            Assert.Equal(AgentActivityStatus.Working, item.Status);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)!))
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ActivityTitle_IsSummarizedFromLatestPrompt()
    {
        var title = AgentActivityScanner.SummarizeTitleForTesting(
            "  # Add Claude parity\n\nPlease parse tool calls, session titles, and state colors consistently.");

        Assert.Equal("Add Claude parity", title);
    }

    [Fact]
    public void IdleAgentRemainsLiveAndHasItsOwnStateText()
    {
        var item = new AgentActivityItem(
            "idle", ProviderId.Claude, "Claude", "Waiting for the next prompt",
            AgentActivityStatus.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.True(item.IsLive);
        Assert.Equal("Idle", item.StatusText);
    }
}
