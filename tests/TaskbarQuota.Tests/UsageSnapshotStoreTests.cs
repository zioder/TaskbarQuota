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

    private static IUsageProvider Provider(ProviderId id) => new SnapshotProvider(id);

    private sealed class SnapshotProvider(ProviderId id) : IUsageProvider
    {
        public ProviderId Id => id;
        public string DisplayName => id.ToString();
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

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
    public void Save_ThenLoad_RestoresCreditWindowDisplayValues()
    {
        var usage = new UsageSnapshot(new RateWindow(20, 300))
        {
            Secondary = new RateWindow(25, 10080),
            Cost = new CostSnapshot(9_600, "USD", "Credits")
            {
                Limit = 12_000,
            },
            Pricing = new UsagePricingSnapshot("OFF-PEAK", 0.5),
        };
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult>
        {
            [ProviderId.Zai] = UsageResult.Success(
                ProviderId.Zai,
                Provider(ProviderId.Zai),
                new ProviderFetchResult(usage, "api")),
        });

        var restored = UsageSnapshotStore.Load(_dir, id => Provider(id))[ProviderId.Zai];

        Assert.Equal(20d, restored.Fetch!.Usage.Primary.UsedPercent, 0);
        Assert.Equal(25d, restored.Fetch.Usage.Secondary!.UsedPercent, 0);
        Assert.NotNull(restored.Fetch.Usage.Cost);
        Assert.Equal("Credits", restored.Fetch.Usage.Cost!.Label);
        Assert.Equal(9_600d, restored.Fetch.Usage.Cost.Amount, 0);
        Assert.Equal(12_000d, restored.Fetch.Usage.Cost.Limit!.Value, 0);
        Assert.Equal("OFF-PEAK", restored.Fetch.Usage.Pricing!.Period);
        Assert.Equal(0.5d, restored.Fetch.Usage.Pricing.Multiplier);
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

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-11, false)]
    [InlineData(1, false)]
    public void SaveThenLoad_PreservesOnlyRecentCachedResetCredits(int minutes, bool expectCredits)
    {
        var result = Result(ProviderId.Codex, 34, 50, DateTimeOffset.UtcNow.AddDays(1));
        var cachedAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
        result.Fetch!.Usage.ResetCredits = new ResetCreditsSnapshot(2, [], cachedAt);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult> { [ProviderId.Codex] = result });

        var restored = UsageSnapshotStore.Load(_dir, Provider)[ProviderId.Codex];
        if (expectCredits)
        {
            Assert.Equal(2, restored.Fetch!.Usage.ResetCredits!.AvailableCount);
            Assert.Equal(cachedAt, restored.Fetch.Usage.ResetCredits.CachedAt);
        }
        else
            Assert.Null(restored.Fetch!.Usage.ResetCredits);
        Assert.Equal(34, restored.Fetch!.Usage.Primary.UsedPercent);
    }

    [Fact]
    public void Load_DropsSnapshotNotConfirmedWithinMaxRestoreAge()
    {
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult>
        {
            [ProviderId.Cursor] = Result(ProviderId.Cursor, 10, 20, resetAt: null),
        });
        AgeSavedAt(DateTimeOffset.Now - UsageSnapshotStore.MaxRestoreAge - TimeSpan.FromMinutes(5));

        Assert.Empty(UsageSnapshotStore.Load(_dir, id => Provider(id)));
    }

    /// <summary>
    /// Rewrites the persisted confirmation stamp to exercise legacy files and the restore age gate.
    /// </summary>
    private void AgeSavedAt(DateTimeOffset savedAt)
    {
        var path = Path.Combine(_dir, "usage-snapshots.json");
        var json = File.ReadAllText(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var entry in node["Entries"]!.AsArray())
            entry!["SavedAt"] = savedAt;
        File.WriteAllText(path, node.ToJsonString());
    }

    [Fact]
    public void AsFresh_ClearsStaleMarkOnceALiveFetchConfirmsTheValues()
    {
        var restored = Result(ProviderId.Codex, 42.5, 61, DateTimeOffset.Now.AddHours(3)).AsStale();

        Assert.True(restored.IsStale);
        Assert.Equal(UsageObservationOrigin.RestoredSnapshot, restored.ObservationOrigin);
        Assert.False(restored.AsFresh().IsStale);
        // FetchedAt means "when the values last changed", so confirming them must not restamp it —
        // the widget's "Last updated" line reads from here.
        Assert.Same(restored.Fetch, restored.AsFresh().Fetch);
    }

    [Fact]
    public void Load_KeepsAnEntryConfirmedRecentlyEvenWhenItsValuesAreOld()
    {
        // Usage that has not moved in days keeps its original FetchedAt, but the previous session
        // re-confirmed it live just before shutdown. Expiry must follow the confirmation, not the value.
        var valueChangedAt = DateTimeOffset.Now - UsageSnapshotStore.MaxRestoreAge - TimeSpan.FromHours(6);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult>
        {
            [ProviderId.Codex] = Result(ProviderId.Codex, 10, 20, resetAt: DateTimeOffset.Now.AddHours(3), fetchedAt: valueChangedAt),
        });

        var restored = UsageSnapshotStore.Load(_dir, id => Provider(id));

        Assert.True(restored.ContainsKey(ProviderId.Codex));
        Assert.Equal(valueChangedAt, restored[ProviderId.Codex].Fetch!.FetchedAt);
    }

    [Fact]
    public void SavingAnotherProvider_DoesNotRenewRestoredConfirmation()
    {
        var confirmedAt = DateTimeOffset.Now.AddHours(-23);
        var original = Result(ProviderId.Claude, 10, 20, resetAt: null)
            .AsLiveObservation(1, confirmedAt);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult> { [ProviderId.Claude] = original });
        var restored = UsageSnapshotStore.Load(_dir, Provider)[ProviderId.Claude];
        Assert.Equal(confirmedAt, restored.LastConfirmedAt);

        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult>
        {
            [ProviderId.Claude] = restored,
            [ProviderId.Codex] = Result(ProviderId.Codex, 34, 50, resetAt: null),
        });
        var again = UsageSnapshotStore.Load(_dir, Provider);
        Assert.Equal(confirmedAt, again[ProviderId.Claude].LastConfirmedAt);

        var path = Path.Combine(_dir, "usage-snapshots.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var entry in node["Entries"]!.AsArray())
            if (entry!["Provider"]!.GetValue<string>() == nameof(ProviderId.Claude))
                entry["SavedAt"] = confirmedAt.AddHours(-2);
        File.WriteAllText(path, node.ToJsonString());
        var afterExpiry = UsageSnapshotStore.Load(_dir, Provider);
        Assert.False(afterExpiry.ContainsKey(ProviderId.Claude));
        Assert.True(afterExpiry.ContainsKey(ProviderId.Codex));
    }

    [Fact]
    public void SaveThenLoad_FreshResetCreditsBecomeLastKnownAndExpiredGrantsArePruned()
    {
        var now = DateTimeOffset.UtcNow;
        var confirmation = now.AddMinutes(-1);
        var result = Result(ProviderId.Codex, 34, 50, resetAt: null);
        result.Fetch!.Usage.ResetCredits = new ResetCreditsSnapshot(2,
        [
            new("available", now.AddDays(-1), now.AddSeconds(-1)),
            new("available", now.AddDays(-1), now.AddDays(1)),
        ], confirmedAt: confirmation);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult> { [ProviderId.Codex] = result });

        var restored = UsageSnapshotStore.Load(_dir, Provider)[ProviderId.Codex];
        Assert.Equal(1, restored.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.Single(restored.Fetch.Usage.ResetCredits.Credits);
        Assert.True(restored.Fetch.Usage.ResetCredits.IsCached);
        Assert.Equal(confirmation, restored.Fetch.Usage.ResetCredits.CachedAt);
        Assert.False(result.Fetch.Usage.ResetCredits.IsCached);
        Assert.Equal(2, result.Fetch.Usage.ResetCredits.AvailableCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_DiscardsOldOrUnstampedFreshCreditsWithoutLosingQuota(bool confirmed)
    {
        var result = Result(ProviderId.Codex, 34, 50, resetAt: null);
        result.Fetch!.Usage.ResetCredits = new ResetCreditsSnapshot(2, [],
            confirmedAt: confirmed ? DateTimeOffset.UtcNow.AddMinutes(-11) : null);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult> { [ProviderId.Codex] = result });

        var restored = UsageSnapshotStore.Load(_dir, Provider)[ProviderId.Codex];
        Assert.Null(restored.Fetch!.Usage.ResetCredits);
        Assert.Equal(34, restored.Fetch.Usage.Primary.UsedPercent);
    }

    [Fact]
    public void LegacyFileWithoutSavedAt_RetainsFetchedAtAsConfirmation()
    {
        var fetchedAt = DateTimeOffset.Now.AddHours(-2);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult>
        {
            [ProviderId.Codex] = Result(ProviderId.Codex, 34, 50, resetAt: null, fetchedAt: fetchedAt),
        });
        var path = Path.Combine(_dir, "usage-snapshots.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var entry in node["Entries"]!.AsArray())
            entry!.AsObject().Remove("SavedAt");
        File.WriteAllText(path, node.ToJsonString());

        var restored = UsageSnapshotStore.Load(_dir, Provider)[ProviderId.Codex];
        Assert.Equal(fetchedAt, restored.LastConfirmedAt);
        UsageSnapshotStore.Save(_dir, new Dictionary<ProviderId, UsageResult> { [ProviderId.Codex] = restored });
        Assert.Equal(fetchedAt, UsageSnapshotStore.Load(_dir, Provider)[ProviderId.Codex].LastConfirmedAt);
    }

    [Fact]
    public async Task BackgroundSaves_WaitForThePreviousSnapshotBeforePublishingNewerValues()
    {
        var service = new UsageService(_dir);
        service.Register(new CountingProvider());
        var field = typeof(UsageService).GetField("_snapshotWriteTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        field.SetValue(service, release.Task);
        Task writes = Task.CompletedTask;
        try
        {
            await service.FetchAsync(ProviderId.Copilot, force: true);
            await service.FetchAsync(ProviderId.Copilot, force: true);
            writes = (Task)field.GetValue(service)!;
            Assert.False(writes.IsCompleted);
            Assert.False(File.Exists(Path.Combine(_dir, "usage-snapshots.json")));
        }
        finally
        {
            release.TrySetResult();
            writes = (Task)field.GetValue(service)!;
            await writes.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var restored = UsageSnapshotStore.Load(_dir, Provider)[ProviderId.Copilot];
        Assert.Equal(2, restored.Fetch!.Usage.Primary.UsedPercent);
    }

    private sealed class CountingProvider : IUsageProvider
    {
        private int _calls;
        public Exception? Error { get; init; }
        public ProviderId Id => ProviderId.Copilot;
        public string DisplayName => "Copilot";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => Error is { } error ? Task.FromException<ProviderFetchResult>(error)
                : Task.FromResult(new ProviderFetchResult(new UsageSnapshot(new RateWindow(++_calls)), "test"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedAttempts_StillHaveMonotonicSequenceAndAttemptTime(bool providerException)
    {
        var service = new UsageService();
        service.Register(new CountingProvider
        {
            Error = providerException
                ? new ProviderException(ProviderErrorKind.Other, "test failure")
                : new InvalidOperationException("test failure"),
        });

        var first = await service.FetchAsync(ProviderId.Copilot, force: true);
        var second = await service.FetchAsync(ProviderId.Copilot, force: true);
        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.True(first.ObservationSequence > 0);
        Assert.True(second.ObservationSequence > first.ObservationSequence);
        Assert.NotNull(first.ObservedAt);
        Assert.NotNull(second.ObservedAt);
        Assert.Null(second.LastConfirmedAt);
        Assert.Equal(UsageObservationOrigin.FailureFallback, second.ObservationOrigin);
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
