using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarQuota.Interop
{
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

    public static class User32
    {
        public static readonly IntPtr HWND_TOP = new IntPtr(0);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow([In] IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect([In] IntPtr hWnd, [Out] out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsWindow([In] IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll")]
        public static extern bool UnregisterClass([In] string lpClassName, [In, Optional] IntPtr hInstance);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent([In] IntPtr hWndChild, [In] IntPtr hWndNewParent);

        [DllImport("User32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow([In, MarshalAs(UnmanagedType.LPWStr)] string? lpClassName, [In, MarshalAs(UnmanagedType.LPWStr)] string? lpWindowName);

        [DllImport("User32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx([In] IntPtr hwndParent, [In] IntPtr hwndChildAfter, [In, MarshalAs(UnmanagedType.LPWStr)] string? lpClassName, [In, MarshalAs(UnmanagedType.LPWStr)] string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(
           WindowStylesExtended dwExStyle,
           [In, MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
           [In, MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
           WindowStyles dwStyle,
           int x, int y, int nWidth, int nHeight,
           IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, GetAncestorFlags gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        public static extern int GetClassName(IntPtr hwnd, [Out] StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetCursorPos([In] int x, [In] int y);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos([Out] out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow([In] IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern uint SetWindowLong(IntPtr hwnd, int index, uint newStyle);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_SETICON = 0x0080;
        public const IntPtr ICON_SMALL = (IntPtr)0;
        public const IntPtr ICON_BIG = (IntPtr)1;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr LoadImage(
            IntPtr hInst,
            string name,
            uint type,
            int cx,
            int cy,
            uint fuLoad);

        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x00000010;
        public const uint LR_SHARED = 0x00008000;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CallWindowProc([In] IntPtr lpPrevWndFunc, [In] IntPtr hWnd, [In] uint Msg, [In] IntPtr wParam, [In] IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow([In] IntPtr hwnd, MonitorFromFlags dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, MonitorFromFlags dwFlags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo([In] IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "GetMonitorInfo")]
        public static extern bool GetMonitorInfo([In] IntPtr hMonitor, ref MONITORINFOEX lpmi);
    }

    public enum MonitorFromFlags : uint
    {
        MONITOR_DEFAULTTONULL = 0,
        MONITOR_DEFAULTTOPRIMARY = 1,
        MONITOR_DEFAULTTONEAREST = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        public static MONITORINFO Create() => new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;

        public static MONITORINFOEX Create() => new()
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>(),
            szDevice = string.Empty,
        };
    }

    public static class Shell32
    {
        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage([In] AppBarMessage msg, [In, Out] ref APPBARDATA data);
    }

    public static class Kernel32
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string? module);

        [DllImport("kernel32.dll")]
        public static extern IntPtr RegisterApplicationRestart(string? pwzCommandline, ApplicationRestart dwFlags = ApplicationRestart.None);
    }

    [Flags]
    public enum WindowStylesExtended
    {
        Default = 0,
        WS_EX_LAYERED = 0x80000,
    }

    [Flags]
    public enum WindowStyles : uint
    {
        WS_POPUP = 0x80000000,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    public enum GetAncestorFlags
    {
        GA_PARENT = 1,
        GA_ROOT = 2,
        GA_ROOTOWNER = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    public enum AppBarMessage : uint
    {
        ABM_GETTASKBARPOS = 0x00000005
    }

    public enum WindowMessage : uint
    {
        WM_QUERYENDSESSION = 0x0011,
        WM_ENDSESSION = 0x0016,
        WM_SETTINGCHANGE = 0x001A
    }

    [Flags]
    public enum ApplicationRestart
    {
        None = 0,
    }
}
