using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Helpers;
using TaskbarQuota.Services;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; } = new();
        private bool _isInitializing;
        // Suppresses the Toggled handlers while a row's toggles are synced programmatically.
        private bool _suppressProviderToggleEvents;
        private bool _suppressTaskbarPlacementEvents;
        private Slider? _floatingOpacitySlider;
        // Per-provider toggles, so changing one updates only its own row instead of rebuilding the list.
        private readonly Dictionary<ProviderId, ProviderToggleRow> _providerRows = new();

        private sealed record TaskbarPlacementOption(
            string Label,
            TaskbarPlacementMode Mode,
            string DisplayKey = "");

        private sealed class ProviderToggleRow
        {
            public ToggleSwitch? Dashboard;
            public ToggleSwitch? Widget;
            public ToggleSwitch? Pinned;
        }

        public SettingsPage()
        {
            _isInitializing = true;
            InitializeComponent();
            BuildFloatingOpacitySlider();
            ThemeCombo.SelectedIndex = ThemeService.Current switch
            {
                ElementTheme.Light => 1,
                ElementTheme.Dark => 2,
                _ => 0,
            };
            WidgetSurfaceCombo.SelectedIndex = WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Floating ? 1 : 0;
            BuildTaskbarPlacementOptions();
            int opacityPercent = (int)System.Math.Round(WidgetSettingsService.FloatingOpacity * 100);
            if (_floatingOpacitySlider is { } slider)
                slider.Value = opacityPercent;
            FloatingOpacityLabel.Text = $"{opacityPercent}%";
            FloatingOpacityCard.IsEnabled = WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Floating;
            TaskbarPlacementCard.IsEnabled = WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Taskbar;
            WidgetModeCombo.SelectedIndex = WidgetSettingsService.Current switch
            {
                WidgetDisplayMode.PercentagesOnly => 1,
                WidgetDisplayMode.BarsAndPercentages => 2,
                _ => 0,
            };
            PercentageModeCombo.SelectedIndex = WidgetSettingsService.CurrentPercentageMode == PercentageDisplayMode.Remaining ? 1 : 0;
            StartupToggle.IsOn = StartupSettingsService.IsEnabled;
            ApplyQuotaAlertSettingsToControls();
            AutoHideUnavailableToggle.IsOn = WidgetSettingsService.AutoHideUnavailable;
            HideWhenUnfocusedToggle.IsOn = WidgetSettingsService.HideWhenProviderUnfocused;
            ViewModel.ReloadProviders();
            RebuildProviderSettings();
            VersionLabel.Text = $"Version {AppVersion.GetDisplayLabel()}";
            Loaded += (_, _) =>
            {
                Log.Information(
                    $"Settings page loaded (surface={WidgetSettingsService.CurrentSurface}, layout={WidgetSettingsService.Current})");
                ViewModel.ReloadProviders();
                RebuildProviderSettings();
                BuildTaskbarPlacementOptions();
            };
            _isInitializing = false;
        }

        private void RebuildProviderSettings()
        {
            ProviderSettingsPanel.Children.Clear();
            _providerRows.Clear();
            foreach (var item in ViewModel.Providers)
            {
                var card = new CommunityToolkit.WinUI.Controls.SettingsCard
                {
                    Margin = new Thickness(0, 0, 0, 4),
                };

                var header = new StackPanel { Spacing = 2 };
                header.Children.Add(new TextBlock
                {
                    Text = item.DisplayName,
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                });
                header.Children.Add(new TextBlock
                {
                    Text = item.StatusText,
                    Opacity = 0.65,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                });
                card.Header = header;

                var toggles = new ProviderToggleRow();
                _providerRows[item.Id] = toggles;

                var content = new StackPanel { Spacing = 8, MinWidth = 180 };
                content.Children.Add(CreateProviderToggleRow("Dashboard", item, ProviderToggleKind.Dashboard, toggles));
                content.Children.Add(CreateProviderToggleRow("Widget", item, ProviderToggleKind.Widget, toggles));
                content.Children.Add(CreateProviderToggleRow("Pinned", item, ProviderToggleKind.Pinned, toggles));
                card.Content = content;

                ProviderSettingsPanel.Children.Add(card);
            }
        }

        private enum ProviderToggleKind { Dashboard, Widget, Pinned }

        private FrameworkElement CreateProviderToggleRow(
            string label,
            ProviderSettingItemViewModel item,
            ProviderToggleKind kind,
            ProviderToggleRow toggles)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Width = 72,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });

            var toggle = new ToggleSwitch
            {
                IsOn = kind switch
                {
                    ProviderToggleKind.Dashboard => item.IsDashboardVisible,
                    ProviderToggleKind.Pinned => item.IsPinned,
                    _ => item.IsWidgetVisible,
                },
                // A provider can only be pinned to the taskbar when the widget may draw it at all.
                IsEnabled = kind != ProviderToggleKind.Pinned || item.IsWidgetVisible,
                Tag = item,
            };
            switch (kind)
            {
                case ProviderToggleKind.Dashboard: toggles.Dashboard = toggle; break;
                case ProviderToggleKind.Pinned: toggles.Pinned = toggle; break;
                default: toggles.Widget = toggle; break;
            }
            toggle.Toggled += kind switch
            {
                ProviderToggleKind.Dashboard => OnProviderDashboardToggled,
                ProviderToggleKind.Pinned => OnProviderPinnedToggled,
                _ => OnProviderWidgetToggled,
            };
            row.Children.Add(toggle);
            return row;
        }

        // Syncs one provider's toggles to its current state in place. Enabling/disabling a provider and
        // pinning both flip sibling toggles, and a whole-list rebuild would flash the page.
        private void RefreshProviderRow(ProviderSettingItemViewModel item)
        {
            if (!_providerRows.TryGetValue(item.Id, out var row))
                return;

            _suppressProviderToggleEvents = true;
            try
            {
                if (row.Dashboard is { } dashboard) dashboard.IsOn = item.IsDashboardVisible;
                if (row.Widget is { } widget) widget.IsOn = item.IsWidgetVisible;
                if (row.Pinned is { } pinned)
                {
                    pinned.IsOn = item.IsPinned;
                    pinned.IsEnabled = item.IsWidgetVisible;
                }
            }
            finally
            {
                _suppressProviderToggleEvents = false;
            }
        }

        private void OnAutoHideUnavailableToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            WidgetSettingsService.ApplyAutoHideUnavailable(AutoHideUnavailableToggle.IsOn);
        }

        private void OnHideWhenUnfocusedToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            WidgetSettingsService.ApplyHideWhenProviderUnfocused(HideWhenUnfocusedToggle.IsOn);
        }

        private void OnProviderDashboardToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _suppressProviderToggleEvents)
                return;
            if (sender is not ToggleSwitch toggle || toggle.Tag is not ProviderSettingItemViewModel item)
                return;

            ViewModel.ApplyDashboardVisibility(item, toggle.IsOn);
            // Enabling/disabling a provider also flips widget visibility (and with it the pin).
            RefreshProviderRow(item);
        }

        private void OnProviderWidgetToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _suppressProviderToggleEvents)
                return;
            if (sender is not ToggleSwitch toggle || toggle.Tag is not ProviderSettingItemViewModel item)
                return;

            ViewModel.ApplyWidgetVisibility(item, toggle.IsOn);
            // Widget visibility gates whether Pinned can be toggled at all.
            RefreshProviderRow(item);
        }

        private void OnProviderPinnedToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _suppressProviderToggleEvents)
                return;
            if (sender is not ToggleSwitch toggle || toggle.Tag is not ProviderSettingItemViewModel item)
                return;

            if (toggle.IsOn && !PinBudgetService.CanPin(item.Id, out var reason))
            {
                _suppressProviderToggleEvents = true;
                try { toggle.IsOn = false; }
                finally { _suppressProviderToggleEvents = false; }
                PinBlockedBar.Message = reason;
                PinBlockedBar.IsOpen = true;
                return;
            }

            PinBlockedBar.IsOpen = false;
            ViewModel.ApplyPinned(item, toggle.IsOn);
            RefreshProviderRow(item);
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var theme = tag switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default,
                };
                ThemeService.Apply(theme);
            }
        }

        private void OnWidgetSurfaceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing)
                return;

            if (WidgetSurfaceCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var mode = tag == "Floating"
                    ? WidgetSurfaceMode.Floating
                    : WidgetSurfaceMode.Taskbar;
                WidgetSettingsService.ApplySurface(mode);
                FloatingOpacityCard.IsEnabled = mode == WidgetSurfaceMode.Floating;
                TaskbarPlacementCard.IsEnabled = mode == WidgetSurfaceMode.Taskbar;
            }
        }

        private void BuildTaskbarPlacementOptions()
        {
            _suppressTaskbarPlacementEvents = true;
            try
            {
                TaskbarPlacementCombo.Items.Clear();
                AddTaskbarPlacementOption(new("All screens", TaskbarPlacementMode.AllDisplays));

                var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                if (TaskbarWindowTarget.TryFindAll(out var targets))
                {
                    foreach (var target in targets
                        .OrderBy(target => target.DisplayNumber == 0 ? int.MaxValue : target.DisplayNumber)
                        .ThenByDescending(target => target.IsPrimary))
                    {
                        if (!seen.Add(target.DisplayKey))
                            continue;

                        string screenName = target.DisplayNumber > 0
                            ? $"Screen {target.DisplayNumber}"
                            : "Detected screen";
                        if (target.IsPrimary)
                            screenName += " (primary)";
                        AddTaskbarPlacementOption(new(
                            screenName,
                            TaskbarPlacementMode.SelectedDisplay,
                            target.DisplayKey));
                    }
                }

                string selectedKey = WidgetSettingsService.SelectedTaskbarDisplayKey;
                if (WidgetSettingsService.CurrentTaskbarPlacement == TaskbarPlacementMode.SelectedDisplay
                    && selectedKey.Length > 0
                    && seen.Add(selectedKey))
                {
                    AddTaskbarPlacementOption(new(
                        $"{selectedKey} (disconnected)",
                        TaskbarPlacementMode.SelectedDisplay,
                        selectedKey));
                }

                AddTaskbarPlacementOption(new("Adaptive (follow each agent)", TaskbarPlacementMode.Adaptive));

                for (int i = 0; i < TaskbarPlacementCombo.Items.Count; i++)
                {
                    if (TaskbarPlacementCombo.Items[i] is not ComboBoxItem { Tag: TaskbarPlacementOption option })
                        continue;
                    if (option.Mode != WidgetSettingsService.CurrentTaskbarPlacement)
                        continue;
                    if (option.Mode == TaskbarPlacementMode.SelectedDisplay
                        && !string.Equals(
                            option.DisplayKey,
                            selectedKey,
                            System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TaskbarPlacementCombo.SelectedIndex = i;
                    break;
                }
            }
            finally
            {
                _suppressTaskbarPlacementEvents = false;
            }
        }

        private void AddTaskbarPlacementOption(TaskbarPlacementOption option)
            => TaskbarPlacementCombo.Items.Add(new ComboBoxItem
            {
                Content = option.Label,
                Tag = option,
            });

        private void OnTaskbarPlacementChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || _suppressTaskbarPlacementEvents)
                return;
            if (TaskbarPlacementCombo.SelectedItem is not ComboBoxItem { Tag: TaskbarPlacementOption option })
                return;

            WidgetSettingsService.ApplyTaskbarPlacement(option.Mode, option.DisplayKey);
        }

        /// <summary>
        /// Builds the opacity slider in code. WinUI RangeBase defaults Maximum=1; assigning Minimum=35
        /// from XAML throws XamlParseException regardless of attribute order.
        /// </summary>
        private void BuildFloatingOpacitySlider()
        {
            if (_floatingOpacitySlider is not null)
                return;

            var slider = new Slider
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                StepFrequency = 1,
            };
            slider.Maximum = 100;
            slider.Minimum = 35;
            slider.Value = System.Math.Clamp(
                System.Math.Round(WidgetSettingsService.FloatingOpacity * 100),
                35,
                100);
            AutomationProperties.SetName(slider, "Floating acrylic strength");
            AutomationProperties.SetAutomationId(slider, "SettingsFloatingOpacitySlider");
            slider.ValueChanged += OnFloatingOpacityChanged;

            FloatingOpacitySliderHost.Children.Clear();
            FloatingOpacitySliderHost.Children.Add(slider);
            _floatingOpacitySlider = slider;
        }

        private void OnFloatingOpacityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializing)
                return;

            int percent = (int)System.Math.Round(e.NewValue);
            FloatingOpacityLabel.Text = $"{percent}%";
            WidgetSettingsService.ApplyFloatingOpacity(percent / 100d);
        }

        private void OnWidgetModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WidgetModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var mode = tag switch
                {
                    "PercentagesOnly" => WidgetDisplayMode.PercentagesOnly,
                    "BarsAndPercentages" => WidgetDisplayMode.BarsAndPercentages,
                    _ => WidgetDisplayMode.BarsOnly,
                };
                WidgetSettingsService.Apply(mode);
            }
        }

        private void OnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            StartupSettingsService.Apply(StartupToggle.IsOn);
        }

        private void OnPercentageModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PercentageModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var mode = tag == "Remaining"
                    ? PercentageDisplayMode.Remaining
                    : PercentageDisplayMode.Consumed;
                WidgetSettingsService.Apply(mode);
            }
        }

        private void OnQuotaAlertsToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            QuotaAlertSettingsService.SetEnabled(QuotaAlertsToggle.IsOn);
        }

        private void OnQuotaReplenishmentToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            QuotaAlertSettingsService.SetReplenishmentEnabled(QuotaReplenishmentToggle.IsOn);
            ApplyQuotaAlertSettingsToControls();
        }

        private void OnCrossSessionReplenishmentToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            QuotaAlertSettingsService.SetCrossSessionReplenishmentEnabled(CrossSessionReplenishmentToggle.IsOn);
        }

        private void OnWarningThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isInitializing || double.IsNaN(args.NewValue))
                return;

            QuotaAlertSettingsService.SetWarningThreshold(args.NewValue);
            ApplyQuotaAlertSettingsToControls();
        }

        private void OnCriticalThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isInitializing || double.IsNaN(args.NewValue))
                return;

            QuotaAlertSettingsService.SetCriticalThreshold(args.NewValue);
            ApplyQuotaAlertSettingsToControls();
        }

        private void OnAlertCooldownChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isInitializing || double.IsNaN(args.NewValue))
                return;

            QuotaAlertSettingsService.SetCooldownMinutes(args.NewValue);
            ApplyQuotaAlertSettingsToControls();
        }

        private void ApplyQuotaAlertSettingsToControls()
        {
            var settings = QuotaAlertSettingsService.Current;
            var wasInitializing = _isInitializing;
            _isInitializing = true;
            try
            {
                QuotaAlertsToggle.IsOn = settings.Enabled;
                QuotaReplenishmentToggle.IsOn = settings.ReplenishmentEnabled;
                CrossSessionReplenishmentToggle.IsOn = settings.CrossSessionReplenishmentEnabled;
                CrossSessionReplenishmentToggle.IsEnabled = settings.ReplenishmentEnabled;
                WarningThresholdBox.Value = settings.WarningThreshold;
                CriticalThresholdBox.Value = settings.CriticalThreshold;
                AlertCooldownBox.Value = settings.CooldownMinutes;
            }
            finally
            {
                _isInitializing = wasInitializing;
            }
        }
    }
}
