using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;

namespace TaskbarQuota.Taskbar;

internal enum TopologyChangeKind
{
    Display,
    SessionConnect,
    SessionDisconnect,
    SessionUnlock,
    ExplorerRestart,
    Resume,
}

internal readonly record struct TopologyChange(TopologyChangeKind Kind, string Reason)
{
    public bool RequiresHostReset => Kind is
        TopologyChangeKind.SessionConnect or
        TopologyChangeKind.SessionDisconnect or
        TopologyChangeKind.SessionUnlock or
        TopologyChangeKind.ExplorerRestart or
        TopologyChangeKind.Resume;
}

/// <summary>
/// Owns a hidden top-level window so broadcast shell/display messages remain independent of taskbar HWNDs
/// and recovery does not depend on a taskbar-injected HWND surviving
/// Remote Desktop, Explorer restart, sleep, or a display-topology replacement.
/// </summary>
internal sealed class SessionTopologyWatcher : IDisposable
{
    internal const int WtsConsoleConnect = 0x1;
    internal const int WtsConsoleDisconnect = 0x2;
    internal const int WtsRemoteConnect = 0x3;
    internal const int WtsRemoteDisconnect = 0x4;
    internal const int WtsSessionLogon = 0x5;
    internal const int WtsSessionUnlock = 0x8;
    internal const int PbtApmResumeAutomatic = 0x12;

    private readonly string className = $"TaskbarQuotaSessionTopology-{Environment.ProcessId}";
    private readonly WndProc windowProc;
    private readonly uint taskbarCreatedMessage;
    private IntPtr hwnd;
    private bool classRegistered;
    private bool sessionRegistered;
    private bool disposed;

    public event Action<TopologyChange>? Changed;

    public SessionTopologyWatcher()
    {
        windowProc = WindowProc;
        taskbarCreatedMessage = User32.RegisterWindowMessage("TaskbarCreated");
    }

    public void Start()
    {
        if (disposed || hwnd != IntPtr.Zero)
            return;

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            hInstance = Kernel32.GetModuleHandle(null),
            lpfnWndProc = windowProc,
            lpszClassName = className,
        };
        if (User32.RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the session topology window class.");
        classRegistered = true;

        hwnd = User32.CreateWindowEx(
            WindowStylesExtended.Default,
            className,
            "TaskbarQuota session topology watcher",
            WindowStyles.WS_POPUP,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            Kernel32.GetModuleHandle(null),
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the session topology message window.");

        sessionRegistered = WtsApi32.WTSRegisterSessionNotification(
            hwnd,
            WtsApi32.NOTIFY_FOR_THIS_SESSION);
        if (!sessionRegistered)
            Log.Warning(new Win32Exception(Marshal.GetLastWin32Error()), "Could not register for session topology notifications");
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == taskbarCreatedMessage && taskbarCreatedMessage != 0)
            Raise(new(TopologyChangeKind.ExplorerRestart, "Explorer taskbar created"));
        else if (message == User32.WM_DISPLAYCHANGE || message == User32.WM_DEVICECHANGE)
            Raise(new(TopologyChangeKind.Display, "display topology changed"));
        else if (message == User32.WM_WTSSESSION_CHANGE
                 && TryMapSessionChange(wParam.ToInt32(), out var change))
            Raise(change);
        else if (message == User32.WM_POWERBROADCAST
                 && wParam.ToInt32() == PbtApmResumeAutomatic)
            Raise(new(TopologyChangeKind.Resume, "system resumed"));

        return User32.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void Raise(TopologyChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"Topology change handler failed ({change.Reason})");
        }
    }

    internal static bool TryMapSessionChange(int code, out TopologyChange change)
    {
        change = code switch
        {
            WtsConsoleConnect => new(TopologyChangeKind.SessionConnect, "console session connected"),
            WtsRemoteConnect => new(TopologyChangeKind.SessionConnect, "Remote Desktop connected"),
            WtsSessionLogon => new(TopologyChangeKind.SessionConnect, "session logged on"),
            WtsConsoleDisconnect => new(TopologyChangeKind.SessionDisconnect, "console session disconnected"),
            WtsRemoteDisconnect => new(TopologyChangeKind.SessionDisconnect, "Remote Desktop disconnected"),
            WtsSessionUnlock => new(TopologyChangeKind.SessionUnlock, "session unlocked"),
            _ => default,
        };
        return code is WtsConsoleConnect
            or WtsRemoteConnect
            or WtsSessionLogon
            or WtsConsoleDisconnect
            or WtsRemoteDisconnect
            or WtsSessionUnlock;
    }

    internal static TimeSpan RetryDelay(int completedAttempts)
    {
        int exponent = Math.Clamp(completedAttempts, 0, 4);
        return TimeSpan.FromMilliseconds(Math.Min(4000, 250 * (1 << exponent)));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        if (sessionRegistered && hwnd != IntPtr.Zero)
        {
            try { WtsApi32.WTSUnRegisterSessionNotification(hwnd); }
            catch { }
            sessionRegistered = false;
        }
        if (hwnd != IntPtr.Zero)
        {
            try { User32.DestroyWindow(hwnd); }
            catch { }
            hwnd = IntPtr.Zero;
        }
        if (classRegistered)
        {
            try { User32.UnregisterClass(className, Kernel32.GetModuleHandle(null)); }
            catch { }
            classRegistered = false;
        }

        Changed = null;
    }
}

/// <summary>Requires the same non-empty taskbar topology twice before recovery commits to it.</summary>
internal sealed class TopologyStabilityTracker
{
    private string? candidate;
    private int samples;

    public bool Observe(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            Reset();
            return false;
        }

        if (!string.Equals(candidate, signature, StringComparison.Ordinal))
        {
            candidate = signature;
            samples = 1;
            return false;
        }

        return ++samples >= 2;
    }

    public void Reset()
    {
        candidate = null;
        samples = 0;
    }
}
