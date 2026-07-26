using System;
using System.Collections.Generic;
using System.Linq;
using TaskbarQuota.Controls;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Services;

/// <summary>
/// Prices pinned providers against a fixed budget so the taskbar row can never be asked to show more than
/// it can hold.
///
/// A flat "three providers" cap is the wrong unit: a provider showing four rows is twice as wide as one
/// showing two, because rows pack two to a column group. So providers are weighted instead — a short one
/// (one or two rows) costs <see cref="ShortSlots"/>, a long one (three or more) costs
/// <see cref="LongSlots"/> — and the user spends a budget of <see cref="TotalSlots"/> however they like:
/// three short, two long, or a long plus two short.
/// </summary>
public static class PinBudgetService
{
    /// <summary>Total weight a user may have pinned at once.</summary>
    public const int TotalSlots = 5;
    /// <summary>Cost of a provider rendering one or two rows — a single column group.</summary>
    public const int ShortSlots = 1;
    /// <summary>Cost of a provider rendering three or more rows — two column groups.</summary>
    public const int LongSlots = 2;
    /// <summary>Rows at which a provider stops being "short".</summary>
    public const int LongRowThreshold = 3;

    /// <summary>Raised after the budget auto-unpins providers, so the UI can refresh and explain.</summary>
    public static event Action<IReadOnlyList<ProviderId>>? ProvidersUnpinned;

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
    // No slack once every width is the widget's own measurement. A margin here refuses layouts that
    // provably fit — the widget was rendering three tiles at 702px of a 706px span while the pin for the
    // third was being rejected at 702 + 6 > 706. There is no model error left to absorb.
    private const int MeasuredFitMarginLogicalPx = 0;

    /// <summary>What one provider costs, from the number of rows its tile would render.</summary>
    public static int SlotCost(ProviderId provider)
        => RowCount(provider) >= LongRowThreshold ? LongSlots : ShortSlots;

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
        => Taskbar.TaskbarSpace.TryGetTileWidth(provider, out int measured)
            ? measured
            : EstimateTileWidth(RowCount(provider));

    /// <summary>
    /// Whether a set of providers fits the taskbar space actually measured. Returns true when nothing has
    /// been measured yet, so a cold start falls back to the weight budget rather than refusing every pin.
    /// </summary>
    /// <remarks>
    /// Only the PINNED set is judged. Reserving extra room for the active tool's tile on top looks prudent
    /// but is wrong: when the provider being pinned is the one in use there is no extra tile at all, and
    /// the reserve then refuses pins that fit perfectly well. The case it was guarding — a third, unpinned
    /// tool in the foreground — is handled where it can be measured exactly, by the widget dropping that
    /// courtesy tile rather than overflowing.
    /// </remarks>
    internal static bool FitsTaskbar(IReadOnlyList<int> rowCounts, int availableWidth)
        => FitsWidth(rowCounts.Select(EstimateTileWidth).ToList(), availableWidth, EstimatedFitMarginLogicalPx);

    private static bool FitsWidth(IReadOnlyList<int> tileWidths, int availableWidth, int margin)
        => availableWidth <= Taskbar.TaskbarSpace.UnknownWidth
        || RowWidth(tileWidths) + margin <= availableWidth;

    /// <summary>Whether these providers' tiles fit, using each one's measured width where known.</summary>
    private static bool FitsTaskbar(IReadOnlyList<ProviderId> providers, int availableWidth)
    {
        bool allMeasured = providers.All(p => Taskbar.TaskbarSpace.TryGetTileWidth(p, out _));
        return FitsWidth(
            providers.Select(TileWidth).ToList(),
            availableWidth,
            allMeasured ? MeasuredFitMarginLogicalPx : EstimatedFitMarginLogicalPx);
    }

    public static bool IsLong(ProviderId provider) => SlotCost(provider) == LongSlots;

    /// <summary>Budget currently spent, optionally ignoring one provider.</summary>
    public static int UsedSlots(ProviderId? excluding = null)
        => PinnedProviders()
            .Where(p => excluding is not { } skip || p != skip)
            .Sum(SlotCost);

    /// <summary>Budget left over, treating <paramref name="excluding"/> as not pinned.</summary>
    public static int RemainingSlots(ProviderId? excluding = null) => TotalSlots - UsedSlots(excluding);

    /// <summary>
    /// Whether <paramref name="provider"/> can be pinned right now. Fails when the tile cap is reached or
    /// the provider's weight does not fit the remaining budget; <paramref name="reason"/> explains which,
    /// for the pin button's tooltip.
    /// </summary>
    public static bool CanPin(ProviderId provider, out string reason)
    {
        if (WidgetSettingsService.IsProviderPinned(provider))
        {
            reason = string.Empty;
            return true;
        }

        var pinned = PinnedProviders();
        string name = ProviderName(provider);

if (pinned.Count >= UsageCoordinator.MaxWidgetTiles)
        {
            reason = $"The taskbar can show at most {UsageCoordinator.MaxWidgetTiles} providers at once, and you already "
                + $"have {string.Join(", ", pinned.Select(ProviderName))} pinned. Unpin one of those to make room for {name}.";
            return false;
        }

        int cost = SlotCost(provider);
        int remaining = RemainingSlots();
        if (cost > remaining)
        {
            reason = $"{name} shows {Describe(provider)}, which costs {cost} of the {TotalSlots} pin slots, "
                + $"and only {remaining} {(remaining == 1 ? "is" : "are")} free. "
                + $"Turn off a row or two for {name} to make it a short provider, or unpin one of "
                + $"{string.Join(", ", pinned.Select(ProviderName))}.";
            return false;
        }

        // The weight budget is a coarse rule; this is the real one — a tile is never trimmed or reduced, so
        // a pin that would not fit the measured free span has to be refused rather than rendered badly.
        var candidate = pinned.Append(provider).ToList();
        if (!FitsTaskbar(candidate, Taskbar.TaskbarSpace.AvailableLogicalWidth))
        {
            // A refusal the user did not expect is impossible to diagnose from the message alone, since it
            // turns on measurements they cannot see. Record what the decision was actually made from.
            Diagnostics.Log.Debug(
                $"[pin] refused {provider}: tiles=[{string.Join(", ", candidate.Select(p => $"{p}:{TileWidth(p)}{(Taskbar.TaskbarSpace.TryGetTileWidth(p, out _) ? "" : "~")}"))}] "
                + $"row={RowWidth(candidate.Select(TileWidth).ToList())} "
                + $"available={Taskbar.TaskbarSpace.AvailableLogicalWidth}");

            reason = $"There isn't room on the taskbar for {name} ({Describe(provider)}) next to "
                + $"{string.Join(" and ", pinned.Select(p => $"{ProviderName(p)} ({Describe(p)})"))}. "
                + $"Turn off some rows for {name} or for a pinned provider, unpin one, or set the Windows "
                + "taskbar to left alignment — that frees up a lot more room.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string Describe(ProviderId provider)
    {
        int rows = RowCount(provider);
        return rows == 1 ? "1 row" : $"{rows} rows";
    }

    /// <summary>
    /// Brings the pinned set back inside the budget by unpinning the least recently used providers, and
    /// reports which went. Called after anything that can change a provider's weight — enabling a row on a
    /// pinned provider promotes it from short to long, and a display setting must never be refused because
    /// of an unrelated pin.
    /// </summary>
    public static IReadOnlyList<ProviderId> EnforceBudget()
    {
        var pinned = PinnedProviders();
        if (pinned.Count == 0)
            return Array.Empty<ProviderId>();

        // Least recently active first: whatever the user has touched most recently is what they want kept.
        var recent = UsageCoordinator.Instance.RecentProviders;
        var recency = new Dictionary<ProviderId, int>();
        for (int i = 0; i < recent.Count; i++)
            recency.TryAdd(recent[i], i);

        var order = pinned
            .OrderByDescending(p => recency.TryGetValue(p, out int index) ? index : int.MaxValue)
            .ToList();

        var dropped = SelectDrops(
                order.Select(p => (p, SlotCost(p))).ToList(),
                TotalSlots,
                UsageCoordinator.MaxWidgetTiles)
            .ToList();

        // Weight alone can pass while the row still overflows the bar — a provider that grew a third row
        // both costs more slots AND takes another column group. Keep dropping until it genuinely fits,
        // because there is no trimming or glyph left to absorb the overflow.
        var keeping = order.Where(p => !dropped.Contains(p)).ToList();
        foreach (var provider in order)
        {
            if (FitsTaskbar(keeping, Taskbar.TaskbarSpace.AvailableLogicalWidth))
                break;

            if (!keeping.Remove(provider))
                continue;

            dropped.Add(provider);
        }

        foreach (var provider in dropped)
            WidgetSettingsService.SetProviderPinnedSilent(provider, false);

        if (dropped.Count > 0)
        {
            WidgetSettingsService.SaveProviderPinsAndNotify();
            ProvidersUnpinned?.Invoke(dropped);
        }

        return dropped;
    }

    /// <summary>Weight of a provider showing <paramref name="rows"/> rows.</summary>
    internal static int SlotCostForRows(int rows) => rows >= LongRowThreshold ? LongSlots : ShortSlots;

    /// <summary>
    /// Which pinned providers to drop so the set fits both the weight budget and the tile cap. Pure so the
    /// arithmetic can be tested without a taskbar or cached usage.
    /// </summary>
    /// <param name="pinned">Pinned providers with their weights, least worth keeping FIRST.</param>
    internal static IReadOnlyList<ProviderId> SelectDrops(
        IReadOnlyList<(ProviderId Provider, int Cost)> pinned,
        int budget,
        int maxCount)
    {
        int used = pinned.Sum(p => p.Cost);
        int count = pinned.Count;
        var dropped = new List<ProviderId>();

        foreach (var (provider, cost) in pinned)
        {
            if (used <= budget && count <= maxCount)
                break;

            used -= cost;
            count--;
            dropped.Add(provider);
        }

        return dropped;
    }

    private static List<ProviderId> PinnedProviders()
        => Enum.GetValues<ProviderId>().Where(WidgetSettingsService.IsProviderPinned).ToList();

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
