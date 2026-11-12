using System.Collections.Generic;
using System;
using System.Linq;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels;

internal static class DashboardResultMerge
{
    // All completed live attempts, including failures, carry a sequence from UsageService.
    // Pending/restored data has sequence zero. Equal observations may enrich history/source metadata.
    internal static IReadOnlyList<UsageResult> KeepNewerObservations(
        IReadOnlyList<UsageResult> incoming,
        IReadOnlyList<UsageResult> current)
    {
        var latest = current.Where(IsCurrentAccount).ToDictionary(result => result.Id);
        return incoming.Select(result =>
        {
            latest.TryGetValue(result.Id, out var previous);
            if (!IsCurrentAccount(result))
                return previous ?? (result.Provider is { } provider
                    ? UsageResult.Pending(result.Id, provider, "Loading current account...")
                    : UsageResult.Failure(result.Id, "Account changed. Refresh to retry."));
            return previous is not null && SameKnownAccount(result, previous)
                && previous.PublicationOrder > result.PublicationOrder ? previous : result;
        }).ToArray();
    }

    // A quota failure or a response without history doesn't invalidate a completed local-history
    // snapshot. Only a newer result carrying history can supersede that snapshot.
    internal static IReadOnlyList<UsageResult> KeepNewerHistorySnapshots(
        IReadOnlyList<UsageResult> incoming,
        IReadOnlyList<UsageResult> current)
    {
        // Retain quota-only identities until after the account comparison: a new account's empty
        // history invalidates the old account's history even when it has a newer publication order.
        var latest = current.Where(IsCurrentAccount).ToDictionary(result => result.Id);
        var merged = new List<UsageResult>();
        var seen = new HashSet<ProviderId>();
        foreach (var result in incoming)
        {
            seen.Add(result.Id);
            latest.TryGetValue(result.Id, out var previous);
            if (!IsCurrentAccount(result) || (previous is not null && !SameKnownAccount(result, previous)))
            {
                if (previous?.Fetch?.Usage.UsageHistory is not null) merged.Add(previous);
                continue;
            }
            if (previous?.Fetch?.Usage.UsageHistory is not null
                && (result.Fetch?.Usage.UsageHistory is null || previous.PublicationOrder > result.PublicationOrder))
                merged.Add(previous);
            else if (result.Fetch?.Usage.UsageHistory is not null)
                merged.Add(result);
        }
        foreach (var result in latest.Values)
            if (!seen.Contains(result.Id) && result.Fetch?.Usage.UsageHistory is not null)
                merged.Add(result);
        return merged;
    }

    private static bool IsCurrentAccount(UsageResult result)
        => Identity(result) is null || UsageCacheIdentity.IsCurrent(result.Provider, result.Fetch, result.CacheIdentity);

    private static string? Identity(UsageResult result) => result.Fetch?.CacheIdentity ?? result.CacheIdentity;

    private static bool SameKnownAccount(UsageResult left, UsageResult right)
        => Identity(left) is not { } a || Identity(right) is not { } b || string.Equals(a, b, StringComparison.Ordinal);
}
