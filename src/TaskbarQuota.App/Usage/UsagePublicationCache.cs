using System;
using System.Collections.Generic;

namespace TaskbarQuota.Usage;

/// <summary>
/// Suppresses redundant <see cref="UsageCoordinator.StateChanged"/> notifications when consecutive
/// fetches produce the same observable quota snapshot for a provider.
/// </summary>
internal sealed class UsagePublicationCache
{
    private readonly Dictionary<ProviderId, UsageFingerprint> _last = new();
    private readonly object _gate = new();

    private readonly record struct UsageFingerprint(
        bool Ok,
        double PrimaryPercent,
        double? SecondaryPercent,
        string? Error,
        bool Pending,
        bool Stale,
        long ObservationSequence,
        DateTimeOffset? ObservedAt,
        DateTimeOffset? FetchedAt,
        long UsageHistorySignature,
        ProviderSourceKind SourceKind,
        string? SourceName);

    internal bool ShouldPublish(UsageResult result, bool force)
    {
        if (force)
            return true;

        var fingerprint = Fingerprint(result);
        lock (_gate)
            return !_last.TryGetValue(result.Id, out var prev) || prev != fingerprint;
    }

    internal void RecordPublished(UsageResult result)
    {
        lock (_gate)
            _last[result.Id] = Fingerprint(result);
    }

    private static UsageFingerprint Fingerprint(UsageResult result)
    {
        double primary = result.Fetch?.Usage.Primary.UsedPercent ?? -1;
        double? secondary = result.Fetch?.Usage.Secondary?.UsedPercent;
        return new(
            result.Ok,
            primary,
            secondary,
            result.Error,
            result.IsPending,
            result.IsStale,
            result.ObservationSequence,
            result.ObservedAt,
            result.Fetch?.FetchedAt,
            HistorySignature(result.Fetch?.Usage.UsageHistory),
            result.Source.Kind,
            result.Source.Name);
    }

    private static long HistorySignature(UsageHistory? history)
    {
        if (history is null)
            return 0;

        unchecked
        {
            long sig = HashCode.Combine(
                history.Daily.Count,
                history.Today?.Tokens,
                history.Yesterday?.Tokens,
                history.Last7Days?.Tokens);
            if (history.Daily.Count > 0)
            {
                var latest = history.Daily[^1];
                sig = HashCode.Combine(sig, latest.Date, latest.Tokens);
            }

            return sig;
        }
    }
}
