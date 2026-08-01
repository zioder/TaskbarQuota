using System;
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
