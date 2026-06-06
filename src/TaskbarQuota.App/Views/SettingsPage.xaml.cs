using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            VersionLabel.Text = $"Version {AppVersion.GetDisplayLabel()}";
            _isInitializing = false;
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
