using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.System;
using TaskbarQuota.Controls;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Taskbar
{
    /// <summary>
    /// Hosts a XAML island inside the Windows taskbar by SetParent-ing a layered popup window into
    /// a primary or secondary taskbar. Auto-positions next to the system tray on the primary taskbar and
    /// at the free outer edge on secondary taskbars. Adapted from Awqat-Salaat's TaskBarWidget.
    /// </summary>
    internal sealed class TaskBarWidget : IDisposable
    {
        private const string ReBarWindow32ClassName = "ReBarWindow32";
        private const string NotificationAreaClassName = "TrayNotifyWnd";
        private const string WidgetsButtonAutomationId = "WidgetsButton";
        private const int DefaultWidgetHostWidth = 172;
        private const int TrayClearanceLogicalPx = 6;
        // Each tile carries a 2px left + 2px right margin that its own desired width excludes, so the host
        // width has to add this back per visible tile or a multi-tile layout clips its last column.
        // Kept tight deliberately: with three tiles, margins and dividers were costing 42px of a ~730px
        // taskbar span, which is the difference between a third provider fitting and being refused.
        private const int TileHorizontalMarginLogicalPx = 4;
        // Fixed width of the "|" divider drawn between adjacent tiles. Fixed rather than measured so the
        // fit math stays exact and doesn't depend on a layout pass having run.
        private const int TileSeparatorLogicalPx = 7;
        // Space assumed available before the first position pass has measured the real taskbar gap. Wide
        // enough for three tiles; the first pass corrects it either way.
        private const int DefaultAvailableLogicalWidth = 640;
        // Rows a pinned tile shows while it is NOT the active tool. Two is one full column group, so such a
        // tile has a fixed width whatever the user enables, and enabling a row can never evict a neighbour.
        private const int PinnedTileMaxRows = 2;
        // Floor for the active tool's tile. It may give up a third row to keep another provider on the bar,
        // but never drops to a single line — that reads as a glitch rather than as a space saving.
        private const int MinActiveRows = 2;
        // A tile only animates when it genuinely moved; below this a value simply changed width.
        private const int TileMoveThresholdLogicalPx = 8;
        // How far a newly shown tile eases in from. Short enough to stay inside the host window, so it
        // reads as arriving rather than being clipped off the taskbar edge.
        private const int TileEntryOffsetLogicalPx = 28;
        // Worth of a reset countdown, by whose tile it is on. Both stay below a row (4 points) so a row is
        // never traded for a countdown, but the active tool keeps its countdowns until every pinned tile
        // has given theirs up.
        private const int ActiveResetWeight = 3;
        private const int PinnedResetWeight = 1;
        private static readonly TimeSpan PositionDisposeWait = TimeSpan.FromSeconds(3);
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        // Approx width of the Win11 far-left Widgets/weather pill; used to reserve clearance when its exact
        // bounds can't be read via UIA, so the widget never anchors on top of it (issue #17).
        private const int WidgetsButtonFallbackLogicalPx = 160;

        private static readonly bool IsRtlUI = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        private static readonly object WindowClassLock = new();
        private static readonly WndProc SharedWndProc = SharedWindowProc;
        private static bool windowClassRegistered;
        private static int windowClassUsers;

        // Minimum horizontal recompute delta (logical px, DPI-scaled) before the resting widget is moved.
        private int RepositionDeadbandPx => (int)Math.Ceiling(2 * dpiScale);
        private int TrayClearancePx => (int)Math.Ceiling(TrayClearanceLogicalPx * dpiScale);

        private readonly double dpiScale;
        private readonly uint taskbarDpi;
        private readonly IntPtr hwndShell;
        private readonly IntPtr hwndTrayNotify;
        private readonly IntPtr hwndReBar;
        private readonly IntPtr hwndStart;
        private readonly bool isPrimaryTaskbar;
        private readonly string displayKey;
        private readonly string WidgetClassName = "TaskbarQuotaWidgetWinRT";
        private readonly TaskbarStructureWatcher taskbarWatcher;
        private readonly string positionPath;
        private readonly CancellationTokenSource positionUpdateCancellation = new();
        private readonly SemaphoreSlim positionUpdateGate = new(1, 1);
        private readonly object positionRequestLock = new();

        private IntPtr hwnd;
        private AppWindow? appWindow;
        // Fixed pool of tile slots, created once and reused. Slot i renders tileProviders[i]; reassigning a
        // slot to another provider re-renders it through WidgetSummary's normal provider-switch path. A
        // fixed pool means the panel's children never change, so no tile is ever unloaded and re-loaded
        // (which would drop its WidgetSettingsService subscription) when the display order shifts.
        private readonly WidgetSummary[] tiles = new WidgetSummary[UsageCoordinator.MaxWidgetTiles];
        // separators[i] is the "|" divider between slot i and slot i+1.
        private readonly Microsoft.UI.Xaml.Controls.TextBlock[] separators =
            new Microsoft.UI.Xaml.Controls.TextBlock[UsageCoordinator.MaxWidgetTiles - 1];
        private readonly ProviderId?[] tileProviders = new ProviderId?[UsageCoordinator.MaxWidgetTiles];
        // Slot has a provider AND fits inside the measured taskbar gap. Trimming only ever drops from the
        // right, so the leading (active) tile is the last one to go.
        private readonly bool[] tileFits = new bool[UsageCoordinator.MaxWidgetTiles];
        // Collapsed to the provider glyph because the row didn't fit at full width.
        private readonly bool[] tileIconOnly = new bool[UsageCoordinator.MaxWidgetTiles];
        // Has a provider, but is being held back this pass because the row would otherwise overflow. Only
        // ever the active tool's tile when that provider is not pinned.
        private readonly bool[] tileSuppressed = new bool[UsageCoordinator.MaxWidgetTiles];
        private ProviderId? activeTileProvider;
        // Where each shown provider sat in the last layout, so the next one can animate the difference.
        private Dictionary<ProviderId, int> lastTilePositions = new();
        private Microsoft.UI.Xaml.Controls.StackPanel? summaryPanel;
        private int availableLogicalWidth = DefaultAvailableLogicalWidth;
        private bool isRecomputingLayout;
        private bool layoutRepositionPending;
        private string? lastLayoutSignature;
        private DesktopWindowXamlSource? host;
        private Microsoft.UI.Xaml.FrameworkElement? hostContent;
        private int WidgetHostWidth;
        private int currentOffsetX = int.MinValue;
        private int currentOffsetY = 0;
        // Last known Widgets/weather pill bounds in taskbar-client coords, captured during the resting
        // reposition so the synchronous drag path can avoid it without an async UIA read.
        private RECT? lastWidgetsButtonClientRect;
        // Last known taskbar button bounds (app icons + system buttons) in taskbar-client coords. On Win11
        // the app icons are XAML, not classic child windows, so they only come from a UIA scan; cached here
        // so the synchronous drag path avoids them without an async read (issue #17).
        private List<RECT> lastTaskButtonClientRects = new();
        private bool isDragging;
        private bool isPointerTracking;
        private bool isDirectDrag;

        // True while the user is actively repositioning the widget (tray "Move" mode or a direct pointer
        // drag). Background repositions (2s watcher poll, taskbar events) must not fire during this, or the
        // widget snaps back to a computed lane mid-drag (issue #17 case 2).
        // isSettling covers the async snap right after release: without it a watcher poll landing mid-snap
        // would recompute a resting position from the not-yet-saved offset and yank the widget away.
        private bool IsUserRepositioning => isDragging || isPointerTracking || isDirectDrag || isSettling;
        private bool isSettling;
        private int draggingInnerOffsetX;
        // Where the drag currently sits, and the free gap it is tracking the cursor inside.
        private int? dragPreviewX;
        private (int start, int end)? activeDragGap;
        private int lastCursorPositionX;
        private int pressCursorPositionX;
        private bool initialized;
        private bool destroyed;
        private bool isVisible;
        private bool windowClassAcquired;
        private bool disposedValue;
        private bool positionRunnerActive;
        private bool positionUpdatePending;
        private TaskbarChangeReason pendingPositionReason;
        private bool pendingTaskbarCentered;
        private bool pendingTaskbarWidgetsEnabled;

        public IntPtr Handle => hwnd != IntPtr.Zero ? hwnd : throw new InvalidOperationException("Widget not initialized.");
        public bool IsAlive => hwnd != IntPtr.Zero && User32.IsWindow(hwnd);
        public bool IsDpiCurrent
        {
            get
            {
                if (!User32.IsWindow(hwndShell))
                    return false;
                uint currentDpi = User32.GetDpiForWindow(hwndShell);
                return (currentDpi == 0 ? 96u : currentDpi) == taskbarDpi;
            }
        }
        public IntPtr TaskbarHandle => hwndShell;
        public bool IsPrimaryTaskbar => isPrimaryTaskbar;
        /// <summary>
        /// Supplies the best available snapshot for a provider when a slot is (re)assigned to it, so a
        /// re-ordered tile paints its new provider immediately instead of holding the previous one's rows
        /// until the next fetch lands. Set by <see cref="TaskBarManager"/>.
        /// </summary>
        public Func<ProviderId, UsageResult?>? HydrateProvider { get; set; }
        /// <summary>Raised when any tile is clicked; provider-agnostic, it just opens the flyout.</summary>
        public event Action? Clicked;
        public event EventHandler? Destroying;

        public TaskBarWidget(TaskbarWindowTarget target)
        {
            hwndShell = target.Handle;
            isPrimaryTaskbar = target.IsPrimary;
            displayKey = target.DisplayKey;
            hwndTrayNotify = User32.FindWindowEx(hwndShell, IntPtr.Zero, NotificationAreaClassName, null);
            hwndReBar = User32.FindWindowEx(hwndShell, IntPtr.Zero, ReBarWindow32ClassName, null);
            hwndStart = User32.FindWindowEx(hwndShell, IntPtr.Zero, "Start", null);

            if (hwndShell == IntPtr.Zero || !User32.IsWindow(hwndShell)
                || (isPrimaryTaskbar && (hwndTrayNotify == IntPtr.Zero || hwndReBar == IntPtr.Zero)))
                throw new InvalidOperationException("Windows taskbar is not ready.");

            uint detectedDpi = User32.GetDpiForWindow(hwndShell);
            taskbarDpi = detectedDpi == 0 ? 96u : detectedDpi;
            dpiScale = taskbarDpi / 96d;
            WidgetHostWidth = (int)Math.Ceiling(dpiScale * DefaultWidgetHostWidth);
            Log.Debug($"Widget ctor: taskbar=0x{hwndShell.ToInt64():X}, primary={isPrimaryTaskbar}, DPI={taskbarDpi}, Width={WidgetHostWidth}");
            positionPath = target.GetPositionPath();

            taskbarWatcher = new TaskbarStructureWatcher(hwndShell, hwndReBar);
            taskbarWatcher.TaskbarChangedNotificationCompleted += (_, e) =>
            {
                if (initialized)
                    QueuePositionUpdate(e.Reason, e.IsTaskbarCentered, e.IsTaskbarWidgetsEnabled);
            };
        }

        public void Initialize()
        {
            Log.Information("Initializing widget host");
            host = new DesktopWindowXamlSource();
            hwnd = CreateHostWindow(hwndShell);

            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            appWindow = AppWindow.GetFromWindowId(id);
            appWindow.IsShownInSwitchers = false;
            appWindow.Destroying += AppWindow_Destroying;

            if (!User32.GetWindowRect(hwndShell, out var taskbarRect)
                || taskbarRect.right <= taskbarRect.left
                || taskbarRect.bottom <= taskbarRect.top)
            {
                throw new InvalidOperationException("Windows taskbar bounds are not ready.");
            }
            appWindow.ResizeClient(new SizeInt32(WidgetHostWidth, taskbarRect.bottom - taskbarRect.top));

            host.Initialize(id);
            host.SiteBridge.ResizePolicy = Microsoft.UI.Content.ContentSizePolicy.ResizeContentToParentWindow;
            summaryPanel = BuildSummaryPanel();
            hostContent = new Microsoft.UI.Xaml.Controls.Grid
            {
                Children = { summaryPanel },
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent)
            };
            host.Content = hostContent;
            ResizeWidgetHost(WidgetWidthForMode(WidgetSettingsService.Current));

            InjectIntoTaskbar();
            QueuePositionUpdate(TaskbarChangeReason.None);

            initialized = true;
            Log.Information("Widget host initialization done");
        }

        private void InjectIntoTaskbar()
        {
            Log.Information("Injecting widget into taskbar");
            int attempts = 0;
            while (attempts++ <= 3)
            {
                var previousParent = User32.SetParent(hwnd, hwndShell);
                if (previousParent != IntPtr.Zero || IsParentedToTaskbar())
                {
                    Log.Information("Widget injected successfully");
                    return;
                }
            }
            Dispose();
            throw new InvalidOperationException("Could not inject the widget into the taskbar.");
        }

        private bool IsParentedToTaskbar()
            => User32.GetAncestor(hwnd, GetAncestorFlags.GA_PARENT) == hwndShell;

        private void AppWindow_Destroying(AppWindow sender, object args)
        {
            appWindow!.Destroying -= AppWindow_Destroying;
            destroyed = true;
            Destroying?.Invoke(this, EventArgs.Empty);
        }

        public bool MatchesTarget(TaskbarWindowTarget target)
            => target.Handle == hwndShell
                && target.IsPrimary == isPrimaryTaskbar
                && string.Equals(target.DisplayKey, displayKey, StringComparison.Ordinal);

        public void SetVisible(bool visible)
        {
            if (appWindow is null || isVisible == visible)
                return;

            isVisible = visible;
            if (visible)
            {
                QueuePositionUpdate(TaskbarChangeReason.None);
                appWindow.Show(false);
            }
            else
            {
                if (isDragging)
                    EndDragging(revert: true);
                appWindow.Hide();
            }
        }

        public void Destroy() => appWindow?.Destroy();

        public void UpdatePosition(bool resetManualPosition = false)
        {
            if (resetManualPosition)
                SaveCustomPosition(-1);
            QueuePositionUpdate(TaskbarChangeReason.None);
        }

        private Microsoft.UI.Xaml.Controls.StackPanel BuildSummaryPanel()
        {
            var panel = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                // The inter-tile gap comes from each tile's own 4px L/R margin plus the divider, so no
                // StackPanel.Spacing — that keeps the width math to content + margins + separators.
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
            };

            for (int i = 0; i < tiles.Length; i++)
            {
                if (i > 0)
                {
                    separators[i - 1] = CreateSeparator();
                    panel.Children.Add(separators[i - 1]);
                }

                tiles[i] = CreateTile();
                panel.Children.Add(tiles[i]);
            }

            return panel;
        }

        private WidgetSummary CreateTile()
        {
            var summary = new WidgetSummary
            {
                Margin = new Microsoft.UI.Xaml.Thickness(2, 0, 2, 0),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
                Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            };
            summary.DesiredHostWidthChanged += WidgetSummary_DesiredHostWidthChanged;
            summary.PointerPressed += WidgetSummary_PointerPressed;
            summary.PointerMoved += WidgetSummary_PointerMoved;
            summary.PointerReleased += WidgetSummary_PointerReleased;
            summary.PointerCanceled += WidgetSummary_PointerCanceled;
            summary.Clicked += OnTileClicked;
            return summary;
        }

        private static Microsoft.UI.Xaml.Controls.TextBlock CreateSeparator() => new()
        {
            Text = "|",
            Width = TileSeparatorLogicalPx,
            FontSize = 12,
            Opacity = 0.35,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        private void OnTileClicked() => Clicked?.Invoke();

        private void WidgetSummary_DesiredHostWidthChanged(int logicalWidth) => RecomputeLayout();

        /// <summary>
        /// Binds the tile slots to <paramref name="providers"/> in order (leftmost first) and re-lays out.
        /// Providers beyond the slot pool are ignored — <see cref="UsageCoordinator.WidgetDisplayProviders"/>
        /// already caps the list at <see cref="UsageCoordinator.MaxWidgetTiles"/>.
        /// </summary>
        public void SetDisplayProviders(IReadOnlyList<ProviderId> providers, ProviderId? activeProvider)
        {
            if (summaryPanel is null)
                return;

            activeTileProvider = activeProvider;

            bool changed = false;
            for (int i = 0; i < tiles.Length; i++)
            {
                ProviderId? desired = i < providers.Count ? providers[i] : null;
                if (tileProviders[i] == desired)
                    continue;

                tileProviders[i] = desired;
                changed = true;
                // Repaint the slot for its new provider straight away, so a re-order never leaves a tile
                // showing the previous provider's rows under the new provider's turn to be fetched. The
                // cross-fade is suppressed because the movement is carried by the slide animation instead;
                // doing both at once is what made adding a third provider look like a flicker.
                if (desired is { } provider && HydrateProvider?.Invoke(provider) is { } seed)
                {
                    tiles[i].SuppressNextTransition = true;
                    tiles[i].Apply(seed, force: true);
                }
            }

            // Always re-run the layout, even when the set is unchanged: the same providers in the same order
            // can still swap which one is active (nothing focused -> Claude focused), and that alone changes
            // how many rows each tile is allowed.
            RecomputeLayout(forceReposition: changed);
        }

        /// <summary>Routes a fetch result to the slot that owns its provider; no-op if it isn't shown.</summary>
        public void ApplyResult(UsageResult result, bool force = false)
        {
            for (int i = 0; i < tiles.Length; i++)
            {
                if (tileProviders[i] != result.Id)
                    continue;

                tiles[i].Apply(result, force);
                tiles[i].SetActiveToolVisible(true);
                return;
            }
        }

        /// <summary>Re-runs the fit math against the currently measured taskbar gap.</summary>
        public void RefreshLayout() => RecomputeLayout();

        /// <summary>
        /// Fits the tiles into the free taskbar space and resizes the host to the result, degrading only as
        /// far as it has to:
        ///
        /// 1. every tile shows every row the user enabled — the normal case, and what runs whenever there
        ///    is room, so a pinned tile is never trimmed just for being pinned;
        /// 2. tiles other than the active tool's drop to <see cref="PinnedTileMaxRows"/> rows, becoming
        ///    fixed-width summaries;
        /// 3. tiles collapse to their provider glyph, from the right, so the active tile keeps its detail
        ///    longest.
        ///
        /// A pinned tile is never dropped — pinning is a promise that the provider stays on the bar, and
        /// even a collapsed tile keeps its numbers in its tooltip.
        ///
        /// Every tile is measured fresh in both variants on each pass rather than measured once and cached.
        /// Caching was wrong twice over: the cache was keyed by slot, but a provider switch re-orders which
        /// provider a slot holds, and a collapsed tile renders as a glyph so it cannot restate its own
        /// expanded width — together that left a tile stuck collapsed on a width belonging to a different
        /// provider. Measuring is cheap (RenderRows only walks columns) and the tree is not painted until
        /// the pass returns, so the intermediate states are never visible.
        /// </summary>
        private void RecomputeLayout(bool forceReposition = false)
        {
            if (summaryPanel is null)
                return;

            // Re-entrant calls are this pass's own re-renders raising DesiredHostWidthChanged. Everything
            // runs on the UI thread with no awaits, so nothing external can interleave — the pass already
            // has the settled widths and re-running would just repeat itself.
            if (isRecomputingLayout)
            {
                layoutRepositionPending |= forceReposition;
                return;
            }

            isRecomputingLayout = true;
            try
            {
                int iconWidth = WidgetSummary.IconOnlyWidth(WidgetSettingsService.Current);

                Array.Clear(tileSuppressed);
                var slots = new List<int>(tiles.Length);
                for (int i = 0; i < tiles.Length; i++)
                {
                    if (tileProviders[i] is not null)
                        slots.Add(i);
                }

                HoldBackTilesThatDoNotFit(slots);

                // Candidate layouts are measured, never rendered — MeasureDesiredWidth is a pure
                // calculation. Rendering to measure made the tile restart its refresh animation on every
                // usage publish, which read as a tile flashing about once a second.
                var rowCounts = new List<int>(slots.Count);
                int activeIndex = -1;
                for (int n = 0; n < slots.Count; n++)
                {
                    rowCounts.Add(tiles[slots[n]].RowCount);
                    if (IsActiveTile(slots[n]))
                        activeIndex = n;
                }

                var forms = SolveTileLayout(
                    rowCounts,
                    activeIndex,
                    (position, form) => tiles[slots[position]].MeasureDesiredWidth(form),
                    availableLogicalWidth);

                var widths = new List<int>(slots.Count);
                int total = 0;
                for (int n = 0; n < slots.Count; n++)
                {
                    int i = slots[n];
                    tileIconOnly[i] = forms[n].IsGlyph;
                    int width = tiles[i].MeasureDesiredWidth(forms[n]);
                    widths.Add(width);
                    if (tileProviders[i] is { } measuredProvider)
                        TaskbarSpace.RecordTileWidth(measuredProvider, width);
                    total += width + TileHorizontalMarginLogicalPx + (n > 0 ? TileSeparatorLogicalPx : 0);
                    tiles[i].SetSummaryMode(forms[n]);
                }

                for (int i = 0; i < tiles.Length; i++)
                {
                    // A suppressed slot still HAS a provider — it is the courtesy tile that had to give way
                    // — so visibility has to consult the suppression too, or this loop hands it straight
                    // back with no form or width assigned, which is what left a stray divider on the bar.
                    bool shown = tileProviders[i] is not null && !tileSuppressed[i];
                    tileFits[i] = shown;
                    tiles[i].Visibility = shown ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
                    tiles[i].SetActiveToolVisible(shown);
                }

                for (int i = 0; i < separators.Length; i++)
                {
                    separators[i].Visibility = tileFits[i] && tileFits[i + 1]
                        ? Microsoft.UI.Xaml.Visibility.Visible
                        : Microsoft.UI.Xaml.Visibility.Collapsed;
                    separators[i].Foreground = TaskbarForegroundBrush();
                }

                AnimateTileMovement(slots, widths);

                // The layout is recomputed on every usage publish, so only report an actual change.
                var signature = $"budget={availableLogicalWidth} rows=[{string.Join(",", rowCounts)}] "
                    + $"forms=[{string.Join(",", forms)}] widths=[{string.Join(",", widths)}] total={total}";
                if (signature != lastLayoutSignature)
                {
                    lastLayoutSignature = signature;
                    Log.Debug($"[widget] layout {signature}");
                }

                bool resized = ResizeWidgetHost(slots.Count == 0 ? DefaultWidgetHostWidth : total);
                if (resized || forceReposition)
                    UpdatePosition();
            }
            finally
            {
                isRecomputingLayout = false;
            }

            if (!layoutRepositionPending)
                return;

            layoutRepositionPending = false;
            UpdatePosition();
        }

        private bool IsActiveTile(int slot)
            => tileProviders[slot] is { } provider && activeTileProvider == provider;

        /// <summary>
        /// Holds tiles back until the row fits the free taskbar span, so the widget never grows over the
        /// shell's own buttons.
        ///
        /// The ACTIVE tool's tile is the one thing never given up — showing the quota of whatever you are
        /// working in is the widget's original job, and a pinned provider you are not currently touching is
        /// the cheaper thing to lose for a moment. Pinned tiles yield least-recently-used first and come
        /// straight back when you switch away, so a pin still guarantees presence the rest of the time.
        /// </summary>
        private void HoldBackTilesThatDoNotFit(List<int> slots)
        {
            if (slots.Count <= 1)
                return;

            // Least recently used first, and never the active tile.
            var recent = UsageCoordinator.Instance.RecentProviders;
            var recency = new Dictionary<ProviderId, int>();
            for (int i = 0; i < recent.Count; i++)
                recency.TryAdd(recent[i], i);

            var dropOrder = slots
                .Where(slot => !IsActiveTile(slot))
                .OrderByDescending(slot => tileProviders[slot] is { } p && recency.TryGetValue(p, out int index)
                    ? index
                    : int.MaxValue)
                .ToList();

            foreach (int slot in dropOrder)
            {
                if (MeasureRow(slots) <= availableLogicalWidth)
                    return;

                slots.Remove(slot);
                tileSuppressed[slot] = true;
            }
        }

        private int MeasureRow(List<int> slots)
        {
            int total = 0;
            for (int n = 0; n < slots.Count; n++)
            {
                total += tiles[slots[n]].MeasureDesiredWidth(
                        new WidgetSummary.SummaryForm(Math.Max(1, tiles[slots[n]].RowCount), HideReset: false))
                    + TileHorizontalMarginLogicalPx
                    + (n > 0 ? TileSeparatorLogicalPx : 0);
            }

            return total;
        }

        /// <summary>
        /// Animates tiles from wherever their provider sat last time to wherever it sits now, so a set or
        /// order change reads as movement: the tiles already on the bar travel sideways to make room and a
        /// newly shown provider eases in rather than appearing fully formed.
        ///
        /// Positions are tracked per PROVIDER, not per slot, which is what makes this work at all — the
        /// slots are a fixed pool and providers move between them, so slot identity says nothing about what
        /// the user saw move.
        /// </summary>
        private void AnimateTileMovement(List<int> slots, List<int> widths)
        {
            var positions = new Dictionary<ProviderId, int>(slots.Count);
            int x = TileHorizontalMarginLogicalPx / 2;
            for (int n = 0; n < slots.Count; n++)
            {
                if (tileProviders[slots[n]] is { } provider)
                    positions[provider] = x;
                x += widths[n] + TileHorizontalMarginLogicalPx + TileSeparatorLogicalPx;
            }

            // First layout of the session: the tiles have their own reveal animation, nothing to move from.
            bool hadPositions = lastTilePositions.Count > 0;
            for (int n = 0; n < slots.Count; n++)
            {
                if (tileProviders[slots[n]] is not { } provider)
                    continue;

                int target = positions[provider];
                if (lastTilePositions.TryGetValue(provider, out int previous))
                {
                    // Ignore sub-threshold drift from a value changing width; only real moves animate.
                    if (Math.Abs(previous - target) >= TileMoveThresholdLogicalPx)
                        tiles[slots[n]].AnimateSlide(previous - target);
                }
                else if (hadPositions)
                {
                    tiles[slots[n]].AnimateSlide(-TileEntryOffsetLogicalPx);
                }
            }

            lastTilePositions = positions;
        }

        /// <summary>
        /// Total logical width of the tile row when the trailing <paramref name="collapsed"/> tiles render
        /// as glyphs only. Works off cached widths, so a layout that isn't on screen yet can be measured —
        /// which is what lets the collapse count be solved outright instead of by trial and error.
        /// </summary>
        internal static int MeasureRowWidth(IReadOnlyList<int> fullWidths, int collapsed, int iconWidth)
        {
            var icons = new bool[fullWidths.Count];
            for (int i = fullWidths.Count - collapsed; i < fullWidths.Count; i++)
                icons[i] = true;
            return MeasureRowWidth(fullWidths, icons, iconWidth);
        }

        /// <summary>Row width with an explicit set of collapsed tiles, which need not be contiguous.</summary>
        internal static int MeasureRowWidth(IReadOnlyList<int> fullWidths, IReadOnlyList<bool> icons, int iconWidth)
        {
            int total = 0;
            for (int i = 0; i < fullWidths.Count; i++)
            {
                total += (icons[i] ? iconWidth : fullWidths[i])
                    + TileHorizontalMarginLogicalPx
                    + (i > 0 ? TileSeparatorLogicalPx : 0);
            }

            return total;
        }

        /// <summary>
        /// Picks the least-degraded layout that fits <paramref name="budget"/>.
        ///
        /// Every tile renders in full — all of its rows, with their reset countdowns. When the row does not
        /// fit, tiles fall back to a bare glyph rather than being trimmed, and the search picks which ones:
        /// giving up a NARROW tile can be the better trade when it lets a wide neighbour render, which a
        /// positional rule cannot see. At most three tiles is a search space of eight, evaluated with pure
        /// arithmetic.
        ///
        /// The active tool's tile is never the one given up unless nothing fits at all.
        /// </summary>
        /// <param name="rowCounts">Rows each tile wants to show, left to right.</param>
        /// <param name="activeIndex">Position of the active tool's tile, or -1 when none is active.</param>
        /// <param name="measure">
        /// (position, level) =&gt; rendered width, where level is a positive row count,
        /// <see cref="WidgetSummary.LevelCompact"/>, or <see cref="WidgetSummary.LevelGlyph"/>.
        /// </param>
        /// <returns>The chosen detail level per tile.</returns>
        internal static WidgetSummary.SummaryForm[] SolveTileLayout(
            IReadOnlyList<int> rowCounts,
            int activeIndex,
            Func<int, WidgetSummary.SummaryForm, int> measure,
            int budget)
        {
            int count = rowCounts.Count;
            if (count == 0)
                return Array.Empty<WidgetSummary.SummaryForm>();

            // A pinned provider shows everything the user asked it to show, countdowns included — that is
            // the whole point of pinning it (issue #25). There is no fallback form: a tile is never trimmed
            // and never reduced to a bare glyph, because a logo with no figures is worse than no pin at all.
            //
            // Keeping the row inside the taskbar is therefore the PIN BUDGET's job, not this method's —
            // PinBudgetService refuses a pin that would not fit, so by the time a provider gets here it is
            // known to have room.
            var ladders = new List<WidgetSummary.SummaryForm>[count];
            for (int i = 0; i < count; i++)
                ladders[i] = [new WidgetSummary.SummaryForm(Math.Max(1, rowCounts[i]), HideReset: false)];

            // Pass 1 keeps the active tile above the floor; pass 2 drops that guard so a bar with no room
            // at all still renders something rather than nothing.
            for (int pass = 0; pass < 2; pass++)
            {
                var best = SearchLevels(ladders, rowCounts, activeIndex, measure, budget, guardActive: pass == 0);
                if (best is not null)
                    return best;
            }

            // The row overflows the free span. Render it in full regardless: there is deliberately no
            // reduced form to fall back to, and the pin budget should have refused the pin that caused
            // this. Overflowing visibly is the honest outcome — and the signal that the budget is wrong.
            var full = new WidgetSummary.SummaryForm[count];
            for (int i = 0; i < count; i++)
                full[i] = ladders[i][0];
            return full;
        }

        private static WidgetSummary.SummaryForm[]? SearchLevels(
            IReadOnlyList<List<WidgetSummary.SummaryForm>> ladders,
            IReadOnlyList<int> rowCounts,
            int activeIndex,
            Func<int, WidgetSummary.SummaryForm, int> measure,
            int budget,
            bool guardActive)
        {
            int count = ladders.Count;
            int combinations = 1;
            for (int i = 0; i < count; i++)
                combinations *= ladders[i].Count;

            WidgetSummary.SummaryForm[]? bestForms = null;
            (int Detail, int Shown, int Rightness) bestScore = (-1, -1, -1);

            for (int combo = 0; combo < combinations; combo++)
            {
                var forms = new WidgetSummary.SummaryForm[count];
                int rest = combo;
                for (int i = 0; i < count; i++)
                {
                    forms[i] = ladders[i][rest % ladders[i].Count];
                    rest /= ladders[i].Count;
                }

                if (guardActive && activeIndex >= 0
                    && forms[activeIndex].Rows < Math.Min(MinActiveRows, Math.Max(1, rowCounts[activeIndex])))
                {
                    continue;
                }

                int total = 0;
                for (int i = 0; i < count; i++)
                {
                    total += measure(i, forms[i])
                        + TileHorizontalMarginLogicalPx
                        + (i > 0 ? TileSeparatorLogicalPx : 0);
                }

                if (total > budget)
                    continue;

                // Rank by how much is actually readable. A row is worth four points, so a row always beats a
                // countdown and dropping the countdown is the cheapest step down. A countdown on the ACTIVE
                // tile is worth more than one on a pinned tile: when you are working in a tool, when its
                // window resets is the number you care about, whereas a pinned provider you are only
                // monitoring can lose its countdown to the tooltip. Without that weighting every countdown
                // scored the same, ties fell to enumeration order, and the active tile — evaluated first —
                // was always the one stripped.
                int detail = 0, shown = 0, rightness = 0;
                for (int i = 0; i < count; i++)
                {
                    if (forms[i].Rows >= 1)
                    {
                        detail += forms[i].Rows * 4;
                        if (forms[i].HideReset)
                            rightness += i;
                        else
                            detail += i == activeIndex ? ActiveResetWeight : PinnedResetWeight;
                        shown++;
                    }
                    else if (forms[i].IsCompact)
                    {
                        detail += 1;
                        shown++;
                        rightness += i;
                    }
                    else
                    {
                        rightness += i;
                    }
                }

                var score = (detail, shown, rightness);
                if (score.CompareTo(bestScore) <= 0)
                    continue;

                bestScore = score;
                bestForms = forms;
            }

            return bestForms;
        }

        /// <summary>
        /// How many trailing tiles must collapse to their glyph for the row to fit <paramref name="budget"/>.
        /// Collapsing from the right means the active tile (slot 0) keeps its detail longest. Can return the
        /// full count when even a row of glyphs overflows — that's the floor, and a clipped row of glyphs
        /// still beats a pinned provider disappearing.
        /// </summary>
        internal static int SolveCollapseCount(IReadOnlyList<int> fullWidths, int iconWidth, int budget)
        {
            int collapsed = 0;
            while (collapsed < fullWidths.Count && MeasureRowWidth(fullWidths, collapsed, iconWidth) > budget)
                collapsed++;
            return collapsed;
        }

        private static Microsoft.UI.Xaml.Media.Brush TaskbarForegroundBrush()
        {
            bool light = SystemInfos.IsSystemLightThemeUsed() == true;
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(
                light ? Windows.UI.Color.FromArgb(255, 28, 28, 28) : Colors.White);
        }

        // Records the widest free gap (logical px) as the budget for how many tiles may render. Called from
        // the background position pass, so it only writes the field — the UI-thread entry points
        // (SetDisplayProviders / a tile re-render / the manager's health tick) re-run the fit math.
        private void UpdateAvailableWidth(List<(int start, int end)> gaps)
        {
            int widest = 0;
            foreach (var (start, end) in gaps)
                widest = Math.Max(widest, end - start);
            if (widest <= 0)
                return;

            availableLogicalWidth = (int)Math.Floor(widest / dpiScale);
            // Published so the pin budget can refuse a pin that would not fit this taskbar.
            TaskbarSpace.AvailableLogicalWidth = availableLogicalWidth;
        }

        private bool ResizeWidgetHost(int logicalWidth)
        {
            if (appWindow is null)
                return false;

            var width = (int)Math.Ceiling(dpiScale * logicalWidth);
            if (WidgetHostWidth == width)
                return false;

            WidgetHostWidth = width;
            appWindow.ResizeClient(new SizeInt32(WidgetHostWidth, appWindow.Size.Height));
            return true;
        }

        private static int WidgetWidthForMode(WidgetDisplayMode mode) => mode switch
        {
            WidgetDisplayMode.PercentagesOnly => 220,
            WidgetDisplayMode.BarsAndPercentages => 280,
            _ => DefaultWidgetHostWidth,
        };

        private void QueuePositionUpdate(TaskbarChangeReason reason)
            => QueuePositionUpdate(reason, SystemInfos.IsTaskBarCentered(), SystemInfos.IsTaskBarWidgetsEnabled());

        private void QueuePositionUpdate(TaskbarChangeReason reason, bool isCentered, bool isWidgetsEnabled)
        {
            lock (positionRequestLock)
            {
                if (disposedValue)
                    return;

                // Coalesce any number of watcher/layout requests into the latest pending state. Alignment
                // invalidates a saved manual position, so preserve that reason until the pending pass runs.
                if (!positionUpdatePending || reason == TaskbarChangeReason.Alignment)
                    pendingPositionReason = reason;
                pendingTaskbarCentered = isCentered;
                pendingTaskbarWidgetsEnabled = isWidgetsEnabled;
                positionUpdatePending = true;

                if (positionRunnerActive)
                    return;

                positionRunnerActive = true;
            }

            _ = ProcessPositionUpdatesAsync();
        }

        private async Task ProcessPositionUpdatesAsync()
        {
            while (true)
            {
                TaskbarChangeReason reason;
                bool isCentered;
                bool isWidgetsEnabled;
                lock (positionRequestLock)
                {
                    if (disposedValue || !positionUpdatePending)
                    {
                        positionRunnerActive = false;
                        return;
                    }

                    reason = pendingPositionReason;
                    isCentered = pendingTaskbarCentered;
                    isWidgetsEnabled = pendingTaskbarWidgetsEnabled;
                    positionUpdatePending = false;
                }

                await UpdatePositionImpl(reason, isCentered, isWidgetsEnabled);
            }
        }

        private async Task UpdatePositionImpl(TaskbarChangeReason reason, bool isCentered, bool isWidgetsEnabled)
        {
            bool gateAcquired = false;
            var cancellationToken = positionUpdateCancellation.Token;
            try
            {
                await positionUpdateGate.WaitAsync(cancellationToken);
                gateAcquired = true;
                cancellationToken.ThrowIfCancellationRequested();

                if (disposedValue || appWindow is null || IsUserRepositioning)
                    return;
                if (!TryGetLayoutRects(
                        out RECT taskbarScreenRect,
                        out RECT notificationScreenRect,
                        out RECT barScreenRect,
                        out bool hasNotificationArea))
                {
                    return;
                }

                var taskbarRect = ToTaskbarClientRect(taskbarScreenRect, taskbarScreenRect);
                var trayNotifyRect = ToTaskbarClientRect(notificationScreenRect, taskbarScreenRect);
                var barRect = ToTaskbarClientRect(barScreenRect, taskbarScreenRect);

                int offsetX = LoadCustomPosition();
                // Taskbar alignment flipped (e.g. centered -> left): the old manual position now sits on the
                // wrong side, so discard it and re-anchor to the new side's default lane (issue #10).
                if (reason == TaskbarChangeReason.Alignment && offsetX != -1)
                {
                    SaveCustomPosition(-1);
                    offsetX = -1;
                }
                bool useDefault = offsetX == -1;

                // The Widgets/weather pill is a XAML element the child-window scan can't see, so fetch its
                // bounds separately (UIA, with a cached fallback) and treat it as an obstacle like any other.
                RECT? wbRect = isWidgetsEnabled ? await taskbarWatcher.GetWidgetsButtonRectAsync() : null;
                cancellationToken.ThrowIfCancellationRequested();
                RECT? wbClient = wbRect is { } wb && wb.right > wb.left ? ToTaskbarClientRect(wb, taskbarScreenRect) : null;
                lastWidgetsButtonClientRect = wbClient;

                // UIA scan of the taskbar's buttons — the only way to see the Win11 XAML app icons. Cache the
                // client rects so the synchronous drag path can reuse them without an async read.
                var taskButtonRects = await taskbarWatcher.GetTaskbarButtonRectsAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (taskButtonRects is not null)
                {
                    var converted = new List<RECT>(taskButtonRects.Count);
                    foreach (var r in taskButtonRects)
                        converted.Add(ToTaskbarClientRect(r, taskbarScreenRect));
                    lastTaskButtonClientRects = converted;
                }

                // Every taskbar button (app icons + system buttons) is an obstacle, so the widget can never rest
                // on top of the app cluster — same set for resting and dragging (issue #17).
                var obstacles = CollectObstacleClientRects(taskbarScreenRect, wbClient, lastTaskButtonClientRects);

                var (leftBound, rightBound) = ComputeUsableHorizontalBounds(
                    taskbarRect,
                    hasNotificationArea ? trayNotifyRect : null,
                    TrayClearancePx,
                    IsRtlUI);

                // Preferred X: the saved manual position, or the side-appropriate default anchor.
                int preferredX;
                if (!useDefault)
                    preferredX = offsetX;
                else if (!hasNotificationArea)
                    preferredX = IsRtlUI ? leftBound : rightBound - WidgetHostWidth;
                else if (isCentered)
                    preferredX = ComputeFarLeftAnchor(taskbarRect, trayNotifyRect, wbRect, taskbarScreenRect, isCentered, isWidgetsEnabled);
                else
                    preferredX = IsRtlUI ? leftBound : rightBound - WidgetHostWidth;

                // Only ever rest inside a gap that FULLY fits the widget; if none does, don't move it into an
                // overlap — keep the last valid spot (issue #17). First run with no fit hugs the tray.
                var gaps = ComputeFreeGaps(leftBound, rightBound, obstacles);
                // The widget is not one of its own obstacles, so the gap it sits in measures the full space
                // it may occupy. That is the budget the tile-fit math trims against.
                UpdateAvailableWidth(gaps);
                int? placed = PlaceInFittingGap(preferredX, gaps, WidgetHostWidth);
                if (placed is not { } fitX)
                {
                    if (currentOffsetX != int.MinValue)
                        return;
                    offsetX = Math.Max(leftBound, rightBound - WidgetHostWidth);
                }
                else
                {
                    offsetX = fitX;
                }

                offsetX = ClampToTaskbarMonitor(
                    offsetX,
                    WidgetHostWidth,
                    taskbarScreenRect,
                    notificationScreenRect,
                    barScreenRect,
                    hwndShell,
                    hasNotificationArea);

                int offsetY = barRect.top;
                cancellationToken.ThrowIfCancellationRequested();
                var targetAppWindow = appWindow;
                if (disposedValue || targetAppWindow is null || !IsAlive)
                    return;

                if (currentOffsetY != offsetY)
                {
                    targetAppWindow.MoveAndResize(new RectInt32(offsetX, offsetY, WidgetHostWidth, barRect.bottom - barRect.top));
                    currentOffsetX = offsetX; currentOffsetY = offsetY;
                }
                else if (Math.Abs(currentOffsetX - offsetX) >= RepositionDeadbandPx)
                {
                    // Deadband: ignore sub-threshold recompute deltas (rounding / transient tray width changes)
                    // so the widget doesn't visibly twitch on routine taskbar events.
                    targetAppWindow.Move(new PointInt32(offsetX, offsetY));
                    currentOffsetX = offsetX;
                }
            }
            catch (OperationCanceledException)
            {
                // Widget disposal/topology reconciliation cancels pending UIA placement work.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Taskbar widget position update failed for taskbar=0x{hwndShell.ToInt64():X}");
            }
            finally
            {
                if (gateAcquired)
                    positionUpdateGate.Release();
            }
        }

        public void StartDragging()
        {
            if (isDragging || appWindow is null || hostContent is null || summaryPanel is null) return;
            SetVisible(true);
            SetTilesHitTestVisible(false);
            User32.GetWindowRect(hwndShell, out var taskbarRect);
            User32.SetCursorPos(
                taskbarRect.left + appWindow.Position.X + appWindow.Size.Width / 2,
                taskbarRect.top + appWindow.Position.Y + appWindow.Size.Height / 2);
            hostContent.KeyUp += Content_KeyUp;
            hostContent.PointerPressed += Content_PointerPressed;
            hostContent.PointerReleased += Content_PointerReleased;
            isDragging = true;
            PrimeObstacleCacheForDrag();
            if (!host!.HasFocus && hwnd != User32.GetForegroundWindow())
                User32.SetForegroundWindow(hwnd);
        }

        public void EndDragging(bool revert)
        {
            if (!isDragging || appWindow is null || hostContent is null || summaryPanel is null) return;
            isDragging = false;
            hostContent.ReleasePointerCaptures();
            hostContent.KeyUp -= Content_KeyUp;
            hostContent.PointerMoved -= Content_PointerMoved;
            hostContent.PointerPressed -= Content_PointerPressed;
            hostContent.PointerReleased -= Content_PointerReleased;
            SetTilesHitTestVisible(true);
            if (revert)
            {
                dragPreviewX = null;
                activeDragGap = null;
                appWindow.Move(new PointInt32(currentOffsetX, currentOffsetY));
                return;
            }
            _ = SnapToValidPositionAsync(dragPreviewX ?? appWindow.Position.X);
        }

        private void Content_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
                EndDragging(true);
        }

        private void Content_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (appWindow is null || hostContent is null) return;
            e.Handled = true;
            hostContent.PointerMoved += Content_PointerMoved;
            hostContent.CapturePointer(e.Pointer);
            User32.GetCursorPos(out var point);
            lastCursorPositionX = point.x;
            User32.GetWindowRect(hwndShell, out var taskbarRect);
            draggingInnerOffsetX = point.x - taskbarRect.left - appWindow.Position.X;
        }

        private void Content_PointerReleased(object sender, PointerRoutedEventArgs e) => EndDragging(false);

        private void Content_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (appWindow is null || hostContent is null) return;
            User32.GetCursorPos(out var point);
            MoveWidgetWithCursor(point.x);
            hostContent.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }

        private void WidgetSummary_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (appWindow is null || sender is not WidgetSummary summary) return;
            isPointerTracking = true;
            isDirectDrag = false;
            PrimeObstacleCacheForDrag();
            summary.CapturePointer(e.Pointer);
            User32.GetCursorPos(out var point);
            pressCursorPositionX = point.x;
            lastCursorPositionX = point.x;
            User32.GetWindowRect(hwndShell, out var taskbarRect);
            draggingInnerOffsetX = point.x - taskbarRect.left - appWindow.Position.X;
        }

        private void WidgetSummary_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!isPointerTracking || appWindow is null || sender is not WidgetSummary) return;
            User32.GetCursorPos(out var point);
            if (!isDirectDrag)
            {
                if (Math.Abs(point.x - pressCursorPositionX) < Math.Ceiling(4 * dpiScale))
                    return;
                isDirectDrag = true;
                SuppressTileClicks();
                e.Handled = true;
            }

            MoveWidgetWithCursor(point.x);
            SuppressTileClicks();
        }

        private void WidgetSummary_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            (sender as WidgetSummary)?.ReleasePointerCaptures();
            if (isDirectDrag && appWindow is not null)
            {
                _ = SnapToValidPositionAsync(dragPreviewX ?? appWindow.Position.X);
                SuppressTileClicks();
                e.Handled = true;
            }
            isPointerTracking = false;
            isDirectDrag = false;
        }

        private void WidgetSummary_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            isPointerTracking = false;
            isDirectDrag = false;
            (sender as WidgetSummary)?.ReleasePointerCaptures();
        }

        private void SetTilesHitTestVisible(bool visible)
        {
            foreach (var tile in tiles)
            {
                if (tile is not null)
                    tile.IsHitTestVisible = visible;
            }
        }

        // A drag can cross tiles, so every tile swallows the click that ends it — otherwise releasing over
        // a neighbour opens the flyout right after a reposition.
        private void SuppressTileClicks()
        {
            foreach (var tile in tiles)
            {
                if (tile is not null)
                    tile.SuppressNextClick = true;
            }
        }

        /// <summary>
        /// Drags the widget with the cursor inside whichever free gap the CURSOR currently occupies, so it
        /// tracks the pointer smoothly across a whole zone and never overlaps a shell element.
        ///
        /// Selecting the gap by cursor position (rather than by distance to the widget, as before) is what
        /// fixes issue #21: while the cursor crosses the centred icon cluster the widget simply waits,
        /// pinned to the edge of the gap it is in, and picks up the pointer again the moment the cursor
        /// enters the next gap. The old "nearest fitting gap" rule flipped between the left and right zones
        /// mid-drag, which read as the widget jumping between lanes at random or refusing to move.
        /// </summary>
        private void MoveWidgetWithCursor(int cursorX)
        {
            if (appWindow is null) return;
            if (!TryGetLayoutRects(
                    out RECT taskbarRect,
                    out RECT notificationRect,
                    out _,
                    out bool hasNotificationArea))
            {
                return;
            }
            var taskbarClientRect = ToTaskbarClientRect(taskbarRect, taskbarRect);
            var trayNotifyClientRect = ToTaskbarClientRect(notificationRect, taskbarRect);

            var (leftBound, rightBound) = ComputeUsableHorizontalBounds(
                taskbarClientRect,
                hasNotificationArea ? trayNotifyClientRect : null,
                TrayClearancePx,
                IsRtlUI);
            var obstacles = CollectObstacleClientRects(taskbarRect, lastWidgetsButtonClientRect, lastTaskButtonClientRects);
            var gaps = ComputeFreeGaps(leftBound, rightBound, obstacles);

            int cursorClientX = cursorX - taskbarRect.left;
            int desiredX = cursorClientX - draggingInnerOffsetX;

            var gap = SelectDragGap(gaps, cursorClientX, desiredX, WidgetHostWidth, activeDragGap);
            if (gap is not { } zone)
            {
                // No gap can hold the widget at all (very crowded bar): leave it where it is.
                lastCursorPositionX = cursorX;
                return;
            }

            activeDragGap = zone;
            int targetX = Math.Clamp(desiredX, zone.start, zone.end - WidgetHostWidth);

            appWindow.Move(new PointInt32(targetX, currentOffsetY));
            dragPreviewX = targetX;
            ResyncGrabPoint(cursorClientX, targetX, desiredX);
            lastCursorPositionX = cursorX;
        }

        /// <summary>
        /// Re-anchors the grab point whenever the widget is pinned and the cursor has run past it (span end
        /// or icon cluster). Without this the pointer builds up an invisible offset and the drag feels dead
        /// until the hand travels all the way back.
        ///
        /// Awqat-Salaat solves the same problem by clamping the physical cursor with SetCursorPos, but that
        /// traps the user's mouse — it cannot be moved past the taskbar end while a drag is held. Moving the
        /// grab point instead keeps the pointer completely free and still responds the instant the user
        /// reverses direction. The offset stays within the widget so the grab never leaves the control.
        /// </summary>
        private void ResyncGrabPoint(int cursorClientX, int targetX, int desiredX)
        {
            if (desiredX == targetX)
                return;

            draggingInnerOffsetX = Math.Clamp(cursorClientX - targetX, 0, WidgetHostWidth);
        }

        /// <summary>
        /// Chooses the gap the dragged widget lives in for this pointer sample:
        /// the gap under the cursor when it fits the widget; otherwise the gap the drag is already in
        /// (so passing over an icon cluster doesn't teleport the widget); otherwise the nearest fitting gap.
        /// Returns null when no gap can hold the widget.
        /// </summary>
        internal static (int start, int end)? SelectDragGap(
            List<(int start, int end)> gaps, int cursorX, int desiredX, int width, (int start, int end)? current)
        {
            (int start, int end)? underCursor = null;
            (int start, int end)? sticky = null;
            (int start, int end)? nearest = null;
            long nearestDistance = long.MaxValue;

            foreach (var gap in gaps)
            {
                if (gap.end - gap.start < width)
                    continue;

                if (cursorX >= gap.start && cursorX < gap.end)
                    underCursor = gap;

                // The current gap is matched by overlap, not equality: obstacle bounds shift by a pixel or
                // two between samples as the shell relayouts, which would otherwise drop the sticky gap.
                if (current is { } c && gap.start < c.end && gap.end > c.start)
                    sticky = gap;

                long distance = Math.Abs((long)Math.Clamp(desiredX, gap.start, gap.end - width) - desiredX);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = gap;
                }
            }

            return underCursor ?? sticky ?? nearest;
        }

        /// <summary>
        /// Settles the widget after a drag: snaps the dropped position to the nearest gap that fully fits
        /// it, so it rests beside shell elements instead of on top of them. Obstacle bounds are re-read
        /// here (UIA, off the UI thread) rather than during the drag, which keeps the drag itself smooth.
        /// Falls back to the pre-drag position when nothing fits.
        /// </summary>
        private async Task SnapToValidPositionAsync(int droppedX)
        {
            if (appWindow is null) return;

            isSettling = true;
            try
            {
                if (!TryGetLayoutRects(
                        out RECT taskbarScreenRect,
                        out RECT notificationScreenRect,
                        out _,
                        out bool hasNotificationArea))
                {
                    return;
                }
                var taskbarRect = ToTaskbarClientRect(taskbarScreenRect, taskbarScreenRect);
                var trayNotifyRect = ToTaskbarClientRect(notificationScreenRect, taskbarScreenRect);

                await RefreshObstacleCacheAsync(taskbarScreenRect);

                // A newer drag or press may have started while this settle was suspended on the UIA read
                // above, and the widget may have been torn down. Either way this result is stale: letting
                // it run would move the window, clear the new drag's dragPreviewX/activeDragGap, and
                // persist a position the user has already dragged away from.
                if (disposedValue || appWindow is null || !IsAlive || isDragging || isPointerTracking)
                    return;

                var obstacles = CollectObstacleClientRects(taskbarScreenRect, lastWidgetsButtonClientRect, lastTaskButtonClientRects);
                var (leftBound, rightBound) = ComputeUsableHorizontalBounds(
                    taskbarRect,
                    hasNotificationArea ? trayNotifyRect : null,
                    TrayClearancePx,
                    IsRtlUI);
                var gaps = ComputeFreeGaps(leftBound, rightBound, obstacles);

                int settledX = PlaceInFittingGap(droppedX, gaps, WidgetHostWidth)
                    ?? (currentOffsetX != int.MinValue ? currentOffsetX : ClampToSpan(droppedX, leftBound, rightBound, WidgetHostWidth));

                appWindow.Move(new PointInt32(settledX, currentOffsetY));
                currentOffsetX = settledX;
                dragPreviewX = null;
                activeDragGap = null;
                SaveCustomPosition(settledX);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to settle widget after drag");
            }
            finally
            {
                isSettling = false;
            }
        }

        /// <summary>
        /// Re-reads the obstacle bounds only visible through UI Automation (the Win11 XAML app icons and
        /// the Widgets/weather pill) into the caches the synchronous drag path reads. Called when a drag
        /// starts and when it ends, so the gaps the drag tracks reflect the bar as it is right now.
        /// </summary>
        private async Task RefreshObstacleCacheAsync(RECT taskbarScreenRect)
        {
            if (SystemInfos.IsTaskBarWidgetsEnabled()
                && await taskbarWatcher.GetWidgetsButtonRectAsync() is { } wb && wb.right > wb.left)
            {
                lastWidgetsButtonClientRect = ToTaskbarClientRect(wb, taskbarScreenRect);
            }

            if (await taskbarWatcher.GetTaskbarButtonRectsAsync() is { } taskButtonRects)
            {
                var converted = new List<RECT>(taskButtonRects.Count);
                foreach (var r in taskButtonRects)
                    converted.Add(ToTaskbarClientRect(r, taskbarScreenRect));
                lastTaskButtonClientRects = converted;
            }
        }

        private void PrimeObstacleCacheForDrag()
        {
            activeDragGap = null;
            User32.GetWindowRect(hwndShell, out RECT taskbarScreenRect);
            _ = PrimeObstacleCacheAsync(taskbarScreenRect);
        }

        /// <summary>
        /// Fire-and-forget wrapper for the drag-start cache prime. A UIA read can fail while Explorer is
        /// rebuilding; without this the discarded task would surface an unobserved exception instead of a
        /// warning, and the drag simply falls back to the previously cached obstacle bounds.
        /// </summary>
        private async Task PrimeObstacleCacheAsync(RECT taskbarScreenRect)
        {
            try
            {
                await RefreshObstacleCacheAsync(taskbarScreenRect);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to prime the taskbar obstacle cache for a drag");
            }
        }


        /// <summary>Keeps a widget of <paramref name="width"/> inside [leftBound, rightBound].</summary>
        internal static int ClampToSpan(int desiredX, int leftBound, int rightBound, int width)
        {
            int maxX = rightBound - width;
            return maxX <= leftBound ? leftBound : Math.Clamp(desiredX, leftBound, maxX);
        }

        // Collects the taskbar-client rects of everything the widget must not overlap: the Start button and
        // search box (classic child windows), every taskbar button from the UIA scan (the Win11 XAML app
        // icons plus system buttons), the Widgets/weather pill, and any other injected sibling widgets. The
        // ReBarWindow32 container is excluded — it spans the whole item area and would leave no gaps.
        // taskButtonClientRects is the cached UIA set so both the resting and drag paths use identical
        // obstacles (issue #17).
        private List<RECT> CollectObstacleClientRects(RECT taskbarScreenRect, RECT? widgetsPillClient, List<RECT> taskButtonClientRects)
        {
            var result = new List<RECT>();

            if (hwndStart != IntPtr.Zero
                && User32.IsWindowVisible(hwndStart)
                && User32.GetWindowRect(hwndStart, out RECT startRect)
                && startRect.right > startRect.left)
            {
                result.Add(ToTaskbarClientRect(startRect, taskbarScreenRect));
            }

            // Classic taskbars (Win10 / third-party shells) expose Start/search as child windows here; on
            // Win11 these return little and the UIA button set below carries the app icons.
            foreach (var bounds in GetTaskbarItemBandWindows(includeAppButtons: true, excludeContainer: true))
                result.Add(ToTaskbarClientRect(bounds, taskbarScreenRect));

            result.AddRange(taskButtonClientRects);

            if (widgetsPillClient is { } pill && pill.right > pill.left)
                result.Add(pill);

            try
            {
                foreach (var wnd in GetOtherInjectedWindows())
                {
                    if (User32.GetWindowRect(wnd, out var injectedBounds) && injectedBounds.right > injectedBounds.left)
                        result.Add(ToTaskbarClientRect(injectedBounds, taskbarScreenRect));
                }
            }
            catch (Exception ex) { Log.Warning(ex, "overlap scan failed"); }

            return FilterContainerRects(result, taskbarScreenRect.right - taskbarScreenRect.left);
        }

        /// <summary>
        /// Drops obstacle rects that are really containers, not elements. The UIA tree exposes grouping
        /// elements whose bounds span most of the bar; treating one as an obstacle wipes out every free gap,
        /// which is why the widget could get stuck in a narrow band or refuse to move at all (issue #21).
        /// </summary>
        internal static List<RECT> FilterContainerRects(List<RECT> rects, int taskbarWidth)
        {
            if (taskbarWidth <= 0)
                return rects;

            int maxObstacleWidth = taskbarWidth / 2;
            var kept = new List<RECT>(rects.Count);
            foreach (var r in rects)
            {
                if (r.right - r.left <= maxObstacleWidth)
                    kept.Add(r);
            }
            return kept;
        }

        // Merges the obstacle rects (clipped to [leftBound, rightBound]) and returns the free horizontal
        // gaps between them. Each gap is a [start, end) span the widget could occupy.
        internal static List<(int start, int end)> ComputeFreeGaps(int leftBound, int rightBound, List<RECT> obstacles)
        {
            var gaps = new List<(int, int)>();
            if (rightBound <= leftBound)
                return gaps;

            var blocked = new List<(int start, int end)>();
            foreach (var o in obstacles)
            {
                int s = Math.Max(leftBound, o.left);
                int e = Math.Min(rightBound, o.right);
                if (e > s)
                    blocked.Add((s, e));
            }
            blocked.Sort((a, b) => a.start.CompareTo(b.start));

            int cursor = leftBound;
            foreach (var (s, e) in blocked)
            {
                if (s > cursor)
                    gaps.Add((cursor, s));
                cursor = Math.Max(cursor, e);
            }
            if (cursor < rightBound)
                gaps.Add((cursor, rightBound));

            return gaps;
        }

        // Picks the position closest to preferredX that fully fits a widget of the given width inside one of
        // the free gaps. Returns null when no gap is wide enough — the caller then declines to move rather
        // than force an overlap (issue #17).
        internal static int? PlaceInFittingGap(int preferredX, List<(int start, int end)> gaps, int width)
        {
            int? best = null;
            long bestDist = long.MaxValue;
            foreach (var (start, end) in gaps)
            {
                if (end - start < width)
                    continue;
                int candidate = Math.Clamp(preferredX, start, end - width);
                long dist = Math.Abs((long)candidate - preferredX);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }
            return best;
        }

        internal static (int left, int right) ComputeUsableHorizontalBounds(
            RECT taskbarRect,
            RECT? notificationRect,
            int clearance,
            bool isRtl)
        {
            if (notificationRect is not { } tray)
            {
                int left = isRtl ? Math.Min(taskbarRect.right, Math.Max(taskbarRect.left, clearance)) : taskbarRect.left;
                int right = isRtl
                    ? taskbarRect.right
                    : Math.Max(left, taskbarRect.right - clearance);
                return (left, right);
            }

            int leftBound = isRtl ? Math.Max(taskbarRect.left, tray.right) : taskbarRect.left;
            int rightBound = Math.Max(
                leftBound,
                (isRtl ? taskbarRect.right : tray.left) - clearance);
            if (!isRtl)
                rightBound = Math.Min(rightBound, taskbarRect.right);
            return (leftBound, rightBound);
        }

        // The default "far left" X: hugging the left end of the taskbar's item area. On Win11 the Widgets
        // button is pinned far-left, so we sit just right of it; on a left-aligned classic taskbar we sit
        // right of the Start button; otherwise the very left edge. RTL mirrors to the visual start (right).
        private int ComputeFarLeftAnchor(RECT taskbarRect, RECT trayNotifyRect, RECT? wbRect, RECT taskbarScreenRect, bool isCentered, bool isWidgetsEnabled)
        {
            int fallbackPillWidth = (int)Math.Ceiling(WidgetsButtonFallbackLogicalPx * dpiScale);

            if (IsRtlUI)
            {
                if (wbRect is { } wb && wb.right > wb.left)
                    return ToTaskbarClientRect(wb, taskbarScreenRect).left - WidgetHostWidth;
                // Widgets pill sits at the visual start (right) but its bounds are unknown: reserve clearance.
                int rightAnchor = taskbarRect.right - WidgetHostWidth;
                if (isWidgetsEnabled)
                    rightAnchor -= fallbackPillWidth;
                return rightAnchor;
            }

            int anchor = Math.Max(0, taskbarRect.left);
            if (wbRect is { } w && w.right > w.left)
                anchor = Math.Max(anchor, ToTaskbarClientRect(w, taskbarScreenRect).right);
            else if (isWidgetsEnabled)
                // Widgets enabled but its exact bounds are unavailable (UIA not ready): step past a
                // conservative pill width so we don't anchor on top of the weather pill (issue #17).
                anchor = Math.Max(anchor, Math.Max(0, taskbarRect.left) + fallbackPillWidth);
            else if (!isCentered && hwndStart != IntPtr.Zero
                     && User32.GetWindowRect(hwndStart, out RECT startRect) && startRect.right > startRect.left)
                anchor = Math.Max(anchor, ToTaskbarClientRect(startRect, taskbarScreenRect).right);
            return anchor;
        }

        // On multi-monitor setups where one taskbar spans displays (e.g. Open-Shell), the widget can land
        // straddling the seam. Primary widgets follow the notification area's monitor; secondary widgets
        // follow the monitor that owns their taskbar window.
        private static int ClampToTaskbarMonitor(
            int offsetX,
            int widgetHostWidth,
            RECT taskbarRect,
            RECT trayNotifyRect,
            RECT barRect,
            IntPtr hwndTaskbar,
            bool hasNotificationArea)
        {
            IntPtr monitor;
            if (hasNotificationArea)
            {
                var anchor = new POINT { x = trayNotifyRect.left - 1, y = (barRect.top + barRect.bottom) / 2 };
                monitor = User32.MonitorFromPoint(anchor, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            }
            else
            {
                monitor = User32.MonitorFromWindow(hwndTaskbar, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            }

            if (monitor == IntPtr.Zero)
                return offsetX;

            var info = MONITORINFO.Create();
            if (!User32.GetMonitorInfo(monitor, ref info))
                return offsetX;

            var m = info.rcMonitor;
            if (m.right - m.left < widgetHostWidth)
                return offsetX;

            int screenX = taskbarRect.left + offsetX;
            screenX = Math.Clamp(screenX, m.left, m.right - widgetHostWidth);
            return screenX - taskbarRect.left;
        }

        private bool TryGetLayoutRects(
            out RECT taskbarScreenRect,
            out RECT notificationScreenRect,
            out RECT barScreenRect,
            out bool hasNotificationArea)
        {
            notificationScreenRect = default;
            barScreenRect = default;
            hasNotificationArea = false;

            if (!User32.GetWindowRect(hwndShell, out taskbarScreenRect)
                || taskbarScreenRect.right <= taskbarScreenRect.left
                || taskbarScreenRect.bottom <= taskbarScreenRect.top)
            {
                return false;
            }

            if (hwndTrayNotify != IntPtr.Zero
                && User32.IsWindow(hwndTrayNotify)
                && User32.GetWindowRect(hwndTrayNotify, out var trayRect)
                && trayRect.right > trayRect.left
                && trayRect.bottom > trayRect.top)
            {
                notificationScreenRect = GetNotificationAreaScreenRect(taskbarScreenRect, trayRect);
                hasNotificationArea = true;
            }
            else
            {
                int edge = IsRtlUI ? taskbarScreenRect.left : taskbarScreenRect.right;
                notificationScreenRect = new RECT
                {
                    left = edge,
                    top = taskbarScreenRect.top,
                    right = edge,
                    bottom = taskbarScreenRect.bottom,
                };
            }

            if (hwndReBar != IntPtr.Zero
                && User32.IsWindow(hwndReBar)
                && User32.GetWindowRect(hwndReBar, out var rebarRect)
                && rebarRect.right > rebarRect.left
                && rebarRect.bottom > rebarRect.top)
            {
                barScreenRect = rebarRect;
            }
            else
            {
                barScreenRect = taskbarScreenRect;
            }

            return true;
        }

        private static RECT ToTaskbarClientRect(RECT rect, RECT taskbarScreenRect)
            => new()
            {
                left = rect.left - taskbarScreenRect.left,
                top = rect.top - taskbarScreenRect.top,
                right = rect.right - taskbarScreenRect.left,
                bottom = rect.bottom - taskbarScreenRect.top,
            };

        private RECT GetNotificationAreaScreenRect(RECT taskbarScreenRect, RECT trayNotifyScreenRect)
        {
            var result = trayNotifyScreenRect;
            IncludeTaskbarChildBounds("ClockButton", taskbarScreenRect, ref result);
            IncludeTaskbarChildBounds("TrayClockWClass", taskbarScreenRect, ref result);
            IncludeTaskbarChildBounds("TrayShowDesktopButtonWClass", taskbarScreenRect, ref result);
            return result;
        }

        private void IncludeTaskbarChildBounds(string className, RECT taskbarScreenRect, ref RECT result)
        {
            for (var child = User32.FindWindowEx(hwndShell, IntPtr.Zero, className, null);
                 child != IntPtr.Zero;
                 child = User32.FindWindowEx(hwndShell, child, className, null))
            {
                if (!User32.GetWindowRect(child, out var bounds)
                    || bounds.right <= bounds.left
                    || bounds.bottom <= bounds.top
                    || !RectsIntersect(bounds, taskbarScreenRect))
                {
                    continue;
                }

                result = Union(result, bounds);
            }
        }

        private static bool RectsIntersect(RECT a, RECT b)
            => a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;

        private static RECT Union(RECT a, RECT b)
            => new()
            {
                left = Math.Min(a.left, b.left),
                top = Math.Min(a.top, b.top),
                right = Math.Max(a.right, b.right),
                bottom = Math.Max(a.bottom, b.bottom),
            };

        // Carries per-pass options through the EnumChildWindows callback via its GCHandle lParam, so the
        // scan is reentrancy-safe (UpdatePositionImpl runs on background threads while a drag runs on the
        // UI thread) instead of relying on a shared field.
        private sealed class BandEnumContext
        {
            public readonly List<RECT> List = new();
            public bool IncludeAppButtons;
            public bool ExcludeContainer;
        }

        private List<RECT> GetTaskbarItemBandWindows(bool includeAppButtons = true, bool excludeContainer = false)
        {
            var ctx = new BandEnumContext { IncludeAppButtons = includeAppButtons, ExcludeContainer = excludeContainer };
            var gc = GCHandle.Alloc(ctx);
            try { User32.EnumChildWindows(hwndShell, EnumTaskbarItemBandWindow, GCHandle.ToIntPtr(gc)); }
            finally { gc.Free(); }
            return ctx.List;
        }

        private static bool EnumTaskbarItemBandWindow(IntPtr hWnd, IntPtr lParam)
        {
            if (GCHandle.FromIntPtr(lParam).Target is not BandEnumContext ctx)
                return true;

            var builder = new StringBuilder(256);
            User32.GetClassName(hWnd, builder, builder.Capacity);
            string className = builder.ToString();
            // MSTaskSwWClass / MSTaskListWClass are the running-app buttons (volatile width). Skip them when
            // computing the stable resting position so opening/focusing an app never nudges the widget.
            bool isVolatileAppButton = className is "MSTaskSwWClass" or "MSTaskListWClass";
            if (isVolatileAppButton && !ctx.IncludeAppButtons)
                return true;
            // ReBarWindow32 is the container spanning the whole item area — excluded for gap solving so it
            // doesn't swallow every free gap; still included for the legacy forbidden-band callers.
            if (className == "ReBarWindow32" && ctx.ExcludeContainer)
                return true;
            if (className is not ("Start" or "TrayDummySearchControl" or "ReBarWindow32" or "MSTaskSwWClass" or "MSTaskListWClass"))
                return true;

            // A hidden shell element takes up no room on screen, so reserving its bounds costs the widget
            // real width for nothing. Windows keeps the Start button and the legacy task list around as
            // hidden windows with live rects, which was quietly stealing ~45px from the free span.
            if (!User32.IsWindowVisible(hWnd))
                return true;

            if (!User32.GetWindowRect(hWnd, out RECT bounds))
                return true;
            int width = bounds.right - bounds.left;
            int height = bounds.bottom - bounds.top;
            if (width <= 0 || height <= 0)
                return true;

            ctx.List.Add(bounds);
            return true;
        }

        private int LoadCustomPosition()
        {
            try
            {
                if (!File.Exists(positionPath))
                    return -1;
                if (!int.TryParse(File.ReadAllText(positionPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset))
                    return -1;

                // A saved offset of 0 lands under the Start button on LTR taskbars and looks "missing".
                return offset == 0 ? -1 : offset;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read widget position");
                return -1;
            }
        }

        private void SaveCustomPosition(int offset)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(positionPath)!);
                File.WriteAllText(positionPath, offset.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not save widget position");
            }
        }


        private IntPtr CreateHostWindow(IntPtr parent)
        {
            RegisterWindowClass();
            return User32.CreateWindowEx(
                WindowStylesExtended.WS_EX_LAYERED, WidgetClassName, "WidgetHost",
                WindowStyles.WS_POPUP, 0, 0, 0, 0, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        private void RegisterWindowClass()
        {
            lock (WindowClassLock)
            {
                if (!windowClassRegistered)
                {
                    var wndClass = new WNDCLASSEX
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                        hInstance = Kernel32.GetModuleHandle(null),
                        lpfnWndProc = SharedWndProc,
                        lpszClassName = WidgetClassName,
                    };
                    if (User32.RegisterClassEx(ref wndClass) == 0)
                    {
                        // A prior UnregisterClass may have failed while a window still held the class, in
                        // which case it is still registered and usable — that is not a failure to create.
                        int error = Marshal.GetLastWin32Error();
                        if (error != ERROR_CLASS_ALREADY_EXISTS)
                            throw new Win32Exception(error, "Could not register the taskbar widget window class.");
                    }
                    windowClassRegistered = true;
                }

                windowClassUsers++;
                windowClassAcquired = true;
            }
        }

        private static IntPtr SharedWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
            => User32.DefWindowProc(hWnd, uMsg, wParam, lParam);

        private void ReleaseWindowClass()
        {
            lock (WindowClassLock)
            {
                if (!windowClassAcquired)
                    return;

                windowClassAcquired = false;
                windowClassUsers--;
                if (windowClassUsers == 0 && windowClassRegistered)
                {
                    // Clear the flag even when UnregisterClass fails. Leaving it set with zero users makes
                    // the next RegisterWindowClass skip registration and hand out a class that may no longer
                    // exist, so every later widget creation fails until the app restarts. Re-registering an
                    // already-registered class is a recoverable no-op; the reverse is not.
                    if (!User32.UnregisterClass(WidgetClassName, Kernel32.GetModuleHandle(null)))
                        Log.Warning("Could not unregister the taskbar widget window class; will re-register on next use");
                    windowClassRegistered = false;
                }
            }
        }

        private List<IntPtr> GetOtherInjectedWindows()
        {
            var childHandles = new List<IntPtr>();
            var gc = GCHandle.Alloc(childHandles);
            try { User32.EnumChildWindows(hwndShell, EnumWindow, GCHandle.ToIntPtr(gc)); }
            finally { gc.Free(); }
            return childHandles;
        }

        private bool EnumWindow(IntPtr hWnd, IntPtr lParam)
        {
            if (hWnd != hwnd
                && User32.IsWindowVisible(hWnd)
                && User32.GetAncestor(hWnd, GetAncestorFlags.GA_PARENT) == hwndShell)
            {
                var builder = new StringBuilder(256);
                User32.GetClassName(hWnd, builder, builder.Capacity);
                var className = builder.ToString();
                if (!IsSystemWindow(className) && className != "#32770")
                {
                    var list = GCHandle.FromIntPtr(lParam).Target as List<IntPtr>;
                    list?.Add(hWnd);
                }
            }
            return true;

            static bool IsSystemWindow(string c) => c is "Start" or "TrayDummySearchControl" or "ReBarWindow32" or "WorkerW"
                or "TrayNotifyWnd" or "TrayButton" or "DynamicContent2"
                or "Windows.UI.Core.CoreWindow" or "Windows.UI.Composition.DesktopWindowContentBridge";
        }

        public void Dispose()
        {
            if (disposedValue)
                return;

            disposedValue = true;
            initialized = false;
            isVisible = false;
            positionUpdateCancellation.Cancel();
            try { appWindow?.Hide(); } catch { }
            foreach (var tile in tiles)
            {
                if (tile is null)
                    continue;

                tile.PointerPressed -= WidgetSummary_PointerPressed;
                tile.DesiredHostWidthChanged -= WidgetSummary_DesiredHostWidthChanged;
                tile.PointerMoved -= WidgetSummary_PointerMoved;
                tile.PointerReleased -= WidgetSummary_PointerReleased;
                tile.PointerCanceled -= WidgetSummary_PointerCanceled;
                tile.Clicked -= OnTileClicked;
            }
            _ = CompleteDisposeAfterPositionUpdatesAsync();
            GC.SuppressFinalize(this);
        }

        private async Task CompleteDisposeAfterPositionUpdatesAsync()
        {
            var gateWait = positionUpdateGate.WaitAsync();
            if (await Task.WhenAny(gateWait, Task.Delay(PositionDisposeWait)) != gateWait)
            {
                // A cross-process UIA call can stall while Explorer is rebuilding. The canceled update checks
                // its token before touching AppWindow again, so release the hidden window/XAML resources now
                // and defer only the watcher/COM cleanup until that call returns.
                Log.Warning($"Taskbar position update did not stop within {PositionDisposeWait.TotalSeconds:0}s; deferring watcher cleanup");
                DisposeWindowResources();
                ReleaseWindowClass();
                _ = DisposeWatcherAfterPositionUpdateAsync(gateWait);
                return;
            }

            try
            {
                DisposeWindowResources();
                try { taskbarWatcher.Dispose(); }
                catch (Exception ex) { Log.Warning(ex, "Failed to dispose the taskbar watcher"); }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Failed to finish disposing taskbar widget for taskbar=0x{hwndShell.ToInt64():X}");
            }
            finally
            {
                ReleaseWindowClass();
                positionUpdateGate.Release();
                DisposeSynchronizationPrimitives();
            }
        }

        private async Task DisposeWatcherAfterPositionUpdateAsync(Task gateWait)
        {
            try
            {
                await gateWait;
                try { taskbarWatcher.Dispose(); }
                catch (Exception ex) { Log.Warning(ex, "Failed to dispose the deferred taskbar watcher"); }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed while waiting to dispose the deferred taskbar watcher");
            }
            finally
            {
                positionUpdateGate.Release();
                DisposeSynchronizationPrimitives();
            }
        }

        /// <summary>
        /// Releases the cancellation source and gate. Called only from the two terminal dispose paths,
        /// after the final Release, since a stalled UpdatePositionImpl reads positionUpdateCancellation.Token
        /// and would throw ObjectDisposedException if these were freed in Dispose itself. Without this each
        /// widget recreation (DPI change, monitor plug, Explorer restart) leaked both handles.
        /// </summary>
        private void DisposeSynchronizationPrimitives()
        {
            try { positionUpdateCancellation.Dispose(); } catch { }
            try { positionUpdateGate.Dispose(); } catch { }
        }

        private void DisposeWindowResources()
        {
            try
            {
                if (!destroyed)
                    appWindow?.Destroy();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to destroy the taskbar widget window");
            }
            try { host?.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "Failed to dispose the taskbar XAML host"); }
            appWindow = null;
            host = null;
            hostContent = null;
            summaryPanel = null;
            Array.Clear(tiles);
            Array.Clear(separators);
            Array.Clear(tileProviders);
            Array.Clear(tileFits);
            Array.Clear(tileIconOnly);
            Array.Clear(tileSuppressed);
            lastTilePositions = new Dictionary<ProviderId, int>();
        }
    }
}
