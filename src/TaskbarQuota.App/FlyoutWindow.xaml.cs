using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using TaskbarQuota.Controls;
using TaskbarQuota.AgentActivity;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;
using TaskbarQuota.Services;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;
using TaskbarQuota.Views;

namespace TaskbarQuota
{
    /// <summary>
    /// A borderless, always-on-top acrylic flyout shown just above the taskbar widget — a compact
    /// "mini dashboard". Reuses DashboardPage. Hides itself when it loses focus.
    /// </summary>
    public sealed partial class FlyoutWindow : Window
    {
        private IntPtr _widgetHandle;
        private bool _shown;
        private bool _prewarmed;
        private bool _dashboardLoaded;
        private string? _selectedActivityId;
        private bool _showingActivity;
        private bool _showingCost;
        private bool _sizeHooksRegistered;
        private bool _applyingBounds;
        private bool _suppressSurfaceControlEvents;
        private DispatcherQueueTimer? _boundsUpdateTimer;
        private RectInt32? _lastAppliedBounds;
        private double _lastObservedScale = -1;
        private readonly DashboardViewModel _dashboardViewModel;
        private readonly Dictionary<ProviderId, FlyoutProviderStripItem> _providerStripItems = new();
        private int _stripIconCount;
        private Slider? _floatingOpacitySlider;
        private static readonly TimeSpan BoundsCoalesceDelay = TimeSpan.FromMilliseconds(80);

        public bool IsShown => _shown;

        public FlyoutWindow()
        {
            InitializeComponent();
            BuildFloatingOpacitySlider();
            UpdateActivityControls();
            UpdateSurfaceControls();
            _dashboardViewModel = DashboardPage.SharedViewModel ?? new DashboardViewModel(DispatcherQueue);
            DashboardPage.SharedViewModel = _dashboardViewModel;
            _dashboardViewModel.Cards.CollectionChanged += DashboardCards_CollectionChanged;
            _dashboardViewModel.SelectedCardChanged += DashboardSelectedCardChanged;
            _dashboardViewModel.DetailContentWidthChanged += DashboardDetailContentWidthChanged;
            _dashboardViewModel.DetailContentHeightChanged += DashboardDetailContentHeightChanged;
            ProviderStrip.Loaded += (_, _) => RebuildProviderStrip();
            WidgetSettingsService.Changed += OnWidgetSettingsChanged;
            AgentActivityService.Instance.Changed += OnActivityChanged;

            SystemBackdrop = new DesktopAcrylicBackdrop();
            ThemeService.Register(Root);
            Root.Loaded += (_, _) => RegisterWindowSizeHooks();
            _boundsUpdateTimer = DispatcherQueue.CreateTimer();
            _boundsUpdateTimer.Interval = BoundsCoalesceDelay;
            _boundsUpdateTimer.Tick += (_, _) =>
            {
                _boundsUpdateTimer.Stop();
                ApplyFlyoutBounds();
            };

            var presenter = OverlappedPresenter.CreateForContextMenu();
            presenter.IsAlwaysOnTop = true;
            var appWindow = GetAppWindow();
            // The flyout is transient taskbar UI, not an application window. Keep it out of the
            // taskbar/Alt+Tab representation; the main window opts in only when Settings or the app
            // itself is explicitly opened.
            appWindow.IsShownInSwitchers = false;
            appWindow.SetPresenter(presenter);
            var cornerPreference = DwmWindowCornerPreference.Round;
            _ = DwmApi.DwmSetWindowAttribute(
                Win32Interop.GetWindowFromWindowId(appWindow.Id),
                DwmApi.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                sizeof(int));

            Activated += OnActivated;
            Closed += OnClosed;
        }

        /// <summary>
        /// Drops every subscription the window owns. <see cref="WidgetSettingsService.Changed"/> is static
        /// and the flyout is discarded and rebuilt (TaskBarManager nulls it on shutdown and on a failed
        /// show), so without this each rebuilt flyout stays rooted for the life of the process and keeps
        /// enqueuing strip refreshes onto a closed window's dispatcher.
        /// </summary>
        private void OnClosed(object sender, WindowEventArgs args)
        {
            Closed -= OnClosed;
            Activated -= OnActivated;
            ThemeService.Unregister(Root);
            WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
            AgentActivityService.Instance.Changed -= OnActivityChanged;
            _dashboardViewModel.Cards.CollectionChanged -= DashboardCards_CollectionChanged;
            _dashboardViewModel.SelectedCardChanged -= DashboardSelectedCardChanged;
            _dashboardViewModel.DetailContentWidthChanged -= DashboardDetailContentWidthChanged;
            _dashboardViewModel.DetailContentHeightChanged -= DashboardDetailContentHeightChanged;
            _boundsUpdateTimer?.Stop();
            _providerStripItems.Clear();
        }

        private void OnWidgetSettingsChanged(object? sender, EventArgs e)
            => DispatcherQueue.TryEnqueue(() =>
            {
                SyncProviderStripPins();
                UpdateActivityControls();
                UpdateSurfaceControls();
            });

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            Log.Debug($"[cost-flyout] activated state={args.WindowActivationState} showingCost={_showingCost} shown={_shown} foreground=0x{User32.GetForegroundWindow().ToInt64():X} widget=0x{_widgetHandle.ToInt64():X}");
            if (args.WindowActivationState == WindowActivationState.Deactivated
                && User32.GetForegroundWindow() != _widgetHandle
                && !IsPointerOverWidget())
            {
                Hide();
            }
        }

        public void ToggleAbove(IntPtr widgetHandle)
        {
            if (_shown && !_showingActivity && !_showingCost)
            {
                Hide();
                return;
            }

            ShowAbove(widgetHandle);
        }

        public void ToggleActivityAbove(IntPtr widgetHandle, string? selectedActivityId = null)
        {
            if (_shown && _showingActivity && _selectedActivityId == selectedActivityId)
            {
                Hide();
                return;
            }

            ShowActivityAbove(widgetHandle, selectedActivityId);
        }

        /// <summary>
        /// Compose the first XAML frame and spin up the acrylic backdrop off-screen once, so the first
        /// real open doesn't flash a black slab while WinUI warms up composition.
        /// </summary>
        public void Prewarm()
        {
            if (_prewarmed)
                return;
            _prewarmed = true;

            EnsureDashboardLoaded();

            var appWindow = GetAppWindow();
            appWindow.Move(new PointInt32(-32000, -32000));
            appWindow.Show(false);
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => { if (!_shown) appWindow.Hide(); });
        }

        private void EnsureDashboardLoaded()
        {
            if (_dashboardLoaded && ContentFrame.CurrentSourcePageType == typeof(DashboardPage))
                return;

            ContentFrame.Navigate(typeof(DashboardPage), true, new SuppressNavigationTransitionInfo());
            _dashboardLoaded = true;
        }

        private void EnsureCostLoaded()
        {
            if (ContentFrame.CurrentSourcePageType == typeof(CostPage))
                return;

            ContentFrame.Navigate(typeof(CostPage), true, new SuppressNavigationTransitionInfo());
        }

        public void ShowAbove(IntPtr widgetHandle)
            => ShowSurfaceAbove(widgetHandle, showActivity: false, selectedActivityId: null);

        public void ShowActivityAbove(IntPtr widgetHandle, string? selectedActivityId = null)
            => ShowSurfaceAbove(widgetHandle, showActivity: true, selectedActivityId);

        private void ShowCostPage()
        {
            if (_showingActivity)
                AgentActivityService.Instance.AcknowledgeAll();
            _selectedActivityId = null;
            _showingActivity = false;
            _showingCost = true;
            ActivityPanel.Visibility = Visibility.Collapsed;
            ContentFrame.Visibility = Visibility.Visible;
            EnsureCostLoaded();
            ScheduleFlyoutBoundsUpdate();
        }

        private void ShowSurfaceAbove(IntPtr widgetHandle, bool showActivity, string? selectedActivityId)
        {
            _widgetHandle = widgetHandle;
            _selectedActivityId = showActivity ? selectedActivityId : null;
            _showingCost = false;
            EnsureDashboardLoaded();
            if (ContentFrame.Content is DashboardPage dashboard)
                dashboard.SetPinHereDisplay(TaskbarWindowTarget.GetDisplayKeyForWindow(widgetHandle));
            if (showActivity)
                ShowActivityPanel();
            else
                ShowProviderDashboard();
            RenderActivity(AgentActivityService.Instance.Snapshot);

            // Sync the strip selection to the provider the taskbar widget is currently showing,
            // so opening the tray highlights/details that provider rather than a stale selection.
            if (UsageCoordinator.Instance.ActiveProvider is { } active)
                _dashboardViewModel.SelectProvider(active);

            PresentAbove();
        }

        private void PresentAbove()
        {
            Log.Debug("[cost-flyout] present above");
            _shown = true;
            UpdateSurfaceControls();
            ApplyFlyoutBounds();
            GetAppWindow().Show();
            ActivateFlyout();
            ScheduleFlyoutBoundsUpdate();

            _ = UpdateAvailabilityService.Instance.CheckSilentlyAsync();
        }

        private void ActivateFlyout()
        {
            Activate();

            // Activity can be opened directly from the injected taskbar island. In that path WinUI's
            // Activate() is not always enough to transfer foreground ownership from Explorer, leaving
            // DesktopAcrylic in its inactive/transparent state until the user clicks the flyout again.
            // Quota normally gets this transfer as a side effect of its provider interaction, so make it
            // explicit for the shared path instead.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
                return;

            var foreground = User32.GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero
                ? 0u
                : User32.GetWindowThreadProcessId(foreground, out _);
            var currentThread = User32.GetCurrentThreadId();
            bool attached = foregroundThread != 0
                && foregroundThread != currentThread
                && User32.AttachThreadInput(foregroundThread, currentThread, true);
            try
            {
                if (User32.GetForegroundWindow() != hwnd)
                    User32.SetForegroundWindow(hwnd);
                User32.SetActiveWindow(hwnd);
                User32.SetFocus(hwnd);
            }
            finally
            {
                if (attached)
                    User32.AttachThreadInput(foregroundThread, currentThread, false);
            }
        }

        private void ShowProviderDashboard()
        {
            if (_showingActivity)
                AgentActivityService.Instance.AcknowledgeAll();
            _showingActivity = false;
            _showingCost = false;
            EnsureDashboardLoaded();
            ActivityPanel.Visibility = Visibility.Collapsed;
            ContentFrame.Visibility = Visibility.Visible;
            ScheduleFlyoutBoundsUpdate();
        }

        private void ShowActivityPanel()
        {
            _showingActivity = true;
            _showingCost = false;
            EnsureDashboardLoaded();
            ActivityPanel.Visibility = Visibility.Visible;
            ContentFrame.Visibility = Visibility.Collapsed;
            UpdateActivityControls();
            ScheduleFlyoutBoundsUpdate();
        }

        private void UpdateActivityControls()
        {
            bool widgetEnabled = WidgetSettingsService.ShowAgentActivityInWidget;
            bool monitoringEnabled = WidgetSettingsService.EnableAgentActivityMonitoring;
            ActivityWidgetButton.IsChecked = widgetEnabled;
            ActivityWidgetButton.IsEnabled = monitoringEnabled;
            ActivityMonitoringButton.IsChecked = monitoringEnabled;
            ToolTipService.SetToolTip(ActivityWidgetButton,
                widgetEnabled ? "Hide agent activity from usage widget" : "Show agent activity in usage widget");
            AutomationProperties.SetName(ActivityWidgetButton,
                widgetEnabled ? "Hide agent activity from usage widget" : "Show agent activity in usage widget");
            ToolTipService.SetToolTip(ActivityMonitoringButton,
                monitoringEnabled ? "Stop monitoring local agent activity" : "Start monitoring local agent activity");
            AutomationProperties.SetName(ActivityMonitoringButton,
                monitoringEnabled ? "Stop monitoring local agent activity" : "Start monitoring local agent activity");
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
            // Order matters: raise Maximum first, then Minimum, then Value.
            slider.Maximum = 100;
            slider.Minimum = 35;
            slider.Value = Math.Clamp(
                Math.Round(WidgetSettingsService.FloatingOpacity * 100),
                35,
                100);
            AutomationProperties.SetName(slider, "Floating acrylic strength");
            AutomationProperties.SetAutomationId(slider, "FlyoutFloatingOpacitySlider");
            ToolTipService.SetToolTip(slider, "Floating Acrylic strength (or scroll the floating window). Lower reveals more of the blurred desktop.");
            slider.ValueChanged += FloatingOpacitySlider_ValueChanged;

            FloatingOpacitySliderHost.Children.Clear();
            FloatingOpacitySliderHost.Children.Add(slider);
            _floatingOpacitySlider = slider;
        }

        private void UpdateSurfaceControls()
        {
            _suppressSurfaceControlEvents = true;
            try
            {
                bool floating = WidgetSettingsService.CurrentSurface == WidgetSurfaceMode.Floating;
                FloatingSurfaceButton.IsChecked = floating;
                FloatingSurfaceButtonLabel.Text = floating ? "Floating" : "Taskbar";
                ToolTipService.SetToolTip(FloatingSurfaceButton,
                    floating
                        ? "Switch back to the taskbar widget"
                        : "Show usage as a floating always-on-top window");
                AutomationProperties.SetName(FloatingSurfaceButton,
                    floating
                        ? "Show usage in the taskbar"
                        : "Show usage as floating window");

                int percent = (int)Math.Round(WidgetSettingsService.FloatingOpacity * 100);
                if (_floatingOpacitySlider is { } slider)
                {
                    slider.Value = percent;
                    slider.IsEnabled = floating;
                    ToolTipService.SetToolTip(slider,
                        floating
                            ? "Floating acrylic strength"
                            : "Acrylic strength applies when floating window mode is on");
                }
                FloatingOpacityLabel.Text = $"{percent}%";
                FloatingOpacityLabel.Opacity = floating ? 0.9 : 0.45;
            }
            finally
            {
                _suppressSurfaceControlEvents = false;
            }
        }

        private void FloatingSurfaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressSurfaceControlEvents)
                return;

            bool wantFloating = FloatingSurfaceButton.IsChecked == true;
            WidgetSettingsService.ApplySurface(
                wantFloating ? WidgetSurfaceMode.Floating : WidgetSurfaceMode.Taskbar);
            UpdateSurfaceControls();
        }

        private void FloatingOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSurfaceControlEvents)
                return;

            int percent = (int)Math.Round(e.NewValue);
            FloatingOpacityLabel.Text = $"{percent}%";
            WidgetSettingsService.ApplyFloatingOpacity(percent / 100d);
        }

        private void OnActivityChanged(AgentActivitySnapshot snapshot)
            => DispatcherQueue.TryEnqueue(() => RenderActivity(snapshot));

        private void RenderActivity(AgentActivitySnapshot snapshot)
        {
            ActivityList.Children.Clear();
            var items = snapshot.ItemsForDisplay(_selectedActivityId);
            bool monitoringEnabled = WidgetSettingsService.EnableAgentActivityMonitoring;
            ActivityEmptyState.Text = monitoringEnabled ? "No recent agent activity" : "Agent activity monitoring is off";
            ActivityEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ActivityScrollViewer.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FrameworkElement? selectedCard = null;

            foreach (var item in items)
            {
                var statusBrush = AgentActivityVisuals.StatusBrush(
                    item.Status,
                    (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]);
                var card = new Border
                {
                    Padding = new Thickness(12, 8, 12, 8),
                    CornerRadius = new CornerRadius(8),
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                };
                var accessibleName = $"{ActivityTitle(item)}, {ActivityProviderLabel(item)}, {item.StatusText}. {item.Step}";
                AutomationProperties.SetName(card, accessibleName);
                AutomationProperties.SetAutomationId(card, $"AgentActivityCard_{ActivityList.Children.Count}");
                if (item.Id == _selectedActivityId)
                {
                    card.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                    card.BorderThickness = new Thickness(2);
                    selectedCard = card;
                    AutomationProperties.SetName(ActivityScrollViewer, $"Selected agent activity. {accessibleName}");
                }
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(CreateActivityProviderVisual(item));
                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(new TextBlock { Text = ActivityTitle(item), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                var metadata = string.IsNullOrWhiteSpace(item.Model)
                    ? $"{ActivityProviderLabel(item)} · {item.StatusText}"
                    : $"{ActivityProviderLabel(item)} · {item.Model} · {item.StatusText}";
                var mutedTextBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                text.Children.Add(new TextBlock { Text = metadata, Foreground = mutedTextBrush, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
                text.Children.Add(new TextBlock { Text = item.Step, Foreground = mutedTextBrush, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
                if (item.SubagentCount > 0)
                    text.Children.Add(new TextBlock { Text = $"▸ {item.SubagentCount} subagents", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                card.Child = row;
                ActivityList.Children.Add(card);
            }

            if (_showingActivity && selectedCard is not null)
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    selectedCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
                    ActivityScrollViewer.Focus(FocusState.Programmatic);
                });
            }
        }

        private static string ActivityProviderDisplayName(ProviderId provider) => provider switch
        {
            ProviderId.ClinePass => "Cline Pass",
            ProviderId.OpenCodeGo => "OpenCode Go",
            ProviderId.Copilot => "GitHub Copilot",
            _ => provider.ToString(),
        };

        private static string ActivityProviderLabel(AgentActivityItem item)
        {
            var provider = ActivityProviderDisplayName(item.Provider);
            return string.IsNullOrWhiteSpace(item.Host) ? provider : $"{provider} through {item.Host}";
        }

        private static string ActivityTitle(AgentActivityItem item)
            => !string.IsNullOrWhiteSpace(item.Host)
                && string.Equals(item.Title, ActivityProviderDisplayName(item.Provider), StringComparison.OrdinalIgnoreCase)
                ? ActivityProviderLabel(item)
                : item.Title;

        private static FrameworkElement CreateActivityProviderVisual(AgentActivityItem item)
        {
            var visual = new Grid { Width = 30, Height = 24 };
            var foreground = AgentActivityVisuals.StatusBrush(
                item.Status,
                (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]);
            var avatar = new ProviderAvatar
            {
                Width = 22,
                Height = 22,
                ProviderId = item.Provider,
                Initial = ActivityProviderDisplayName(item.Provider) is { Length: > 0 } name ? name[0].ToString() : "?",
                ForegroundBrush = foreground,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            visual.Children.Add(avatar);

            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = foreground,
            };
            visual.Children.Add(dot);

            if (string.Equals(item.Host, "T3 Code", StringComparison.OrdinalIgnoreCase)
                && Ui.ParseFreshGeometry(ProviderGlyphs.T3Code) is { } t3Glyph)
            {
                var hostMark = new Path
                {
                    Width = 11,
                    Height = 11,
                    Data = t3Glyph,
                    Stretch = Stretch.Uniform,
                    Fill = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                visual.Children.Add(hostMark);
            }

            ToolTipService.SetToolTip(visual, ActivityProviderLabel(item));
            Grid.SetColumn(visual, 0);
            return visual;
        }

        private void ActivityButton_Click(object sender, RoutedEventArgs e)
        {
            if (_showingActivity)
                ShowProviderDashboard();
            else
                ShowActivityPanel();
        }

        private void CostButton_Click(object sender, RoutedEventArgs e)
        {
            if (_showingCost)
                ShowProviderDashboard();
            else
                ShowCostPage();
        }

        private void ActivityWidgetButton_Click(object sender, RoutedEventArgs e)
        {
            WidgetSettingsService.ApplyShowAgentActivityInWidget(
                !WidgetSettingsService.ShowAgentActivityInWidget);
            UpdateActivityControls();
        }

        private void ActivityMonitoringButton_Click(object sender, RoutedEventArgs e)
        {
            WidgetSettingsService.ApplyEnableAgentActivityMonitoring(
                !WidgetSettingsService.EnableAgentActivityMonitoring);
            UpdateActivityControls();
            RenderActivity(AgentActivityService.Instance.Snapshot);
        }

        private void RegisterWindowSizeHooks()
        {
            if (_sizeHooksRegistered)
                return;

            _sizeHooksRegistered = true;
            if (Root.XamlRoot is { } xamlRoot)
            {
                _lastObservedScale = xamlRoot.RasterizationScale;
                xamlRoot.Changed += (_, _) =>
                {
                    double scale = xamlRoot.RasterizationScale;
                    if (Math.Abs(scale - _lastObservedScale) <= 0.001)
                        return;

                    _lastObservedScale = scale;
                    ScheduleFlyoutBoundsUpdate();
                };
            }
        }

        private void ScheduleFlyoutBoundsUpdate()
        {
            if (!_shown)
                return;

            if (_boundsUpdateTimer is null)
            {
                DispatcherQueue.TryEnqueue(ApplyFlyoutBounds);
                return;
            }

            _boundsUpdateTimer.Interval = BoundsCoalesceDelay;
            _boundsUpdateTimer.Stop();
            _boundsUpdateTimer.Start();
        }

        private void ApplyFlyoutBounds()
        {
            if (!_shown || _widgetHandle == IntPtr.Zero || _applyingBounds)
                return;

            _applyingBounds = true;
            try
            {
                var scale = Root.XamlRoot?.RasterizationScale ?? GetWindowScale();
                int w = WindowDpi.ToPhysical(
                    FlyoutLayout.ComputeLogicalWidth(_stripIconCount, _dashboardViewModel.DetailContentWidth),
                    scale);
                int h = WindowDpi.ToPhysical(
                    FlyoutLayout.ComputeLogicalHeight(_dashboardViewModel.DetailContentHeight),
                    scale);

                if (!User32.GetWindowRect(_widgetHandle, out RECT wr))
                    return;

                int gap = WindowDpi.ToPhysical(8, scale);

                // Confine to the monitor that hosts the widget so it never straddles a display
                // (issue #10). Prefer above the anchor; flip below when the top would crush height
                // (floating window near the top of the screen).
                RECT work;
                if (!TryGetWorkArea(_widgetHandle, out work))
                {
                    work = new RECT
                    {
                        left = 0,
                        top = 0,
                        right = Math.Max(w, wr.right),
                        bottom = Math.Max(h + wr.bottom, wr.bottom + h),
                    };
                }

                var placement = FlyoutLayout.ComputePlacement(
                    wr.left, wr.top, wr.right, wr.bottom,
                    work.left, work.top, work.right, work.bottom,
                    w, h, gap);

                var bounds = new RectInt32(
                    placement.X, placement.Y, placement.Width, placement.Height);
                if (_lastAppliedBounds is { } last
                    && last.X == bounds.X
                    && last.Y == bounds.Y
                    && last.Width == bounds.Width
                    && last.Height == bounds.Height)
                    return;

                _lastAppliedBounds = bounds;
                GetAppWindow().MoveAndResize(bounds);
            }
            finally
            {
                _applyingBounds = false;
            }
        }

        private double GetWindowScale()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = User32.GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96d : 1d;
        }

        // Work area (taskbar-excluded) of the monitor hosting the given window, in physical pixels.
        private static bool TryGetWorkArea(IntPtr hwnd, out RECT work)
        {
            work = default;
            if (hwnd == IntPtr.Zero)
                return false;

            var monitor = User32.MonitorFromWindow(hwnd, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;

            var info = MONITORINFO.Create();
            if (!User32.GetMonitorInfo(monitor, ref info))
                return false;

            work = info.rcWork;
            return work.right > work.left && work.bottom > work.top;
        }

        public void Hide()
        {
            if (!_shown) return;
            Log.Debug($"[cost-flyout] hide showingCost={_showingCost}");
            _shown = false;
            _lastAppliedBounds = null;
            GetAppWindow().Hide();
            if (_showingActivity)
                AgentActivityService.Instance.AcknowledgeAll();
        }

        private void DashboardCards_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if ((e.Action == NotifyCollectionChangedAction.Replace
                    || e.Action == NotifyCollectionChangedAction.Move)
                && ProviderStripStillMatchesCards())
            {
                SyncProviderStripSelection(animate: true);
                return;
            }

            RebuildProviderStrip();
        }

        private void DashboardSelectedCardChanged(ProviderCardViewModel? card)
        {
            // Option A: the flyout is sized to the tallest provider and grows only. Switching
            // providers leaves bounds unchanged (ApplyFlyoutBounds early-returns on equal bounds),
            // so the content cross-fades inside a fixed frame with no native resize.
            SyncProviderStripSelection(animate: true);
            ScheduleFlyoutBoundsUpdate();
        }

        private void DashboardDetailContentWidthChanged(double _)
            => ScheduleFlyoutBoundsUpdate();

        private void DashboardDetailContentHeightChanged(double _)
            => ScheduleFlyoutBoundsUpdate();

        private void RebuildProviderStrip()
        {
            ProviderStrip.Children.Clear();
            _providerStripItems.Clear();

            _stripIconCount = 0;
            foreach (var card in _dashboardViewModel.Cards)
            {
                ProviderStrip.Children.Add(CreateStripButton(card));
                _stripIconCount++;
            }

            SyncProviderStripSelection(animate: false);
            ScheduleFlyoutBoundsUpdate();
        }

        private Button CreateStripButton(ProviderCardViewModel card)
        {
            var icon = new ProviderAvatar
            {
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ProviderId = card.ProviderId,
                Initial = card.DisplayName.Length > 0 ? card.DisplayName[0].ToString() : "?",
                ForegroundBrush = GetSelectionBrush(isSelected: false),
                Opacity = 0.78,
            };
            var indicator = new Border
            {
                Width = 24,
                Height = 3,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = GetSelectionBrush(isSelected: true),
                Opacity = 0,
            };

            // Pin mark, top-right of the glyph. The strip is icons only, so without it there is no way to
            // tell which providers are pinned to the taskbar without opening each one.
            var pin = new FontIcon
            {
                Glyph = PinGlyph,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Foreground = GetSelectionBrush(isSelected: true),
                Visibility = Visibility.Collapsed,
            };

            var buttonContent = new Grid
            {
                Width = FlyoutLayout.IconButtonWidth,
                Height = FlyoutLayout.IconButtonWidth,
            };
            buttonContent.Children.Add(icon);
            buttonContent.Children.Add(indicator);
            buttonContent.Children.Add(pin);

            var button = new Button
            {
                Width = FlyoutLayout.IconButtonWidth,
                Height = FlyoutLayout.IconButtonWidth,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                Tag = card.ProviderId,
                Content = buttonContent,
            };
            button.Click += ProviderStripButton_Click;
            AutomationProperties.SetName(button, card.DisplayName);
            AutomationProperties.SetAutomationId(button, $"FlyoutProvider{card.ProviderId}Button");

            _providerStripItems[card.ProviderId] = new FlyoutProviderStripItem(icon, indicator, pin);
            ApplyStripPin(card.ProviderId, button, card.DisplayName);
            return button;
        }

        // Segoe Fluent Icons "Pin".
        private const string PinGlyph = "";

        private void ApplyStripPin(ProviderId id, Button button, string displayName)
        {
            bool pinned = WidgetSettingsService.IsProviderPinned(id);
            if (_providerStripItems.TryGetValue(id, out var item))
                item.Pin.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;

            string? fixedDisplay = WidgetSettingsService.GetPinnedProviderDisplay(id);
            string pinDescription = fixedDisplay is null
                ? "pinned — follows app screen"
                : $"pinned to {TaskbarWindowTarget.GetDisplayLabel(fixedDisplay)}";
            ToolTipService.SetToolTip(button, pinned ? $"{displayName} — {pinDescription}" : displayName);
        }

        /// <summary>
        /// Refreshes the pin marks in place. Pins change from the dashboard card, from Settings, and when
        /// the pin budget auto-unpins one, and the flyout may be open while any of those happen.
        /// </summary>
        private void SyncProviderStripPins()
        {
            foreach (var child in ProviderStrip.Children)
            {
                if (child is not Button { Tag: ProviderId id } button)
                    continue;

                var card = _dashboardViewModel.Cards.FirstOrDefault(c => c.ProviderId == id);
                ApplyStripPin(id, button, card?.DisplayName ?? id.ToString());
            }
        }

        private void ProviderStripButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ProviderId providerId })
            {
                ShowProviderDashboard();
                _dashboardViewModel.SelectProvider(providerId);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            if (Application.Current is App app)
                app.ShowSettings();
        }

        private void SyncProviderStripSelection(bool animate = false)
        {
            var selected = _dashboardViewModel.SelectedCard?.ProviderId;
            foreach (var pair in _providerStripItems)
            {
                bool isSelected = selected is ProviderId id && id == pair.Key;
                ApplyIconBrush(pair.Value.Icon, isSelected);
                double iconOpacity = isSelected ? 1 : 0.78;
                double indicatorOpacity = isSelected ? 1 : 0;
                if (animate)
                {
                    AnimateOpacity(pair.Value.Icon, iconOpacity, 140);
                    AnimateOpacity(pair.Value.Indicator, indicatorOpacity, 160);
                }
                else
                {
                    pair.Value.Icon.Opacity = iconOpacity;
                    pair.Value.Indicator.Opacity = indicatorOpacity;
                }
            }
        }

        private static void ApplyIconBrush(ProviderAvatar icon, bool isSelected)
            => icon.ForegroundBrush = GetSelectionBrush(isSelected);

        private static void AnimateOpacity(UIElement target, double to, int milliseconds)
        {
            if (Math.Abs(target.Opacity - to) <= 0.001)
                return;

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                From = target.Opacity,
                To = to,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private bool ProviderStripStillMatchesCards()
        {
            if (_providerStripItems.Count != _dashboardViewModel.Cards.Count)
                return false;

            foreach (var card in _dashboardViewModel.Cards)
            {
                if (!_providerStripItems.ContainsKey(card.ProviderId))
                    return false;
            }

            return true;
        }

        private static Brush GetSelectionBrush(bool isSelected) => isSelected
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        private AppWindow GetAppWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        }

        private bool IsPointerOverWidget()
        {
            if (_widgetHandle == IntPtr.Zero)
                return false;
            if (!User32.GetCursorPos(out var point))
                return false;
            if (!User32.GetWindowRect(_widgetHandle, out var rect))
                return false;

            return point.x >= rect.left
                && point.x <= rect.right
                && point.y >= rect.top
                && point.y <= rect.bottom;
        }

        private sealed class FlyoutProviderStripItem
        {
            public FlyoutProviderStripItem(ProviderAvatar icon, Border indicator, FontIcon pin)
            {
                Icon = icon;
                Indicator = indicator;
                Pin = pin;
            }

            public ProviderAvatar Icon { get; }
            public Border Indicator { get; }
            public FontIcon Pin { get; }
        }
    }
}
