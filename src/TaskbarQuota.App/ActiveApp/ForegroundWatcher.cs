using System;
using TaskbarQuota.Interop;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Raises <see cref="ForegroundChanged"/> the moment Windows switches the foreground window, so the
    /// focus-follows-provider widget reacts on the switch itself instead of on the next 500 ms detect
    /// tick. Must be created on a thread with a message pump (the UI thread): WINEVENT_OUTOFCONTEXT
    /// callbacks are delivered through that thread's message queue.
    /// </summary>
    internal sealed class ForegroundWatcher : IDisposable
    {
        private readonly User32.WinEventProc _callback;
        private IntPtr _hook;
        private bool _disposed;

        public event Action? ForegroundChanged;

        public ForegroundWatcher()
        {
            // Held in a field: the delegate is passed to native code, and a collected callback would take
            // the process down the first time the user alt-tabs.
            _callback = OnWinEvent;
        }

        public void Start()
        {
            if (_hook != IntPtr.Zero || _disposed)
                return;

            _hook = User32.SetWinEventHook(
                User32.EVENT_SYSTEM_FOREGROUND,
                User32.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _callback,
                idProcess: 0,
                idThread: 0,
                User32.WINEVENT_OUTOFCONTEXT);

            if (_hook == IntPtr.Zero)
                Diagnostics.Log.Warning("[focus] foreground hook could not be installed; falling back to the detect tick");
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            // idObject/idChild filter out the per-control accessibility noise that shares this event id.
            if (eventType != User32.EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero || idObject != 0)
                return;

            try
            {
                ForegroundChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "[focus] foreground change handler failed");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_hook == IntPtr.Zero)
                return;

            try { User32.UnhookWinEvent(_hook); }
            catch { }
            _hook = IntPtr.Zero;
        }
    }
}
