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
}
