using System;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using TaskbarQuota.Interop;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;
using TaskbarQuota.Views;

namespace TaskbarQuota
{
    public sealed partial class MainWindow : Window
    {
        private const double LogicalWidth = 1120;
        private const double LogicalHeight = 820;

        private readonly DashboardViewModel _dashboardViewModel;
        private DashboardNavigationBinder? _navigationBinder;
        private bool _initialWindowSizeApplied;

        public MainWindow()
        {
            InitializeComponent();
            _dashboardViewModel = DashboardPage.SharedViewModel ?? new DashboardViewModel(DispatcherQueue);
            DashboardPage.SharedViewModel = _dashboardViewModel;
            ApplyFluentChrome();
            ThemeService.Register(Root);
            _navigationBinder = new DashboardNavigationBinder(Nav, ProvidersNavigationItem, _dashboardViewModel);
            Root.SizeChanged += (_, _) => UpdateResponsiveNavigation();
            Root.Loaded += OnRootLoaded;
            Nav.Loaded += (_, _) => _navigationBinder?.ReapplySelection();
            // The window is discarded on close and rebuilt on the next tray open, so the binder has to let
            // go of the static settings event or every reopen leaks one.
            Closed += (_, _) =>
            {
                ThemeService.Unregister(Root);
                _navigationBinder?.Dispose();
                _navigationBinder = null;
            };
            UpdateResponsiveNavigation();
        }

        private void OnRootLoaded(object sender, RoutedEventArgs e)
        {
            Root.Loaded -= OnRootLoaded;
            ApplyInitialWindowSize();
            _navigationBinder?.SetProviderPageActive(false);
            Nav.SelectedItem = CostNavigationItem;
            if (ContentFrame.CurrentSourcePageType != typeof(CostPage))
                ContentFrame.Navigate(typeof(CostPage), null, new SuppressNavigationTransitionInfo());
        }

        private void ApplyInitialWindowSize()
        {
            if (_initialWindowSizeApplied || Root.XamlRoot is null)
                return;

            _initialWindowSizeApplied = true;
            var scale = Root.XamlRoot.RasterizationScale;
            var size = WindowDpi.ToPhysicalSize(LogicalWidth, LogicalHeight, scale);
            GetAppWindow().Resize(ClampToWorkArea(size));
        }

        /// <summary>
        /// Caps the requested outer size to the monitor work area. Without this the fixed logical
        /// height (820) exceeds the workspace at common 125%/150% display scaling, so the window is
        /// taller than the screen and the content is clipped at the top and bottom.
        /// </summary>
        private SizeInt32 ClampToWorkArea(SizeInt32 desired)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var monitor = User32.MonitorFromWindow(hwnd, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            var info = MONITORINFO.Create();
            if (User32.GetMonitorInfo(monitor, ref info))
            {
                int workWidth = info.rcWork.right - info.rcWork.left;
                int workHeight = info.rcWork.bottom - info.rcWork.top;
                desired.Width = Math.Min(desired.Width, Math.Max(workWidth, 1));
                desired.Height = Math.Min(desired.Height, Math.Max(workHeight, 1));
            }
            return desired;
        }

        private void ApplyFluentChrome()
        {
            // Use Mica (an opaque tint) where supported. A translucent backdrop — acrylic in
            // particular — is not re-composited during a live drag-resize, so the uncovered region
            // renders black until the resize finishes. Mica is opaque and never flashes black.
            // Fall back to acrylic only on builds that don't support Mica (Windows 10).
            SystemBackdrop = MicaController.IsSupported()
                ? new MicaBackdrop()
                : new DesktopAcrylicBackdrop();

            // Custom title bar.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var appWindow = GetAppWindow();
            appWindow.Title = "TaskbarQuota";
            appWindow.Closing += MainAppWindow_Closing;
            SetWindowIcon();
            if (appWindow.TitleBar is { } tb)
            {
                tb.ButtonBackgroundColor = Colors.Transparent;
                tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }

        private void SetWindowIcon()
        {
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "TaskBarQuota.ico");
            if (!System.IO.File.Exists(icoPath))
                return;

            try { GetAppWindow().SetIcon(icoPath); } catch { }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var small = User32.LoadImage(IntPtr.Zero, icoPath, User32.IMAGE_ICON, 32, 32, User32.LR_LOADFROMFILE | User32.LR_SHARED);
            var big = User32.LoadImage(IntPtr.Zero, icoPath, User32.IMAGE_ICON, 48, 48, User32.LR_LOADFROMFILE | User32.LR_SHARED);
            if (small != IntPtr.Zero) User32.SendMessage(hwnd, User32.WM_SETICON, User32.ICON_SMALL, small);
            if (big != IntPtr.Zero) User32.SendMessage(hwnd, User32.WM_SETICON, User32.ICON_BIG, big);
        }

        private void UpdateResponsiveNavigation()
        {
            if (Root.ActualWidth < 760)
            {
                Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
                Nav.IsPaneOpen = false;
                Nav.OpenPaneLength = 180;
            }
            else
            {
                Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                Nav.IsPaneOpen = true;
                Nav.OpenPaneLength = 220;
            }
        }

        public void ShowFromTray()
        {
            var appWindow = GetAppWindow();
            appWindow.IsShownInSwitchers = true;
            appWindow.Show();
            Activate();
        }

        public void ShowSettings()
        {
            ShowFromTray();
            _navigationBinder?.SetProviderPageActive(false);
            var info = new SuppressNavigationTransitionInfo();
            ContentFrame.Navigate(typeof(SettingsPage), null, info);
            if (Nav.SettingsItem is not null)
                Nav.SelectedItem = Nav.SettingsItem;
        }

        private void MainAppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (App.IsQuitting)
                return;

            args.Cancel = true;
            sender.IsShownInSwitchers = false;
            sender.Hide();
        }

        private AppWindow GetAppWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        }

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_navigationBinder?.IsSyncing != false)
                return;

            var info = new EntranceNavigationTransitionInfo();
            if (args.IsSettingsSelected)
            {
                _navigationBinder?.SetProviderPageActive(false);
                ContentFrame.Navigate(typeof(SettingsPage), null, info);
                return;
            }

            if (args.SelectedItemContainer is NavigationViewItem { Tag: "cost" })
            {
                _navigationBinder?.SetProviderPageActive(false);
                if (ContentFrame.CurrentSourcePageType != typeof(CostPage))
                    ContentFrame.Navigate(typeof(CostPage), null, info);
                return;
            }

            _navigationBinder?.SetProviderPageActive(true);
            if (_navigationBinder?.SelectFromNavigation(args) == true)
            {
                if (ContentFrame.CurrentSourcePageType != typeof(DashboardPage))
                    ContentFrame.Navigate(typeof(DashboardPage), false, info);
            }
        }
    }
}
