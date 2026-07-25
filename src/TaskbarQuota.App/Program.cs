using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota
{
    /// <summary>
    /// Replaces the XAML-generated entry point so the app can enforce a single instance.
    /// WinUI 3 desktop apps are not single-instance by default: every activation (Start menu,
    /// Microsoft Store "Open", a second double-click) spawns a new process, and each process
    /// injects its own taskbar widget and tray icon. The key instance registered here owns the
    /// widget; later launches redirect their activation to it and exit.
    /// </summary>
    public static class Program
    {
        /// <summary>Identifies the single instance that owns the taskbar widget. Per-user by design:
        /// AppInstance keys are scoped to the session, so different users get their own widget.</summary>
        private const string SingleInstanceKey = "TaskbarQuota.MainInstance";

        [STAThread]
        private static int Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            if (RedirectToExistingInstance())
                return 0;

            Microsoft.UI.Xaml.Application.Start(initializationParameters =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });

            return 0;
        }

        /// <summary>Returns true when this process handed its activation to the already running
        /// instance and should exit without creating an <see cref="App"/>.</summary>
        private static bool RedirectToExistingInstance()
        {
            try
            {
                var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
                var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

                if (keyInstance.IsCurrent)
                {
                    keyInstance.Activated += OnKeyInstanceActivated;
                    return false;
                }

                RedirectActivationTo(activationArguments, keyInstance);
                return true;
            }
            catch (Exception ex)
            {
                // Never block startup on the single-instance machinery; a duplicate widget is
                // better than an app that refuses to launch.
                Log.Warning(ex, "Single-instance registration failed; continuing as a new instance");
                return false;
            }
        }

        private static void OnKeyInstanceActivated(object? sender, AppActivationArguments args)
        {
            try
            {
                App.HandleRedirectedActivation(GetLaunchArguments(args));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to handle redirected activation");
            }
        }

        internal static string? GetLaunchArguments(AppActivationArguments args)
            => args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                ? launch.Arguments
                : null;

        private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
        {
            // The redirect must complete while this thread pumps COM messages, otherwise the
            // cross-process call deadlocks. CoWaitForMultipleObjects keeps the STA pumping.
            var redirectCompleted = CreateEvent(IntPtr.Zero, true, false, null);
            _ = Task.Run(async () =>
            {
                try
                {
                    await keyInstance.RedirectActivationToAsync(args);
                }
                finally
                {
                    SetEvent(redirectCompleted);
                }
            });

            _ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, [redirectCompleted], out _);
            CloseHandle(redirectCompleted);
        }

        private const uint CWMO_DEFAULT = 0;
        private const uint INFINITE = 0xFFFFFFFF;

        [DllImport("ole32.dll")]
        private static extern uint CoWaitForMultipleObjects(
            uint flags, uint timeoutMilliseconds, ulong handleCount, IntPtr[] handles, out uint handleIndex);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
