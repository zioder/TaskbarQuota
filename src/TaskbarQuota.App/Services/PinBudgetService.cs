using System;
using System.Collections.Generic;
using System.Linq;
using TaskbarQuota.Controls;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Services;

/// <summary>
/// Decides whether a provider can be pinned, by the only measure that matters: whether its tile fits the
/// free space the taskbar actually has.
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

    /// <summary>
    /// Free width assumed when the usage UI is a floating window rather than a taskbar island. Wide enough
    /// that pins are limited by <see cref="UsageCoordinator.MaxDisplayedWidgetTiles"/>, not by the tray gap.
    /// </summary>
    public const int FloatingAvailableLogicalWidth = 1200;

    /// <summary>
    /// Free width the pin budget uses for the current surface: measured taskbar span, or a large fixed
    /// budget when usage is shown in the floating window.
    /// </summary>
    public static int AvailableLogicalWidth
        => WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Floating
            ? FloatingAvailableLogicalWidth
            : Taskbar.TaskbarSpace.AvailableLogicalWidth;

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
    /// Whether tiles of these row counts fit. Returns true when nothing has been measured yet — for the
    /// second or two before a widget reports a span, the tile cap is the only bound, and refusing every
    /// pin in that window would look broken.
    /// </summary>
    /// <remarks>
    /// Only the PINNED set is judged. Reserving extra room for the active tool's tile on top looks prudent
    /// but is wrong: when the provider being pinned is the one in use there is no extra tile at all, and
    /// the reserve then refuses pins that fit perfectly well. The case it was guarding — a third, unpinned
    /// tool in the foreground — is handled where it can be measured exactly, by the widget holding that
    /// tile back rather than overflowing.
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

    /// <summary>
    /// Whether <paramref name="provider"/> can be pinned right now: only the tile cap and the measured
    /// taskbar space can refuse it. <paramref name="reason"/> says which, and what to change.
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

        if (pinned.Count >= UsageCoordinator.MaxDisplayedWidgetTiles)
        {
            reason = $"The taskbar can show at most {UsageCoordinator.MaxDisplayedWidgetTiles} quota providers at once, and you already "
                + $"have {string.Join(", ", pinned.Select(ProviderName))} pinned. Unpin one of those to make room for {name}.";
            return false;
        }

        var candidate = pinned.Append(provider).ToList();
        int available = AvailableLogicalWidth;
        if (!FitsTaskbar(candidate, available))
        {
            // A refusal the user did not expect is impossible to diagnose from the message alone, since it
            // turns on measurements they cannot see. Record what the decision was actually made from.
            Diagnostics.Log.Debug(
                $"[pin] refused {provider}: tiles=[{string.Join(", ", candidate.Select(p => $"{p}:{TileWidth(p)}{(Taskbar.TaskbarSpace.TryGetTileWidth(p, out _) ? "" : "~")}"))}] "
                + $"row={RowWidth(candidate.Select(TileWidth).ToList())} "
                + $"available={available}");

            bool floating = WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Floating;
            reason = floating
                ? $"There isn't room in the floating widget for {name} ({Describe(provider)}) next to "
                  + $"{string.Join(" and ", pinned.Select(p => $"{ProviderName(p)} ({Describe(p)})"))}. "
                  + $"Turn off some rows for {name} or for a pinned provider, or unpin one."
                : $"There isn't room on the taskbar for {name} ({Describe(provider)}) next to "
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
    /// Brings the pinned set back inside the taskbar by unpinning the least recently used providers, and
    /// reports which went. Called after anything that can change a tile's width — enabling a row on a
    /// pinned provider can add a whole column group, and a display setting must never be refused because
    /// of an unrelated pin.
    /// </summary>
    /// <param name="notify">
    /// False when the caller raises <see cref="WidgetSettingsService.Changed"/> itself straight after, so
    /// one user action does not rebuild the nav badges, the flyout strip and every widget tile twice.
    /// </param>
    public static IReadOnlyList<ProviderId> EnforceBudget(bool notify = true)
    {
        // The 5s widget health tick calls this forever. Nothing can need unpinning unless the pinned set,
        // one of its tile widths, or the free span has moved since the last run, and all three are already
        // ints — so the common case costs a hash instead of a sort and three list allocations.
        int key = BudgetKey();
        if (key == _lastBudgetKey)
            return Array.Empty<ProviderId>();

        _lastBudgetKey = key;

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

        var keeping = new List<ProviderId>(order);
        var dropped = new List<ProviderId>();
        int available = AvailableLogicalWidth;
        foreach (var provider in order)
        {
            if (keeping.Count <= UsageCoordinator.MaxDisplayedWidgetTiles
                && FitsTaskbar(keeping, available))
            {
                break;
            }

            keeping.Remove(provider);
            dropped.Add(provider);
        }

        foreach (var provider in dropped)
            WidgetSettingsService.SetProviderPinnedSilent(provider, false);

        if (dropped.Count > 0)
        {
            // The set just changed, so the key computed above is stale. Recompute rather than clear, or the
            // next tick redoes the whole sort to reach the same answer.
            _lastBudgetKey = BudgetKey();
            if (notify)
                WidgetSettingsService.SaveProviderPinsAndNotify();
            else
                WidgetSettingsService.SaveProviderPins();
            ProvidersUnpinned?.Invoke(dropped);
        }

        return dropped;
    }

    // Guards the early-out above. Covers everything the decision reads: the free span, which providers are
    // pinned, and each one's measured tile width (which changes when the user toggles a row).
    private static int _lastBudgetKey = -1;

    private static int BudgetKey()
    {
        var hash = new HashCode();
        hash.Add(AvailableLogicalWidth);
        hash.Add((int)WidgetSettingsService.CurrentSurface);
        hash.Add(UsageCoordinator.MaxDisplayedWidgetTiles);
        foreach (var provider in AllProviders)
        {
            if (!WidgetSettingsService.IsProviderPinned(provider))
                continue;

            hash.Add(provider);
            hash.Add(Taskbar.TaskbarSpace.TryGetTileWidth(provider, out int width) ? width : 0);
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// Which pinned providers to drop so the rest fit <paramref name="availableWidth"/> and the tile cap.
    /// Pure so the ordering can be tested without a taskbar or cached usage.
    /// </summary>
    /// <param name="pinned">Pinned providers with their tile widths, least worth keeping FIRST.</param>
    internal static IReadOnlyList<ProviderId> SelectDrops(
        IReadOnlyList<(ProviderId Provider, int Width)> pinned,
        int availableWidth,
        int maxCount)
    {
        var keeping = pinned.ToList();
        var dropped = new List<ProviderId>();

        foreach (var entry in pinned)
        {
            if (keeping.Count <= maxCount
                && RowWidth(keeping.Select(k => k.Width).ToList()) <= availableWidth)
            {
                break;
            }

            keeping.Remove(entry);
            dropped.Add(entry.Provider);
        }

        return dropped;
    }

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
