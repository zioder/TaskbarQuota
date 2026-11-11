using System.Collections.Generic;

namespace TaskbarQuota.Usage;

// UI-owned ordering for pooled provider tiles. A force redraw never permits older data.
internal sealed class UsageObservationSequenceGate
{
    private readonly Dictionary<ProviderId, UsagePublicationOrder> _latest = new();

    internal bool TryAccept(UsageResult result)
    {
        if (_latest.TryGetValue(result.Id, out var previous) && result.PublicationOrder < previous)
            return false;
        _latest[result.Id] = result.PublicationOrder;
        return true;
    }
}
