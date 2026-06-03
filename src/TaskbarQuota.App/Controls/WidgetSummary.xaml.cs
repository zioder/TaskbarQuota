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
using TaskbarQuota.Usage;

namespace TaskbarQuota.Controls
{
    public enum DisplayMode { Default, Compact }

    public sealed partial class WidgetSummary : UserControl
    {
        private const int MaxRowsPerGroup = 2;
        private const int MinLabelColumnWidth = 0;
        private const int MinResetColumnWidth = 0;
        private const int ValueColumnWidth = 34;
        private const string MaxCreditValueSample = "10,000/10,000";
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

        public event Action? Clicked;
        public event Action<DisplayMode>? DisplayModeChanged;
        public event Action<int>? DesiredHostWidthChanged;

        public bool SuppressNextClick { get; set; }

        private readonly List<RenderedRow> _renderedRows = new();
        private List<WidgetUsageRow> _rows = new();
        private bool _forcePercentagesOnly;
        private UsageResult? _lastResult;
        private string? _lastRenderSignature;
        private bool _hasRevealed;
        private bool _isActiveToolVisible = true;
        private Storyboard? _visibilityStoryboard;
        private Storyboard? _softRefreshStoryboard;

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
            Unloaded += (_, _) => WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
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
            if (!force && _lastRenderSignature == signature)
                return;

            var isFirstReveal = !_hasRevealed;
            _lastRenderSignature = signature;
            _lastResult = result;
            ApplyTaskbarForeground();

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

            _forcePercentagesOnly = false;
            if (!result.Ok || result.Fetch is null)
            {
                _rows = new()
                {
                    new WidgetUsageRow(CompactLabel(result.Provider?.SessionLabel ?? "Usage"), 0, "--"),
                    new WidgetUsageRow(CompactLabel(result.Provider?.WeeklyLabel ?? "Usage"), 100, "!"),
                };
                RenderRows();
                AnimateRender(isFirstReveal);
                ToolTipService.SetToolTip(this, $"{widgetName}: {result.Error ?? "Unavailable"}");
                return;
            }

            var usage = result.Fetch.Usage;
            if (result.Id == ProviderId.OpenCode)
            {
                ApplyZenDisplay(usage);
                return;
            }
            if (result.Id == ProviderId.Antigravity)
            {
                ApplyAntigravityDisplay(usage);
                return;
            }
            if (result.Id == ProviderId.Copilot && usage.Cost is { Label: "Credits" } credits)
            {
                ApplyCopilotCreditsDisplay(result, usage, credits);
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
            AnimateRender(isFirstReveal);

            var tooltipLines = _rows.Select(FormatTooltipLine);
            var plan = FormatPlanLabel(result.Id, widgetName, usage.LoginMethod);
            var costTooltip = WidgetCostTooltipLine(result.Id, usage.Cost);
            ToolTipService.SetToolTip(this,
                string.IsNullOrEmpty(plan)
                    ? $"{widgetName}\n{string.Join("\n", tooltipLines)}{costTooltip}"
                    : $"{widgetName} · {plan}\n{string.Join("\n", tooltipLines)}{costTooltip}");
        }

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

        private static List<WidgetUsageRow> BuildBaseRows(UsageResult result, UsageSnapshot usage)
        {
            var rows = new List<WidgetUsageRow>();
            if (WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowPrimary))
            {
                rows.Add(new WidgetUsageRow(
                    CompactLabel(result.Provider?.SessionLabel ?? "Usage"),
                    WidgetSettingsService.DisplayPercent(usage.Primary.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Primary.UsedPercent),
                    usage.Primary.ResetDescription));
            }
            if (usage.Secondary != null && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowSecondary))
            {
                rows.Add(new WidgetUsageRow(
                    CompactLabel(result.Provider?.WeeklyLabel ?? "Usage"),
                    WidgetSettingsService.DisplayPercent(usage.Secondary.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Secondary.UsedPercent),
                    usage.Secondary.ResetDescription));
            }
            if (usage.ModelSpecific != null && WidgetSettingsService.IsRowVisible(result.Id, WidgetSettingsService.RowModelSpecific))
            {
                rows.Add(new WidgetUsageRow(
                    CompactLabel(ModelSpecificLabel(result.Id)),
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

        private void ApplyZenDisplay(UsageSnapshot usage)
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
            AnimateRender(!_hasRevealed);

            ToolTipService.SetToolTip(this,
                $"{usage.LoginMethod}\n" +
                $"Usage: {usage.Cost?.Display ?? "--"}\n" +
                $"Balance: {(balanceText != null ? "$" + balanceText.Split(' ')[0] : "--")}");
        }

        private void ApplyCopilotCreditsDisplay(UsageResult result, UsageSnapshot usage, CostSnapshot credits)
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
            AnimateRender(!_hasRevealed);

            var plan = FormatPlanLabel(result.Id, widgetName, usage.LoginMethod);
            var tooltip = string.IsNullOrEmpty(plan)
                ? $"{widgetName}\nCredits: {value} ({FormatCreditCount(remaining)} remaining)"
                : $"{widgetName} · {plan}\nCredits: {value} ({FormatCreditCount(remaining)} remaining)";
            if (usage.AdditionalUsage is { Enabled: true } addl)
                tooltip += $"\nAdditional usage: {addl.StatusText} ({addl.SpendText})";
            if (usage.Primary.ResetDescription is { } resetDesc)
                tooltip += $"\nresets in {resetDesc}";
            ToolTipService.SetToolTip(this, tooltip);
        }

        private static string FormatCreditCount(double value)
            => value.ToString(value % 1 == 0 ? "N0" : "N1", CultureInfo.InvariantCulture);

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

        private void ApplyAntigravityDisplay(UsageSnapshot usage)
        {
            var rows = new List<WidgetUsageRow>();
            if (WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowPrimary))
            {
                rows.Add(new WidgetUsageRow("Gemini", WidgetSettingsService.DisplayPercent(usage.Primary.UsedPercent),
                    WidgetSettingsService.FormatDisplayPercent(usage.Primary.UsedPercent), usage.Primary.ResetDescription,
                    GlyphData: ProviderGlyphs.Gemini));
            }
            if (WidgetSettingsService.IsRowVisible(ProviderId.Antigravity, WidgetSettingsService.RowSecondary))
            {
                rows.Add(new WidgetUsageRow("Non-Gemini", WidgetSettingsService.DisplayPercent(usage.Secondary?.UsedPercent ?? 0),
                    WidgetSettingsService.FormatDisplayPercent(usage.Secondary?.UsedPercent ?? 0), usage.Secondary?.ResetDescription,
                    GlyphData: ProviderGlyphs.GeminiBarred));
            }
            _rows = rows;
            if (_rows.Count == 0)
            {
                SetActiveToolVisible(false);
                return;
            }
            RenderRows();
            AnimateRender(!_hasRevealed);

            var plan = FormatPlanLabel(ProviderId.Antigravity, "Antigravity", usage.LoginMethod);
            ToolTipService.SetToolTip(this,
                string.IsNullOrEmpty(plan) ? "Antigravity\n" : $"Antigravity · {plan}\n" +
                $"Gemini: {WidgetSettingsService.FormatDisplayPercent(usage.Primary.UsedPercent)}" +
                (usage.Primary.ResetDescription is { } r1 ? $" (resets {r1})" : "") + "\n" +
                $"Non-Gemini: {WidgetSettingsService.FormatDisplayPercent(usage.Secondary?.UsedPercent ?? 0)}" +
                (usage.Secondary?.ResetDescription is { } r2 ? $" (resets {r2})" : ""));
        }

        private void OnWidgetSettingsChanged(object? sender, EventArgs e)
        {
            if (_lastResult is { } result)
                Apply(result, force: true);
            else
                RenderRows();
        }

        private void AnimateRender(bool isFirstReveal)
        {
            _hasRevealed = true;
            if (isFirstReveal)
                AnimateFirstReveal();
            else
                AnimateSoftRefresh();
        }

        private void AnimateFirstReveal()
        {
            Root.Opacity = 0;
            RootTranslate.Y = 4;

            AnimateVisibility(toOpacity: _isActiveToolVisible ? 1 : 0, toOffset: _isActiveToolVisible ? 0 : 4, milliseconds: 260);
        }

        private void AnimateSoftRefresh()
        {
            Panel.Opacity = 0.72;

            _softRefreshStoryboard?.Stop();
            _softRefreshStoryboard = new Storyboard();
            _softRefreshStoryboard.Children.Add(CreateDoubleAnimation(Panel, "Opacity", 0.72, 1, 180));
            _softRefreshStoryboard.Begin();
        }

        private void AnimateVisibility(double toOpacity, double toOffset, int milliseconds)
        {
            _visibilityStoryboard?.Stop();
            _visibilityStoryboard = new Storyboard();
            _visibilityStoryboard.Children.Add(CreateDoubleAnimation(Root, "Opacity", Root.Opacity, toOpacity, milliseconds));
            _visibilityStoryboard.Children.Add(CreateDoubleAnimation(RootTranslate, "Y", RootTranslate.Y, toOffset, milliseconds));
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
            var rows = _rows.Count > 0 ? _rows : new List<WidgetUsageRow> { new("Usage", 0, "--") };

            ClearDynamicContent();
            ConfigureStaticColumns(mode);

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
                bool isSingleRowGroup = rows.Count == 1 && groupCount == 1;
                var layout = CalculateLayoutMetrics(rows, mode, group);
                int firstColumn = EnsureGroupColumns(mode, group, layout);
                AddRow(rows[i], mode, isSingleRowGroup ? 0 : row, firstColumn, showBars, showPercentages, barWidth, isSingleRowGroup);
            }

            ApplyTaskbarForeground();
            SetBars();
            DesiredHostWidthChanged?.Invoke(CalculateDesiredWidth());
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
            Panel.ColumnSpacing = 5;
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

        private static WidgetLayoutMetrics CalculateLayoutMetrics(
            IReadOnlyList<WidgetUsageRow> rows,
            WidgetDisplayMode mode,
            int group)
        {
            int start = group * MaxRowsPerGroup;
            int count = Math.Min(MaxRowsPerGroup, rows.Count - start);
            double widestLabel = 0;
            double widestReset = 0;
            for (int i = 0; i < count; i++)
            {
                var row = rows[start + i];
                double iconWidth = row.GlyphData != null ? RowLabelGlyphReserve : 0;
                widestLabel = Math.Max(widestLabel, MeasureTextWidth(BaseLabelText(row, mode)) + iconWidth);
                if (!string.IsNullOrWhiteSpace(row.ResetDescription))
                    widestReset = Math.Max(widestReset, MeasureTextWidth($"({CompactResetDescription(row.ResetDescription)})"));
            }

            double widestValue = 0;
            for (int i = 0; i < count; i++)
            {
                var row = rows[start + i];
                widestValue = Math.Max(widestValue, MeasureTextWidth(row.Value));
                if (row.Label == "Credits")
                    widestValue = Math.Max(widestValue, MeasureTextWidth(MaxCreditValueSample));
            }

            return new WidgetLayoutMetrics(
                Math.Max(MinLabelColumnWidth, widestLabel + 1),
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
            var value = CreateText(usageRow.Value, 0.86, TextAlignment.Center, textSize);
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
                    barHost.Visibility = showBars && usageRow.HasBar ? Visibility.Visible : Visibility.Collapsed;
                    AddToPanel(label, row, firstColumn, rowSpan);
                    AddToPanel(reset, row, firstColumn + 1, rowSpan);
                    AddToPanel(barHost, row, firstColumn + 2, rowSpan);
                    AddToPanel(value, row, firstColumn + 3, rowSpan);
                    break;

                default:
                    barHost.Visibility = showBars && usageRow.HasBar ? Visibility.Visible : Visibility.Collapsed;
                    AddToPanel(label, row, firstColumn, rowSpan);
                    AddToPanel(reset, row, firstColumn + 1, rowSpan);
                    AddToPanel(barHost, row, firstColumn + 2, rowSpan);
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

        private int CalculateDesiredWidth()
        {
            double columns = Panel.ColumnDefinitions.Sum(c => c.Width.Value);
            double spacing = Math.Max(0, Panel.ColumnDefinitions.Count - 1) * Panel.ColumnSpacing;
            double padding = Root.Padding.Left + Root.Padding.Right + 2;
            return (int)Math.Ceiling(columns + spacing + padding);
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

        private static string BuildRenderSignature(UsageResult result)
        {
            var parts = new List<string>
            {
                result.Id.ToString(),
                result.DisplayName,
                result.Error ?? string.Empty,
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

            string reset = row.ResetDescription == "now"
                ? "resets now"
                : $"resets in {row.ResetDescription}";
            return $"{row.Label}: {row.Value} - {reset}";
        }

        private static string BaseLabelText(WidgetUsageRow row, WidgetDisplayMode mode)
            => mode == WidgetDisplayMode.PercentagesOnly ? row.Label + ":" : row.Label;

        private static double MeasureTextWidth(string text)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = WidgetFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            };
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(textBlock.DesiredSize.Width);
        }

        private static string CompactResetDescription(string resetDescription)
            => resetDescription == "now" ? "now" : resetDescription.Replace(" ", "", StringComparison.Ordinal);

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
