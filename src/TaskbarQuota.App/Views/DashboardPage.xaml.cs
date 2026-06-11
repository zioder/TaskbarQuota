using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;
using Windows.System;

namespace TaskbarQuota.Views
{
    public sealed partial class DashboardPage : Page
    {
        private readonly bool _ownsViewModel;
        private static bool _suppressWidgetEvents;
        private bool _useCompactLayout;

        public DashboardViewModel ViewModel { get; }
        public static DashboardViewModel? SharedViewModel { get; set; }

        /// <summary>The content panel; used by the main window scroll area.</summary>
        public FrameworkElement ContentPanel => DashboardContent;

        public static void SetSuppressWidgetEvents(bool suppress)
            => _suppressWidgetEvents = suppress;

        public DashboardPage()
        {
            ViewModel = SharedViewModel ?? new DashboardViewModel(DispatcherQueue);
            _ownsViewModel = SharedViewModel is null;
            InitializeComponent();
            ViewModel.ScrollToTopRequested += OnScrollToTopRequested;
            ViewModel.DetailContentWidthChanged += OnDetailContentWidthChanged;
            Loaded += (_, _) =>
            {
                ApplyLayoutState();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = ViewModel.LoadAsync());
            };
            Unloaded += (_, _) =>
            {
                ViewModel.ScrollToTopRequested -= OnScrollToTopRequested;
                ViewModel.DetailContentWidthChanged -= OnDetailContentWidthChanged;
                if (_ownsViewModel)
                    ViewModel.Dispose();
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is bool compact)
            {
                _useCompactLayout = compact;
                ApplyLayoutState();
            }
        }

        private void OnDetailContentWidthChanged(double width)
            => ApplyLayoutState();

        private void ApplyLayoutState()
        {
            if (_useCompactLayout)
            {
                DashboardContent.HorizontalAlignment = HorizontalAlignment.Left;
                DashboardContent.Width = ViewModel.DetailContentWidth;
                MainScrollViewer.HorizontalContentAlignment = HorizontalAlignment.Left;
            }
            else
            {
                DashboardContent.ClearValue(FrameworkElement.WidthProperty);
                DashboardContent.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainScrollViewer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            }
        }

        private void OnScrollToTopRequested()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                MainScrollViewer.ChangeView(null, 0, null);
            });
        }

        private void FixCredentials_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not HyperlinkButton button || button.Tag is not ProviderId id)
                return;

            var vm = CreateCredentialVm(id);
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };

            var stack = new StackPanel { Spacing = 12, MaxWidth = 340 };

            if (vm.IsApiKey)
            {
                var pwd = new PasswordBox { Password = vm.ApiKey, MinWidth = 280, PlaceholderText = "Paste your GitHub token..." };
                pwd.PasswordChanged += (_, _) => vm.ApiKey = pwd.Password;

                stack.Children.Add(new TextBlock { Text = "API key", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                stack.Children.Add(new TextBlock { Text = vm.ApiKeyHeader, Opacity = 0.6, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
                stack.Children.Add(pwd);
            }
            else
            {
                var tb = new TextBox { Text = vm.CookieHeader, MinWidth = 280, PlaceholderText = "name1=value1; name2=value2", AcceptsReturn = false };
                tb.TextChanged += (_, _) => vm.CookieHeader = tb.Text;

                stack.Children.Add(new TextBlock { Text = "Cookie header", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                stack.Children.Add(new TextBlock { Text = "Leave blank to keep auto-detection.", Opacity = 0.6, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
                stack.Children.Add(tb);
            }

            var saveBtn = new Button { Content = "Save & Refresh", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Margin = new Thickness(0, 4, 0, 0) };
            saveBtn.Click += (_, _) =>
            {
                vm.SaveCommand.Execute(null);
                flyout.Hide();
                ViewModel.RefreshCommand.Execute(null);
            };
            stack.Children.Add(saveBtn);

            flyout.Content = stack;
            flyout.ShowAt(button);
        }

        private static ProviderCredentialViewModel CreateCredentialVm(ProviderId id) => id switch
        {
            ProviderId.Copilot => new ProviderCredentialViewModel(id, "GitHub Copilot",
                "Paste a GitHub token if `gh auth login` is not available.",
                CredentialKind.ApiKey, "GITHUB_TOKEN / GH_TOKEN"),
            ProviderId.Cursor => new ProviderCredentialViewModel(id, "Cursor",
                "Paste a cursor.com Cookie header to override browser detection.",
                CredentialKind.Cookie),
            ProviderId.OpenCode => new ProviderCredentialViewModel(id, "OpenCode",
                "Paste an opencode.ai Cookie header to override browser detection.",
                CredentialKind.Cookie),
            ProviderId.OpenCodeGo => new ProviderCredentialViewModel(id, "OpenCode Go",
                "Paste an opencode.ai Cookie header to override browser detection.",
                CredentialKind.Cookie),
            _ => throw new System.ArgumentException($"No credential fix for {id}", nameof(id)),
        };

        private void OpenHelp_Click(object sender, RoutedEventArgs e)
            => _ = Launcher.LaunchUriAsync(new Uri("https://github.com/robinebers/openusage"));

        private void OpenUsageDashboard_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedCard?.UsageDashboardUrl is { Length: > 0 } url)
                _ = Launcher.LaunchUriAsync(new Uri(url));
        }

        private void WidgetRowVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressWidgetEvents)
                return;
            if (sender is not ToggleButton toggle || toggle.Tag is not IWidgetRowToggle row)
                return;

            var desired = toggle.IsChecked == true;
            if (WidgetSettingsService.IsRowVisible(row.ProviderId, row.WidgetRowId) == desired)
                return;

            WidgetSettingsService.SetRowVisible(row.ProviderId, row.WidgetRowId, desired);
        }

        private void ProviderWidgetToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressWidgetEvents)
                return;
            if (sender is not ToggleButton toggle || toggle.Tag is not ProviderCardViewModel card)
                return;

            WidgetSettingsService.SetProviderVisible(card.ProviderId, toggle.IsChecked == true);
        }

        private void OpenSetupUrl_Click(object sender, RoutedEventArgs e)
        {
            ProviderId? providerId = sender switch
            {
                HyperlinkButton { Tag: ProviderId id } => id,
                Button { Tag: ProviderId id } => id,
                _ => null,
            };

            if (providerId is not { } resolvedId)
                return;

            var url = ProviderSetupInfo.SetupUrl(resolvedId);
            if (!string.IsNullOrEmpty(url))
                _ = Launcher.LaunchUriAsync(new Uri(url));
        }
    }
}
