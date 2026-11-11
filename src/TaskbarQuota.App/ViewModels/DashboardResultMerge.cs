using System.Collections.Generic;
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
        var latest = current.ToDictionary(result => result.Id);
        return incoming.Select(result => latest.TryGetValue(result.Id, out var previous)
            && previous.PublicationOrder > result.PublicationOrder
                ? previous
                : result).ToArray();
    }
}
