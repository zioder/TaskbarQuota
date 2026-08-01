using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
        private bool _heightReportQueued;
        private Storyboard? _selectionStoryboard;
        private TranslateTransform? _dashboardTranslate;
        private ProviderId? _lastAnimatedProvider;

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
            _dashboardTranslate = new TranslateTransform();
            DashboardContent.RenderTransform = _dashboardTranslate;
            ViewModel.ScrollToTopRequested += OnScrollToTopRequested;
            ViewModel.SelectedCardChanged += OnSelectedCardChanged;
            ViewModel.DetailContentWidthChanged += OnDetailContentWidthChanged;
            DashboardContent.SizeChanged += DashboardContent_SizeChanged;
            Loaded += (_, _) =>
            {
                _lastAnimatedProvider = ViewModel.SelectedCard?.ProviderId;
                ApplyLayoutState();
                QueueMeasuredHeightReport();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = ViewModel.LoadAsync());
            };
            Unloaded += (_, _) =>
            {
                ViewModel.ScrollToTopRequested -= OnScrollToTopRequested;
                ViewModel.SelectedCardChanged -= OnSelectedCardChanged;
                ViewModel.DetailContentWidthChanged -= OnDetailContentWidthChanged;
                DashboardContent.SizeChanged -= DashboardContent_SizeChanged;
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
        {
            ApplyLayoutState();
            QueueMeasuredHeightReport();
        }

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

        private void DashboardContent_SizeChanged(object sender, SizeChangedEventArgs e)
            => QueueMeasuredHeightReport();

        private void OnSelectedCardChanged(ProviderCardViewModel? card)
        {
            QueueMeasuredHeightReport();
            if (card is null)
                return;

            if (_lastAnimatedProvider == card.ProviderId)
                return;

            bool shouldAnimate = _lastAnimatedProvider is not null;
            _lastAnimatedProvider = card.ProviderId;
            if (shouldAnimate)
                AnimateProviderSelection();
        }

        private void AnimateProviderSelection()
        {
            if (_dashboardTranslate is null)
                return;

            _selectionStoryboard?.Stop();
            DashboardContent.Opacity = 0.86;
            _dashboardTranslate.Y = 8;

            _selectionStoryboard = new Storyboard();
            _selectionStoryboard.Children.Add(CreateDoubleAnimation(DashboardContent, "Opacity", 1, 180));
            _selectionStoryboard.Children.Add(CreateDoubleAnimation(_dashboardTranslate, "Y", 0, 220));
            _selectionStoryboard.Begin();
        }

        private static DoubleAnimation CreateDoubleAnimation(
            DependencyObject target,
            string property,
            double to,
            int milliseconds)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            return animation;
        }

        private void QueueMeasuredHeightReport()
        {
            if (!_useCompactLayout || _heightReportQueued)
                return;

            _heightReportQueued = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _heightReportQueued = false;
                if (_useCompactLayout && DashboardContent.ActualHeight > 0)
                    ViewModel.ReportMeasuredDetailHeight(DashboardContent.ActualHeight);
            });
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

            var stack = new StackPanel { Spacing = 12, MaxWidth = 380 };

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
                bool isOpenCode = vm.Id is ProviderId.OpenCode or ProviderId.OpenCodeGo;
                var tb = new TextBox
                {
                    Text = vm.CookieHeader,
                    MinWidth = 280,
                    PlaceholderText = isOpenCode ? "Cookie value or full copied cURL command" : "name1=value1; name2=value2",
                    AcceptsReturn = isOpenCode,
                    TextWrapping = isOpenCode ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MaxHeight = isOpenCode ? 120 : double.PositiveInfinity,
                };
                tb.TextChanged += (_, _) => vm.CookieHeader = tb.Text;

                stack.Children.Add(new TextBlock { Text = "Cookie header", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                stack.Children.Add(new TextBlock { Text = "Leave blank to keep auto-detection.", Opacity = 0.6, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
                stack.Children.Add(tb);

                if (isOpenCode)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Chromium 127+ manual setup",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Chrome, Edge, and Brave 127+ protect cookies with App-Bound Encryption, so TaskbarQuota may not be able to read them automatically.",
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = "1. Open opencode.ai in the browser where you are signed in. Press F12, choose Network, and reload the page.",
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = "2. Click an opencode.ai request, right-click it, then choose Copy → Copy as cURL.",
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = "3. Paste either the full copied cURL command or only the text after Cookie:. TaskbarQuota extracts the cookie automatically; it should include auth=... or __Host-auth=....",
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    });

                    var workspace = new TextBox
                    {
                        Text = vm.WorkspaceId,
                        MinWidth = 280,
                        PlaceholderText = "wrk_... or workspace URL",
                        AcceptsReturn = false,
                    };
                    workspace.TextChanged += (_, _) => vm.WorkspaceId = workspace.Text;
                    stack.Children.Add(new TextBlock { Text = "Workspace ID (optional; usually auto-detected)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Only fill this when detection fails or you have multiple workspaces. Copy the wrk_... part from the OpenCode workspace URL; a full workspace URL also works.",
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    });
                    stack.Children.Add(workspace);

                    stack.Children.Add(new TextBlock
                    {
                        Text = "Cookie headers are session credentials. Keep them private.",
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    });
                }
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

        private async void ConnectOAuth_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not ProviderId id)
                return;

            await ConnectProviderAsync(button, id);
        }

        private async Task ConnectProviderAsync(Button button, ProviderId id)
        {
            button.IsEnabled = false;
            var original = button.Content;
            button.Content = "Waiting for browser…";
            try
            {
                if (id == ProviderId.Claude)
                    await LoginWithClaudeAsync(button);
                ViewModel.EnableAvailableProvider(id);
                ViewModel.RefreshCommand.Execute(null);
            }
            catch (System.Exception ex)
            {
                TaskbarQuota.Diagnostics.Log.Debug($"[oauth] login failed for {id}: {ex.Message}");
            }
            finally
            {
                button.Content = original;
                button.IsEnabled = true;
            }
        }

        private async Task LoginWithClaudeAsync(Button button)
        {
            var request = TaskbarQuota.Services.ClaudeOAuth.CreateLoginRequest();
            TaskbarQuota.Services.ClaudeOAuth.OpenLoginPage(request);

            button.Content = "Paste code…";
            var code = await PromptForClaudeCodeAsync(button);
            if (string.IsNullOrWhiteSpace(code))
                throw new OperationCanceledException("Claude login cancelled.");

            button.Content = "Connecting…";
            await TaskbarQuota.Services.ClaudeOAuth.CompleteLoginAsync(request, code);
        }

        private async Task<string?> PromptForClaudeCodeAsync(FrameworkElement owner)
        {
            var input = new TextBox
            {
                AcceptsReturn = false,
                MinWidth = 360,
                PlaceholderText = "Paste Claude code or callback URL",
            };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "After approving access in Claude, paste the authorization code shown in the browser.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            });
            panel.Children.Add(input);

            var dialog = new ContentDialog
            {
                Title = "Complete Claude login",
                Content = panel,
                PrimaryButtonText = "Connect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = owner.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? input.Text : null;
        }

        private async void OnboardingConnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string)
                return;

            await ConnectProviderAsync(button, ProviderId.Claude);
        }

        private void OnboardingOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            Frame?.Navigate(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
        }

        private void DismissOnboarding_Click(object sender, RoutedEventArgs e)
            => ViewModel.DismissOnboarding();

        private static ProviderCredentialViewModel CreateCredentialVm(ProviderId id) => id switch
        {
            ProviderId.Copilot => new ProviderCredentialViewModel(id, "GitHub Copilot",
                "Paste a GitHub token if `gh auth login` is not available.",
                CredentialKind.ApiKey, "GITHUB_TOKEN / GH_TOKEN"),
            ProviderId.Cursor => new ProviderCredentialViewModel(id, "Cursor",
                "Paste a cursor.com Cookie header to override browser detection.",
                CredentialKind.Cookie),
            ProviderId.OpenCode => new ProviderCredentialViewModel(id, "OpenCode",
                "Paste an opencode.ai Cookie header and optionally select a workspace.",
                CredentialKind.Cookie),
            ProviderId.OpenCodeGo => new ProviderCredentialViewModel(id, "OpenCode Go",
                "Paste an opencode.ai Cookie header and optionally select a workspace.",
                CredentialKind.Cookie),
            ProviderId.Kimi => new ProviderCredentialViewModel(id, "Kimi Code",
                "Paste your Kimi Code API key (KIMI_CODE_API_KEY) to fetch Coding Plan usage without CLI login.",
                CredentialKind.ApiKey, "KIMI_CODE_API_KEY"),
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
            card.IsProviderWidgetVisible = WidgetSettingsService.IsProviderVisible(card.ProviderId);
            // Hiding a provider from the widget also drops its pin, so keep the pin button in step.
            card.IsProviderPinned = WidgetSettingsService.IsProviderPinned(card.ProviderId);
            ProviderPinToggle.IsChecked = card.IsProviderPinned;
            ProviderPinToggle.IsEnabled = card.IsProviderPinToggleEnabled;
        }

        private void ProviderPinToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressWidgetEvents)
                return;
            if (sender is not ToggleButton toggle || toggle.Tag is not ProviderCardViewModel card)
                return;

            bool wantPinned = toggle.IsChecked == true;
            if (wantPinned && !Services.PinBudgetService.CanPin(card.ProviderId, out var reason))
            {
                // Over budget: refuse rather than silently unpinning something the user still wants, and
                // show why plus what to change — a toggle that springs back with no explanation reads as
                // a broken button.
                toggle.IsChecked = false;
                PinBlockedTip.Target = toggle;
                PinBlockedTip.Subtitle = reason;
                PinBlockedTip.IsOpen = true;
                return;
            }

            PinBlockedTip.IsOpen = false;

            WidgetSettingsService.SetProviderPinned(card.ProviderId, wantPinned);
            card.IsProviderPinned = WidgetSettingsService.IsProviderPinned(card.ProviderId);
            card.IsProviderWidgetVisible = WidgetSettingsService.IsProviderVisible(card.ProviderId);
            ToolTipService.SetToolTip(toggle, DefaultPinTooltip);
        }

        private const string DefaultPinTooltip =
            "Keep this provider on the taskbar even when another tool is active";

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
