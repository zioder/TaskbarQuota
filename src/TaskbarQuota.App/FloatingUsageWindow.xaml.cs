using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using TaskbarQuota.AgentActivity;
using TaskbarQuota.Controls;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;
using TaskbarQuota.Usage;

namespace TaskbarQuota;

/// <summary>
/// Semi-transparent always-on-top host for the same compact usage layout as the taskbar widget.
/// Single window, draggable, position persisted under app data.
/// </summary>
public sealed partial class FloatingUsageWindow : Window
{
    private static readonly string PositionPath =
        Path.Combine(AppStorage.AppDataDirectory, "floating-widget-position.txt");

    private const int ChromePaddingLogicalX = 12; // Border padding 6*2
    private const int ChromePaddingLogicalY = 8;  // Border padding 4*2
    private const int DefaultLogicalWidth = 184;
    private const int DefaultLogicalHeight = 48;
    private const int DragThresholdLogicalPx = 4;

    private AppWindow? _appWindow;
    private bool _shown;
    private bool _isDragging;
    private bool _isPointerTracking;
    private bool _suppressNextClick;
    private POINT _pressCursor;
    private PointInt32 _pressWindowPos;
    private int _logicalWidth = DefaultLogicalWidth;
    private int _logicalHeight = DefaultLogicalHeight;
    private bool _hasManualPosition;

    public event Action? Clicked;
    public event Action<AgentActivityItem?>? ActivityClicked;

    public bool IsShown => _shown;
    public bool IsDragging => _isDragging;
    public bool IsAlive
    {
        get
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                return hwnd != IntPtr.Zero && User32.IsWindow(hwnd);
            }
            catch
            {
                return false;
            }
        }
    }

    public IntPtr Handle
    {
        get
        {
            try { return WinRT.Interop.WindowNative.GetWindowHandle(this); }
            catch { return IntPtr.Zero; }
        }
    }

    public Func<ProviderId, UsageResult?>? HydrateProvider
    {
        get => ContentHost.HydrateProvider;
        set => ContentHost.HydrateProvider = value;
    }

    public FloatingUsageWindow()
    {
        InitializeComponent();

        // No SystemBackdrop: acrylic paints an opaque composition surface so brush alpha never
        // shows the desktop. Real transparency comes from WS_EX_LAYERED + SetLayeredWindowAttributes.
        SystemBackdrop = null;
        ThemeService.Register(Root);

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        _appWindow = GetAppWindow();
        _appWindow.IsShownInSwitchers = false;
        _appWindow.SetPresenter(presenter);
        _appWindow.Title = "TaskbarQuota";

        ContentHost.Clicked += OnContentClicked;
        ContentHost.ActivityClicked += item => ActivityClicked?.Invoke(item);
        ContentHost.DesiredSizeChanged += OnDesiredSizeChanged;
        WidgetSettingsService.Changed += OnWidgetSettingsChanged;
        Root.ActualThemeChanged += (_, _) => ApplyChromeOpacity();
        Root.Loaded += (_, _) => ApplyChromeOpacity();

        Root.PointerPressed += Root_PointerPressed;
        Root.PointerMoved += Root_PointerMoved;
        Root.PointerReleased += Root_PointerReleased;
        Root.PointerCanceled += Root_PointerCanceled;
        Root.PointerCaptureLost += Root_PointerCanceled;

        Closed += OnClosed;
        EnsureLayeredWindow();
        ApplyChromeOpacity();
        LoadPositionOrDefault();
        ApplyBounds(forceShow: false);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        ThemeService.Unregister(Root);
        WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
        ContentHost.Clicked -= OnContentClicked;
        Root.PointerPressed -= Root_PointerPressed;
        Root.PointerMoved -= Root_PointerMoved;
        Root.PointerReleased -= Root_PointerReleased;
        Root.PointerCanceled -= Root_PointerCanceled;
        Root.PointerCaptureLost -= Root_PointerCanceled;
        _shown = false;
    }

    private void OnWidgetSettingsChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(ApplyChromeOpacity);

    private void EnsureLayeredWindow()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int ex = GetExtendedStyle(hwnd);
        if ((ex & (int)WindowStylesExtended.WS_EX_LAYERED) == 0)
            SetExtendedStyle(hwnd, ex | (int)WindowStylesExtended.WS_EX_LAYERED);
    }

    private static int GetExtendedStyle(IntPtr hwnd)
        => IntPtr.Size == 8
            ? unchecked((int)User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE).ToInt64())
            : User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);

    private static void SetExtendedStyle(IntPtr hwnd, int style)
    {
        if (IntPtr.Size == 8)
            User32.SetWindowLongPtr(hwnd, User32.GWL_EXSTYLE, (IntPtr)style);
        else
            User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, unchecked((uint)style));
    }

    /// <summary>
    /// Applies user opacity via true layered-window alpha so the desktop shows through.
    /// XAML brush alpha alone cannot pierce WinUI's opaque composition surface.
    /// </summary>
    private void ApplyChromeOpacity()
    {
        EnsureLayeredWindow();

        double opacity = WidgetSettingsService.FloatingOpacity;
        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 1, 255);

        var hwnd = Handle;
        if (hwnd != IntPtr.Zero)
            User32.SetLayeredWindowAttributes(hwnd, 0, alpha, User32.LWA_ALPHA);

        // Solid chrome fill (window-level alpha handles see-through). Keeps shape readable
        // without relying on theme resources that fight layered composition.
        bool light = ThemeService.IsLightChrome(Root);
        byte r = light ? (byte)248 : (byte)28;
        byte g = light ? (byte)248 : (byte)28;
        byte b = light ? (byte)248 : (byte)28;
        ChromeFill.Background = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        ChromeFill.Opacity = 1;
    }

    private void OnContentClicked()
    {
        if (_suppressNextClick)
        {
            _suppressNextClick = false;
            return;
        }
        Clicked?.Invoke();
    }

    private void OnDesiredSizeChanged(int logicalWidth, int logicalHeight)
    {
        _logicalWidth = Math.Max(DefaultLogicalWidth, logicalWidth + ChromePaddingLogicalX);
        _logicalHeight = Math.Max(DefaultLogicalHeight, logicalHeight + ChromePaddingLogicalY);
        ApplyBounds(forceShow: _shown);
    }

    public void SetDisplayProviders(IReadOnlyList<ProviderId> providers, ProviderId? activeProvider)
        => ContentHost.SetDisplayProviders(providers, activeProvider);

    public void ApplyResult(UsageResult result, bool force = false)
        => ContentHost.ApplyResult(result, force);

    public void SetActivitySnapshot(AgentActivitySnapshot snapshot)
        => ContentHost.SetActivitySnapshot(snapshot);

    public void SetVisible(bool visible)
    {
        if (_appWindow is null)
            return;

        if (visible)
        {
            ApplyBounds(forceShow: true);
            if (!_appWindow.IsVisible)
                _appWindow.Show(false);
            _shown = true;
            // Layered alpha is sometimes reset after Show; re-apply.
            ApplyChromeOpacity();
            // Hide/show and settings thrash can leave tiles at Opacity 0 while still "assigned".
            ContentHost.EnsureVisibleContent();
        }
        else
        {
            if (_appWindow.IsVisible)
                _appWindow.Hide();
            _shown = false;
        }
    }

    public void StartDragging()
    {
        // Tray "Move" — nudge the user by ensuring the window is visible; they can then drag.
        SetVisible(true);
        try
        {
            var hwnd = Handle;
            if (hwnd != IntPtr.Zero)
                User32.SetForegroundWindow(hwnd);
        }
        catch { }
    }

    public void ResetPosition()
    {
        _hasManualPosition = false;
        try
        {
            if (File.Exists(PositionPath))
                File.Delete(PositionPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not clear floating widget position");
        }

        PlaceAtDefault();
        ApplyBounds(forceShow: _shown);
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
            return;

        _isPointerTracking = true;
        _isDragging = false;
        _suppressNextClick = false;
        if (!User32.GetCursorPos(out _pressCursor))
            _pressCursor = default;
        if (_appWindow is not null)
            _pressWindowPos = _appWindow.Position;
        Root.CapturePointer(e.Pointer);
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerTracking || _appWindow is null)
            return;

        if (!User32.GetCursorPos(out var cursor))
            return;

        int dx = cursor.x - _pressCursor.x;
        int dy = cursor.y - _pressCursor.y;
        double scale = Root.XamlRoot?.RasterizationScale ?? GetWindowScale();
        int threshold = Math.Max(1, (int)Math.Round(DragThresholdLogicalPx * scale));

        if (!_isDragging)
        {
            if (Math.Abs(dx) < threshold && Math.Abs(dy) < threshold)
                return;

            _isDragging = true;
            _suppressNextClick = true;
        }

        int newX = _pressWindowPos.X + dx;
        int newY = _pressWindowPos.Y + dy;
        ClampToWorkArea(ref newX, ref newY, _appWindow.Size.Width, _appWindow.Size.Height);
        _appWindow.Move(new PointInt32(newX, newY));
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerTracking)
            return;

        bool wasDragging = _isDragging;
        _isPointerTracking = false;
        _isDragging = false;
        try { Root.ReleasePointerCapture(e.Pointer); } catch { }

        if (wasDragging && _appWindow is not null)
        {
            SavePosition(_appWindow.Position.X, _appWindow.Position.Y);
            _hasManualPosition = true;
        }
    }

    private void Root_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPointerTracking = false;
        _isDragging = false;
        try { Root.ReleasePointerCapture(e.Pointer); } catch { }
    }

    private void LoadPositionOrDefault()
    {
        try
        {
            if (File.Exists(PositionPath))
            {
                var parts = File.ReadAllText(PositionPath).Split(',');
                if (parts.Length >= 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                {
                    _hasManualPosition = true;
                    // Bounds applied after size is known; stash as window position first.
                    if (_appWindow is not null)
                        _appWindow.Move(new PointInt32(x, y));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load floating widget position");
        }

        PlaceAtDefault();
    }

    private void PlaceAtDefault()
    {
        if (_appWindow is null)
            return;

        double scale = GetWindowScale();
        int w = WindowDpi.ToPhysical(_logicalWidth, scale);
        int h = WindowDpi.ToPhysical(_logicalHeight, scale);

        // Primary work area, bottom-right (tray-adjacent), with a small inset.
        var monitor = User32.MonitorFromPoint(new POINT { x = 0, y = 0 }, MonitorFromFlags.MONITOR_DEFAULTTOPRIMARY);
        var info = MONITORINFO.Create();
        if (monitor != IntPtr.Zero && User32.GetMonitorInfo(monitor, ref info))
        {
            int margin = WindowDpi.ToPhysical(12, scale);
            int x = info.rcWork.right - w - margin;
            int y = info.rcWork.bottom - h - margin;
            _appWindow.Move(new PointInt32(x, y));
            return;
        }

        _appWindow.Move(new PointInt32(100, 100));
    }

    private void ApplyBounds(bool forceShow)
    {
        if (_appWindow is null)
            return;

        double scale = Root.XamlRoot?.RasterizationScale ?? GetWindowScale();
        int w = WindowDpi.ToPhysical(_logicalWidth, scale);
        int h = WindowDpi.ToPhysical(_logicalHeight, scale);

        if (!_hasManualPosition && !_shown)
            PlaceAtDefault();

        // Clamp once from the current position, then move+resize in a single call.
        var pos = _appWindow.Position;
        ClampToWorkArea(ref pos, w, h);
        _appWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, w, h));

        if (forceShow && !_appWindow.IsVisible)
            _appWindow.Show(false);
    }

    /// <summary>Pure clamp: updates <paramref name="pos"/> only; does not move the window.</summary>
    private void ClampToWorkArea(ref PointInt32 pos, int width, int height)
    {
        int x = pos.X;
        int y = pos.Y;
        ClampToWorkArea(ref x, ref y, width, height);
        pos = new PointInt32(x, y);
    }

    private void ClampToWorkArea(ref int x, ref int y, int width, int height)
    {
        // Use the candidate rectangle, not the current window, so a drag can cross monitors.
        var center = new POINT { x = x + (width / 2), y = y + (height / 2) };
        var monitor = User32.MonitorFromPoint(center, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
        var info = MONITORINFO.Create();
        if (monitor == IntPtr.Zero || !User32.GetMonitorInfo(monitor, ref info))
            return;

        var work = info.rcWork;
        if (width > work.right - work.left)
            width = work.right - work.left;
        if (height > work.bottom - work.top)
            height = work.bottom - work.top;

        x = Math.Clamp(x, work.left, work.right - width);
        y = Math.Clamp(y, work.top, work.bottom - height);
    }

    private static void SavePosition(int x, int y)
    {
        try
        {
            Directory.CreateDirectory(AppStorage.AppDataDirectory);
            File.WriteAllText(PositionPath, string.Create(CultureInfo.InvariantCulture, $"{x},{y}"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save floating widget position");
        }
    }

    private double GetWindowScale()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero)
            return 1d;
        var dpi = User32.GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96d : 1d;
    }

    private AppWindow GetAppWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
    }
}
