using System;
using TaskbarQuota.Usage;

namespace TaskbarQuota;

internal static class FlyoutNavigation
{
    // A second click on the same tile closes its popup. Another tile (or monitor)
    // should instead show the requested provider at that tile's anchor.
    internal static bool ShouldHideProvider(
        bool isShown,
        bool showingActivity,
        bool showingCost,
        IntPtr currentAnchor,
        IntPtr requestedAnchor,
        ProviderId? selectedProvider,
        ProviderId requestedProvider)
        => isShown && !showingActivity && !showingCost
            && currentAnchor == requestedAnchor
            && selectedProvider == requestedProvider;
}
