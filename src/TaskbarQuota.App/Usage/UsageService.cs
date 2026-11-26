using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Usage
{
    /// <summary>Registry of providers with TTL caching for successful and failed fetches.</summary>
    public sealed class UsageService
    {
        internal static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(45);
        internal static readonly TimeSpan RateLimitedCacheTtl = TimeSpan.FromMinutes(5);

        private readonly Dictionary<ProviderId, IUsageProvider> _providers = new();
        private readonly Dictionary<ProviderId, CacheEntry> _cache = new();
        private readonly Dictionary<ProviderId, UsageResult> _lastSuccessfulLiveResults = new();
        private readonly Dictionary<(ProviderId, string?), CacheEntry> _completedResults = new();
        private readonly Dictionary<ProviderId, SortedSet<long>> _inFlightSequences = new();
        private readonly Dictionary<ProviderId, UsagePublicationOrder> _publicationFrontiers = new();
        private readonly object _lock = new();
        private readonly string? _snapshotDirectory;
        private readonly Action<UsageResult>? _fetchObserver;
        private long _nextObservationSequence;
        private Task _snapshotWriteTask = Task.CompletedTask;
        private readonly Dictionary<ProviderId, DateTimeOffset> _lastSnapshotQueuedAt = new();

        // How long a persisted snapshot may keep its original timestamp while the usage values are
        // unchanged. Well inside UsageSnapshotStore.MaxRestoreAge, so a still-live entry can never age out
        // on disk, while unchanged polls still avoid a write on almost every tick.
        private static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromHours(1);

        // Serializes snapshot writes; see QueueSnapshotSave.
        private static readonly SemaphoreSlim SnapshotWriteGate = new(1, 1);

        /// <param name="snapshotDirectory">
        /// Where the last-good snapshots are persisted so the widget can render real numbers at boot
        /// (issue #21). Null disables persistence — the default, so tests never touch %LOCALAPPDATA%.
        /// </param>
        /// <param name="fetchObserver">
        /// Optional bookkeeping for actual provider attempts, before cache/history fallback. Cache reads,
        /// restored snapshots and caller cancellation do not notify it; observer failures are isolated.
        /// </param>
        public UsageService(string? snapshotDirectory = null, Action<UsageResult>? fetchObserver = null)
        {
            _snapshotDirectory = snapshotDirectory;
            _fetchObserver = fetchObserver;
            Register(new CodexProvider());
            Register(new CopilotProvider());
            Register(new ClaudeProvider());
            Register(new AntigravityProvider());
            Register(new CursorProvider());
            Register(new OpenCodeProvider());
            Register(new OpenCodeGoProvider());
            Register(new GrokProvider());
            Register(new DevinProvider());
            Register(new ClineProvider());
            Register(new ClinePassProvider());
            Register(new ZaiProvider());
            Register(new KimiProvider());

            // Rehydrate the previous session's values so the widget can paint real numbers at boot
            // instead of a placeholder while the first fetch runs (issue #21).
            if (_snapshotDirectory is { } directory)
                foreach (var (id, result) in UsageSnapshotStore.Load(directory, Get))
                    _lastSuccessfulLiveResults[id] = result;
        }

        public void Register(IUsageProvider provider) => _providers[provider.Id] = provider;

        public IUsageProvider? Get(ProviderId id) => _providers.TryGetValue(id, out var p) ? p : null;

        public IReadOnlyCollection<IUsageProvider> All => _providers.Values;

        public IReadOnlyList<UsageResult> Snapshot(ProviderId? active = null, string message = "Loading...")
            => _providers.Values
                .Select(p => UsageResult.Pending(p.Id, p, active == p.Id ? "Loading active provider..." : message))
                .ToArray();

        public bool TryGetCached(ProviderId id, out UsageResult result)
        {
            UsageResult? candidate = null;
            lock (_lock)
            {
                if (TryGetValidEntry(id, out var cached))
                    candidate = cached.Result;
            }
            // Credential files are read outside the state lock, including UI cache lookups.
            if (candidate is not null && UsageCacheIdentity.IsCurrent(candidate.Provider, candidate.Fetch, candidate.CacheIdentity))
            {
                result = candidate.AsMemoryCache();
                return true;
            }

            result = UsageResult.Failure(id, "No cached result.");
            return false;
        }

        // Callers include UI-thread timers, and the cache-hit path can parse session history for seconds.
        public Task<UsageResult> FetchAsync(ProviderId id, bool force = false, CancellationToken ct = default)
            => Task.Run(() => FetchCoreAsync(id, force, ct), CancellationToken.None);

        private async Task<UsageResult> FetchCoreAsync(ProviderId id, bool force, CancellationToken ct)
        {
            if (!_providers.TryGetValue(id, out var provider))
                return UsageResult.Failure(id, "Provider not available yet.");

            UsageResult? cachedResult = null;
            lock (_lock)
            {
                if (!force && TryGetValidEntry(id, out var cached))
                    cachedResult = cached.Result.AsMemoryCache();
            }
            // History parsing reads every session log, so it runs outside _lock to keep UI-thread cache reads unblocked.
            if (cachedResult is not null && UsageCacheIdentity.IsCurrent(provider, cachedResult.Fetch, cachedResult.CacheIdentity))
                return PublishResult(id, AttachLocalHistory(id, cachedResult));

            long observationSequence;
            lock (_lock)
            {
                observationSequence = ++_nextObservationSequence;
                if (!_inFlightSequences.TryGetValue(id, out var pending))
                    _inFlightSequences[id] = pending = new SortedSet<long>();
                pending.Add(observationSequence);
            }
            try
            {
                var result = await FetchAttemptAsync(id, provider, observationSequence, ct).ConfigureAwait(false);
                return PublishResult(id, result);
            }
            finally
            {
                lock (_lock)
                {
                    var pending = _inFlightSequences[id];
                    pending.Remove(observationSequence);
                    foreach (var key in _completedResults.Keys.Where(key => key.Item1 == id).ToArray())
                    {
                        bool isCurrentCache = _cache.TryGetValue(id, out var current) && current.Result.CacheIdentity == key.Item2;
                        if (!isCurrentCache && (pending.Count == 0 || _completedResults[key].Result.ObservationSequence < pending.Min))
                            _completedResults.Remove(key);
                    }
                    if (pending.Count == 0)
                        _inFlightSequences.Remove(id);
                }
            }
        }

        private UsageResult PublishResult(ProviderId id, UsageResult result)
        {
            bool eligible = UsageCacheIdentity.IsCurrent(result.Provider, result.Fetch, result.CacheIdentity);
            bool accountChanged = result.Provider is IUsageCacheIdentityProvider && !eligible
                && (result.Ok || result.CacheIdentity is not null || UsageCacheIdentity.CurrentFor(result.Provider) is not null);
            if (accountChanged)
                result = IdentityChangedResult(result);
            return Store(id, result, TimeSpan.Zero, result.ObservationSequence, publicationOnly: true);
        }

        private async Task<UsageResult> FetchAttemptAsync(ProviderId id, IUsageProvider provider, long observationSequence, CancellationToken ct)
        {
            var attemptIdentity = UsageCacheIdentity.CurrentFor(provider);
            try
            {
                var fetch = await FetchProviderAsync(provider, ct).ConfigureAwait(false);
                if (fetch.Usage.UsageHistory is null && UsageHistoryService.TryLoad(id, out var history))
                    fetch.Usage.UsageHistory = history;
                var observedAt = DateTimeOffset.Now;
                var result = UsageResult.Success(id, provider, fetch)
                    .AsLiveObservation(observationSequence, observedAt);
                if (!UsageCacheIdentity.IsCurrent(provider, fetch))
                    return IdentityChangedResult(result);
                if (TryGetLastSuccessfulLiveResult(id, out var lastSuccess) && IsSuspiciousClaudeZeroResult(result, lastSuccess))
                {
                    Diagnostics.Log.Warning("Claude returned a sudden 0%/0% usage snapshot before the previous window reset; keeping the last good Claude usage values.");
                    // The server answered, so the kept values are confirmed current — drop any stale mark
                    // carried over from a snapshot restored at startup.
                    lastSuccess = lastSuccess.AsFresh()
                        .AsLiveObservation(observationSequence, observedAt);
                    lastSuccess = Store(id, lastSuccess, FetchCachePolicy.TtlForSuccess(), observationSequence);
                    StoreLastSuccessfulLiveResult(id, lastSuccess);
                    return lastSuccess;
                }

                if (TryGetLastSuccessfulLiveResult(id, out lastSuccess) && SameUsage(lastSuccess, result))
                {
                    // Preserve the value-change timestamp, but keep the new endpoint confirmation
                    // metadata. Reusing the previous object would leave reset credits ageing forever.
                    var previousFetch = lastSuccess.Fetch!;
                    var confirmedFetch = ReferenceEquals(previousFetch.Usage.ResetCredits, fetch.Usage.ResetCredits)
                        ? previousFetch
                        : new ProviderFetchResult(previousFetch.Usage.CopyWithResetCredits(fetch.Usage.ResetCredits),
                            previousFetch.SourceLabel, previousFetch.FetchedAt, previousFetch.CacheIdentity);
                    lastSuccess = UsageResult.Success(id, provider, confirmedFetch)
                        .AsLiveObservation(observationSequence, observedAt);
                    lastSuccess = Store(id, lastSuccess, FetchCachePolicy.TtlForSuccess(), observationSequence);
                    StoreLastSuccessfulLiveResult(id, lastSuccess);
                    return lastSuccess;
                }

                result = Store(id, result, FetchCachePolicy.TtlForSuccess(), observationSequence);
                StoreLastSuccessfulLiveResult(id, result);
                return result;
            }
            catch (ProviderException pe)
            {
                if (ShouldReuseLastSuccessfulResult(pe.Kind) && TryGetLastSuccessfulLiveResult(id, out var lastSuccess))
                {
                    var fallback = lastSuccess.AsFailureFallback(observationSequence, DateTimeOffset.Now);
                    return Store(id, fallback, FetchCachePolicy.TtlForFailure(pe.Kind), observationSequence);
                }

                if (pe.Kind is not ProviderErrorKind.AuthRequired and not ProviderErrorKind.NotInstalled
                    && TryCreateLocalHistoryResult(id, provider, out var localResult))
                {
                    localResult = localResult.AsFailureFallback(observationSequence, DateTimeOffset.Now);
                    return Store(id, localResult, FetchCachePolicy.TtlForFailure(pe.Kind), observationSequence);
                }

                var result = UsageResult.Failure(id, pe.Message, provider, pe.Kind, attemptIdentity)
                    .AsFailureFallback(observationSequence, DateTimeOffset.Now);
                return Store(id, result, FetchCachePolicy.TtlForFailure(pe.Kind), observationSequence);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (TryCreateLocalHistoryResult(id, provider, out var localResult))
                {
                    localResult = localResult.AsFailureFallback(observationSequence, DateTimeOffset.Now);
                    return Store(id, localResult, FetchCachePolicy.TtlForFailure(null), observationSequence);
                }

                var result = UsageResult.Failure(id, ex.Message, provider, cacheIdentity: attemptIdentity)
                    .AsFailureFallback(observationSequence, DateTimeOffset.Now);
                return Store(id, result, FetchCachePolicy.TtlForFailure(null), observationSequence);
            }
        }

        private async Task<ProviderFetchResult> FetchProviderAsync(IUsageProvider provider, CancellationToken ct)
        {
            ProviderFetchResult fetch;
            try
            {
                fetch = await provider.FetchUsageAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderException ex)
            {
                NotifyFetchObserver(UsageResult.Failure(provider.Id, ex.Message, provider, ex.Kind));
                throw;
            }
            catch (Exception ex)
            {
                NotifyFetchObserver(UsageResult.Failure(provider.Id, ex.Message, provider));
                throw;
            }

            // Report the provider's actual outcome, before history/cache fallbacks can look successful.
            // Cache hits and restored snapshots never enter this method.
            NotifyFetchObserver(UsageResult.Success(provider.Id, provider, fetch));
            return fetch;
        }

        private void NotifyFetchObserver(UsageResult result)
        {
            try { _fetchObserver?.Invoke(result); }
            catch (Exception ex)
            {
                // Discovery is best effort; an observer must not change the usage result or retry a fetch.
                Log.Warning(ex, "Provider fetch observer failed");
            }
        }

        private UsageResult Store(ProviderId id, UsageResult result, TimeSpan ttl, long sequence, bool publicationOnly = false)
        {
            bool eligible = UsageCacheIdentity.IsCurrent(result.Provider, result.Fetch, result.CacheIdentity);
            if (!eligible && !publicationOnly)
                return result;
            bool cacheRead = publicationOnly && result.ObservationOrigin is UsageObservationOrigin.MemoryCache or UsageObservationOrigin.RestoredSnapshot;
            lock (_lock)
            {
                var key = (id, result.CacheIdentity);
                var entry = new CacheEntry(DateTimeOffset.Now, result, ttl);
                if (eligible && _completedResults.TryGetValue(key, out var completed))
                {
                    // Delivery revisions never change which actual request is newest for an account.
                    if (result.ObservationOrigin == UsageObservationOrigin.AccountTransition
                        || completed.Result.ObservationSequence > sequence
                        || (publicationOnly && completed.Result.ObservationSequence == sequence))
                    {
                        cacheRead |= completed.Result.ObservationSequence > sequence;
                        entry = completed;
                    }
                }
                result = entry.Result;
                if (_publicationFrontiers.TryGetValue(id, out var frontier) && result.PublicationOrder < frontier)
                    result = result.WithPublicationOrder(frontier.NextRevision());
                _publicationFrontiers[id] = result.PublicationOrder;
                entry = entry with { Result = result };
                if (eligible)
                {
                    // A detected account transition is a delivery event, not an actual attempt
                    // by the new account. It mustn't outrank that account's in-flight requests.
                    if (result.ObservationOrigin != UsageObservationOrigin.AccountTransition)
                        _completedResults[key] = entry;
                    _cache[id] = entry;
                }
                return cacheRead ? result.AsMemoryCache() : result;
            }
        }

        private void StoreLastSuccessfulLiveResult(ProviderId id, UsageResult result)
        {
            if (!result.Ok || result.ObservationOrigin != UsageObservationOrigin.Live
                || !UsageCacheIdentity.IsCurrent(result.Provider, result.Fetch, result.CacheIdentity))
                return;
            lock (_lock)
            {
                if (_lastSuccessfulLiveResults.TryGetValue(id, out var latestSuccess)
                    && latestSuccess.PublicationOrder > result.PublicationOrder)
                    return;
                if (_completedResults.TryGetValue((id, result.CacheIdentity), out var completed)
                    && result.ObservationSequence < completed.Result.ObservationSequence)
                    return;
                if (_snapshotDirectory is null)
                {
                    _lastSuccessfulLiveResults[id] = result;
                    return;
                }

                // Persist unchanged live confirmations periodically, without treating another
                // provider's write as a confirmation or using the value-change time as a write clock.
                var refreshInterval = result.Fetch?.Usage.ResetCredits is null
                    ? SnapshotRefreshInterval
                    : TimeSpan.FromTicks(Math.Min(SnapshotRefreshInterval.Ticks, ResetCreditsSnapshot.MaxCacheAge.Ticks / 2));
                if (_lastSuccessfulLiveResults.TryGetValue(id, out var existing)
                    && SameUsage(existing, result)
                    && _lastSnapshotQueuedAt.TryGetValue(id, out var queuedAt)
                    && DateTimeOffset.Now >= queuedAt
                    && DateTimeOffset.Now - queuedAt < refreshInterval)
                {
                    _lastSuccessfulLiveResults[id] = result;
                    return;
                }

                _lastSuccessfulLiveResults[id] = result;
                var toPersist = new Dictionary<ProviderId, UsageResult>(_lastSuccessfulLiveResults);
                _lastSnapshotQueuedAt[id] = DateTimeOffset.Now;
                // Create the task chain while holding the state lock. Merely serializing writes on
                // background threads allows an older dictionary to be queued after a newer one.
                _snapshotWriteTask = QueueSnapshotSave(_snapshotWriteTask, _snapshotDirectory, toPersist);
            }
        }

        /// <summary>
        /// Persists a snapshot set on a background thread, one write at a time. Callers hand off and
        /// return immediately; failures are logged rather than surfaced, since losing a snapshot only
        /// costs the next boot its restored values.
        /// </summary>
        private static Task QueueSnapshotSave(Task previous, string directory, IReadOnlyDictionary<ProviderId, UsageResult> toPersist)
            => Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await SnapshotWriteGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    UsageSnapshotStore.Save(directory, toPersist);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to persist the usage snapshot");
                }
                finally
                {
                    SnapshotWriteGate.Release();
                }
            });

        public bool TryGetLastSuccessfulLiveResult(ProviderId id, out UsageResult result)
        {
            UsageResult? candidate = null;
            lock (_lock)
            {
                if (_lastSuccessfulLiveResults.TryGetValue(id, out var stored))
                    candidate = stored;
            }
            if (candidate is not null && UsageCacheIdentity.IsCurrent(candidate.Provider, candidate.Fetch, candidate.CacheIdentity))
            {
                result = candidate.AsMemoryCache();
                return true;
            }

            result = UsageResult.Failure(id, "No successful live result.");
            return false;
        }

        private bool TryGetValidEntry(ProviderId id, out CacheEntry entry)
        {
            if (_cache.TryGetValue(id, out entry!) && entry.IsValid)
                return true;

            entry = default;
            return false;
        }

        private static bool ShouldReuseLastSuccessfulResult(ProviderErrorKind kind)
            => kind is not ProviderErrorKind.AuthRequired and not ProviderErrorKind.NotInstalled;

        private static bool IsSuspiciousClaudeZeroResult(UsageResult result, UsageResult previous)
            => result.Id == ProviderId.Claude
            && result.Fetch?.Usage is { } usage
            && previous.Fetch?.Usage is { } previousUsage
            && usage.Primary.UsedPercent == 0
            && usage.Secondary?.UsedPercent == 0
            && (previousUsage.Primary.UsedPercent > 0 || previousUsage.Secondary?.UsedPercent > 0)
            && !WindowResetAdvancedOrPassed(previousUsage.Primary, usage.Primary)
            && !WindowResetAdvancedOrPassed(previousUsage.Secondary, usage.Secondary);

        private static bool WindowResetAdvancedOrPassed(RateWindow? previous, RateWindow? current)
        {
            if (previous?.ResetAt is not { } previousReset)
                return false;

            if (DateTimeOffset.Now >= previousReset)
                return true;

            return current?.ResetAt is { } currentReset && currentReset > previousReset;
        }

        private static bool SameUsage(UsageResult left, UsageResult right)
            => left.Fetch is { } lf
            && right.Fetch is { } rf
            && lf.SourceLabel == rf.SourceLabel
            && lf.CacheIdentity == rf.CacheIdentity
            && SameSnapshot(lf.Usage, rf.Usage);

        private static bool SameSnapshot(UsageSnapshot left, UsageSnapshot right)
        {
            if (left.HasPrimaryWindow != right.HasPrimaryWindow
                || !SameWindow(left.Primary, right.Primary)
                || !SameWindow(left.Secondary, right.Secondary)
                || !SameWindow(left.ModelSpecific, right.ModelSpecific)
                || !SameWindow(left.Monthly, right.Monthly)
                || left.LoginMethod != right.LoginMethod
                || left.Email != right.Email
                || !SameHistory(left.UsageHistory, right.UsageHistory)
                || !SameCost(left.Cost, right.Cost)
                || !SamePricing(left.Pricing, right.Pricing)
                || !SameAdditional(left.AdditionalUsage, right.AdditionalUsage)
                || !SameResetCredits(left.ResetCredits, right.ResetCredits)
                || left.UsageDashboardUrl != right.UsageDashboardUrl
                || left.ExtraRateWindows.Count != right.ExtraRateWindows.Count)
                return false;

            for (int i = 0; i < left.ExtraRateWindows.Count; i++)
            {
                var l = left.ExtraRateWindows[i];
                var r = right.ExtraRateWindows[i];
                if (l.Id != r.Id || l.Title != r.Title || !SameWindow(l.Window, r.Window))
                    return false;
            }

            return true;
        }

        private static UsageResult AttachLocalHistory(ProviderId id, UsageResult result)
        {
            if (result.Fetch?.Usage is not { } usage)
                return result;

            if (usage.UsageHistory is null && UsageHistoryService.TryLoad(id, out var history))
            {
                usage.UsageHistory = history;
            }

            return result;
        }

        private static bool TryCreateLocalHistoryResult(
            ProviderId id,
            IUsageProvider provider,
            out UsageResult result)
        {
            var identity = UsageCacheIdentity.CurrentFor(provider);
            if (provider is IUsageCacheIdentityProvider && identity is null)
            {
                result = UsageResult.Failure(id, "Current account identity is unavailable.", provider);
                return false;
            }
            if (!UsageHistoryService.TryLoad(id, out var history))
            {
                result = UsageResult.Failure(id, "No local usage history found.", provider);
                return false;
            }

            var usage = new UsageSnapshot(new RateWindow(0))
            {
                UsageHistory = history,
            };
            result = UsageResult.Success(
                id,
                provider,
                new ProviderFetchResult(usage, "Local usage history", cacheIdentity: identity));
            return true;
        }

        private static UsageResult IdentityChangedResult(UsageResult result)
            => UsageResult.Failure(result.Id, "Account credentials changed while fetching usage. Refresh to retry.",
                result.Provider, ProviderErrorKind.Other,
                result.Provider is { } provider ? UsageCacheIdentity.CurrentFor(provider) : null)
                .AsAccountTransition(result.ObservationSequence, result.ObservedAt ?? DateTimeOffset.Now)
                .WithPublicationOrder(result.PublicationOrder);

        private static bool SameHistory(UsageHistory? left, UsageHistory? right)
        {
            if (left is null || right is null)
                return left is null && right is null;

            return SamePeriod(left.Today, right.Today)
                && SamePeriod(left.Yesterday, right.Yesterday)
                && SamePeriod(left.Last7Days, right.Last7Days)
                && SamePeriod(left.Last30Days, right.Last30Days)
                && SamePeriod(left.Last90Days, right.Last90Days)
                && left.Daily.Count == right.Daily.Count
                && left.Daily.Zip(right.Daily).All(pair =>
                    pair.First.Date == pair.Second.Date
                    && pair.First.Tokens == pair.Second.Tokens
                    && NullableNearlyEqual(pair.First.EstimatedCostUsd, pair.Second.EstimatedCostUsd)
                    && SameTokens(pair.First.TokenBreakdown, pair.Second.TokenBreakdown)
                    && NearlyEqual(pair.First.CacheSavingsUsd, pair.Second.CacheSavingsUsd));
        }

        private static bool SamePeriod(UsagePeriod? left, UsagePeriod? right)
        {
            if (left is null || right is null)
                return left is null && right is null;
            if (left.Tokens != right.Tokens
                || !NullableNearlyEqual(left.EstimatedCostUsd, right.EstimatedCostUsd)
                || left.CostEstimated != right.CostEstimated
                || left.EstimateComplete != right.EstimateComplete
                || !SameTokens(left.TokenBreakdown, right.TokenBreakdown)
                || !NearlyEqual(left.CacheSavingsUsd, right.CacheSavingsUsd))
                return false;

            var leftModels = left.ModelBreakdown?.Models ?? Array.Empty<ModelUsageEntry>();
            var rightModels = right.ModelBreakdown?.Models ?? Array.Empty<ModelUsageEntry>();
            return leftModels.Count == rightModels.Count
                && leftModels.Zip(rightModels).All(pair =>
                    pair.First.Model == pair.Second.Model
                    && pair.First.TotalTokens == pair.Second.TotalTokens
                    && NullableNearlyEqual(pair.First.CostUsd, pair.Second.CostUsd));
        }

        private static bool SameTokens(TokenBreakdown left, TokenBreakdown right)
            => left.Input == right.Input
                && left.CacheWrite5m == right.CacheWrite5m
                && left.CacheWrite1h == right.CacheWrite1h
                && left.CacheRead == right.CacheRead
                && left.Output == right.Output
                && left.Reasoning == right.Reasoning;

        private static bool SameWindow(RateWindow? left, RateWindow? right)
            => (left, right) switch
            {
                (null, null) => true,
                ({ } l, { } r) =>
                    NearlyEqual(l.UsedPercent, r.UsedPercent)
                    && l.IsIncluded == r.IsIncluded
                    && l.WindowMinutes == r.WindowMinutes
                    && l.Label == r.Label
                    && l.ShowCostValue == r.ShowCostValue
                    && l.ResetAt == r.ResetAt
                    && l.ResetDescription == r.ResetDescription,
                _ => false,
            };

        private static bool SameCost(CostSnapshot? left, CostSnapshot? right)
            => (left, right) switch
            {
                (null, null) => true,
                ({ } l, { } r) =>
                    NearlyEqual(l.Amount, r.Amount)
                    && l.Currency == r.Currency
                    && l.Label == r.Label
                    && NullableNearlyEqual(l.Limit, r.Limit)
                    && l.ResetsAt == r.ResetsAt,
                _ => false,
            };

        private static bool SamePricing(UsagePricingSnapshot? left, UsagePricingSnapshot? right)
            => (left, right) switch
            {
                (null, null) => true,
                ({ } l, { } r) => l.Period == r.Period && NearlyEqual(l.Multiplier, r.Multiplier),
                _ => false,
            };

        private static bool SameAdditional(AdditionalUsageSnapshot? left, AdditionalUsageSnapshot? right)
            => (left, right) switch
            {
                (null, null) => true,
                ({ } l, { } r) =>
                    l.Enabled == r.Enabled
                    && l.IsCredits == r.IsCredits
                    && NearlyEqual(l.SpentUsd, r.SpentUsd)
                    && NullableNearlyEqual(l.BudgetUsd, r.BudgetUsd),
                _ => false,
            };

        private static bool SameResetCredits(ResetCreditsSnapshot? left, ResetCreditsSnapshot? right)
        {
            if (left is null || right is null)
                return left is null && right is null;
            if (left.AvailableCount != right.AvailableCount || left.Credits.Count != right.Credits.Count
                || left.CachedAt != right.CachedAt)
                return false;

            for (int i = 0; i < left.Credits.Count; i++)
            {
                var l = left.Credits[i];
                var r = right.Credits[i];
                if (l.Status != r.Status || l.GrantedAt != r.GrantedAt || l.ExpiresAt != r.ExpiresAt)
                    return false;
            }

            return true;
        }

        private static bool NullableNearlyEqual(double? left, double? right)
            => (left, right) switch
            {
                (null, null) => true,
                ({ } l, { } r) => NearlyEqual(l, r),
                _ => false,
            };

        private static bool NearlyEqual(double left, double right)
            => Math.Abs(left - right) < 0.0001;

        private readonly record struct CacheEntry(DateTimeOffset At, UsageResult Result, TimeSpan Ttl)
        {
            public bool IsValid => DateTimeOffset.Now - At < Ttl;
        }
    }

    internal static class FetchCachePolicy
    {
        public static TimeSpan TtlForSuccess() => UsageService.SuccessCacheTtl;

        public static TimeSpan TtlForFailure(ProviderErrorKind? kind) => kind switch
        {
            ProviderErrorKind.RateLimited => UsageService.RateLimitedCacheTtl,
            _ => UsageService.FailureCacheTtl,
        };
    }

    internal enum UsageObservationOrigin
    {
        Unknown,
        Live,
        MemoryCache,
        RestoredSnapshot,
        FailureFallback,
        AccountTransition,
    }

    public sealed class UsageResult
    {
        public ProviderId Id { get; private init; }
        public IUsageProvider? Provider { get; private init; }
        public ProviderFetchResult? Fetch { get; private init; }
        public string? Error { get; private init; }
        public ProviderErrorKind? ErrorKind { get; private init; }
        public ProviderSource Source { get; private init; } = ProviderSource.Unknown;
        /// <summary>
        /// True for a placeholder created before any fetch has completed. Distinguishes "no data yet"
        /// from "the fetch failed" so the taskbar widget can show a loading state instead of the red
        /// error bar it used to render for both (issue #21).
        /// </summary>
        public bool IsPending { get; private init; }
        /// <summary>
        /// True for a snapshot restored from disk at startup: real numbers from the previous session that
        /// no live fetch has confirmed yet. The widget renders these dimmed with an "as of" tooltip.
        /// </summary>
        public bool IsStale { get; private init; }
        internal UsageObservationOrigin ObservationOrigin { get; private init; }
        internal long ObservationSequence { get; private init; }
        private UsagePublicationOrder? ExplicitPublicationOrder { get; init; }
        internal UsagePublicationOrder PublicationOrder => ExplicitPublicationOrder ?? new(ObservationSequence);
        internal DateTimeOffset? ObservedAt { get; private init; }
        /// <summary>Last successful live confirmation, preserved across fallback and restore.</summary>
        internal DateTimeOffset? LastConfirmedAt { get; private init; }
        internal string? CacheIdentity { get; private init; }
        public bool Ok => Fetch != null;
        public string DisplayName => Provider?.DisplayName ?? Id.ToString();

        public static UsageResult Success(ProviderId id, IUsageProvider provider, ProviderFetchResult fetch,
            DateTimeOffset? lastConfirmedAt = null)
            => new() { Id = id, Provider = provider, Fetch = fetch, LastConfirmedAt = lastConfirmedAt ?? DateTimeOffset.Now,
                CacheIdentity = fetch.CacheIdentity };

        public static UsageResult Failure(ProviderId id, string error, IUsageProvider? provider = null,
            ProviderErrorKind? kind = null, string? cacheIdentity = null)
            => new() { Id = id, Provider = provider, Error = error, ErrorKind = kind, CacheIdentity = cacheIdentity };

        public static UsageResult Pending(ProviderId id, IUsageProvider provider, string message)
            => new() { Id = id, Provider = provider, Error = message, IsPending = true,
                CacheIdentity = UsageCacheIdentity.CurrentFor(provider) };

        public UsageResult WithSource(ProviderSource? source)
            => new()
            {
                Id = Id,
                Provider = Provider,
                Fetch = Fetch,
                Error = Error,
                ErrorKind = ErrorKind,
                IsPending = IsPending,
                IsStale = IsStale,
                ObservationOrigin = ObservationOrigin,
                ObservationSequence = ObservationSequence,
                ExplicitPublicationOrder = ExplicitPublicationOrder,
                ObservedAt = ObservedAt,
                LastConfirmedAt = LastConfirmedAt,
                CacheIdentity = CacheIdentity,
                Source = source ?? ProviderSource.Unknown,
            };

        /// <summary>Marks a snapshot as restored-from-disk (see <see cref="IsStale"/>).</summary>
        public UsageResult AsStale()
            => WithObservation(UsageObservationOrigin.RestoredSnapshot, ObservationSequence, ObservedAt, isStale: true);

        /// <summary>
        /// Clears the stale mark once a live fetch has confirmed the values. FetchedAt is deliberately
        /// left alone: it means "when these values last changed", which is what the widget's
        /// "Last updated" line reports. Snapshot expiry tracks confirmation separately, via the SavedAt
        /// stamp UsageSnapshotStore persists from LastConfirmedAt.
        /// </summary>
        public UsageResult AsFresh() => IsStale ? WithStale(false) : this;

        internal UsageResult AsLiveObservation(long sequence, DateTimeOffset observedAt)
            => WithObservation(UsageObservationOrigin.Live, sequence, observedAt, isStale: false, newAttempt: true);

        internal UsageResult AsMemoryCache()
            => WithObservation(
                ObservationOrigin switch
                {
                    UsageObservationOrigin.FailureFallback => UsageObservationOrigin.FailureFallback,
                    UsageObservationOrigin.RestoredSnapshot => UsageObservationOrigin.RestoredSnapshot,
                    _ => UsageObservationOrigin.MemoryCache,
                },
                ObservationSequence,
                ObservedAt,
                IsStale);

        internal UsageResult AsFailureFallback(long sequence, DateTimeOffset observedAt)
            => WithObservation(UsageObservationOrigin.FailureFallback, sequence, observedAt, isStale: false, newAttempt: true);

        internal UsageResult AsAccountTransition(long sequence, DateTimeOffset observedAt)
            => WithObservation(UsageObservationOrigin.AccountTransition, sequence, observedAt, isStale: false, newAttempt: true);

        internal UsageResult WithPublicationOrder(UsagePublicationOrder order)
            => WithObservation(ObservationOrigin, ObservationSequence, ObservedAt, IsStale, publicationOrder: order);

        private UsageResult WithStale(bool isStale)
            => WithObservation(ObservationOrigin, ObservationSequence, ObservedAt, isStale);

        private UsageResult WithObservation(
            UsageObservationOrigin origin,
            long sequence,
            DateTimeOffset? observedAt,
            bool isStale,
            bool newAttempt = false,
            UsagePublicationOrder? publicationOrder = null)
            => new()
            {
                Id = Id,
                Provider = Provider,
                Fetch = CurrentFetch(Fetch, origin is UsageObservationOrigin.FailureFallback or UsageObservationOrigin.RestoredSnapshot,
                    origin == UsageObservationOrigin.Live ? observedAt ?? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow),
                Error = Error,
                ErrorKind = ErrorKind,
                IsPending = IsPending,
                IsStale = isStale,
                ObservationOrigin = origin,
                ObservationSequence = sequence,
                ExplicitPublicationOrder = newAttempt ? null : publicationOrder ?? ExplicitPublicationOrder,
                ObservedAt = observedAt,
                LastConfirmedAt = origin == UsageObservationOrigin.Live ? observedAt : LastConfirmedAt,
                CacheIdentity = CacheIdentity,
                Source = Source,
            };

        private static ProviderFetchResult? CurrentFetch(ProviderFetchResult? fetch, bool asCached, DateTimeOffset now)
        {
            if (fetch?.Usage.ResetCredits is not { } cached)
                return fetch;
            var current = cached.CurrentAt(now, asCached);
            return ReferenceEquals(current, cached) ? fetch : new ProviderFetchResult(
                fetch.Usage.CopyWithResetCredits(current), fetch.SourceLabel, fetch.FetchedAt, fetch.CacheIdentity);
        }
    }
}
