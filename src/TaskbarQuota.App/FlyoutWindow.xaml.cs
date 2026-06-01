using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI;
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
        private bool _dashboardLoaded;
        private bool _sizeHooksRegistered;
        private readonly DashboardViewModel _dashboardViewModel;
        private readonly Dictionary<ProviderId, FlyoutProviderStripItem> _providerStripItems = new();

        public bool IsShown => _shown;

        public FlyoutWindow()
        {
            InitializeComponent();
            _dashboardViewModel = DashboardPage.SharedViewModel ?? new DashboardViewModel(DispatcherQueue);
            DashboardPage.SharedViewModel = _dashboardViewModel;
            _dashboardViewModel.Cards.CollectionChanged += DashboardCards_CollectionChanged;
            _dashboardViewModel.SelectedCardChanged += DashboardSelectedCardChanged;
            ProviderStrip.Loaded += (_, _) => RebuildProviderStrip();

            SystemBackdrop = new DesktopAcrylicBackdrop();
            ThemeService.Register(Root);
            Root.Loaded += (_, _) => RegisterWindowSizeHooks();

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

        public void ShowAbove(IntPtr widgetHandle)
        {
            _widgetHandle = widgetHandle;
            if (!_dashboardLoaded)
            {
                ContentFrame.Navigate(typeof(DashboardPage), null, new SuppressNavigationTransitionInfo());
                _dashboardLoaded = true;
            }

            _shown = true;
            ApplyFlyoutBounds();
            GetAppWindow().Show();
            Activate();
            ApplyFlyoutBounds();
        }

        private void RegisterWindowSizeHooks()
        {
            if (_sizeHooksRegistered)
                return;

            _sizeHooksRegistered = true;
            if (Root.XamlRoot is { } xamlRoot)
                xamlRoot.Changed += (_, _) => ApplyFlyoutBounds();
        }

        private void ApplyFlyoutBounds()
        {
            if (!_shown || _widgetHandle == IntPtr.Zero)
                return;

            var scale = Root.XamlRoot?.RasterizationScale ?? GetWindowScale();
            int w = WindowDpi.ToPhysical(FlyoutLayout.LogicalWidth, scale);
            int h = WindowDpi.ToPhysical(FlyoutLayout.LogicalHeight, scale);

            if (!User32.GetWindowRect(_widgetHandle, out RECT wr))
                return;

            int x = wr.right - w;
            int y = wr.top - h - WindowDpi.ToPhysical(8, scale);
            if (y < 0) y = 0;
            if (x < 0) x = 0;

            GetAppWindow().MoveAndResize(new RectInt32(x, y, w, h));
        }

        private double GetWindowScale()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = User32.GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96d : 1d;
        }

        public void Hide()
        {
            if (!_shown) return;
            _shown = false;
            GetAppWindow().Hide();
        }

        private void DashboardCards_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => RebuildProviderStrip();

        private void DashboardSelectedCardChanged(ProviderCardViewModel? card)
            => SyncProviderStripSelection();

        private void RebuildProviderStrip()
        {
            ProviderStrip.Children.Clear();
            _providerStripItems.Clear();

            foreach (var card in _dashboardViewModel.Cards)
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
                    Width = 48,
                    Height = 48,
                };
                buttonContent.Children.Add(icon);
                buttonContent.Children.Add(indicator);

                var button = new Button
                {
                    Width = 48,
                    Height = 48,
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
                ProviderStrip.Children.Add(button);
            }

            SyncProviderStripSelection();
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

        private void SyncProviderStripSelection()
        {
            var selected = _dashboardViewModel.SelectedCard?.ProviderId;
            foreach (var pair in _providerStripItems)
            {
                bool isSelected = selected is ProviderId id && id == pair.Key;
                ApplyIconBrush(pair.Value.Icon, isSelected);
                pair.Value.Indicator.Opacity = isSelected ? 1 : 0;
            }
        }

        private static void ApplyIconBrush(ProviderAvatar icon, bool isSelected)
            => icon.ForegroundBrush = GetSelectionBrush(isSelected);

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
