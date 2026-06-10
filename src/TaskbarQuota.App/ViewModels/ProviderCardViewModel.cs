using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
    public interface IWidgetRowToggle
    {
        ProviderId ProviderId { get; }
        string WidgetRowId { get; }
        bool IsWidgetVisible { get; }
        bool IsWidgetToggleEnabled { get; }
        string WidgetToggleName { get; }
    }

    /// <summary>A single usage bar (session / weekly / model) for the card template.</summary>
    public sealed class BarViewModel : IWidgetRowToggle
    {
        public ProviderId ProviderId { get; }
        public string WidgetRowId { get; }
        public string Label { get; }
        public double Percent { get; }
        public string PercentText { get; }
        public string ResetText { get; }
        public Visibility ResetVisibility { get; }
        public Brush BarBrush { get; }
        public Brush PercentForeground { get; }
        public bool IsWidgetVisible { get; internal set; }
        public bool IsWidgetToggleEnabled { get; internal set; }
        public string WidgetToggleName => $"Show {Label} in taskbar widget";

        public BarViewModel(ProviderId providerId, string widgetRowId, string label, RateWindow w)
        {
            double displayPercent = WidgetSettingsService.DisplayPercent(w.UsedPercent);
            ProviderId = providerId;
            WidgetRowId = widgetRowId;
            Label = label;
            Percent = displayPercent;
            PercentText = WidgetSettingsService.FormatDisplayPercent(w.UsedPercent);
            ResetText = w.ResetDescription is { } r ? $"resets in {r}" : string.Empty;
            ResetVisibility = w.ResetDescription is null ? Visibility.Collapsed : Visibility.Visible;
            BarBrush = Ui.UsageBrush(displayPercent);
            PercentForeground = BarBrush;
            IsWidgetVisible = WidgetSettingsService.IsRowVisible(providerId, widgetRowId);
            IsWidgetToggleEnabled = WidgetSettingsService.IsProviderVisible(providerId);
        }

        public BarViewModel(ProviderId providerId, NamedRateWindow w)
            : this(providerId, WidgetSettingsService.RowExtra, w.Title, w.Window) { }

        internal void RefreshVisibility()
        {
            IsWidgetVisible = WidgetSettingsService.IsRowVisible(ProviderId, WidgetRowId);
            IsWidgetToggleEnabled = WidgetSettingsService.IsProviderVisible(ProviderId);
        }
    }

    public sealed class TextMetricViewModel : IWidgetRowToggle
    {
        public ProviderId ProviderId { get; }
        public string WidgetRowId { get; }
        public string Label { get; }
        public string Value { get; }
        public double MutedOpacity { get; }
        public bool IsWidgetVisible { get; internal set; }
        public bool IsWidgetToggleEnabled { get; internal set; }
        public string WidgetToggleName => $"Show {Label} in taskbar widget";

        public TextMetricViewModel(ProviderId providerId, string widgetRowId, string label, string value, bool muted = false)
        {
            ProviderId = providerId;
            WidgetRowId = widgetRowId;
            Label = label;
            Value = value;
            MutedOpacity = muted ? 0.55 : 1.0;
            IsWidgetVisible = WidgetSettingsService.IsRowVisible(providerId, widgetRowId);
            IsWidgetToggleEnabled = WidgetSettingsService.IsProviderVisible(providerId);
        }

        internal void RefreshVisibility()
        {
            IsWidgetVisible = WidgetSettingsService.IsRowVisible(ProviderId, WidgetRowId);
            IsWidgetToggleEnabled = WidgetSettingsService.IsProviderVisible(ProviderId);
        }
    }

    public sealed class WidgetRowToggleViewModel : IWidgetRowToggle
    {
        public ProviderId ProviderId { get; }
        public string WidgetRowId { get; }
        public string Label { get; }
        public bool IsWidgetVisible { get; internal set; }
        public bool IsWidgetToggleEnabled { get; internal set; }
        public string WidgetToggleName => $"Show {Label} in taskbar widget";

        public WidgetRowToggleViewModel(ProviderId providerId, WidgetRowOption option)
        {
            ProviderId = providerId;
            WidgetRowId = option.Id;
            Label = option.Label;
            IsWidgetVisible = WidgetSettingsService.IsRowVisible(providerId, option.Id);
            IsWidgetToggleEnabled = WidgetSettingsService.IsProviderVisible(providerId);
        }

        internal void RefreshVisibility()
        {
            IsWidgetVisible = WidgetSettingsService.IsRowVisible(ProviderId, WidgetRowId);
            IsWidgetToggleEnabled = WidgetSettingsService.IsProviderVisible(ProviderId);
        }
    }

    /// <summary>Everything the dashboard card template binds to, computed once from a fetch result.</summary>
    public sealed class ProviderCardViewModel
    {
        public string DisplayName { get; }
        public string Initial { get; }
        public bool IsActive { get; }

        public string Plan { get; }
        public Visibility PlanVisibility { get; }
        public string PageTitle { get; }
        public string PageTitleAccent { get; }
        public Visibility PageTitleAccentVisibility { get; }
        public Visibility ActiveVisibility { get; }

        public string Error { get; }
        public Visibility ErrorVisibility { get; }
        public Visibility ContentVisibility { get; }

        public bool IsFixable { get; }
        public Visibility FixVisibility { get; }
        public ProviderId ProviderId { get; }

        public string Email { get; }
        public Visibility EmailVisibility { get; }

        public string CostText { get; }
        public Visibility CostVisibility { get; }
        public string CreditLeftText { get; }
        public string CreditLimitText { get; }
        public double CreditPercent { get; }
        public Brush CreditBrush { get; }
        public Visibility CreditVisibility { get; }
        public double CreditOpacity { get; }

        public string SourceText { get; }
        public Visibility SourceVisibility { get; }
        public bool IsProviderWidgetVisible { get; internal set; }
        public string ProviderWidgetToggleName { get; }
        public string ProviderWidgetToggleText => IsProviderWidgetVisible ? "Widget" : "Ignored";

        public IReadOnlyList<BarViewModel> Bars { get; }
        public Visibility BarsVisibility { get; }
        public IReadOnlyList<TextMetricViewModel> TextMetrics { get; }
        public Visibility TextMetricsVisibility { get; }
        public WidgetRowToggleViewModel CreditWidgetToggle { get; }
        public WidgetRowToggleViewModel AdditionalUsageWidgetToggle { get; }

        public string AdditionalUsageStatusText { get; }
        public string AdditionalUsageSpendText { get; }
        public Visibility AdditionalUsageVisibility { get; }
        public double AdditionalUsageOpacity { get; }

        public Brush AvatarBrush { get; }
        public Brush AvatarForeground { get; }
        public Brush CardBorderBrush { get; }
        public Thickness CardBorderThickness { get; }

        public string UsageDashboardUrl { get; }

        public ProviderCardViewModel(UsageResult r, bool isActive)
        {
            DisplayName = r.DisplayName;
            Initial = string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
            IsActive = isActive;
            ActiveVisibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            ProviderId = r.Id;
            IsProviderWidgetVisible = WidgetSettingsService.IsProviderVisible(r.Id);
            ProviderWidgetToggleName = $"Show {DisplayName} in taskbar widget";

            var bars = new List<BarViewModel>();
            var textMetrics = new List<TextMetricViewModel>();
            AdditionalUsageStatusText = string.Empty;
            AdditionalUsageSpendText = string.Empty;
            AdditionalUsageVisibility = Visibility.Collapsed;
            AdditionalUsageOpacity = 1.0;
            CreditLeftText = string.Empty;
            CreditLimitText = string.Empty;
            CreditPercent = 0;
            CreditBrush = Ui.ConsumedUsageBrush(0);
            CreditOpacity = 1.0;
            CreditWidgetToggle = new WidgetRowToggleViewModel(r.Id, new WidgetRowOption(WidgetSettingsService.RowCredits, "Credits"));
            AdditionalUsageWidgetToggle = new WidgetRowToggleViewModel(r.Id, new WidgetRowOption(WidgetSettingsService.RowAdditionalUsage, "Additional usage"));
            if (r.Ok && r.Fetch is { } f)
            {
                var u = f.Usage;
                if (r.Id == ProviderId.OpenCode)
                {
                    var usageVal = u.Cost != null ? u.Cost.Display : "—";
                    var balanceText = u.Secondary?.ResetDescription;
                    var balanceVal = balanceText != null
                        ? "$" + balanceText.Split(' ')[0]
                        : "—";
                    textMetrics.Add(new TextMetricViewModel(r.Id, WidgetSettingsService.RowUsage, "Usage", usageVal));
                    textMetrics.Add(new TextMetricViewModel(r.Id, WidgetSettingsService.RowBalance, "Balance", balanceVal));
                    CostText = string.Empty;
                }
                else if (r.Id == ProviderId.Antigravity)
                {
                    bars.Add(new BarViewModel(r.Id, WidgetSettingsService.RowPrimary, "Gemini", u.Primary));
                    if (u.Secondary != null) bars.Add(new BarViewModel(r.Id, WidgetSettingsService.RowSecondary, "Non-Gemini", u.Secondary));
                    CostText = u.Cost != null ? $"{u.Cost.Label}: {u.Cost.Display}" : string.Empty;
                }
                else
                {
                    bool copilotCredits = r.Id == ProviderId.Copilot && u.Cost is { Label: "Credits" };
                    if (!copilotCredits)
                    {
                        bars.Add(new BarViewModel(r.Id, WidgetSettingsService.RowPrimary, r.Provider?.SessionLabel ?? "Session", u.Primary));
                        if (u.Secondary != null) bars.Add(new BarViewModel(r.Id, WidgetSettingsService.RowSecondary, r.Provider?.WeeklyLabel ?? "Weekly", u.Secondary));
                        if (u.ModelSpecific != null) bars.Add(new BarViewModel(r.Id, WidgetSettingsService.RowModelSpecific, ModelSpecificLabel(r.Id), u.ModelSpecific));
                        if (u.Monthly != null) bars.Add(new BarViewModel(r.Id, WidgetSettingsService.RowMonthly, "Monthly", u.Monthly));
                        foreach (var extra in u.ExtraRateWindows) bars.Add(new BarViewModel(r.Id, extra));
                    }

                    if (u.Cost is { Label: "Credits" } credits)
                    {
                        var limit = credits.Limit ?? 0;
                        var remaining = credits.Amount;
                        var used = System.Math.Max(0, limit - remaining);
                        CreditPercent = limit <= 0 ? 0 : System.Math.Clamp(used / limit * 100, 0, 100);
                        CreditBrush = Ui.ConsumedUsageBrush(CreditPercent);
                        CreditLeftText = $"{FormatCount(used)}/{FormatCount(limit)} Credits";
                        CreditLimitText = FormatCreditReset(credits.ResetsAt, u.Primary.ResetDescription);
                        CreditOpacity = r.Id == ProviderId.Codex && remaining <= 0 ? 0.55 : 1.0;
                        CostText = string.Empty;
                    }
                    else
                    {
                        CostText = u.Cost != null ? $"{u.Cost.Label}: {u.Cost.Display}" : string.Empty;
                    }

                    if (r.Id == ProviderId.Copilot && u.AdditionalUsage is { } additional)
                    {
                        bool muted = !additional.Enabled;
                        AdditionalUsageStatusText = additional.StatusText;
                        AdditionalUsageSpendText = additional.SpendText;
                        AdditionalUsageVisibility = Visibility.Visible;
                        AdditionalUsageOpacity = muted ? 0.55 : 1.0;
                    }
                }

                Plan = PlanDisplayNames.ForTitle(r.Id, r.DisplayName, u.LoginMethod);
                Email = u.Email ?? string.Empty;
                SourceText = $"via {f.SourceLabel}";
                Error = string.Empty;
            }
            else
            {
                Plan = string.Empty; Email = string.Empty; CostText = string.Empty;
                CreditLeftText = string.Empty; CreditLimitText = string.Empty;
                SourceText = string.Empty;
                Error = r.Error ?? "Unavailable";
            }
            Bars = bars;
            BarsVisibility = bars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TextMetrics = textMetrics;
            TextMetricsVisibility = textMetrics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool ok = r.Ok;
            IsFixable = !ok && r.Id is ProviderId.Copilot or ProviderId.Cursor or ProviderId.OpenCode or ProviderId.OpenCodeGo;
            FixVisibility = IsFixable ? Visibility.Visible : Visibility.Collapsed;
            PlanVisibility = string.IsNullOrEmpty(Plan) ? Visibility.Collapsed : Visibility.Visible;

            var loginForHeader = r.Ok && r.Fetch is { } fetch ? fetch.Usage.LoginMethod : null;
            (PageTitle, PageTitleAccent) = PlanDisplayNames.ForPageHeader(r.Id, DisplayName, loginForHeader);
            PageTitleAccentVisibility = string.IsNullOrEmpty(PageTitleAccent)
                ? Visibility.Collapsed
                : Visibility.Visible;
            EmailVisibility = string.IsNullOrEmpty(Email) ? Visibility.Collapsed : Visibility.Visible;
            CostVisibility = string.IsNullOrEmpty(CostText) ? Visibility.Collapsed : Visibility.Visible;
            CreditVisibility = string.IsNullOrEmpty(CreditLeftText) ? Visibility.Collapsed : Visibility.Visible;
            SourceVisibility = string.IsNullOrEmpty(SourceText) ? Visibility.Collapsed : Visibility.Visible;
            ErrorVisibility = ok ? Visibility.Collapsed : Visibility.Visible;
            ContentVisibility = ok ? Visibility.Visible : Visibility.Collapsed;

            AvatarBrush = isActive ? Ui.Accent : new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
            AvatarForeground = isActive ? new SolidColorBrush(Colors.White) : Ui.Res("TextFillColorPrimaryBrush");
            CardBorderBrush = isActive ? Ui.Accent : Ui.Res("CardStrokeColorDefaultBrush");
            CardBorderThickness = new Thickness(isActive ? 1.5 : 1);

            UsageDashboardUrl = ResolveLinkUrl(r, u => u.UsageDashboardUrl, DefaultUsageDashboardUrl);
        }

        internal void RefreshVisibility()
        {
            IsProviderWidgetVisible = WidgetSettingsService.IsProviderVisible(ProviderId);
            foreach (var bar in Bars) bar.RefreshVisibility();
            foreach (var metric in TextMetrics) metric.RefreshVisibility();
            CreditWidgetToggle.RefreshVisibility();
            AdditionalUsageWidgetToggle.RefreshVisibility();
        }

        private static string ResolveLinkUrl(
            UsageResult r,
            Func<UsageSnapshot, string?> fromSnapshot,
            Func<ProviderId, string> fallback)
        {
            if (r.Ok && r.Fetch is { } f)
            {
                var url = fromSnapshot(f.Usage);
                if (!string.IsNullOrEmpty(url))
                    return url;
            }
            return fallback(r.Id);
        }

        private static string DefaultUsageDashboardUrl(ProviderId id) => id switch
        {
            ProviderId.Codex => "https://chatgpt.com/codex/settings/usage",
            ProviderId.Claude => "https://claude.ai/new#settings/usage",
            ProviderId.Cursor => "https://cursor.com/dashboard/spending",
            ProviderId.OpenCode or ProviderId.OpenCodeGo => "https://opencode.ai",
            ProviderId.Copilot => "https://github.com/settings/billing/ai_usage",
            ProviderId.Antigravity => "https://aistudio.google.com/usage",
            ProviderId.Grok => "https://grok.com/?_s=usage",
            _ => string.Empty,
        };

        private static string FormatCount(double value)
            => value.ToString(value % 1 == 0 ? "N0" : "N2", CultureInfo.CurrentCulture);

        private static string FormatCreditReset(DateTimeOffset? resetsAt, string? resetCountdown)
        {
            if (resetsAt is DateTimeOffset reset)
            {
                var local = reset.ToLocalTime();
                return $"Resets {local:MMM d 'at' h:mm tt}";
            }

            return resetCountdown is { Length: > 0 } countdown ? $"resets in {countdown}" : string.Empty;
        }

        private static string ModelSpecificLabel(ProviderId id) => id switch
        {
            ProviderId.Cursor => "API Usage",
            ProviderId.Copilot => "Completions",
            _ => "Model",
        };
    }

    /// <summary>Small helpers for resolving theme brushes from code (keeps templates converter-free).</summary>
    internal static class Ui
    {
        public static Brush Res(string key)
        {
            if (Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush)
                return brush;
            return new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));
        }
        public static Brush Accent => Res("AccentFillColorDefaultBrush");

        /// <summary>Usage color for a display percent (already converted by <see cref="WidgetSettingsService.DisplayPercent"/>).</summary>
        public static Brush UsageBrush(double displayPercent)
            => CopyBrush(Res(WidgetSettingsService.GetUsageBrushResourceKeyForDisplayPercent(displayPercent)));

        /// <summary>Usage color from raw consumed percent, honoring percentage display mode.</summary>
        public static Brush UsageBrushFromUsed(double usedPercent)
            => CopyBrush(Res(WidgetSettingsService.GetUsageBrushResourceKey(usedPercent)));

        /// <summary>Consumed-meter color (credits), always using consumed thresholds.</summary>
        public static Brush ConsumedUsageBrush(double consumedPercent)
            => CopyBrush(Res(WidgetSettingsService.GetConsumedUsageBrushResourceKey(consumedPercent)));

        private static Brush CopyBrush(Brush brush)
            => brush is SolidColorBrush solid
                ? new SolidColorBrush(solid.Color)
                : brush;

        /// <summary>
        /// Parse a provider's logo path into a fresh Geometry. A Geometry can have only one parent,
        /// so callers that render provider glyphs must build a new instance per element — never share.
        /// Returns null if unavailable.
        /// </summary>
        public static Microsoft.UI.Xaml.Media.Geometry? Glyph(ProviderId id)
        {
            if (!ProviderGlyphs.Data.TryGetValue(id, out var data) || string.IsNullOrWhiteSpace(data))
                return null;
            try
            {
                return ParseFreshGeometry(data);
            }
            catch { return null; }
        }

        /// <summary>Parse provider path markup into geometry (same format as ProviderGlyphs / XamlBindingHelper).</summary>
        internal static Microsoft.UI.Xaml.Media.Geometry? ParseFreshGeometry(string pathData)
        {
            if (string.IsNullOrWhiteSpace(pathData))
                return null;
            try
            {
                return (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper
                    .ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), pathData);
            }
            catch { return null; }
        }
    }
}
