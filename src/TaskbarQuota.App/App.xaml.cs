using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Taskbar;

namespace TaskbarQuota
{
    public partial class App : Application
    {
        public static DispatcherQueue? Dispatcher { get; private set; }
        public static event Action? Quitting;
        public static bool IsQuitting { get; private set; }

        private MainWindow? _mainWindow;

        public App()
        {
            InitializeComponent();
            UnhandledException += (_, e) => Log.Error(e.Exception, "Unhandled exception");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Dispatcher = DispatcherQueue.GetForCurrentThread();
            AppStorage.MigrateLegacyDataIfNeeded();
            StartupSettingsService.MigrateLegacyStartupEntryIfNeeded();
            Log.Information("TaskbarQuota launching");

            UsageCoordinator.Instance.Start();

            var startupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            startupTimer.Tick += (_, _) =>
            {
                startupTimer.Stop();
                TaskBarManager.Initialize(Dispatcher, ShowMainWindow);
            };
            startupTimer.Start();

            if (!IsWidgetStartup(args.Arguments))
                ShowMainWindow();
        }

        public void ShowMainWindow()
        {
            if (_mainWindow is null)
            {
                _mainWindow = new MainWindow();
                _mainWindow.Closed += (_, _) => _mainWindow = null;
            }
            _mainWindow.ShowFromTray();
        }

        public void ShowSettings()
        {
            if (_mainWindow is null)
            {
                _mainWindow = new MainWindow();
                _mainWindow.Closed += (_, _) => _mainWindow = null;
            }
            _mainWindow.ShowSettings();
        }

        public static void Quit()
        {
            IsQuitting = true;
            Quitting?.Invoke();
            Current.Exit();
        }

        private static bool IsWidgetStartup(string? arguments)
            => StartupSettingsService.IsEnabled
            && !string.IsNullOrWhiteSpace(arguments)
            && arguments.Contains(StartupSettingsService.StartupArgument, StringComparison.OrdinalIgnoreCase);
    }
}
