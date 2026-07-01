using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Services;
using TaskbarQuota.Taskbar;

namespace TaskbarQuota
{
    public partial class App : Application
    {
        public static DispatcherQueue? Dispatcher { get; private set; }
        public static event Action? Quitting;
        public static bool IsQuitting { get; private set; }
        internal const int TaskbarInitializationMaxAttempts = 20;
        private const int TaskbarInitializationInitialDelayMilliseconds = 1500;
        private const int TaskbarInitializationRetryDelayMilliseconds = 2500;

        private MainWindow? _mainWindow;
        private Timer? _taskbarInitializationTimer;
        private int _taskbarInitializationAttempts;
        private int _taskbarInitializationQueued;

        public App()
        {
            InitializeComponent();
            UnhandledException += (_, e) =>
            {
                Log.Error(e.Exception, "Unhandled exception");
                e.Handled = true;
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Dispatcher = DispatcherQueue.GetForCurrentThread();
            AppStorage.MigrateLegacyDataIfNeeded();
            StartupSettingsService.MigrateLegacyStartupEntryIfNeeded();

            Log.Information("TaskbarQuota launching");

            UsageCoordinator.Instance.Start();
            QuotaAlertService.Instance.Start();

            _ = Task.Run(() =>
            {
                ProviderInstallDetector.WarmCliCache();
                ProviderDiscoveryService.SyncInstalledProviderVisibility();
            });

            ScheduleTaskbarInitialization();

            var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            updateTimer.Tick += (_, _) =>
            {
                updateTimer.Stop();
                _ = UpdateAvailabilityService.Instance.CheckSilentlyAsync();
            };
            updateTimer.Start();

            if (!IsWidgetStartup(args.Arguments, Environment.GetCommandLineArgs()))
            {
                try
                {
                    ShowMainWindow();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to open main window on launch");
                }
            }

        }

        public void ShowMainWindow()
        {
            try
            {
                if (_mainWindow is null)
                {
                    _mainWindow = new MainWindow();
                    _mainWindow.Closed += (_, _) => _mainWindow = null;
                }
                _mainWindow.ShowFromTray();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to show main window");
            }
        }

        private void ScheduleTaskbarInitialization()
        {
            _taskbarInitializationTimer?.Dispose();
            _taskbarInitializationAttempts = 0;
            _taskbarInitializationQueued = 0;
            _taskbarInitializationTimer = new Timer(
                _ =>
                {
                    var dispatcher = Dispatcher;
                    if (dispatcher is not null)
                    {
                        if (Interlocked.Exchange(ref _taskbarInitializationQueued, 1) != 0)
                            return;

                        if (dispatcher.TryEnqueue(InitializeTaskbarManager))
                            return;

                        Interlocked.Exchange(ref _taskbarInitializationQueued, 0);
                    }

                    var completedAttempts = Interlocked.Increment(ref _taskbarInitializationAttempts);
                    Log.Warning("Could not enqueue taskbar manager initialization");
                    if (!ShouldRetryTaskbarInitialization(completedAttempts))
                        StopTaskbarInitializationTimer();
                },
                null,
                TimeSpan.FromMilliseconds(TaskbarInitializationInitialDelayMilliseconds),
                TimeSpan.FromMilliseconds(TaskbarInitializationRetryDelayMilliseconds));
        }

        private void InitializeTaskbarManager()
        {
            var completedAttempts = Interlocked.Increment(ref _taskbarInitializationAttempts);

            try
            {
                var dispatcher = Dispatcher;
                if (dispatcher is null)
                {
                    Log.Warning("Taskbar manager initialization skipped because the dispatcher is unavailable");
                    if (!ShouldRetryTaskbarInitialization(completedAttempts))
                        StopTaskbarInitializationTimer();
                    return;
                }

                TaskBarManager.Initialize(dispatcher, ShowMainWindow);
                StopTaskbarInitializationTimer();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Taskbar manager initialization failed");
                if (!ShouldRetryTaskbarInitialization(completedAttempts))
                    StopTaskbarInitializationTimer();
            }
            finally
            {
                Interlocked.Exchange(ref _taskbarInitializationQueued, 0);
            }
        }

        private void StopTaskbarInitializationTimer()
        {
            _taskbarInitializationTimer?.Dispose();
            _taskbarInitializationTimer = null;
            Interlocked.Exchange(ref _taskbarInitializationQueued, 0);
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

        internal static bool ShouldRetryTaskbarInitialization(int completedAttempts)
            => completedAttempts < TaskbarInitializationMaxAttempts;

        internal static bool IsWidgetStartup(string? activationArguments, string[]? commandLineArguments = null)
        {
            if (ContainsStartupArgument(activationArguments))
                return true;

            if (commandLineArguments is not null)
            {
                foreach (var argument in commandLineArguments)
                {
                    if (string.Equals(argument, StartupSettingsService.StartupArgument, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static bool ContainsStartupArgument(string? arguments)
            => !string.IsNullOrWhiteSpace(arguments)
            && arguments.Contains(StartupSettingsService.StartupArgument, StringComparison.OrdinalIgnoreCase);
    }
}
