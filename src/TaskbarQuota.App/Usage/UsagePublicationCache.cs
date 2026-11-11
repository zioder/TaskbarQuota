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
        ProviderSourceKind SourceKind,
        string? SourceName);

    internal bool ShouldPublish(UsageResult result, bool force)
    {
        if (force)
            return true;

        var fingerprint = Fingerprint(result);
        lock (_gate)
        {
            if (_last.TryGetValue(result.Id, out var prev) && prev == fingerprint)
                return false;

            _last[result.Id] = fingerprint;
            return true;
        }
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
            result.Source.Kind,
            result.Source.Name);
    }
}
