using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaskbarQuota.Controls;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Services;

/// <summary>A pinned-provider group and the taskbar display on which that group renders.</summary>
internal readonly record struct PinBudgetDisplay(
    string DisplayKey,
    int AvailableWidth,
    IReadOnlyList<ProviderId> Providers,
    int FitMargin = 0);

/// <summary>State carried between budget evaluations so transient geometry does not evict a pin.</summary>
internal readonly record struct BudgetHysteresisState(
    string? Signature,
    int ConsecutiveOverBudget);

/// <summary>
/// Decides whether a provider can be pinned, by the only measure that matters: whether the tile set fits the
/// free space each taskbar display actually has.
///
/// A pinned tile is never trimmed or reduced — it renders exactly the rows the user configured — so a set
/// that does not fit has to be refused up front rather than rendered badly. There is deliberately no
/// second, abstract allowance on top of this. An earlier weight budget (a provider costing one or two
/// "slots" out of five) both duplicated this check and contradicted it: three three-row providers come to
/// 1241px, which fits a left-aligned taskbar comfortably, yet cost six slots and were refused. Measured
/// space is the rule; anything else is a guess that eventually says no to something that plainly works.
/// </summary>
public static class PinBudgetService
{
    /// <summary>Raised after the budget auto-unpins providers, so the UI can refresh and explain.</summary>
    public static event Action<IReadOnlyList<ProviderId>>? ProvidersUnpinned;

    /// <summary>Number of consecutive over-budget evaluations required before silent eviction.</summary>
    internal const int BudgetHysteresisEvaluationCount = 3;

    /// <summary>
    /// Free width the pin budget uses for the current surface. Floating mode is constrained only by the
    /// tile cap; its host scrolls horizontally when content is wider than the monitor work area.
    /// </summary>
    public static int AvailableLogicalWidth
        => WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Floating
            ? int.MaxValue
            : TaskbarSpace.AvailableLogicalWidth;

    // Tile chrome, mirroring TaskBarWidget: 4px of margin per tile and a 7px divider between neighbours.
    private const int TileMarginLogicalPx = 4;
    private const int TileSeparatorLogicalPx = 7;
    // A tile is an icon plus one column group per two rows. Calibrated from measured tiles: a one-group
    // tile lands around 225px and a two-group tile around 405px.
    private const int TileBaseLogicalPx = 45;
    private const int TileGroupLogicalPx = 180;
    private const int RowsPerGroup = 2;
    // Slack for a set containing a provider that has never been rendered, whose width can only be modelled.
    private const int EstimatedFitMarginLogicalPx = 12;
    // No slack once every width in a display group is the widget's own measurement.
    private const int MeasuredFitMarginLogicalPx = 0;

    private static BudgetHysteresisState budgetHysteresis;

    /// <summary>Width a provider's tile takes, from the column groups its rows occupy.</summary>
    internal static int EstimateTileWidth(int rows)
        => TileBaseLogicalPx + (((Math.Max(1, rows) + RowsPerGroup - 1) / RowsPerGroup) * TileGroupLogicalPx);

    /// <summary>Width a whole row of tiles takes, including margins and dividers.</summary>
    internal static int RowWidth(IReadOnlyList<int> tileWidths)
    {
        int total = 0;
        for (int i = 0; i < tileWidths.Count; i++)
            total += tileWidths[i] + TileMarginLogicalPx + (i > 0 ? TileSeparatorLogicalPx : 0);
        return total;
    }

    /// <summary>Width a whole row takes, given each tile's row count. Modelled, for tests and estimates.</summary>
    internal static int EstimateRowWidth(IReadOnlyList<int> rowCounts)
        => RowWidth(rowCounts.Select(EstimateTileWidth).ToList());

    /// <summary>
    /// A provider's tile width: what the widget actually measured for it, or the model when it has never
    /// been rendered.
    /// </summary>
    private static int TileWidth(ProviderId provider)
        => TaskbarSpace.TryGetTileWidth(provider, out int measured)
            ? measured
            : EstimateTileWidth(RowCount(provider));

    private static bool IsTileWidthMeasured(ProviderId provider)
        => TaskbarSpace.TryGetTileWidth(provider, out _);

    /// <summary>
    /// Whether tiles of these row counts fit. Returns true when nothing has been measured yet — for the
    /// second or two before a widget reports a span, the tile cap is the only bound, and refusing every pin
    /// in that window would look broken.
    /// </summary>
    internal static bool FitsTaskbar(IReadOnlyList<int> rowCounts, int availableWidth)
        => FitsWidth(rowCounts.Select(EstimateTileWidth).ToList(), availableWidth, EstimatedFitMarginLogicalPx);

    private static bool FitsWidth(IReadOnlyList<int> tileWidths, int availableWidth, int margin)
        => availableWidth <= TaskbarSpace.UnknownWidth
        || RowWidth(tileWidths) + (tileWidths.Count == 0 ? 0 : margin) <= availableWidth;

    /// <summary>Whether these providers fit all displays where their pins are routed.</summary>
    internal static bool FitsTaskbar(
        IReadOnlyList<ProviderId> providers,
        IReadOnlyList<PinBudgetDisplay> displays,
        int maxCount)
    {
        return FitsBudget(providers, displays, maxCount, out _);
    }

    /// <summary>Whether these providers fit all displays using the current effective tile cap.</summary>
    private static bool FitsTaskbar(
        IReadOnlyList<ProviderId> providers,
        IReadOnlyList<PinBudgetDisplay> displays)
        => FitsTaskbar(providers, displays, UsageCoordinator.MaxDisplayedWidgetTiles);

    /// <summary>
    /// Whether these providers' tiles fit, using each one's measured width where known. A display with no
    /// width measurement is intentionally permissive; another measured display may still provide an
    /// accurate reason to refuse or evict.
    /// </summary>
    private static bool FitsBudget(
        IReadOnlyList<ProviderId> providers,
        IReadOnlyList<PinBudgetDisplay> displays,
        int maxCount,
        out IReadOnlyList<DisplayBudgetCalculation> calculations)
    {
        calculations = CalculateDisplays(providers, displays);
        if (displays.Count == 0 && providers.Count > maxCount)
            return false;

        foreach (var calculation in calculations)
        {
            if (calculation.Providers.Length > maxCount)
                return false;
            if (calculation.AvailableWidth > TaskbarSpace.UnknownWidth
                && calculation.RequiredWidth > calculation.AvailableWidth)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="provider"/> can be pinned right now. The reason states the cap and the
    /// per-display width calculations that led to a refusal, so a multi-monitor layout is diagnosable.
    /// </summary>
    public static bool CanPin(ProviderId provider, out string reason)
        => CanPin(provider, WidgetSettingsService.GetPinnedProviderDisplay(provider), out reason);

    /// <summary>
    /// Whether <paramref name="provider"/> can be pinned at the prospective adaptive destination. The
    /// destination must be evaluated before it is persisted; otherwise admission uses the provider's old
    /// route and can accept or reject the pin against the wrong display.
    /// </summary>
    public static bool CanPin(ProviderId provider, string? prospectivePinnedDisplay, out string reason)
    {
        string? currentPinnedDisplay = WidgetSettingsService.GetPinnedProviderDisplay(provider);
        if (WidgetSettingsService.IsProviderPinned(provider)
            && string.Equals(
                currentPinnedDisplay,
                prospectivePinnedDisplay,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = string.Empty;
            return true;
        }

        var pinned = PinnedProviders();
        string name = ProviderName(provider);
        bool floating = WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Floating;
        string surfaceNoun = floating ? "floating widget" : "taskbar";
        var candidate = pinned.Contains(provider)
            ? pinned
            : pinned.Append(provider).ToList();
        var displays = BuildDisplayBudgets(candidate, provider, prospectivePinnedDisplay);
        var calculations = CalculateDisplays(candidate, displays);
        int maxTiles = UsageCoordinator.MaxDisplayedWidgetTiles;
        string calculationText = DescribeCalculations(calculations, candidate.Count, maxTiles);

        if (!FitsTaskbar(candidate, displays, maxTiles))
        {
            var blockingCaps = calculations
                .Where(calculation => calculation.Providers.Length > maxTiles)
                .ToList();
            var blockingWidths = calculations
                .Where(calculation => calculation.AvailableWidth > TaskbarSpace.UnknownWidth
                    && calculation.RequiredWidth > calculation.AvailableWidth)
                .ToList();
            bool globalCapFallback = displays.Count == 0 && candidate.Count > maxTiles;

            LogPinRefusal(provider, calculations, calculationText);
            if (floating || globalCapFallback)
            {
                reason = $"The {surfaceNoun} can show at most {maxTiles} quota providers at once, and you already "
                    + $"have {string.Join(", ", pinned.Select(ProviderName))} pinned ({calculationText}). "
                    + $"Unpin one of those to make room for {name}.";
                return false;
            }

            var blocking = blockingCaps
                .Select(calculation =>
                    $"{DisplayLabel(calculation.DisplayKey)} ({calculation.Providers.Length}/{maxTiles} routed tiles)")
                .Concat(blockingWidths.Select(calculation =>
                    $"{DisplayLabel(calculation.DisplayKey)} ({calculation.RequiredWidth}px needed vs {calculation.AvailableWidth}px available)"))
                .ToList();
            string blockedDisplays = blocking.Count == 0
                ? string.Empty
                : $" Blocked by {string.Join(" and ", blocking)}.";

            reason = $"There isn't room on the taskbar for {name} ({Describe(provider)}) next to "
                  + $"{string.Join(" and ", pinned.Select(p => $"{ProviderName(p)} ({Describe(p)})"))}. "
                  + $"{calculationText}.{blockedDisplays} Turn off some rows for {name} or for a pinned provider, unpin one, or set the Windows "
                  + "taskbar to left alignment — that frees up a lot more room.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static void LogPinRefusal(
        ProviderId provider,
        IReadOnlyList<DisplayBudgetCalculation> calculations,
        string calculationText)
    {
        Diagnostics.Log.Debug(
            $"[pin] refused {provider}: {calculationText} displays="
            + $"[{string.Join(", ", calculations.Select(FormatCalculationForLog))}]");
    }

    private static string Describe(ProviderId provider)
    {
        int rows = RowCount(provider);
        return rows == 1 ? "1 row" : $"{rows} rows";
    }

    /// <summary>
    /// Brings the pinned set back inside the taskbar by unpinning the least recently used providers, and
    /// reports which went. The same over-budget condition must persist for three evaluations before a pin
    /// is removed, protecting against a transient or false geometry measurement.
    /// </summary>
    /// <param name="notify">
    /// False when the caller raises <see cref="WidgetSettingsService.Changed"/> itself straight after, so
    /// one user action does not rebuild the nav badges, the flyout strip and every widget tile twice.
    /// </param>
    public static IReadOnlyList<ProviderId> EnforceBudget(bool notify = true)
    {
        var pinned = PinnedProviders();
        if (pinned.Count == 0)
        {
            budgetHysteresis = default;
            return Array.Empty<ProviderId>();
        }

        var recent = UsageCoordinator.Instance.RecentProviders;
        var displays = BuildDisplayBudgets(pinned);
        var calculations = CalculateDisplays(pinned, displays);
        string signature = BudgetSignature(pinned, displays, recent);

        var recency = new Dictionary<ProviderId, int>();
        for (int i = 0; i < recent.Count; i++)
            recency.TryAdd(recent[i], i);

        // Least recently active first: whatever the user has touched most recently is what they want kept.
        var order = pinned
            .OrderByDescending(p => recency.TryGetValue(p, out int index) ? index : int.MaxValue)
            .ToList();
        var entries = order.Select(provider => (Provider: provider, Width: TileWidth(provider))).ToList();
        var dropped = SelectDropsForDisplays(entries, displays, UsageCoordinator.MaxDisplayedWidgetTiles);
        bool overBudget = dropped.Count > 0;

        budgetHysteresis = AdvanceBudgetHysteresis(budgetHysteresis, signature, overBudget);
        if (!IsBudgetEvictionDue(budgetHysteresis))
            return Array.Empty<ProviderId>();

        foreach (var provider in dropped)
            WidgetSettingsService.SetProviderPinnedSilent(provider, false);

        if (dropped.Count > 0)
        {
            string blockingDisplays = string.Join(
                ", ",
                calculations
                    .Where(calculation => calculation.Providers.Length > UsageCoordinator.MaxDisplayedWidgetTiles
                        || (calculation.AvailableWidth > TaskbarSpace.UnknownWidth
                            && calculation.RequiredWidth > calculation.AvailableWidth))
                    .Select(FormatCalculationForLog));
            Diagnostics.Log.Warning(
                $"[pin] auto-unpinned after {BudgetHysteresisEvaluationCount} consecutive over-budget evaluations: "
                + $"dropped=[{string.Join(", ", dropped)}] blocked=[{blockingDisplays}] displays="
                + $"[{string.Join(", ", calculations.Select(FormatCalculationForLog))}]");

            // The set just changed; do not carry the old condition into the next set.
            budgetHysteresis = default;
            if (notify)
                WidgetSettingsService.SaveProviderPinsAndNotify();
            else
                WidgetSettingsService.SaveProviderPins();
            ProvidersUnpinned?.Invoke(dropped);
        }

        return dropped;
    }

    /// <summary>Advances hysteresis state; pure so consecutive-evaluation behavior can be tested directly.</summary>
    internal static BudgetHysteresisState AdvanceBudgetHysteresis(
        BudgetHysteresisState previous,
        string signature,
        bool overBudget)
    {
        if (!overBudget)
            return default;

        int consecutive = string.Equals(previous.Signature, signature, StringComparison.Ordinal)
            ? previous.ConsecutiveOverBudget + 1
            : 1;
        return new BudgetHysteresisState(signature, consecutive);
    }

    internal static bool IsBudgetEvictionDue(BudgetHysteresisState state)
        => state.ConsecutiveOverBudget >= BudgetHysteresisEvaluationCount;

    /// <summary>
    /// Which pinned providers to drop so the rest fit every routed display and the tile cap. Pure so the
    /// ordering can be tested without a taskbar or cached usage.
    /// </summary>
    /// <param name="pinned">Pinned providers with their tile widths, least worth keeping FIRST.</param>
    internal static IReadOnlyList<ProviderId> SelectDropsForDisplays(
        IReadOnlyList<(ProviderId Provider, int Width)> pinned,
        IReadOnlyList<PinBudgetDisplay> displays,
        int maxCount)
    {
        var keeping = pinned.ToList();
        var dropped = new List<ProviderId>();

        while (keeping.Count > 0 && !FitsEntries(keeping, displays, maxCount))
        {
            bool globalCapExceeded = displays.Count == 0 && keeping.Count > maxCount;
            var budgetOffenders = globalCapExceeded
                ? new List<ProviderId>()
                : BudgetOffendingProviders(keeping, displays, maxCount);
            ProviderId? dropProvider = null;

            // When width or a per-display cap is the problem, do not sacrifice a pin routed exclusively to
            // another display. Only the no-identity/floating fallback uses a global cap.
            foreach (var entry in pinned)
            {
                if (!keeping.Any(kept => kept.Provider == entry.Provider))
                    continue;
                if (globalCapExceeded || budgetOffenders.Contains(entry.Provider))
                {
                    dropProvider = entry.Provider;
                    break;
                }
            }

            // Defensive fallback for malformed input (for example, a display group naming a provider not
            // present in the tile list): make progress rather than looping forever.
            dropProvider ??= keeping[0].Provider;
            int keepAt = keeping.FindIndex(entry => entry.Provider == dropProvider.Value);
            if (keepAt < 0)
                break;

            keeping.RemoveAt(keepAt);
            dropped.Add(dropProvider.Value);
        }

        return dropped;
    }

    /// <summary>
    /// Which pinned providers to drop for one display. Kept as the small compatibility seam used by the
    /// original unit tests.
    /// </summary>
    internal static IReadOnlyList<ProviderId> SelectDrops(
        IReadOnlyList<(ProviderId Provider, int Width)> pinned,
        int availableWidth,
        int maxCount)
        => SelectDropsForDisplays(
            pinned,
            new[]
            {
                new PinBudgetDisplay(
                    string.Empty,
                    availableWidth,
                    pinned.Select(entry => entry.Provider).ToArray()),
            },
            maxCount);

    private static bool FitsEntries(
        IReadOnlyList<(ProviderId Provider, int Width)> entries,
        IReadOnlyList<PinBudgetDisplay> displays,
        int maxCount)
    {
        if (displays.Count == 0 && entries.Count > maxCount)
            return false;

        foreach (var display in displays)
        {
            var routed = entries
                .Where(entry => display.Providers.Contains(entry.Provider))
                .ToList();
            if (routed.Count > maxCount)
                return false;

            if (display.AvailableWidth > TaskbarSpace.UnknownWidth
                && routed.Count > 0
                && RowWidth(routed.Select(entry => entry.Width).ToList()) + display.FitMargin > display.AvailableWidth)
            {
                return false;
            }
        }

        return true;
    }

    private static List<ProviderId> BudgetOffendingProviders(
        IReadOnlyList<(ProviderId Provider, int Width)> entries,
        IReadOnlyList<PinBudgetDisplay> displays,
        int maxCount)
    {
        var offenders = new List<ProviderId>();
        foreach (var display in displays)
        {
            var routed = entries
                .Where(entry => display.Providers.Contains(entry.Provider))
                .ToList();
            bool capOver = routed.Count > maxCount;
            bool widthOver = display.AvailableWidth > TaskbarSpace.UnknownWidth
                && routed.Count > 0
                && RowWidth(routed.Select(entry => entry.Width).ToList()) + display.FitMargin > display.AvailableWidth;

            if (!capOver && !widthOver)
            {
                continue;
            }

            foreach (var entry in routed)
            {
                if (!offenders.Contains(entry.Provider))
                {
                    offenders.Add(entry.Provider);
                }
            }
        }

        return offenders;
    }

    private static IReadOnlyList<DisplayBudgetCalculation> CalculateDisplays(
        IReadOnlyList<ProviderId> providers,
        IReadOnlyList<PinBudgetDisplay> displays)
    {
        var calculations = new List<DisplayBudgetCalculation>(displays.Count);
        foreach (var display in displays)
        {
            var routed = display.Providers
                .Where(providers.Contains)
                .ToArray();
            if (routed.Length == 0)
                continue;

            int required = RowWidth(routed.Select(TileWidth).ToList()) + display.FitMargin;
            calculations.Add(new DisplayBudgetCalculation(
                display.DisplayKey,
                display.AvailableWidth,
                routed,
                required));
        }

        return calculations;
    }

    private static List<PinBudgetDisplay> BuildDisplayBudgets(
        IReadOnlyList<ProviderId> providers,
        ProviderId? prospectiveProvider = null,
        string? prospectivePinnedDisplay = null)
    {
        if (WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Floating)
            return [];

        var known = TaskbarSpace.KnownDisplayKeys;
        if (known.Count == 0)
        {
            // Before a display identity is published, preserve the old single-display/global fallback.
            return
            [
                new PinBudgetDisplay(
                    string.Empty,
                    TaskbarSpace.AvailableLogicalWidth,
                    providers,
                    providers.Any(provider => !IsTileWidthMeasured(provider))
                        ? EstimatedFitMarginLogicalPx
                        : MeasuredFitMarginLogicalPx),
            ];
        }

        var availableDisplays = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string primaryDisplay = TaskbarSpace.PrimaryDisplayKey
            ?? known.FirstOrDefault()
            ?? string.Empty;
        var displays = new List<PinBudgetDisplay>(known.Count);

        foreach (string displayKey in known)
        {
            var routed = providers
                .Where(provider => TaskbarContentRouter.IsRoutedToDisplay(
                    provider,
                    WidgetSettingsService.CurrentTaskbarPlacement,
                    WidgetSettingsService.SelectedTaskbarDisplayKey,
                    displayKey,
                    primaryDisplay,
                    availableDisplays,
                    WidgetSettingsService.GetAdaptiveProviderDisplay,
                    _ => true,
                    provider => prospectiveProvider == provider
                        ? prospectivePinnedDisplay
                        : WidgetSettingsService.GetPinnedProviderDisplay(provider)))
                .ToArray();
            if (routed.Length == 0)
                continue;

            int availableWidth = TaskbarSpace.TryGetAvailableWidth(displayKey, out int measured)
                ? measured
                : TaskbarSpace.UnknownWidth;
            int fitMargin = routed.Any(provider => !IsTileWidthMeasured(provider))
                ? EstimatedFitMarginLogicalPx
                : MeasuredFitMarginLogicalPx;
            displays.Add(new PinBudgetDisplay(displayKey, availableWidth, routed, fitMargin));
        }

        return displays;
    }

    private static string BudgetSignature(
        IReadOnlyList<ProviderId> providers,
        IReadOnlyList<PinBudgetDisplay> displays,
        IReadOnlyList<ProviderId> recent)
    {
        var builder = new StringBuilder(256);
        builder.Append((int)WidgetSettingsService.CurrentSurface).Append('|')
            .Append((int)WidgetSettingsService.CurrentTaskbarPlacement).Append('|')
            .Append(WidgetSettingsService.SelectedTaskbarDisplayKey).Append('|')
            .Append(UsageCoordinator.MaxDisplayedWidgetTiles);

        foreach (var provider in providers.OrderBy(provider => provider))
        {
            int recentIndex = int.MaxValue;
            for (int i = 0; i < recent.Count; i++)
            {
                if (recent[i] == provider)
                {
                    recentIndex = i;
                    break;
                }
            }

            builder.Append("|p:").Append(provider)
                .Append(':').Append(WidgetSettingsService.GetPinnedProviderDisplay(provider) ?? string.Empty)
                .Append(':').Append(WidgetSettingsService.GetAdaptiveProviderDisplay(provider) ?? string.Empty)
                .Append(':').Append(TileWidth(provider))
                .Append(':').Append(recentIndex);
        }

        foreach (var display in displays.OrderBy(display => display.DisplayKey, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("|d:").Append(display.DisplayKey)
                .Append(':').Append(display.AvailableWidth)
                .Append(':').Append(display.FitMargin)
                .Append(':').Append(string.Join(',', display.Providers));
        }

        return builder.ToString();
    }

    private static string DescribeCalculations(
        IReadOnlyList<DisplayBudgetCalculation> calculations,
        int pinCount,
        int maxTiles)
    {
        if (calculations.Count == 0)
            return $"slots {pinCount}/{maxTiles}; taskbar width unknown";

        return string.Join("; ", calculations.Select(calculation => FormatCalculation(calculation, maxTiles)));
    }

    private static string FormatCalculation(DisplayBudgetCalculation calculation, int maxTiles)
    {
        string width = calculation.AvailableWidth <= TaskbarSpace.UnknownWidth
            ? "width unknown"
            : $"{calculation.RequiredWidth}px needed vs {calculation.AvailableWidth}px available";
        return $"{DisplayLabel(calculation.DisplayKey)}: {calculation.Providers.Length}/{maxTiles} routed pinned tile(s), {width}";
    }

    private static string FormatCalculationForLog(DisplayBudgetCalculation calculation)
        => $"{DisplayLabel(calculation.DisplayKey)} pins={calculation.Providers.Length}/{UsageCoordinator.MaxDisplayedWidgetTiles} "
            + $"needed={calculation.RequiredWidth} available={calculation.AvailableWidth}";

    private static string DisplayLabel(string displayKey)
    {
        if (displayKey.Length == 0)
            return "current taskbar";

        string label = TaskbarWindowTarget.GetDisplayLabel(displayKey);
        return label == "this screen" ? $"{label} ({displayKey})" : label;
    }

    private readonly record struct DisplayBudgetCalculation(
        string DisplayKey,
        int AvailableWidth,
        ProviderId[] Providers,
        int RequiredWidth);

    // Enum.GetValues allocates a fresh array on every call, and this type is on the 5s tick path.
    private static readonly ProviderId[] AllProviders = Enum.GetValues<ProviderId>();

    private static List<ProviderId> PinnedProviders()
    {
        var pinned = new List<ProviderId>();
        foreach (var provider in AllProviders)
        {
            if (WidgetSettingsService.IsProviderPinned(provider))
                pinned.Add(provider);
        }
        return pinned;
    }

    /// <summary>
    /// Rows a provider's tile would render. Uses its cached usage when there is any, because the enabled
    /// row settings alone overstate it badly — most providers have several rows switched on that their
    /// plan never reports.
    /// </summary>
    private static int RowCount(ProviderId provider)
    {
        var service = UsageCoordinator.Instance.Service;
        if (service.TryGetCached(provider, out var cached))
            return WidgetSummary.CountRenderedRows(cached);
        if (service.TryGetLastSuccessfulLiveResult(provider, out var lastSuccess))
            return WidgetSummary.CountRenderedRows(lastSuccess);
        return WidgetSummary.AssumedRowCount;
    }

    private static string ProviderName(ProviderId provider)
        => UsageCoordinator.Instance.Service.Get(provider)?.DisplayName ?? provider.ToString();
}
