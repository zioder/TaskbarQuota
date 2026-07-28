using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

                // A failed redirect means the activation went nowhere. Launching normally costs a
                // duplicate widget, but exiting here would make the click do nothing at all.
                return TryRedirectActivationTo(activationArguments, keyInstance);
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

        /// <summary>Hands this process's activation to <paramref name="keyInstance"/>.
        /// Returns false when the activation was not delivered, so the caller can fall back to
        /// launching normally rather than exiting and losing it.</summary>
        private static bool TryRedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
        {
            var redirectCompleted = CreateEvent(IntPtr.Zero, true, false, null);
            if (redirectCompleted == IntPtr.Zero)
            {
                Log.Warning($"CreateEvent failed while redirecting activation (win32 error {Marshal.GetLastWin32Error()})");
                return false;
            }

            // Read after the wait, which the SetEvent below happens-before. Reading the task's own
            // state would race: the event is signalled before the task is marked completed. Boxed and
            // accessed through Volatile so the write is published even without that ordering.
            var redirectFailure = new StrongBox<Exception?>(null);
            bool signalled = false;

            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await keyInstance.RedirectActivationToAsync(args);
                    }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref redirectFailure.Value, ex);
                    }
                    finally
                    {
                        SetEvent(redirectCompleted);
                    }
                });

                // The redirect must complete while this thread pumps COM messages, otherwise the
                // cross-process call deadlocks. CoWaitForMultipleObjects keeps the STA pumping.
                // Bounded, so a wedged key instance can't hang this process indefinitely.
                var waitResult = CoWaitForMultipleObjects(
                    CWMO_DEFAULT, RedirectTimeoutMilliseconds, 1, [redirectCompleted], out _);

                signalled = waitResult == WAIT_OBJECT_0;
                if (!signalled)
                {
                    Log.Warning($"Activation redirect did not complete within {RedirectTimeoutMilliseconds}ms (result 0x{waitResult:X8})");
                    return false;
                }

                if (Volatile.Read(ref redirectFailure.Value) is { } failure)
                {
                    Log.Warning(failure, "Activation redirect to the running instance failed");
                    return false;
                }

                return true;
            }
            finally
            {
                // Only close once the redirect task has signalled. On the timeout path that task is
                // still running and about to call SetEvent, which would then signal a closed handle —
                // or, once the value is recycled, an unrelated object. Leaking one event handle in a
                // process that is seconds from either exiting or starting the app is the cheaper bug.
                if (signalled)
                    CloseHandle(redirectCompleted);
            }
        }

        private const uint CWMO_DEFAULT = 0;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint RedirectTimeoutMilliseconds = 10_000;

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
