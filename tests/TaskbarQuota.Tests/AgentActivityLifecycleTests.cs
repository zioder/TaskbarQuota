using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaskbarQuota;
using TaskbarQuota.AgentActivity;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests;

public sealed class AgentActivityLifecycleTests
{
    [Fact]
    public void SuccessfulScan_RemovesScannerItemsMissingFromNextSnapshot()
    {
        var service = new AgentActivityService();
        service.ApplyScan([Item("live", AgentActivityStatus.Working)]);

        service.ApplyScan([]);

        Assert.Empty(service.Snapshot.Items);
        Assert.False(service.Snapshot.HasLiveItems);
    }

    [Fact]
    public void SuccessfulScan_DoesNotRetainExpiredCompletion()
    {
        var service = new AgentActivityService();
        var old = Item("old", AgentActivityStatus.Completed) with
        {
            UpdatedAt = DateTimeOffset.Now - AgentActivityService.CompletedRetention - TimeSpan.FromSeconds(1),
        };

        service.ApplyScan([old]);

        Assert.Empty(service.Snapshot.Items);
    }

    [Fact]
    public void AcknowledgedCompletion_RemainsHiddenWhenScannerReturnsItAgain()
    {
        var service = new AgentActivityService();
        var completed = Item("done", AgentActivityStatus.Completed);
        service.ApplyScan([completed]);
        service.Acknowledge("done");

        service.ApplyScan([completed]);

        Assert.Empty(service.Snapshot.Items);
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotBlockOtherSubscribers()
    {
        var service = new AgentActivityService();
        var observed = 0;
        service.Changed += _ => throw new InvalidOperationException("test");
        service.Changed += _ => observed++;

        service.ApplyScan([Item("live", AgentActivityStatus.Working)]);

        Assert.Equal(1, observed);
    }

    [Fact]
    public void ItemsForDisplay_MovesSelectedAgentToFront()
    {
        var snapshot = new AgentActivitySnapshot([
            Item("first", AgentActivityStatus.Working),
            Item("selected", AgentActivityStatus.Waiting),
            Item("last", AgentActivityStatus.Completed),
        ]);

        Assert.Equal(["selected", "first", "last"], snapshot.ItemsForDisplay("selected").Select(item => item.Id));
        Assert.Equal(["first", "selected", "last"], snapshot.ItemsForDisplay("missing").Select(item => item.Id));
    }

    [Fact]
    public void CandidateSelection_AppliesLimitPerProvider()
    {
        var now = DateTimeOffset.Now;
        var candidates = Enumerable.Range(0, 25)
            .Select(index => (ProviderId.Codex, $"codex-{index}", now.AddSeconds(-index)))
            .Concat(Enumerable.Range(0, 3)
                .Select(index => (ProviderId.Claude, $"claude-{index}", now.AddMinutes(-10).AddSeconds(-index))))
            .ToArray();

        var selected = AgentActivityScanner.SelectCandidates(candidates, maxPerProvider: 20);

        Assert.Equal(20, selected.Count(item => item.Provider == ProviderId.Codex));
        Assert.Equal(3, selected.Count(item => item.Provider == ProviderId.Claude));
    }

    [Theory]
    [InlineData("powershell", "powershell.exe -Command rg codex", null)]
    [InlineData("node", "node.exe C:\\tools\\unrelated.js --query cline", null)]
    [InlineData("codex", "codex.exe exec", ProviderId.Codex)]
    [InlineData("node", "node.exe C:\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js", ProviderId.Claude)]
    public void TerminalDetection_RequiresExecutableOrKnownPackage(
        string executable, string commandLine, ProviderId? expected)
        => Assert.Equal(expected, AgentActivityScanner.DetectTerminalProviderForTesting(executable, commandLine));

    [Fact]
    public void ClineSession_UsesFilenameWhenSessionIdIsBlank()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "fallback-session.json");
            File.WriteAllText(path, "{\"session_id\":\"\",\"metadata\":{\"title\":\"Fallback\"}}");

            var item = AgentActivityScanner.ReadClineForTesting(path);

            Assert.Equal("cline:fallback-session", item?.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CodexThreadIndex_SkipsMalformedAndWrongTypeRows()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "session_index.jsonl");
            File.WriteAllLines(path,
            [
                "{\"id\":42,\"thread_name\":false}",
                "{not-finished",
                "{\"id\":\"thread-1\",\"thread_name\":\"Useful name\"}",
            ]);
            var resolver = new CodexThreadNameResolver(path);

            Assert.Equal("Useful name", resolver.GetName("thread-1"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProviderSwitchDebounce_RequiresConsecutiveMatchingSamples()
    {
        ProviderId? pending = null;
        var samples = 0;

        Assert.False(UsageCoordinator.ShouldAcceptDetectedProvider(
            ref pending, ref samples, ProviderId.Claude, requiredSamples: 2));
        Assert.False(UsageCoordinator.ShouldAcceptDetectedProvider(
            ref pending, ref samples, ProviderId.Codex, requiredSamples: 2));
        Assert.True(UsageCoordinator.ShouldAcceptDetectedProvider(
            ref pending, ref samples, ProviderId.Codex, requiredSamples: 2));
        Assert.Null(pending);
        Assert.Equal(0, samples);
    }

    private static AgentActivityItem Item(string id, AgentActivityStatus status)
    {
        var now = DateTimeOffset.Now;
        return new AgentActivityItem(id, ProviderId.Codex, id, status.ToString(), status, now, now);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TaskbarQuotaTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
