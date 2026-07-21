using System;
using Microsoft.UI;
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
            _navigationBinder = new DashboardNavigationBinder(Nav, _dashboardViewModel);
            Root.SizeChanged += (_, _) => UpdateResponsiveNavigation();
            Root.Loaded += OnRootLoaded;
            Nav.Loaded += (_, _) => _navigationBinder?.ReapplySelection();
            UpdateResponsiveNavigation();
        }

        private void OnRootLoaded(object sender, RoutedEventArgs e)
        {
            Root.Loaded -= OnRootLoaded;
            ApplyInitialWindowSize();
            ContentFrame.Navigate(typeof(DashboardPage), false, new SuppressNavigationTransitionInfo());
        }

        private void ApplyInitialWindowSize()
        {
            if (_initialWindowSizeApplied || Root.XamlRoot is null)
                return;

            _initialWindowSizeApplied = true;
            var size = WindowDpi.ToPhysicalSize(
                LogicalWidth,
                LogicalHeight,
                Root.XamlRoot.RasterizationScale);
            GetAppWindow().Resize(size);
        }

        private void ApplyFluentChrome()
        {
            // Desktop acrylic works on all supported targets; Mica can fail on some installs.
            SystemBackdrop = new DesktopAcrylicBackdrop();

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
                ContentFrame.Navigate(typeof(SettingsPage), null, info);
                return;
            }
            if (args.SelectedItemContainer is NavigationViewItem { Tag: "Cost" })
            {
                if (ContentFrame.CurrentSourcePageType != typeof(Views.CostPage))
                    ContentFrame.Navigate(typeof(Views.CostPage), null, info);
                return;
            }
            if (_navigationBinder?.SelectFromNavigation(args) == true)
            {
                if (ContentFrame.CurrentSourcePageType != typeof(DashboardPage))
                    ContentFrame.Navigate(typeof(DashboardPage), false, info);
            }
        }
    }
}
