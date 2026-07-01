using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using TaskbarQuota.Controls;
using TaskbarQuota.Interop;
using TaskbarQuota.Services;
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
        private bool _sizeHooksRegistered;
        private bool _applyingBounds;
        private DispatcherQueueTimer? _boundsUpdateTimer;
        private RectInt32? _lastAppliedBounds;
        private double _lastObservedScale = -1;
        private readonly DashboardViewModel _dashboardViewModel;
        private readonly Dictionary<ProviderId, FlyoutProviderStripItem> _providerStripItems = new();
        private int _stripIconCount;
        private static readonly TimeSpan BoundsCoalesceDelay = TimeSpan.FromMilliseconds(80);

        public bool IsShown => _shown;

        public FlyoutWindow()
        {
            InitializeComponent();
            _dashboardViewModel = DashboardPage.SharedViewModel ?? new DashboardViewModel(DispatcherQueue);
            DashboardPage.SharedViewModel = _dashboardViewModel;
            _dashboardViewModel.Cards.CollectionChanged += DashboardCards_CollectionChanged;
            _dashboardViewModel.SelectedCardChanged += DashboardSelectedCardChanged;
            _dashboardViewModel.DetailContentWidthChanged += DashboardDetailContentWidthChanged;
            _dashboardViewModel.DetailContentHeightChanged += DashboardDetailContentHeightChanged;
            ProviderStrip.Loaded += (_, _) => RebuildProviderStrip();

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
            GetAppWindow().SetPresenter(presenter);

            Activated += OnActivated;
        }

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated
                && User32.GetForegroundWindow() != _widgetHandle
                && !IsPointerOverWidget())
            {
                Hide();
            }
        }

        public void ToggleAbove(IntPtr widgetHandle)
        {
            if (_shown) { Hide(); return; }
            ShowAbove(widgetHandle);
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
            if (_dashboardLoaded)
                return;

            ContentFrame.Navigate(typeof(DashboardPage), true, new SuppressNavigationTransitionInfo());
            _dashboardLoaded = true;
        }

        public void ShowAbove(IntPtr widgetHandle)
        {
            _widgetHandle = widgetHandle;
            EnsureDashboardLoaded();

            // Sync the strip selection to the provider the taskbar widget is currently showing,
            // so opening the tray highlights/details that provider rather than a stale selection.
            if (UsageCoordinator.Instance.ActiveProvider is { } active)
                _dashboardViewModel.SelectProvider(active);

            _shown = true;
            ApplyFlyoutBounds();
            GetAppWindow().Show();
            Activate();
            ScheduleFlyoutBoundsUpdate();

            _ = UpdateAvailabilityService.Instance.CheckSilentlyAsync();
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
                int maxHeight = Math.Max(WindowDpi.ToPhysical(320, scale), wr.top - gap);
                h = Math.Min(h, maxHeight);

                // Right-align the flyout to the widget, floating just above the taskbar.
                int x = wr.right - w;
                int y = wr.top - h - gap;

                // Confine the flyout to the monitor that hosts the widget so it never straddles a
                // monitor boundary on multi-display setups (issue #10).
                if (TryGetWorkArea(_widgetHandle, out RECT work))
                {
                    w = Math.Min(w, work.right - work.left);
                    h = Math.Min(h, work.bottom - work.top);
                    x = Math.Clamp(wr.right - w, work.left, work.right - w);
                    y = Math.Clamp(y, work.top, work.bottom - h);
                }
                else
                {
                    if (y < 0) y = 0;
                    if (x < 0) x = 0;
                }

                var bounds = new RectInt32(x, y, w, h);
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
            _shown = false;
            _lastAppliedBounds = null;
            GetAppWindow().Hide();
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

            var buttonContent = new Grid
            {
                Width = FlyoutLayout.IconButtonWidth,
                Height = FlyoutLayout.IconButtonWidth,
            };
            buttonContent.Children.Add(icon);
            buttonContent.Children.Add(indicator);

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
            ToolTipService.SetToolTip(button, card.DisplayName);
            AutomationProperties.SetName(button, card.DisplayName);
            AutomationProperties.SetAutomationId(button, $"FlyoutProvider{card.ProviderId}Button");

            _providerStripItems[card.ProviderId] = new FlyoutProviderStripItem(icon, indicator);
            return button;
        }

        private void ProviderStripButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ProviderId providerId })
                _dashboardViewModel.SelectProvider(providerId);
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
            public FlyoutProviderStripItem(ProviderAvatar icon, Border indicator)
            {
                Icon = icon;
                Indicator = indicator;
            }

            public ProviderAvatar Icon { get; }
            public Border Indicator { get; }
        }
    }
}
