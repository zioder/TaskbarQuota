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
        private readonly object _lock = new();
        private readonly string? _snapshotDirectory;

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
        public UsageService(string? snapshotDirectory = null)
        {
            _snapshotDirectory = snapshotDirectory;
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
            lock (_lock)
            {
                if (TryGetValidEntry(id, out var cached))
                {
                    result = cached.Result;
                    return true;
                }
            }

            result = UsageResult.Failure(id, "No cached result.");
            return false;
        }

        public async Task<UsageResult> FetchAsync(ProviderId id, bool force = false, CancellationToken ct = default)
        {
            if (!_providers.TryGetValue(id, out var provider))
                return UsageResult.Failure(id, "Provider not available yet.");

            lock (_lock)
            {
                if (!force && TryGetValidEntry(id, out var cached))
                    return cached.Result;
            }

            try
            {
                var fetch = await provider.FetchUsageAsync(ct).ConfigureAwait(false);
                var result = UsageResult.Success(id, provider, fetch);
                if (TryGetLastSuccessfulLiveResult(id, out var lastSuccess) && IsSuspiciousClaudeZeroResult(result, lastSuccess))
                {
                    Diagnostics.Log.Warning("Claude returned a sudden 0%/0% usage snapshot before the previous window reset; keeping the last good Claude usage values.");
                    // The server answered, so the kept values are confirmed current — drop any stale mark
                    // carried over from a snapshot restored at startup.
                    lastSuccess = lastSuccess.AsFresh();
                    Store(id, lastSuccess, FetchCachePolicy.TtlForSuccess());
                    StoreLastSuccessfulLiveResult(id, lastSuccess);
                    return lastSuccess;
                }

                if (TryGetLastSuccessfulLiveResult(id, out lastSuccess) && SameUsage(lastSuccess, result))
                {
                    lastSuccess = lastSuccess.AsFresh();
                    Store(id, lastSuccess, FetchCachePolicy.TtlForSuccess());
                    StoreLastSuccessfulLiveResult(id, lastSuccess);
                    return lastSuccess;
                }

                Store(id, result, FetchCachePolicy.TtlForSuccess());
                StoreLastSuccessfulLiveResult(id, result);
                return result;
            }
            catch (ProviderException pe)
            {
                if (ShouldReuseLastSuccessfulResult(pe.Kind) && TryGetLastSuccessfulLiveResult(id, out var lastSuccess))
                {
                    Store(id, lastSuccess, FetchCachePolicy.TtlForFailure(pe.Kind));
                    return lastSuccess;
                }

                var result = UsageResult.Failure(id, pe.Message, provider, pe.Kind);
                Store(id, result, FetchCachePolicy.TtlForFailure(pe.Kind));
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var result = UsageResult.Failure(id, ex.Message, provider);
                Store(id, result, FetchCachePolicy.TtlForFailure(null));
                return result;
            }
        }

        private void Store(ProviderId id, UsageResult result, TimeSpan ttl)
        {
            lock (_lock)
            {
                _cache[id] = new CacheEntry(DateTimeOffset.Now, result, ttl);
            }
        }

        private void StoreLastSuccessfulLiveResult(ProviderId id, UsageResult result)
        {
            Dictionary<ProviderId, UsageResult> toPersist;
            lock (_lock)
            {
                if (_snapshotDirectory is null)
                {
                    _lastSuccessfulLiveResults[id] = result;
                    return;
                }

                // Unchanged usage re-stores an equivalent value every poll, so skip the disk write for
                // those — but re-persist periodically anyway, because each write refreshes the SavedAt
                // stamp that UsageSnapshotStore expiry reads. Skipping forever would let a continuously
                // confirmed entry age past MaxRestoreAge and be discarded on the next boot. The older
                // instance stays as the comparison baseline: its age is what triggers the next rewrite.
                if (_lastSuccessfulLiveResults.TryGetValue(id, out var existing)
                    && SameUsage(existing, result)
                    && existing.Fetch is { } existingFetch
                    && DateTimeOffset.Now - existingFetch.FetchedAt < SnapshotRefreshInterval)
                {
                    return;
                }

                _lastSuccessfulLiveResults[id] = result;
                toPersist = new Dictionary<ProviderId, UsageResult>(_lastSuccessfulLiveResults);
            }

            // Off the fetch path: persistence is best-effort and must never delay a usage update. Writes
            // are serialized because Save stages through a single shared temp path — two providers
            // persisting concurrently would otherwise interleave the write/move and could leave a
            // truncated file or let an older snapshot land on top of a newer one.
            QueueSnapshotSave(_snapshotDirectory, toPersist);
        }

        /// <summary>
        /// Persists a snapshot set on a background thread, one write at a time. Callers hand off and
        /// return immediately; failures are logged rather than surfaced, since losing a snapshot only
        /// costs the next boot its restored values.
        /// </summary>
        private static void QueueSnapshotSave(string directory, IReadOnlyDictionary<ProviderId, UsageResult> toPersist)
            => _ = Task.Run(async () =>
            {
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
            lock (_lock)
            {
                if (_lastSuccessfulLiveResults.TryGetValue(id, out result!))
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
            && SameSnapshot(lf.Usage, rf.Usage);

        private static bool SameSnapshot(UsageSnapshot left, UsageSnapshot right)
        {
            if (!SameWindow(left.Primary, right.Primary)
                || !SameWindow(left.Secondary, right.Secondary)
                || !SameWindow(left.ModelSpecific, right.ModelSpecific)
                || !SameWindow(left.Monthly, right.Monthly)
                || left.LoginMethod != right.LoginMethod
                || left.Email != right.Email
                || !SameCost(left.Cost, right.Cost)
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

        private static bool SameWindow(RateWindow? left, RateWindow? right)
            => (left, right) switch
            {
                (null, null) => true,
                ({ } l, { } r) =>
                    NearlyEqual(l.UsedPercent, r.UsedPercent)
                    && l.WindowMinutes == r.WindowMinutes
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

        private static bool SameAdditional(AdditionalUsageSnapshot? left, AdditionalUsageSnapshot? right)
            => (left, right) switch
            {
                (null, null) => true,
                ({ } l, { } r) =>
                    l.Enabled == r.Enabled
                    && NearlyEqual(l.SpentUsd, r.SpentUsd)
                    && NullableNearlyEqual(l.BudgetUsd, r.BudgetUsd),
                _ => false,
            };

        private static bool SameResetCredits(ResetCreditsSnapshot? left, ResetCreditsSnapshot? right)
        {
            if (left is null || right is null)
                return left is null && right is null;
            if (left.AvailableCount != right.AvailableCount || left.Credits.Count != right.Credits.Count)
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
        public bool Ok => Fetch != null;
        public string DisplayName => Provider?.DisplayName ?? Id.ToString();

        public static UsageResult Success(ProviderId id, IUsageProvider provider, ProviderFetchResult fetch)
            => new() { Id = id, Provider = provider, Fetch = fetch };

        public static UsageResult Failure(ProviderId id, string error, IUsageProvider? provider = null, ProviderErrorKind? kind = null)
            => new() { Id = id, Provider = provider, Error = error, ErrorKind = kind };

        public static UsageResult Pending(ProviderId id, IUsageProvider provider, string message)
            => new() { Id = id, Provider = provider, Error = message, IsPending = true };

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
                Source = source ?? ProviderSource.Unknown,
            };

        /// <summary>Marks a snapshot as restored-from-disk (see <see cref="IsStale"/>).</summary>
        public UsageResult AsStale() => WithStale(true);

        /// <summary>
        /// Clears the stale mark once a live fetch has confirmed the values. FetchedAt is deliberately
        /// left alone: it means "when these values last changed", which is what the widget's
        /// "Last updated" line reports. Snapshot expiry tracks confirmation separately, via the SavedAt
        /// stamp UsageSnapshotStore writes on each persist.
        /// </summary>
        public UsageResult AsFresh() => IsStale ? WithStale(false) : this;

        private UsageResult WithStale(bool isStale)
            => new()
            {
                Id = Id,
                Provider = Provider,
                Fetch = Fetch,
                Error = Error,
                ErrorKind = ErrorKind,
                IsPending = IsPending,
                IsStale = isStale,
                Source = Source,
            };
    }
}
