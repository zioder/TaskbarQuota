using System;
using System.Collections.Generic;
using System.IO;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests;

/// <summary>
/// The taskbar widget hydrates from these persisted snapshots at boot (issue #21), so a restored value
/// must survive the round trip intact, be marked stale, and be dropped once it can no longer be true.
/// </summary>
public class UsageSnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tbq-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static IUsageProvider Provider(ProviderId id) => new UsageService().Get(id)!;

    private static UsageResult Result(ProviderId id, double session, double weekly, DateTimeOffset? resetAt, DateTimeOffset? fetchedAt = null)
    {
        var usage = new UsageSnapshot(new RateWindow(session, 300, resetAt, "resets soon"))
        {
            Secondary = new RateWindow(weekly, 10080, resetAt),
            LoginMethod = "Pro",
            Email = "user@example.com",
        };
        return UsageResult.Success(id, Provider(id), new ProviderFetchResult(usage, "cli", fetchedAt));
    }

    [Fact]
    public void Save_ThenLoad_RestoresUsageValuesMarkedStale()
    {
        var resetAt = DateTimeOffset.Now.AddHours(3);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult>
        {
            [ProviderId.Codex] = Result(ProviderId.Codex, 42.5, 61, resetAt),
        });

        var restored = UsageSnapshotStore.Load(_dir, id => Provider(id));

        var codex = Assert.Contains(ProviderId.Codex, (IDictionary<ProviderId, UsageResult>)restored);
        Assert.True(codex.Ok);
        Assert.True(codex.IsStale);
        Assert.Equal(42.5, codex.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(61, codex.Fetch!.Usage.Secondary!.UsedPercent);
        Assert.Equal("Pro", codex.Fetch!.Usage.LoginMethod);
        Assert.Equal("cli", codex.Fetch!.SourceLabel);
    }

    [Fact]
    public void Load_DropsSnapshotWhoseWindowAlreadyReset()
    {
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult>
        {
            [ProviderId.Claude] = Result(ProviderId.Claude, 80, 90, DateTimeOffset.Now.AddMinutes(-1)),
        });

        Assert.Empty(UsageSnapshotStore.Load(_dir, id => Provider(id)));
    }

    [Fact]
    public void Load_DropsSnapshotOlderThanMaxRestoreAge()
    {
        var stale = DateTimeOffset.Now - UsageSnapshotStore.MaxRestoreAge - TimeSpan.FromMinutes(5);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult>
        {
            [ProviderId.Cursor] = Result(ProviderId.Cursor, 10, 20, resetAt: null, fetchedAt: stale),
        });

        Assert.Empty(UsageSnapshotStore.Load(_dir, id => Provider(id)));
    }

    [Fact]
    public void AsFresh_ClearsStaleMarkOnceALiveFetchConfirmsTheValues()
    {
        var restored = Result(ProviderId.Codex, 42.5, 61, DateTimeOffset.Now.AddHours(3)).AsStale();

        Assert.True(restored.IsStale);
        Assert.False(restored.AsFresh().IsStale);
        Assert.Same(restored.Fetch, restored.AsFresh().Fetch);
    }

    [Fact]
    public void Pending_IsFlaggedSoTheWidgetCanTellLoadingFromFailure()
    {
        var pending = UsageResult.Pending(ProviderId.Codex, Provider(ProviderId.Codex), "Loading...");
        var failure = UsageResult.Failure(ProviderId.Codex, "boom", Provider(ProviderId.Codex));

        Assert.True(pending.IsPending);
        Assert.False(failure.IsPending);
        Assert.False(pending.Ok);
        Assert.True(pending.WithSource(null).IsPending);
    }
}
