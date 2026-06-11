using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TaskbarQuota.Helpers;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; } = new();
        private bool _isInitializing;

        public SettingsPage()
        {
            _isInitializing = true;
            InitializeComponent();
            ThemeCombo.SelectedIndex = ThemeService.Current switch
            {
                ElementTheme.Light => 1,
                ElementTheme.Dark => 2,
                _ => 0,
            };
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
            ViewModel.ReloadProviders();
            RebuildProviderSettings();
            VersionLabel.Text = $"Version {AppVersion.GetDisplayLabel()}";
            Loaded += (_, _) =>
            {
                ViewModel.ReloadProviders();
                RebuildProviderSettings();
            };
            _isInitializing = false;
        }

        private void RebuildProviderSettings()
        {
            ProviderSettingsPanel.Children.Clear();
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

                var content = new StackPanel { Spacing = 8, MinWidth = 180 };
                content.Children.Add(CreateProviderToggleRow("Dashboard", item, dashboard: true));
                content.Children.Add(CreateProviderToggleRow("Widget", item, dashboard: false));
                card.Content = content;

                ProviderSettingsPanel.Children.Add(card);
            }
        }

        private FrameworkElement CreateProviderToggleRow(string label, ProviderSettingItemViewModel item, bool dashboard)
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
                IsOn = dashboard ? item.IsDashboardVisible : item.IsWidgetVisible,
                Tag = item,
            };
            toggle.Toggled += dashboard ? OnProviderDashboardToggled : OnProviderWidgetToggled;
            row.Children.Add(toggle);
            return row;
        }

        private void OnAutoHideUnavailableToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            WidgetSettingsService.ApplyAutoHideUnavailable(AutoHideUnavailableToggle.IsOn);
        }

        private void OnProviderDashboardToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;
            if (sender is not ToggleSwitch toggle || toggle.Tag is not ProviderSettingItemViewModel item)
                return;

            ViewModel.ApplyDashboardVisibility(item, toggle.IsOn);
        }

        private void OnProviderWidgetToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;
            if (sender is not ToggleSwitch toggle || toggle.Tag is not ProviderSettingItemViewModel item)
                return;

            ViewModel.ApplyWidgetVisibility(item, toggle.IsOn);
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
