using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using TaskbarQuota.ActiveApp;
using TaskbarQuota.Services;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Controls
{
    public enum DisplayMode { Default, Compact }

    public sealed partial class WidgetSummary : UserControl
    {
        private const int MaxRowsPerGroup = 2;
        private const int MinLabelColumnWidth = 0;
        private const int MinResetColumnWidth = 0;
        private const int ValueColumnWidth = 34;
        private const int WidgetFontSize = 11;
        private const int BarHeight = 6;
        private const int SingleRowBarHeight = 8;
        private const int BarWidthBarsOnly = 54;
        private const int BarWidthBarsAndPercentages = 46;
        private const int BarColumnWidthBarsOnly = 54;
        private const int BarColumnWidthBarsAndPercentages = 46;
        private const int IconHostSizeBars = 30;
        private const int IconHostSizePercentagesOnly = 26;
        private const double RowLabelGlyphSize = 12;
        private const double RowLabelGlyphReserve = 18;
        private const double GlyphViewportSize = 100;
        private const double NormalizedGlyphExtent = 88;
        private const double StaleOpacity = 0.55;
        private const int PanelColumnSpacing = 5;
        private const int SlideMilliseconds = 300;
        // Slack the width math reserves on top of the measured columns, so a rounding difference between
        // the analytic total and what the Grid actually arranges can never clip the last column.
        private const int WidthSlack = 2;

        /// <summary>Placeholder shown before any usage has been applied. Shared and never mutated, so a
        /// tile that renders on every usage publish does not allocate a list per pass.</summary>
        private static readonly List<WidgetUsageRow> PlaceholderRows = [new("Usage", 0, "--")];

        public event Action? Clicked;
        public event Action<DisplayMode>? DisplayModeChanged;
        public event Action<int>? DesiredHostWidthChanged;

        public bool SuppressNextClick { get; set; }

        /// <summary>
        /// Skips the cross-fade on the next render. Set when a tile is taking over another provider as part
        /// of a re-order: the movement is being conveyed by <see cref="AnimateSlide"/>, and fading the
        /// content at the same time turns a clean shift into a flicker.
        /// </summary>
        public bool SuppressNextTransition { get; set; }

        /// <summary>
        /// The width (logical px) this tile last asked its host for. The taskbar host sums this across the
        /// tiles it shows to size the multi-provider widget and to decide how many tiles actually fit.
        /// </summary>
        public int DesiredLogicalWidth { get; private set; }

        private readonly List<RenderedRow> _renderedRows = new();
        private List<WidgetUsageRow> _rows = new();
        private bool _forcePercentagesOnly;
        private UsageResult? _lastResult;
        private ProviderId? _lastAppliedProvider;
        private string? _lastRenderSignature;
        private string? _lastSourceSignature;
        private Func<ProviderSource, string>? _tooltipBuilder;
        private bool _hasRevealed;
        private bool _isActiveToolVisible = true;
        // Storyboards and their animations are allocated on first use and re-aimed afterwards; all three
        // run on ordinary usage publishes, so rebuilding them per pass was continuous garbage.
        private Storyboard? _visibilityStoryboard;
        private DoubleAnimation? _visibilityOpacity;
        private DoubleAnimation? _visibilityOffset;
        private Storyboard? _softRefreshStoryboard;
        private DoubleAnimation? _softRefreshAnimation;
        private Storyboard? _slideStoryboard;
        private DoubleAnimation? _slideAnimation;

        /// <summary>
        /// Returns the display name for the constrained taskbar widget.
        /// Providers with long brand names (e.g. "GitHub Copilot") expose a short DisplayName ("Copilot")
        /// so the tray widget stays compact; the app dashboard maps the short name back to the full brand
        /// name via <see cref="PlanDisplayNames.ForPageHeader"/>.
        /// </summary>
        private static string WidgetDisplayName(string fullName)
            => string.IsNullOrEmpty(fullName) ? fullName
            : fullName switch
            {
                "GitHub Copilot" => "Copilot",
                _ => fullName,
            };

        /// <summary>Rows this tile shows. A pinned provider always renders exactly what the user configured
        /// (issue #25); keeping the row inside the bar is <see cref="PinBudgetService"/>'s job, not this
        /// control's, so there is no reduced form to fall back to.</summary>
        public int RowCount => Math.Max(1, _rows.Count);

        /// <summary>
        /// The width this tile WOULD take, without rendering it.
        ///
        /// The host sums this across its tiles to size the widget host window. Rendering to read the width
        /// made the tile visibly flash — every re-render restarts the refresh animation, and the host
        /// re-runs this on every usage publish — so the column widths, which are a pure function of the
        /// rows and the display mode, are computed directly instead.
        /// </summary>
        public int MeasureDesiredWidth()
            => CalculateDesiredWidth(
                CurrentRows(),
                _forcePercentagesOnly ? WidgetDisplayMode.PercentagesOnly : WidgetSettingsService.Current);

        public HorizontalAlignment ElementsAlignment
        {
            get => Panel.HorizontalAlignment;
            set => Panel.HorizontalAlignment = value;
        }

        public WidgetSummary()
        {
            InitializeComponent();
            ApplyTaskbarForeground();
            RenderRows();
            WidgetSettingsService.Changed += OnWidgetSettingsChanged;
            Tapped += (_, _) =>
            {
                if (SuppressNextClick)
                {
                    SuppressNextClick = false;
                    return;
                }
                Clicked?.Invoke();
            };
            Unloaded += (_, _) =>
            {
                WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
            };
        }

        private void ApplyTaskbarForeground()
        {
            bool light = Interop.SystemInfos.IsSystemLightThemeUsed() == true;
            Foreground = new SolidColorBrush(light ? Color.FromArgb(255, 28, 28, 28) : Colors.White);
            var track = new SolidColorBrush(light ? Color.FromArgb(90, 28, 28, 28) : Color.FromArgb(110, 255, 255, 255));

            foreach (var row in _renderedRows)
            {
                row.Track.Background = track;
                row.Value.Foreground = Foreground;
            }
            BadgeGlyph.Fill = Foreground;
        }

        public void Apply(UsageResult result, bool force = false)
        {
            if (!WidgetSettingsService.IsProviderVisible(result.Id))
            {
                SetActiveToolVisible(false);
                return;
            }

            var signature = BuildRenderSignature(result);
            var sourceSignature = BuildSourceSignature(result);
            if (!force && _lastRenderSignature == signature)
            {
                // Focus changes only alter the small source badge (desktop/browser/terminal). Rebuilding
                // every row for that cosmetic change clears the Grid and starts the 180 ms refresh pulse,
                // which makes the whole widget flash whenever focus enters or leaves a supported app.
                // Keep the usage content in place and update only the badge.
                _lastResult = result;
                if (_lastSourceSignature != sourceSignature)
                {
                    _lastSourceSignature = sourceSignature;
                    ApplySourceBadge(result);
                    RefreshTileTooltip(result.Source);
                }
                return;
            }

            var isFirstReveal = !_hasRevealed;
            var providerChanged = _lastAppliedProvider != result.Id;
            _lastAppliedProvider = result.Id;
            _lastRenderSignature = signature;
            _lastSourceSignature = sourceSignature;
            _lastResult = result;
            ApplyTaskbarForeground();
            // Values restored from the previous session render dimmed until a live fetch confirms them,
            // so a boot-time snapshot never reads as current data (issue #21).
            Panel.Opacity = RestingPanelOpacity;

            var widgetName = WidgetDisplayName(result.DisplayName);
            BadgeText.Text = Abbrev(widgetName);

            var glyph = TaskbarQuota.ViewModels.Ui.Glyph(result.Id);
            if (glyph != null)
            {
                SetNormalizedGlyph(BadgeGlyph, glyph, Foreground);
                BadgeGlyphBox.Visibility = Visibility.Visible;
                BadgeText.Visibility = Visibility.Collapsed;
            }
            else
            {
                BadgeGlyphBox.Visibility = Visibility.Collapsed;
                BadgeText.Visibility = Visibility.Visible;
            }

            ApplySourceBadge(result);

            _forcePercentagesOnly = false;
            if (result.IsPending && result.Fetch is null)
            {
                // No fetch has completed yet (first paint after boot). Show a neutral placeholder rather
                // than the failure rendering — a full red bar with "!" reads as invalid data (issue #21).
                _rows = new()
                {
                    new WidgetUsageRow(CompactLabel(result.Provider?.SessionLabel ?? "Usage"), 0, "--", HasBar: false),
                    new WidgetUsageRow(CompactLabel(result.Provider?.WeeklyLabel ?? "Usage"), 0, "--", HasBar: false),
                };
                RenderRows();
                AnimateRender(isFirstReveal, providerSwitch: providerChanged);
                SetTileTooltip(
                    result.Source,
                    source => $"{WidgetTooltipTitle(widgetName, source)}: {result.Error ?? "Loading..."}");
                return;
            }

            if (!result.Ok || result.Fetch is null)
            {
                // Claude needs an interactive OAuth login — say so instead of a red blank bar.
                if (result.Id is ProviderId.Claude && result.ErrorKind == ProviderErrorKind.AuthRequired)
                {
                    _rows = new() { new WidgetUsageRow("Login", 0, "required", HasBar: false) };
                    RenderRows();
                    AnimateRender(isFirstReveal, providerSwitch: providerChanged);
                    SetTileTooltip(
                        result.Source,
                        source => $"{WidgetTooltipTitle(widgetName, source)}: Login required — open the app to connect.");
                    return;
                }

                _rows = new()
                {
                    new WidgetUsageRow(CompactLabel(result.Provider?.SessionLabel ?? "Usage"), 0, "--"),
                    new WidgetUsageRow(CompactLabel(result.Provider?.WeeklyLabel ?? "Usage"), 100, "!"),
                };
                RenderRows();
                AnimateRender(isFirstReveal, providerSwitch: providerChanged);
                SetTileTooltip(
                    result.Source,
                    source => $"{WidgetTooltipTitle(widgetName, source)}: {result.Error ?? "Unavailable"}");
                return;
            }

            var usage = result.Fetch.Usage;
            if (result.Id == ProviderId.OpenCode)
            {
                ApplyZenDisplay(result, usage, providerChanged);
                return;
            }
            if (result.Id == ProviderId.Cline)
            {
                ApplyClineCreditsDisplay(result, usage, providerChanged);
                return;
            }
            if (result.Id == ProviderId.Antigravity)
            {
                ApplyAntigravityDisplay(result, usage, providerChanged);
                return;
            }
            if (result.Id is ProviderId.Copilot or ProviderId.Grok && usage.Cost is { Label: "Credits" } credits)
            {
                ApplyCreditsDisplay(result, usage, credits, providerChanged);
                return;
            }

            _rows = BuildRows(result, usage);
            if (_rows.Count == 0)
            {
                SetActiveToolVisible(false);
                return;
            }
            RenderRows();
            SetBars();
            AnimateRender(isFirstReveal, providerSwitch: providerChanged);

            var tooltipLines = _rows.Select(FormatTooltipLine);
            var plan = FormatPlanLabel(result.Id, widgetName, usage.LoginMethod);
            var costTooltip = WidgetCostTooltipLine(result.Id, usage.Cost);
            var resetCreditsTooltip = WidgetResetCreditsTooltipLine(usage.ResetCredits);
            var staleTooltip = StaleTooltipLine(result);
            var tooltipBody = string.Join("\n", tooltipLines);
            SetTileTooltip(
                result.Source,
                source => string.IsNullOrEmpty(plan)
                    ? $"{WidgetTooltipTitle(widgetName, source)}\n{tooltipBody}{costTooltip}{resetCreditsTooltip}{staleTooltip}"
                    : $"{WidgetTooltipTitle(widgetName, source)} · {plan}\n{tooltipBody}{costTooltip}{resetCreditsTooltip}{staleTooltip}");
        }

        /// <summary>
        /// Badge the provider glyph with its active source (browser, host app, terminal, desktop app).
        /// Synara/T3 Code keep their host marks; other sources use a small generic source mark.
        /// </summary>
        private void ApplySourceBadge(UsageResult result)
        {
            var id = result.Id;
            var host = UsageCoordinator.Instance.ActiveSynaraHost;
            if (host is { } h
                && h.Provider == id
                && UsageCoordinator.Instance.ActiveProvider == id)
            {
                var isT3Code = h.Host == ActiveApp.HostApp.T3Code;
                var glyphPath = isT3Code ? ProviderGlyphs.T3Code : ProviderGlyphs.Synara;
                SetSourceBadge(glyphPath, BuildSynaraTooltip(h, isT3Code));
                return;
            }

            var source = result.Source;
            if (!source.IsKnown || UsageCoordinator.Instance.ActiveProvider != id)
            {
                HostBadgeBox.Visibility = Visibility.Collapsed;
                ToolTipService.SetToolTip(HostBadgeBox, null);
                return;
            }

            var sourceGlyph = source.Kind switch
            {
                ProviderSourceKind.Browser => ProviderGlyphs.Browser,
                ProviderSourceKind.Cli => ProviderGlyphs.Terminal,
                ProviderSourceKind.DesktopApp => ProviderGlyphs.Desktop,
                _ => null,
            };

            if (sourceGlyph is null)
            {
                HostBadgeBox.Visibility = Visibility.Collapsed;
                ToolTipService.SetToolTip(HostBadgeBox, null);
                return;
            }

            SetSourceBadge(sourceGlyph, $"{result.DisplayName} {source.ShortViaText}");
        }

        private void SetSourceBadge(string glyphPath, string tooltip)
        {
            if (ViewModels.Ui.ParseFreshGeometry(glyphPath) is { } hostGlyph)
                SetNormalizedGlyph(HostBadgeGlyph, hostGlyph, Foreground);
            HostBadgeBox.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(HostBadgeBox, tooltip);
        }

        private static string BuildSynaraTooltip(SynaraStateReader.SynaraSelection host, bool isT3Code)
        {
            var hostName = isT3Code ? "T3 Code" : "Synara";
            var tip = host.Model is { Length: > 0 } model ? $"{hostName} · {model}" : hostName;
            if (host.ThreadTitle is { Length: > 0 } title)
                tip += $"\n{title}";
            return tip;
        }

        private static string WidgetTooltipTitle(string widgetName, ProviderSource source)
            => source.IsKnown ? $"{widgetName} {source.ShortViaText}" : widgetName;

        private void SetTileTooltip(
            ProviderSource source,
            Func<ProviderSource, string> builder)
        {
            _tooltipBuilder = builder;
            RefreshTileTooltip(source);
        }

        private void RefreshTileTooltip(ProviderSource source)
        {
            if (_tooltipBuilder is { } builder)
                ToolTipService.SetToolTip(this, BuildTooltipForSource(builder, source));
        }

        internal static string BuildTooltipForSource(
            Func<ProviderSource, string> builder,
            ProviderSource source)
            => builder(source);

        public void SetActiveToolVisible(bool isVisible)
        {
            if (_isActiveToolVisible == isVisible)
                return;

            _isActiveToolVisible = isVisible;
            IsHitTestVisible = isVisible;
            if (isVisible)
            {
                Visibility = Visibility.Visible;
                if (!_hasRevealed)
                {
                    if (_lastResult is { } pending)
                        Apply(pending, force: true);
                    return;
                }

                AnimateVisibility(toOpacity: 1, toOffset: 0, milliseconds: 300);
                return;
            }

            AnimateVisibility(toOpacity: 0, toOffset: 6, milliseconds: 460);
        }

        private static List<WidgetUsageRow> BuildRows(UsageResult result, UsageSnapshot usage)
        {
            if (result.Id == ProviderId.Codex)
            {
                var rows = BuildBaseRows(result, usage);
                // Codex credits (raw balance, or used/limit when the API reports a real cap). Lets org/
                // Business plans surface credits in the widget — the only meter they have. See issue #12.
                if (usage.Cost is { Label: "Credits" } codexCredits && ShouldShowCodexCredits(usage, codexCredits))
                {
                    if (codexCredits.Limit is { } creditLimit && creditLimit > 0)
                    {
                        double creditUsed = Math.Max(0, creditLimit - codexCredits.Amount);
                        rows.Add(new WidgetUsageRow(
                            "Credits",
                            WidgetSettingsService.DisplayPercent(Math.Clamp(creditUsed / creditLimit * 100, 0, 100)),
                            $"{FormatCreditCount(creditUsed)}/{FormatCreditCount(creditLimit)}"));
                    }
                    else
                    {
                        rows.Add(new WidgetUsageRow("Credits", 0, FormatCreditCount(codexCredits.Amount), HasBar: false));
                    }
                }
                if (usage.ResetCredits is { AvailableCount: > 0 } resetCredits && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowResetCredits))
                {
                    string? expiresIn = CodexProvider.FormatResetCountdown(resetCredits.EarliestExpiresAt);
                    rows.Add(new WidgetUsageRow(
                        "Resets",
                        0,
                        resetCredits.AvailableCount.ToString("N0", CultureInfo.InvariantCulture),
                        expiresIn,
                        HasBar: false));
                }

                if (WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowExtra))
                {
                    rows.AddRange(usage.ExtraRateWindows.Select(w => new WidgetUsageRow(
                        CompactLabel(w.Title),
                        WidgetSettingsService.DisplayPercent(w.Window.UsedPercent),
                        WidgetSettingsService.FormatDisplayPercent(w.Window.UsedPercent),
                        w.Window.ResetDescription)));
                }
                return rows;
            }

            if (result.Id is ProviderId.Claude or ProviderId.Zai)
            {
                var rows = BuildBaseRows(result, usage);
                if (WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowExtra))
                {
                    rows.AddRange(usage.ExtraRateWindows.Select(w => new WidgetUsageRow(
                        CompactLabel(w.Title),
                        WidgetSettingsService.DisplayPercent(w.Window.UsedPercent),
                        WidgetSettingsService.FormatDisplayPercent(w.Window.UsedPercent),
                        w.Window.ResetDescription)));
                }
                return rows;
            }

            if (usage.ExtraRateWindows.Count > 0)
            {
                if (WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowExtra))
                {
                    return usage.ExtraRateWindows
                        .Select(w => new WidgetUsageRow(
                            CompactLabel(w.Title),
                            WidgetSettingsService.DisplayPercent(w.Window.UsedPercent),
                            WidgetSettingsService.FormatDisplayPercent(w.Window.UsedPercent),
                            w.Window.ResetDescription))
                        .ToList();
                }
                return new List<WidgetUsageRow>();
            }

            if (result.Id == ProviderId.Cursor)
                return BuildCursorRows(result, usage);

            return BuildBaseRows(result, usage);
        }

        internal static IReadOnlyList<string> BuildRowLabelsForTesting(UsageResult result, UsageSnapshot usage)
            => BuildRows(result, usage).Select(row => row.Label).ToList();

        /// <summary>Rows assumed for a provider whose usage has not been fetched yet.</summary>
        public const int AssumedRowCount = 2;

        /// <summary>
        /// How many rows this provider's tile would render. Used to price a provider against the pin
        /// budget before its tile exists, so the dashboard can tell a "short" provider from a "long" one.
        ///
        /// Mirrors the provider dispatch in <see cref="Apply"/> — the specialised displays build their own
        /// row sets rather than going through <see cref="BuildRows"/>, so both have to be consulted. Keep
        /// the two in step when a provider grows a new display path.
        /// </summary>
        public static int CountRenderedRows(UsageResult result)
        {
            if (!result.Ok || result.Fetch is not { } fetch)
                return AssumedRowCount;

            var usage = fetch.Usage;
            int count = result.Id switch
            {
                ProviderId.OpenCode =>
                    (WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowUsage) ? 1 : 0)
                    + (WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowBalance) ? 1 : 0),
                ProviderId.Cline => 1,
                ProviderId.Antigravity => CountAntigravityRows(usage),
                ProviderId.Copilot or ProviderId.Grok when usage.Cost is { Label: "Credits" } =>
                    CountCreditRows(result.Id, usage),
                _ => BuildRows(result, usage).Count,
            };

            return Math.Max(1, count);
        }

        private static int CountAntigravityRows(UsageSnapshot usage)
            => (WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowPrimary) ? 1 : 0)
            + (usage.ModelSpecific != null && WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowModelSpecific) ? 1 : 0)
            + (usage.Secondary != null && WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowSecondary) ? 1 : 0)
            + (usage.Monthly != null && WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowMonthly) ? 1 : 0);

        private static int CountCreditRows(ProviderId id, UsageSnapshot usage)
            => (WidgetSettingsService.IsRowVisible(id, WidgetSettingsService.RowCredits) ? 1 : 0)
            + (usage.AdditionalUsage is { Enabled: true }
                && WidgetSettingsService.IsRowVisible(id, WidgetSettingsService.RowAdditionalUsage) ? 1 : 0);

        private static bool ShouldShowCodexCredits(UsageSnapshot usage, CostSnapshot credits)
        {
            if (WidgetSettingsService.TryGetRowVisibilityOverride(ProviderId.Codex, WidgetSettingsService.RowCredits, out bool userVisible))
                return userVisible;

            return credits.Amount > 0 || !IsNormalCodexPlan(usage.LoginMethod);
        }

        private static bool IsNormalCodexPlan(string? plan)
        {
            var normalized = (plan ?? string.Empty).Trim().ToLowerInvariant();
            return normalized is "free" or "plus"
                || normalized.StartsWith("pro", StringComparison.Ordinal);
        }

        private static List<WidgetUsageRow> BuildBaseRows(UsageResult result, UsageSnapshot usage)
        {
            var rows = new List<WidgetUsageRow>();
            // Skip the primary bar when the provider reported no session window (Codex org/Business):
            // otherwise the widget shows a bogus "Session 0%". Honor the window's Label override so
            // Claude Enterprise reads "Spend limit" instead of "Session".
            if (usage.HasPrimaryWindow && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowPrimary))
            {
                var primaryLabel = usage.Primary.Label ?? result.Provider?.SessionLabel ?? "Usage";
                // Spend-limit meter (Claude Enterprise): show the money value "$9.27/$100.00" instead of a
                // bare percent so it matches Codex's "used/limit credits". The bar still tracks used %.
                string primaryValue = usage.Primary.ShowCostValue && usage.Cost is { } spend
                    ? FormatSpendValue(spend)
                    : WidgetSettingsService.FormatDisplayPercent(usage.Primary.UsedPercent);
                rows.Add(new WidgetUsageRow(
                    CompactLabel(primaryLabel),
                    WidgetSettingsService.DisplayPercent(usage.Primary.UsedPercent),
                    primaryValue,
                    usage.Primary.ResetDescription));
            }
            if (usage.Secondary != null && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowSecondary))
            {
                var secondaryLabel = result.Provider?.WeeklyLabel ?? "Usage";
                rows.Add(new WidgetUsageRow(
                    CompactLabel(secondaryLabel),
                    WidgetSettingsService.DisplayPercent(usage.Secondary.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Secondary.UsedPercent),
                    usage.Secondary.ResetDescription));
            }
            if (usage.ModelSpecific != null && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowModelSpecific))
            {
                rows.Add(new WidgetUsageRow(
                    CompactLabel(usage.ModelSpecific.Label ?? ModelSpecificLabel(result.Id)),
                    WidgetSettingsService.DisplayPercent(usage.ModelSpecific.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.ModelSpecific.UsedPercent),
                    usage.ModelSpecific.ResetDescription));
            }
            if (usage.Monthly != null && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowMonthly))
            {
                rows.Add(new WidgetUsageRow(
                    "Monthly",
                    WidgetSettingsService.DisplayPercent(usage.Monthly.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Monthly.UsedPercent),
                    usage.Monthly.ResetDescription));
            }

            return rows;
        }

        private static List<WidgetUsageRow> BuildCursorRows(UsageResult result, UsageSnapshot usage)
        {
            var rows = new List<WidgetUsageRow>();

            if (usage.Secondary != null && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowSecondary))
                rows.Add(new WidgetUsageRow(
                    CompactLabel(result.Provider?.WeeklyLabel ?? "Auto + Composer Usage"),
                    WidgetSettingsService.DisplayPercent(usage.Secondary.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Secondary.UsedPercent),
                    usage.Secondary.ResetDescription));

            if (usage.ModelSpecific != null && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowModelSpecific))
                rows.Add(new WidgetUsageRow(
                    CompactLabel(ModelSpecificLabel(result.Id)),
                    WidgetSettingsService.DisplayPercent(usage.ModelSpecific.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.ModelSpecific.UsedPercent),
                    usage.ModelSpecific.ResetDescription));

            if (WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowPrimary))
                rows.Add(new WidgetUsageRow(
                    CompactLabel(result.Provider?.SessionLabel ?? "Total usage"),
                    WidgetSettingsService.DisplayPercent(usage.Primary.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Primary.UsedPercent),
                    usage.Primary.ResetDescription));

            return rows;
        }

        private void ApplyZenDisplay(UsageResult result, UsageSnapshot usage, bool providerChanged = false)
        {
            _forcePercentagesOnly = true;
            var balanceText = usage.Secondary?.ResetDescription;
            var rows = new List<WidgetUsageRow>();
            if (WidgetSettingsService.IsRowVisible(ProviderId.OpenCode, WidgetSettingsService.RowUsage))
            {
                rows.Add(new WidgetUsageRow("Usage", 0, usage.Cost?.Display ?? "--", HasBar: false));
            }
            if (WidgetSettingsService.IsRowVisible(ProviderId.OpenCode, WidgetSettingsService.RowBalance))
            {
                rows.Add(new WidgetUsageRow("Balance", 0, balanceText != null ? "$" + balanceText.Split(' ')[0] : "--", HasBar: false));
            }
            _rows = rows;
            if (_rows.Count == 0)
            {
                SetActiveToolVisible(false);
                return;
            }
            RenderRows();
            AnimateRender(!_hasRevealed, providerSwitch: providerChanged);

            SetTileTooltip(
                result.Source,
                source =>
                    $"{WidgetTooltipTitle(result.DisplayName, source)} · {usage.LoginMethod}\n" +
                    $"Usage: {usage.Cost?.Display ?? "--"}\n" +
                    $"Balance: {(balanceText != null ? "$" + balanceText.Split(' ')[0] : "--")}" +
                    StaleTooltipLine(result));
        }

        /// <summary>
        /// Cline usage-billing: pay-as-you-go credit balance, laid out like the OpenCode Zen block.
        /// The 5h/weekly/monthly percent windows don't apply here (that's ClinePass), so we show the
        /// remaining balance as a single tight row. Label is "Balance" (not "Credits") to avoid the
        /// oversized credit-meter value column that would push the amount to the far edge.
        /// </summary>
        private void ApplyClineCreditsDisplay(UsageResult result, UsageSnapshot usage, bool providerChanged = false)
        {
            _forcePercentagesOnly = true;
            _rows = new List<WidgetUsageRow>
            {
                new WidgetUsageRow("Balance", 0, usage.Cost?.Display ?? "--", HasBar: false),
            };
            RenderRows();
            AnimateRender(!_hasRevealed, providerSwitch: providerChanged);

            SetTileTooltip(
                result.Source,
                source =>
                    $"{WidgetTooltipTitle(result.DisplayName, source)} · {usage.LoginMethod}\n" +
                    $"Credit balance: {usage.Cost?.Display ?? "--"}" +
                    StaleTooltipLine(result));
        }

        private void ApplyCreditsDisplay(UsageResult result, UsageSnapshot usage, CostSnapshot credits, bool providerChanged = false)
        {
            double limit = credits.Limit ?? 0;
            double remaining = credits.Amount;
            double used = Math.Max(0, limit - remaining);
            double usedPercent = limit <= 0 ? 0 : Math.Clamp(used / limit * 100, 0, 100);
            string value = $"{FormatCreditCount(used)}/{FormatCreditCount(limit)}";

            var widgetName = WidgetDisplayName(result.DisplayName);
            var rows = new List<WidgetUsageRow>();
            if (WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowCredits))
            {
                rows.Add(new WidgetUsageRow(
                    "Credits",
                    usedPercent,
                    value,
                    usage.Primary.ResetDescription));
            }

            if (usage.AdditionalUsage is { Enabled: true } additional && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowAdditionalUsage))
            {
                double spendPercent = additional.BudgetUsd is > 0
                    ? Math.Clamp(additional.SpentUsd / additional.BudgetUsd.Value * 100, 0, 100)
                    : 0;
                rows.Add(new WidgetUsageRow(
                    "Add'l usage",
                    spendPercent,
                    additional.SpendText,
                    additional.StatusText,
                    HasBar: false));
            }

            _rows = rows;
            if (_rows.Count == 0)
            {
                SetActiveToolVisible(false);
                return;
            }
            RenderRows();
            SetBars();
            AnimateRender(!_hasRevealed, providerSwitch: providerChanged);

            var plan = FormatPlanLabel(result.Id, widgetName, usage.LoginMethod);
            SetTileTooltip(
                result.Source,
                source =>
                {
                    var tooltip = string.IsNullOrEmpty(plan)
                        ? $"{WidgetTooltipTitle(widgetName, source)}\nCredits: {value} ({FormatCreditCount(remaining)} remaining)"
                        : $"{WidgetTooltipTitle(widgetName, source)} · {plan}\nCredits: {value} ({FormatCreditCount(remaining)} remaining)";
                    if (usage.AdditionalUsage is { Enabled: true } addl)
                        tooltip += $"\nAdditional usage: {addl.StatusText} ({addl.SpendText})";
                    if (usage.Primary.ResetDescription is { } resetDesc)
                        tooltip += $"\nresets in {resetDesc}";
                    return tooltip + StaleTooltipLine(result);
                });
        }

        /// <summary>
        /// Width reserve for a "used/limit" credits value, so the column doesn't twitch as the used side
        /// grows. Sized from the tile's OWN limit — the used side can never be wider than the limit — rather
        /// than from a fixed worst case: reserving room for "10,000/10,000" on a plan whose limit is 300
        /// padded the tile by tens of pixels of permanent dead space, which on a crowded taskbar was enough
        /// to cost the provider its tile entirely.
        /// </summary>
        internal static string CreditValueSample(string value)
        {
            int slash = value.IndexOf('/');
            if (slash <= 0 || slash == value.Length - 1)
                return value;

            var limit = value[(slash + 1)..];
            return new string('0', limit.Length) + "/" + limit;
        }

        private static string FormatCreditCount(double value)
            => value.ToString(value % 1 == 0 ? "N0" : "N1", CultureInfo.InvariantCulture);

        /// <summary>Compact "used / limit" money string for a spend-limit meter, e.g. "$9.27/$100".
        /// Space-free to fit the widget's narrow value column.</summary>
        private static string FormatSpendValue(CostSnapshot cost)
        {
            string Money(double v)
            {
                string n = v.ToString(v % 1 == 0 ? "N0" : "N2", CultureInfo.InvariantCulture);
                return string.Equals(cost.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? $"${n}" : $"{n} {cost.Currency}";
            }
            return cost.Limit is { } limit ? $"{Money(cost.Amount)}/{Money(limit)}" : Money(cost.Amount);
        }

        private static string WidgetCostTooltipLine(ProviderId id, CostSnapshot? cost)
        {
            if (cost is null)
                return string.Empty;

            if (id == ProviderId.Codex && cost.Label == "Credits")
            {
                if (cost.Amount <= 0)
                    return string.Empty;

                return $"\nCredits: {FormatCreditCount(cost.Amount)} remaining";
            }

            return $"\n{cost.Label}: {cost.Display}";
        }

        private static string WidgetResetCreditsTooltipLine(ResetCreditsSnapshot? resetCredits)
        {
            if (resetCredits is null)
                return string.Empty;

            var lines = new List<string>
            {
                $"Reset credits: {resetCredits.AvailableCount.ToString("N0", CultureInfo.InvariantCulture)} available",
            };

            int shown = 0;
            for (int i = 0; i < resetCredits.Credits.Count && shown < 3; i++)
            {
                var credit = resetCredits.Credits[i];
                string granted = FormatLocalDateTime(credit.GrantedAt);
                string expires = FormatLocalDateTime(credit.ExpiresAt);
                lines.Add($"Reset {shown + 1}: granted {granted}, expires {expires}");
                shown++;
            }

            if (resetCredits.Credits.Count > shown)
                lines.Add($"+{resetCredits.Credits.Count - shown} more reset credits");

            return "\n" + string.Join("\n", lines);
        }

        private void ApplyAntigravityDisplay(UsageResult result, UsageSnapshot usage, bool providerChanged = false)
        {
            var rows = new List<WidgetUsageRow>();
            // Icon already conveys the model family (Gemini vs Non-Gemini), so the widget row only needs the window.
            if (WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowPrimary))
            {
                rows.Add(new WidgetUsageRow("Weekly", WidgetSettingsService.DisplayPercent(usage.Primary.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Primary.UsedPercent), usage.Primary.ResetDescription,
                    GlyphData: ProviderGlyphs.Gemini));
            }
            if (usage.ModelSpecific != null && WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowModelSpecific))
            {
                rows.Add(new WidgetUsageRow("5h", WidgetSettingsService.DisplayPercent(usage.ModelSpecific.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.ModelSpecific.UsedPercent), usage.ModelSpecific.ResetDescription,
                    GlyphData: ProviderGlyphs.Gemini));
            }
            if (usage.Secondary != null && WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowSecondary))
            {
                rows.Add(new WidgetUsageRow("Weekly", WidgetSettingsService.DisplayPercent(usage.Secondary.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Secondary.UsedPercent), usage.Secondary.ResetDescription,
                    GlyphData: ProviderGlyphs.GeminiBarred));
            }
            if (usage.Monthly != null && WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowMonthly))
            {
                rows.Add(new WidgetUsageRow("5h", WidgetSettingsService.DisplayPercent(usage.Monthly.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Monthly.UsedPercent), usage.Monthly.ResetDescription,
                    GlyphData: ProviderGlyphs.GeminiBarred));
            }
            _rows = rows;
            if (_rows.Count == 0)
            {
                SetActiveToolVisible(false);
                return;
            }
            RenderRows();
            AnimateRender(!_hasRevealed, providerSwitch: providerChanged);

            var plan = FormatPlanLabel(ProviderId.Antigravity, "Antigravity", usage.LoginMethod);
            var body =
                $"Gemini: {WidgetSettingsService.FormatDisplayPercent(usage.Primary.UsedPercent)}" +
                (usage.Primary.ResetDescription is { } r1 ? $" (resets {r1})" : "") + "\n" +
                $"Non-Gemini: {WidgetSettingsService.FormatDisplayPercent(usage.Secondary?.UsedPercent ?? 0)}" +
                (usage.Secondary?.ResetDescription is { } r2 ? $" (resets {r2})" : "");
            SetTileTooltip(
                result.Source,
                source =>
                {
                    var title = WidgetTooltipTitle("Antigravity", source);
                    // Header and body stay separate because `cond ? a : b + c` binds the concatenation to
                    // the false branch only — inlining these dropped the body whenever plan was empty.
                    var header = string.IsNullOrEmpty(plan) ? $"{title}\n" : $"{title} · {plan}\n";
                    return header + body + StaleTooltipLine(result);
                });
        }

        private void OnWidgetSettingsChanged(object? sender, EventArgs e)
        {
            if (_lastResult is { } result)
                Apply(result, force: true);
            else
                RenderRows();
        }

        /// <summary>
        /// Slides this tile into its new position from <paramref name="fromOffsetX"/> logical px away.
        ///
        /// The tiles occupy fixed slots and providers are re-assigned between them, so a re-order is really
        /// a content swap. Starting each tile at the offset where its provider used to sit and easing that
        /// back to zero turns the swap into what the eye expects: the existing tiles travel sideways and
        /// the newcomer arrives from the edge.
        /// </summary>
        public void AnimateSlide(double fromOffsetX)
        {
            _slideStoryboard?.Stop();

            // The RESTING value is written before starting, and the animation supplies the offset through
            // its From. Storyboard.Stop reverts a property to its local value, so a slide interrupted by the
            // next layout pass — which happens constantly, the layout is recomputed on every usage publish —
            // lands at zero instead of stranding the tile at the offset it started from.
            RootTranslate.X = 0;

            // Storyboard and animation are built once and re-aimed, not rebuilt. These run on every layout
            // pass across every tile, and a fresh Storyboard + DoubleAnimation + CubicEase per pass was
            // steady garbage for the life of the process.
            if (_slideStoryboard is null)
            {
                _slideAnimation = CreateDoubleAnimation(RootTranslate, "X", fromOffsetX, 0, SlideMilliseconds);
                _slideStoryboard = new Storyboard();
                _slideStoryboard.Children.Add(_slideAnimation);
            }

            _slideAnimation!.From = fromOffsetX;
            _slideStoryboard.Begin();
        }

        /// <summary>
        /// Whether a render that skips the cross-fade still has to put the tile on screen outright.
        ///
        /// True exactly when this is the tile's first render and it is meant to be showing: the root ships
        /// at Opacity 0, and the reveal is the only thing that raises it. A suppressed transition that
        /// returned early here left the tile permanently invisible even though it measured, laid out and
        /// reported itself Visible.
        /// </summary>
        internal static bool ShouldRevealWithoutTransition(bool isFirstReveal, bool isActiveToolVisible)
            => isFirstReveal && isActiveToolVisible;

        private void AnimateRender(bool isFirstReveal, bool providerSwitch = false)
        {
            if (SuppressNextTransition)
            {
                SuppressNextTransition = false;
                Panel.Opacity = RestingPanelOpacity;

                // Suppressing the cross-fade must not swallow the first reveal. Root ships at Opacity 0 and
                // only the reveal raises it, so a tile seeded from the boot snapshot (which suppresses the
                // transition) used to stay fully transparent for the life of the process: measured, laid
                // out, Visible, and painting nothing. Show it outright instead — skipping the fade is the
                // whole point of the suppression, showing it is not.
                if (ShouldRevealWithoutTransition(isFirstReveal, _isActiveToolVisible))
                {
                    Root.Opacity = 1;
                    RootTranslate.Y = 0;
                }

                _hasRevealed = true;
                return;
            }

            _hasRevealed = true;

            if (isFirstReveal)
                AnimateFirstReveal();
            else if (providerSwitch)
                AnimateProviderSwitch();
            else
                AnimateSoftRefresh();
        }

        // A provider switch rebuilds every row and usually resizes the host, so a hard content swap
        // reads as the whole widget flashing. Cross-fade the new content in (from fully hidden, not the
        // soft-refresh's partial dim) so the switch feels like a transition rather than a redraw.
        private void AnimateProviderSwitch()
        {
            Panel.Opacity = 0;
            AnimatePanelOpacity(from: 0, to: RestingPanelOpacity, milliseconds: 200);
        }

        private void AnimateFirstReveal()
        {
            Root.Opacity = 0;
            RootTranslate.Y = 4;

            AnimateVisibility(toOpacity: _isActiveToolVisible ? 1 : 0, toOffset: _isActiveToolVisible ? 0 : 4, milliseconds: 260);
        }

        private void AnimateSoftRefresh()
        {
            // Start below the resting value so the refresh still reads as a pulse, but never brighten
            // past it — a stale snapshot must stay dimmed once the animation settles.
            double targetOpacity = RestingPanelOpacity;
            double startOpacity = Math.Min(0.72, targetOpacity);
            Panel.Opacity = startOpacity;

            AnimatePanelOpacity(startOpacity, targetOpacity, 180);
        }

        /// <summary>
        /// Runs the shared Panel.Opacity storyboard. Both callers fire on ordinary usage publishes, so the
        /// storyboard is built once and re-aimed rather than reallocated per refresh.
        /// </summary>
        private void AnimatePanelOpacity(double from, double to, int milliseconds)
        {
            _softRefreshStoryboard?.Stop();
            if (_softRefreshStoryboard is null)
            {
                _softRefreshAnimation = CreateDoubleAnimation(Panel, "Opacity", from, to, milliseconds);
                _softRefreshStoryboard = new Storyboard();
                _softRefreshStoryboard.Children.Add(_softRefreshAnimation);
            }

            _softRefreshAnimation!.From = from;
            _softRefreshAnimation.To = to;
            _softRefreshAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
            _softRefreshStoryboard.Begin();
        }

        /// <summary>
        /// The opacity the panel must settle at for the result currently shown. A DoubleAnimation's final
        /// To value becomes the property's resting value, so every animation that touches Panel.Opacity has
        /// to end here or it animates away the stale-snapshot dimming applied in Apply (#21).
        /// </summary>
        private double RestingPanelOpacity => _lastResult?.IsStale == true ? StaleOpacity : 1.0;

        private void AnimateVisibility(double toOpacity, double toOffset, int milliseconds)
        {
            double fromOpacity = Root.Opacity;
            double fromOffset = RootTranslate.Y;

            _visibilityStoryboard?.Stop();
            // Same rule as AnimateSlide: park the local values at the destination and let the animation
            // supply the start through From, so an interrupted transition can never leave a tile stuck
            // invisible or offset.
            Root.Opacity = toOpacity;
            RootTranslate.Y = toOffset;

            if (_visibilityStoryboard is null)
            {
                _visibilityOpacity = CreateDoubleAnimation(Root, "Opacity", fromOpacity, toOpacity, milliseconds);
                _visibilityOffset = CreateDoubleAnimation(RootTranslate, "Y", fromOffset, toOffset, milliseconds);
                _visibilityStoryboard = new Storyboard();
                _visibilityStoryboard.Children.Add(_visibilityOpacity);
                _visibilityStoryboard.Children.Add(_visibilityOffset);
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
            _visibilityOpacity!.From = fromOpacity;
            _visibilityOpacity.To = toOpacity;
            _visibilityOpacity.Duration = duration;
            _visibilityOffset!.From = fromOffset;
            _visibilityOffset.To = toOffset;
            _visibilityOffset.Duration = duration;
            _visibilityStoryboard.Begin();
        }

        private static DoubleAnimation CreateDoubleAnimation(
            DependencyObject target,
            string property,
            double from,
            double to,
            int milliseconds)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            return animation;
        }

        private void RenderRows()
        {
            var mode = _forcePercentagesOnly ? WidgetDisplayMode.PercentagesOnly : WidgetSettingsService.Current;

            ClearDynamicContent();
            ConfigureStaticColumns(mode);

            var rows = CurrentRows();

            bool showBars = mode is WidgetDisplayMode.BarsOnly or WidgetDisplayMode.BarsAndPercentages;
            bool showPercentages = mode is WidgetDisplayMode.PercentagesOnly or WidgetDisplayMode.BarsAndPercentages;
            double barWidth = mode == WidgetDisplayMode.BarsAndPercentages
                ? BarWidthBarsAndPercentages
                : BarWidthBarsOnly;

            for (int i = 0; i < rows.Count; i++)
            {
                int group = i / MaxRowsPerGroup;
                int row = i % MaxRowsPerGroup;
                int groupStart = group * MaxRowsPerGroup;
                int groupCount = Math.Min(MaxRowsPerGroup, rows.Count - groupStart);
                // Only a tile that holds ONE row overall gets the full-height treatment (the Grok/Copilot
                // credits meter). A trailing lone row in a multi-group tile stays on the top line, level
                // with the first row of the group beside it — centring it there just reads as misaligned.
                bool isSingleRowGroup = rows.Count == 1 && groupCount == 1;
                var layout = CalculateLayoutMetrics(rows, mode, group);
                int firstColumn = EnsureGroupColumns(mode, group, layout);
                AddRow(rows[i], mode, isSingleRowGroup ? 0 : row, firstColumn, showBars, showPercentages, barWidth, isSingleRowGroup);
            }

            ApplyTaskbarForeground();
            SetBars();
            DesiredLogicalWidth = CalculateDesiredWidth(rows, mode);
            DesiredHostWidthChanged?.Invoke(DesiredLogicalWidth);
        }

        private void ClearDynamicContent()
        {
            _renderedRows.Clear();
            for (int i = Panel.Children.Count - 1; i >= 0; i--)
            {
                if (Panel.Children[i] != BadgeHost)
                    Panel.Children.RemoveAt(i);
            }

            while (Panel.ColumnDefinitions.Count > 1)
                Panel.ColumnDefinitions.RemoveAt(1);
        }

        private void ConfigureStaticColumns(WidgetDisplayMode mode)
        {
            Panel.ColumnSpacing = PanelColumnSpacing;
            int iconSize = mode == WidgetDisplayMode.PercentagesOnly ? IconHostSizePercentagesOnly : IconHostSizeBars;
            IconColumn.Width = new GridLength(iconSize);
            BadgeHost.Width = iconSize;
            BadgeHost.Height = iconSize;
            Grid.SetColumn(BadgeHost, 0);
        }

        private int EnsureGroupColumns(WidgetDisplayMode mode, int group, WidgetLayoutMetrics layout)
        {
            int columnsPerGroup = mode switch
            {
                WidgetDisplayMode.PercentagesOnly => 3,
                WidgetDisplayMode.BarsAndPercentages => 4,
                _ => 3,
            };

            while (Panel.ColumnDefinitions.Count < 1 + ((group + 1) * columnsPerGroup))
            {
                switch (mode)
                {
                    case WidgetDisplayMode.PercentagesOnly:
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.LabelWidth) });
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ResetWidth) });
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ValueWidth) });
                        break;
                    case WidgetDisplayMode.BarsAndPercentages:
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.LabelWidth) });
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ResetWidth) });
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarColumnWidthBarsAndPercentages) });
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ValueWidth) });
                        break;
                    default:
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.LabelWidth) });
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ResetWidth) });
                        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarColumnWidthBarsOnly) });
                        break;
                }
            }

            return 1 + (group * columnsPerGroup);
        }

        /// <summary>The rows this tile draws — everything the user enabled, or the placeholder until the
        /// first result lands. Never copies: the measure and render paths both run on every usage publish.
        /// </summary>
        private List<WidgetUsageRow> CurrentRows() => _rows.Count > 0 ? _rows : PlaceholderRows;

        /// <summary>
        /// Total width of the tile for a given row set: the icon column, then per two-row group a label,
        /// reset and bar/value column, plus inter-column spacing and the root padding. Mirrors exactly what
        /// <see cref="ConfigureStaticColumns"/> and <see cref="EnsureGroupColumns"/> build, so a measured
        /// candidate and the rendered result can never disagree.
        ///
        /// Root.Padding is read from the live element rather than mirrored as a constant: the analytic
        /// width and the XAML have to agree exactly or a column is clipped, and a constant only agrees
        /// until someone edits the XAML.
        /// </summary>
        private int CalculateDesiredWidth(IReadOnlyList<WidgetUsageRow> rows, WidgetDisplayMode mode)
        {
            int columnsPerGroup = mode switch
            {
                WidgetDisplayMode.PercentagesOnly => 3,
                WidgetDisplayMode.BarsAndPercentages => 4,
                _ => 3,
            };

            double total = mode == WidgetDisplayMode.PercentagesOnly ? IconHostSizePercentagesOnly : IconHostSizeBars;
            int columnCount = 1;
            int groups = (rows.Count + MaxRowsPerGroup - 1) / MaxRowsPerGroup;

            for (int group = 0; group < groups; group++)
            {
                var layout = CalculateLayoutMetrics(rows, mode, group);
                total += layout.LabelWidth + layout.ResetWidth;
                total += mode switch
                {
                    WidgetDisplayMode.PercentagesOnly => layout.ValueWidth,
                    WidgetDisplayMode.BarsAndPercentages => BarColumnWidthBarsAndPercentages + layout.ValueWidth,
                    _ => BarColumnWidthBarsOnly,
                };
                columnCount += columnsPerGroup;
            }

            double padding = Root.Padding.Left + Root.Padding.Right + WidthSlack;
            return (int)Math.Ceiling(total + (Math.Max(0, columnCount - 1) * PanelColumnSpacing) + padding);
        }

        private static WidgetLayoutMetrics CalculateLayoutMetrics(
            IReadOnlyList<WidgetUsageRow> rows,
            WidgetDisplayMode mode,
            int group)
        {
            int start = group * MaxRowsPerGroup;
            int count = Math.Min(MaxRowsPerGroup, rows.Count - start);
            // Single-row groups (e.g. the Grok/Copilot credits meter) render one point larger, so
            // measure at that size — otherwise the label ("Credits") is sized too narrow and clips.
            bool isSingleRowGroup = rows.Count == 1 && count == 1;
            int labelFont = isSingleRowGroup ? WidgetFontSize + 1 : WidgetFontSize;
            double widestLabel = 0;
            double widestReset = 0;
            for (int i = 0; i < count; i++)
            {
                var row = rows[start + i];
                double iconWidth = row.GlyphData != null ? RowLabelGlyphReserve : 0;
                widestLabel = Math.Max(widestLabel, MeasureTextWidth(BaseLabelText(row, mode), labelFont) + iconWidth);
                if (!string.IsNullOrWhiteSpace(row.ResetDescription))
                    widestReset = Math.Max(widestReset, MeasureTextWidth($"({CompactResetDescription(row.ResetDescription)})", labelFont));
            }

            double widestValue = 0;
            for (int i = 0; i < count; i++)
            {
                var row = rows[start + i];
                widestValue = Math.Max(widestValue, MeasureTextWidth(row.Value, labelFont));
                if (row.Label == "Credits")
                    widestValue = Math.Max(widestValue, MeasureTextWidth(CreditValueSample(row.Value), labelFont));
                // Reserve room for a large dollar balance so amounts like "$1,000.00" aren't clipped.
                if (row.Label == "Balance")
                    widestValue = Math.Max(widestValue, MeasureTextWidth("$1,000.00", labelFont));
            }

            return new WidgetLayoutMetrics(
                Math.Max(MinLabelColumnWidth, widestLabel + 3),
                widestReset == 0 ? MinResetColumnWidth : widestReset + 2,
                Math.Max(ValueColumnWidth, widestValue + 4));
        }

        private void AddRow(
            WidgetUsageRow usageRow,
            WidgetDisplayMode mode,
            int row,
            int firstColumn,
            bool showBars,
            bool showPercentages,
            double barWidth,
            bool isSingleRowGroup)
        {
            int rowSpan = isSingleRowGroup ? MaxRowsPerGroup : 1;
            int textSize = isSingleRowGroup ? WidgetFontSize + 1 : WidgetFontSize;
            bool compactTextOnlyValue = !usageRow.HasBar && mode != WidgetDisplayMode.PercentagesOnly;
            var value = CreateText(
                usageRow.Value,
                0.86,
                compactTextOnlyValue ? TextAlignment.Left : TextAlignment.Center,
                textSize);
            var reset = CreateResetText(usageRow, textSize);

            FrameworkElement label;
            if (usageRow.GlyphData != null)
            {
                var icon = CreateNormalizedGlyph(usageRow.GlyphData, RowLabelGlyphSize, Foreground, new Thickness(0, 0, 4, 0));
                var labelText = CreateLabelText(usageRow, mode, textSize);
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { icon, labelText },
                };
                label = sp;
            }
            else
            {
                label = CreateLabelText(usageRow, mode, textSize);
            }

            var track = new Border { CornerRadius = new CornerRadius(2), Opacity = 0.28 };
            var bar = new Border
            {
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
            };
            var barHost = new Grid
            {
                Width = barWidth,
                Height = isSingleRowGroup ? SingleRowBarHeight : BarHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            barHost.Children.Add(track);
            barHost.Children.Add(bar);

            switch (mode)
            {
                case WidgetDisplayMode.PercentagesOnly:
                    AddToPanel(label, row, firstColumn, rowSpan);
                    AddToPanel(reset, row, firstColumn + 1, rowSpan);
                    AddToPanel(value, row, firstColumn + 2, rowSpan);
                    break;

                case WidgetDisplayMode.BarsAndPercentages:
                    value.Visibility = showPercentages ? Visibility.Visible : Visibility.Collapsed;
                    AddToPanel(label, row, firstColumn, rowSpan);
                    AddToPanel(reset, row, firstColumn + 1, rowSpan);
                    if (usageRow.HasBar)
                    {
                        barHost.Visibility = showBars ? Visibility.Visible : Visibility.Collapsed;
                        AddToPanel(barHost, row, firstColumn + 2, rowSpan);
                        AddToPanel(value, row, firstColumn + 3, rowSpan);
                    }
                    else
                    {
                        Grid.SetColumnSpan(value, 2);
                        AddToPanel(value, row, firstColumn + 2, rowSpan);
                    }
                    break;

                default:
                    AddToPanel(label, row, firstColumn, rowSpan);
                    AddToPanel(reset, row, firstColumn + 1, rowSpan);
                    if (usageRow.HasBar)
                    {
                        barHost.Visibility = showBars ? Visibility.Visible : Visibility.Collapsed;
                        AddToPanel(barHost, row, firstColumn + 2, rowSpan);
                    }
                    else
                    {
                        AddToPanel(value, row, firstColumn + 2, rowSpan);
                    }
                    break;
            }

            _renderedRows.Add(new RenderedRow(usageRow, track, bar, barWidth, label, value));
        }

        private static FrameworkElement CreateNormalizedGlyph(
            string glyphData,
            double size,
            Brush foreground,
            Thickness margin)
        {
            var path = new Path
            {
                Data = ViewModels.Ui.ParseFreshGeometry(glyphData),
                Fill = foreground,
            };
            SetNormalizedGlyphTransform(path);

            var canvas = new Canvas { Width = GlyphViewportSize, Height = GlyphViewportSize };
            canvas.Children.Add(path);

            return new Viewbox
            {
                Width = size,
                Height = size,
                Child = canvas,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = margin,
            };
        }

        private static void SetNormalizedGlyph(Path path, Geometry glyph, Brush foreground)
        {
            path.Data = glyph;
            path.Fill = foreground;
            SetNormalizedGlyphTransform(path);
        }

        private static void SetNormalizedGlyphTransform(Path path)
        {
            var bounds = path.Data?.Bounds ?? Rect.Empty;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.RenderTransform = null;
                return;
            }

            double scale = NormalizedGlyphExtent / Math.Max(bounds.Width, bounds.Height);
            path.RenderTransform = new CompositeTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                TranslateX = (GlyphViewportSize / 2) - ((bounds.X + bounds.Width / 2) * scale),
                TranslateY = (GlyphViewportSize / 2) - ((bounds.Y + bounds.Height / 2) * scale),
            };
        }

        private static TextBlock CreateText(string text, double opacity, TextAlignment alignment, int fontSize = WidgetFontSize) => new()
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = fontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            Opacity = opacity,
            TextAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = alignment switch
            {
                TextAlignment.Center => HorizontalAlignment.Stretch,
                TextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static FrameworkElement CreateLabelText(WidgetUsageRow row, WidgetDisplayMode mode, int fontSize = WidgetFontSize)
        {
            var baseLabel = CreateText(BaseLabelText(row, mode), 0.78, TextAlignment.Left, fontSize);
            baseLabel.TextTrimming = TextTrimming.None;
            return baseLabel;
        }

        private static TextBlock CreateResetText(WidgetUsageRow row, int fontSize = WidgetFontSize)
        {
            if (string.IsNullOrWhiteSpace(row.ResetDescription))
                return CreateText("", 0.9, TextAlignment.Left, fontSize);

            var reset = CreateText($"({CompactResetDescription(row.ResetDescription)})", 0.9, TextAlignment.Left, fontSize);
            reset.Foreground = ResetBrush(row.ResetDescription);
            reset.TextTrimming = TextTrimming.None;
            return reset;
        }

        private void AddToPanel(FrameworkElement element, int row, int column, int rowSpan = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetRowSpan(element, rowSpan);
            Grid.SetColumn(element, column);
            Panel.Children.Add(element);
        }


        private void SetBars()
        {
            foreach (var row in _renderedRows)
                SetBar(row.Bar, row.Source.Percent, row.BarWidth);
        }

        private static void SetBar(FrameworkElement bar, double percent, double maxWidth)
        {
            bar.Width = Math.Clamp(percent, 0, 100) * (maxWidth / 100d);
            string key = WidgetSettingsService.GetUsageBrushResourceKeyForDisplayPercent(percent);
            if (bar is Border border)
            {
                bool emphasized = WidgetSettingsService.CurrentPercentageMode == PercentageDisplayMode.Remaining
                    ? percent <= 25
                    : percent >= 75;
                border.Background = (Brush)Application.Current.Resources[key];
                border.Opacity = emphasized ? 0.95 : 0.78;
            }
        }

        private static string Abbrev(string name)
            => string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();

        /// <summary>Names the age of a snapshot restored from the previous session; empty when live.</summary>
        private static string StaleTooltipLine(UsageResult result)
            => result.IsStale && result.Fetch is { } fetch
                ? $"\nLast updated {fetch.FetchedAt.ToLocalTime():t} — refreshing…"
                : string.Empty;

        private static string BuildRenderSignature(UsageResult result)
        {
            var parts = new List<string>
            {
                result.Id.ToString(),
                result.DisplayName,
                result.Error ?? string.Empty,
                result.IsPending ? "pending" : "settled",
                result.IsStale ? "stale" : "live",
            };

            if (result.Fetch is not { } fetch)
                return string.Join("|", parts);

            var usage = fetch.Usage;
            parts.Add(fetch.SourceLabel);
            parts.Add(usage.LoginMethod ?? string.Empty);
            parts.Add(usage.Email ?? string.Empty);
            parts.Add(usage.Cost?.Display ?? string.Empty);
            if (usage.AdditionalUsage is { Enabled: true } additional)
            {
                parts.Add(additional.SpentUsd.ToString(CultureInfo.InvariantCulture));
                parts.Add(additional.BudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                parts.Add(additional.IsCredits ? "credits" : "usd");
            }
            if (usage.ResetCredits is { } resetCredits)
            {
                parts.Add(resetCredits.AvailableCount.ToString(CultureInfo.InvariantCulture));
                foreach (var credit in resetCredits.Credits)
                {
                    parts.Add(credit.Status);
                    parts.Add(credit.GrantedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                    parts.Add(credit.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
            AppendRateWindow(parts, usage.Primary);
            AppendRateWindow(parts, usage.Secondary);
            AppendRateWindow(parts, usage.ModelSpecific);
            AppendRateWindow(parts, usage.Monthly);
            foreach (var extra in usage.ExtraRateWindows)
            {
                parts.Add(extra.Id);
                parts.Add(extra.Title);
                AppendRateWindow(parts, extra.Window);
            }

            return string.Join("|", parts);
        }

        private static string BuildSourceSignature(UsageResult result)
            => string.Join(
                "|",
                result.Source.Kind,
                result.Source.DisplayName,
                result.Source.IconKey);

        internal static bool HasSameRenderedContentForTesting(UsageResult left, UsageResult right)
            => BuildRenderSignature(left) == BuildRenderSignature(right);

        private static void AppendRateWindow(List<string> parts, RateWindow? window)
        {
            if (window is null)
            {
                parts.Add("null");
                return;
            }

            parts.Add(window.UsedPercent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            parts.Add(window.ResetDescription ?? string.Empty);
        }

        private static string FormatPlanLabel(ProviderId id, string displayName, string? loginMethod)
            => PlanDisplayNames.ForTitle(id, displayName, loginMethod);

        private static string FormatTooltipLine(WidgetUsageRow row)
        {
            if (string.IsNullOrWhiteSpace(row.ResetDescription))
                return $"{row.Label}: {row.Value}";

            if (row.Label == "Resets")
            {
                string expiry = row.ResetDescription == "now"
                    ? "oldest expires now"
                    : $"oldest expires in {row.ResetDescription}";
                return $"{row.Label}: {row.Value} - {expiry}";
            }

            string reset = row.ResetDescription == "now"
                ? "resets now"
                : $"resets in {row.ResetDescription}";
            return $"{row.Label}: {row.Value} - {reset}";
        }

        private static string BaseLabelText(WidgetUsageRow row, WidgetDisplayMode mode)
            => mode == WidgetDisplayMode.PercentagesOnly ? row.Label + ":" : row.Label;

        private static double MeasureTextWidth(string text, int fontSize = WidgetFontSize)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = fontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            };
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(textBlock.DesiredSize.Width);
        }

        private static string CompactResetDescription(string resetDescription)
            => resetDescription == "now" ? "now" : resetDescription.Replace(" ", "", StringComparison.Ordinal);

        private static string FormatLocalDateTime(DateTimeOffset? timestamp)
        {
            if (timestamp is not DateTimeOffset value)
                return "unknown";

            var local = value.ToLocalTime();
            return $"{local:MMM d h:mm tt}";
        }

        private static Brush ResetBrush(string resetDescription)
        {
            string key = TryParseResetMinutes(resetDescription) switch
            {
                <= 30 => "AccentFillColorDefaultBrush",
                <= 120 => "AccentFillColorSecondaryBrush",
                _ => "TextFillColorSecondaryBrush",
            };
            return (Brush)Application.Current.Resources[key];
        }

        private static int? TryParseResetMinutes(string resetDescription)
        {
            if (resetDescription == "now")
                return 0;

            int total = 0;
            foreach (var part in resetDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length < 2 || !int.TryParse(part[..^1], out int value))
                    return null;

                total += part[^1] switch
                {
                    'd' => value * 24 * 60,
                    'h' => value * 60,
                    'm' => value,
                    _ => 0,
                };
            }

            return total;
        }

        private static string ModelSpecificLabel(ProviderId id) => id switch
        {
            ProviderId.Cursor => "API Usage",
            ProviderId.Copilot => "Completions",
            _ => "Model",
        };

        private static string CompactLabel(string label)
        {
            label = label.Trim();
            return label switch
            {
                "Total usage" => "Total",
                "Auto + Composer Usage" => "Auto+Composer",
                "API Usage" => "API",
                "Session" => "Session",
                "Spark Session" => "Spark Session",
                _ when label.Contains("claude", StringComparison.OrdinalIgnoreCase) => "Claude",
                _ when label.Contains("gemini", StringComparison.OrdinalIgnoreCase) && label.Contains("flash", StringComparison.OrdinalIgnoreCase) => "Gemini Flash",
                _ when label.Contains("gemini", StringComparison.OrdinalIgnoreCase) && label.Contains("pro", StringComparison.OrdinalIgnoreCase) => "Gemini Pro",
                _ when label.Contains("github copilot", StringComparison.OrdinalIgnoreCase) => "Copilot",
                _ => label.Length > 12 ? label[..12] : label,
            };
        }

        public void RaiseDisplayMode(DisplayMode mode) => DisplayModeChanged?.Invoke(mode);

        private sealed record WidgetUsageRow(
            string Label,
            double Percent,
            string Value,
            string? ResetDescription = null,
            bool HasBar = true,
            string? GlyphData = null);

        private sealed record RenderedRow(
            WidgetUsageRow Source,
            Border Track,
            Border Bar,
            double BarWidth,
            FrameworkElement? Label,
            TextBlock Value);

        private sealed record WidgetLayoutMetrics(double LabelWidth, double ResetWidth, double ValueWidth);
    }
}
