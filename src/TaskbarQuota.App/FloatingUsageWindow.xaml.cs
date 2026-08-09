using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using TaskbarQuota.AgentActivity;
using TaskbarQuota.Controls;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;
using TaskbarQuota.Usage;
using WinRT;

namespace TaskbarQuota;

/// <summary>
/// Always-on-top host for the same compact usage layout as the taskbar widget.
/// Single window, draggable, position persisted under app data.
/// Scroll wheel adjusts Acrylic strength; right-click returns usage to the taskbar.
/// </summary>
public sealed partial class FloatingUsageWindow : Window
{
    private static readonly string PositionPath =
        Path.Combine(AppStorage.AppDataDirectory, "floating-widget-position.txt");

    private const int ChromePaddingLogicalX = 20; // Border padding 10*2
    private const int ChromePaddingLogicalY = 12; // Border padding 6*2
    private const int DefaultLogicalWidth = 200;
    private const int DefaultLogicalHeight = 60;
    private const int HorizontalScrollbarLogicalHeight = 12;
    private const int DragThresholdLogicalPx = 4;
    private const float AcrylicTintOpacityMin = 0.25f;
    private const float AcrylicTintOpacityMax = 0.80f;
    private const float AcrylicLuminosityOpacityMin = 0.45f;
    private const float AcrylicLuminosityOpacityMax = 0.80f;
    /// <summary>Matches the settings / flyout opacity slider step (5%).</summary>
    public const double OpacityWheelStep = 0.05;

    private AppWindow? _appWindow;
    private bool _shown;
    private bool _isDragging;
    private bool _isPointerTracking;
    private bool _isTransferringPointerCapture;
    private POINT _pressCursor;
    private PointInt32 _pressWindowPos;
    private int _logicalWidth = DefaultLogicalWidth;
    private int _logicalHeight = DefaultLogicalHeight;
    private bool _hasManualPosition;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private AccessibilitySettings? _accessibilitySettings;

    // Same instance for AddHandler/RemoveHandler; method-group casts would not match.
    private readonly PointerEventHandler _rootPointerPressed;
    private readonly PointerEventHandler _rootPointerMoved;
    private readonly PointerEventHandler _rootPointerReleased;
    private readonly PointerEventHandler _rootPointerCanceled;
    private readonly PointerEventHandler _rootPointerCaptureLost;
    private readonly PointerEventHandler _rootPointerWheelChanged;

    public event Action? Clicked;
    public event Action<AgentActivityItem?>? ActivityClicked;

    public bool IsShown => _shown;
    public bool IsDragging => _isDragging;
    public bool HasVisibleContent => ContentHost.HasVisibleContent;
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

        _rootPointerPressed = Root_PointerPressed;
        _rootPointerMoved = Root_PointerMoved;
        _rootPointerReleased = Root_PointerReleased;
        _rootPointerCanceled = Root_PointerCanceled;
        _rootPointerCaptureLost = Root_PointerCaptureLost;
        _rootPointerWheelChanged = Root_PointerWheelChanged;

        // Use the lower-level native controller rather than DesktopAcrylicBackdrop so this deliberately
        // non-activating HUD can keep its material active without stealing focus from the user's app.
        SystemBackdrop = null;
        ThemeService.Register(Root);
        InitializeAcrylicBackdrop();

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
        Root.ActualThemeChanged += (_, _) => ApplyAcrylicMaterial();
        Root.Loaded += (_, _) =>
        {
            ApplyAcrylicMaterial();
            ContentHost.EnsureVisibleContent();
        };

        // handledEventsToo: activity title/step live inside a Button that marks PointerPressed handled.
        // Without this, press-on-activity never reaches Root and the floating window cannot be dragged
        // (same pattern as TaskBarWidget's activity island).
        Root.AddHandler(UIElement.PointerPressedEvent, _rootPointerPressed, handledEventsToo: true);
        Root.AddHandler(UIElement.PointerMovedEvent, _rootPointerMoved, handledEventsToo: true);
        Root.AddHandler(UIElement.PointerReleasedEvent, _rootPointerReleased, handledEventsToo: true);
        Root.AddHandler(UIElement.PointerCanceledEvent, _rootPointerCanceled, handledEventsToo: true);
        Root.AddHandler(UIElement.PointerCaptureLostEvent, _rootPointerCaptureLost, handledEventsToo: true);
        Root.AddHandler(UIElement.PointerWheelChangedEvent, _rootPointerWheelChanged, handledEventsToo: true);
        Root.RightTapped += Root_RightTapped;

        Closed += OnClosed;
        ApplyAcrylicMaterial();
        LoadPositionOrDefault();
        ApplyBounds(forceShow: false);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        ThemeService.Unregister(Root);
        WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
        ContentHost.Clicked -= OnContentClicked;
        Root.RemoveHandler(UIElement.PointerPressedEvent, _rootPointerPressed);
        Root.RemoveHandler(UIElement.PointerMovedEvent, _rootPointerMoved);
        Root.RemoveHandler(UIElement.PointerReleasedEvent, _rootPointerReleased);
        Root.RemoveHandler(UIElement.PointerCanceledEvent, _rootPointerCanceled);
        Root.RemoveHandler(UIElement.PointerCaptureLostEvent, _rootPointerCaptureLost);
        Root.RemoveHandler(UIElement.PointerWheelChangedEvent, _rootPointerWheelChanged);
        Root.RightTapped -= Root_RightTapped;

        _accessibilitySettings = null;

        if (_acrylicController is not null)
        {
            _acrylicController.RemoveAllSystemBackdropTargets();
            _acrylicController.Dispose();
            _acrylicController = null;
        }
        _backdropConfiguration = null;
        _shown = false;
    }

    private void OnWidgetSettingsChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(ApplyAcrylicMaterial);

    private void InitializeAcrylicBackdrop()
    {
        try
        {
            if (!DesktopAcrylicController.IsSupported())
                return;

            // Composition backdrops rely on the system DispatcherQueue in addition to WinUI's queue.
            DispatcherQueue.EnsureSystemDispatcherQueue();

            var configuration = new SystemBackdropConfiguration
            {
                // The HUD is always shown with Show(false). Treating it as input-active keeps native
                // acrylic alive while another application correctly retains keyboard focus.
                IsInputActive = true,
            };
            var controller = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin,
            };

            if (!controller.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>()))
            {
                controller.Dispose();
                return;
            }

            // Store the controller before the remaining configuration calls so the catch path can
            // always detach and dispose it if a later WinRT operation fails.
            _acrylicController = controller;
            controller.SetSystemBackdropConfiguration(configuration);
            _backdropConfiguration = configuration;

            _accessibilitySettings = new AccessibilitySettings();
        }
        catch (Exception ex)
        {
            _acrylicController?.RemoveAllSystemBackdropTargets();
            _acrylicController?.Dispose();
            _acrylicController = null;
            _backdropConfiguration = null;
            Log.Warning(ex, "Could not initialize native acrylic for the floating widget; using a solid fallback");
        }
    }

    /// <summary>
    /// Maps the persisted 35-100% setting to the useful range of the native Acrylic recipe. Keeping
    /// the tint below fully opaque preserves the blur/noise character even at the strongest setting.
    /// </summary>
    internal static (float TintOpacity, float LuminosityOpacity) ComputeAcrylicMaterial(double opacity)
    {
        double clamped = Math.Clamp(
            opacity,
            WidgetSettingsService.FloatingOpacityMin,
            WidgetSettingsService.FloatingOpacityMax);
        double range = WidgetSettingsService.FloatingOpacityMax - WidgetSettingsService.FloatingOpacityMin;
        double normalized = range <= 0
            ? 1
            : (clamped - WidgetSettingsService.FloatingOpacityMin) / range;

        return (
            AcrylicTintOpacityMin + ((AcrylicTintOpacityMax - AcrylicTintOpacityMin) * (float)normalized),
            AcrylicLuminosityOpacityMin
                + ((AcrylicLuminosityOpacityMax - AcrylicLuminosityOpacityMin) * (float)normalized));
    }

    private void ApplyAcrylicMaterial()
    {
        bool light = ThemeService.IsLightChrome(Root);
        byte r = light ? (byte)248 : (byte)28;
        byte g = light ? (byte)248 : (byte)28;
        byte b = light ? (byte)248 : (byte)28;
        var tint = Color.FromArgb(255, r, g, b);

        // The XAML fill is only used if Acrylic cannot be initialized on this machine. When the
        // controller enters policy fallback (for example, Transparency effects off), it draws its own
        // solid FallbackColor behind the transparent content.
        ChromeFill.Background = new SolidColorBrush(tint);
        ChromeFill.Opacity = _acrylicController is null ? 1 : 0;

        if (_acrylicController is null || _backdropConfiguration is null)
            return;

        var material = ComputeAcrylicMaterial(WidgetSettingsService.FloatingOpacity);
        _acrylicController.TintColor = tint;
        _acrylicController.TintOpacity = material.TintOpacity;
        _acrylicController.LuminosityOpacity = material.LuminosityOpacity;
        _acrylicController.FallbackColor = tint;

        _backdropConfiguration.Theme = light
            ? SystemBackdropTheme.Light
            : SystemBackdropTheme.Dark;
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.IsHighContrast = _accessibilitySettings?.HighContrast == true;
        _backdropConfiguration.HighContrastBackgroundColor = tint;
    }

    /// <summary>
    /// Pure step helper for wheel opacity (and tests). Positive <paramref name="wheelDelta"/>
    /// increases opacity (wheel up); negative decreases.
    /// </summary>
    public static double StepOpacityFromWheel(double current, int wheelDelta)
    {
        if (wheelDelta == 0)
            return Math.Clamp(current, WidgetSettingsService.FloatingOpacityMin, WidgetSettingsService.FloatingOpacityMax);

        // Standard mouse notch is 120; precision touchpads may send smaller deltas.
        int steps = wheelDelta / 120;
        if (steps == 0)
            steps = wheelDelta > 0 ? 1 : -1;

        double next = current + (steps * OpacityWheelStep);
        return Math.Clamp(
            Math.Round(next / OpacityWheelStep) * OpacityWheelStep,
            WidgetSettingsService.FloatingOpacityMin,
            WidgetSettingsService.FloatingOpacityMax);
    }

    private void OnContentClicked()
        => Clicked?.Invoke();

    private void OnDesiredSizeChanged(int logicalWidth, int logicalHeight)
    {
        _logicalWidth = Math.Max(DefaultLogicalWidth, logicalWidth + ChromePaddingLogicalX);
        _logicalHeight = Math.Max(DefaultLogicalHeight, logicalHeight + ChromePaddingLogicalY);

        // The activity control can intentionally retain the last non-empty snapshot for a short grace
        // period. When that timer finally settles to empty and there are no quota tiles, hide the chrome
        // with the content instead of leaving an empty floating rectangle behind.
        if (_shown && !ContentHost.HasVisibleContent)
        {
            SetVisible(false);
            return;
        }

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
            ApplyAcrylicMaterial();
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

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
            return;

        var props = e.GetCurrentPoint(Root).Properties;
        if (props.IsHorizontalMouseWheel || props.MouseWheelDelta == 0)
            return;

        e.Handled = true;
        double next = StepOpacityFromWheel(WidgetSettingsService.FloatingOpacity, props.MouseWheelDelta);
        WidgetSettingsService.ApplyFloatingOpacity(next);
    }

    private void Root_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        // Reinject into the taskbar; TaskBarManager reacts to WidgetSettingsService.Changed.
        WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Taskbar);
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
            return;
        if (IsScrollBarSource(e.OriginalSource))
            return;

        _isPointerTracking = true;
        _isDragging = false;
        if (!User32.GetCursorPos(out _pressCursor))
            _pressCursor = default;
        if (_appWindow is not null)
            _pressWindowPos = _appWindow.Position;
        // Do not capture here: stealing capture from a child Button prevents its release/click from
        // completing. The handled-events-too move handler will see the first few pixels and capture only
        // after the gesture crosses the drag threshold.
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
            // A child Button normally owns capture at this point. Transferring it to Root raises
            // PointerCaptureLost on that child synchronously; ignore only that expected routed event,
            // while retaining normal cancellation when Root later loses capture for real.
            _isTransferringPointerCapture = true;
            try { Root.CapturePointer(e.Pointer); } catch { }
            finally { _isTransferringPointerCapture = false; }
            // Prevent the activity Open button (and quota tiles) from firing after a drag.
            ContentHost.SuppressNextClicks();
            e.Handled = true;
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

        if (wasDragging)
        {
            e.Handled = true;
            if (_appWindow is not null)
            {
                SavePosition(_appWindow.Position.X, _appWindow.Position.Y);
                _hasManualPosition = true;
            }

            // The child consumes any click synthesized by this release. Clear every other child's flag
            // only after routed input finishes so the next genuine single click is never discarded.
            DispatcherQueue.TryEnqueue(ContentHost.ClearSuppressedClicks);
        }
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isTransferringPointerCapture && !ReferenceEquals(e.OriginalSource, Root))
            return;

        Root_PointerCanceled(sender, e);
    }

    private void Root_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        bool wasDragging = _isDragging;
        _isPointerTracking = false;
        _isDragging = false;
        try { Root.ReleasePointerCapture(e.Pointer); } catch { }

        // A Button can report capture-lost before Root receives PointerReleased. Persist the position
        // here too, otherwise activity-origin drags visibly move but revert on the next launch.
        if (wasDragging && _appWindow is not null)
        {
            SavePosition(_appWindow.Position.X, _appWindow.Position.Y);
            _hasManualPosition = true;
        }
        DispatcherQueue.TryEnqueue(ContentHost.ClearSuppressedClicks);
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

        var pos = _appWindow.Position;
        bool hasWorkArea = TryGetWorkArea(pos.X, pos.Y, w, h, out var workArea);
        bool horizontalOverflow = hasWorkArea && w > workArea.Width;
        ContentScroller.HorizontalScrollBarVisibility = horizontalOverflow
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Hidden;
        if (horizontalOverflow)
            h += WindowDpi.ToPhysical(HorizontalScrollbarLogicalHeight, scale);

        var bounds = hasWorkArea
            ? ConstrainBoundsToWorkArea(new RectInt32(pos.X, pos.Y, w, h), workArea)
            : new RectInt32(pos.X, pos.Y, w, h);
        _appWindow.MoveAndResize(bounds);

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
        if (!TryGetWorkArea(x, y, width, height, out var workArea))
            return;

        var constrained = ConstrainBoundsToWorkArea(
            new RectInt32(x, y, width, height),
            workArea);
        x = constrained.X;
        y = constrained.Y;
    }

    private static bool TryGetWorkArea(int x, int y, int width, int height, out RectInt32 workArea)
    {
        // Use the candidate rectangle, not the current window, so a drag can cross monitors.
        var center = new POINT { x = x + (width / 2), y = y + (height / 2) };
        var monitor = User32.MonitorFromPoint(center, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
        var info = MONITORINFO.Create();
        if (monitor == IntPtr.Zero || !User32.GetMonitorInfo(monitor, ref info))
        {
            workArea = default;
            return false;
        }

        workArea = new RectInt32(
            info.rcWork.left,
            info.rcWork.top,
            info.rcWork.right - info.rcWork.left,
            info.rcWork.bottom - info.rcWork.top);
        return workArea.Width > 0 && workArea.Height > 0;
    }

    internal static RectInt32 ConstrainBoundsToWorkArea(RectInt32 desired, RectInt32 workArea)
    {
        int width = Math.Clamp(desired.Width, 1, Math.Max(1, workArea.Width));
        int height = Math.Clamp(desired.Height, 1, Math.Max(1, workArea.Height));
        int x = Math.Clamp(
            desired.X,
            workArea.X,
            Math.Max(workArea.X, workArea.X + workArea.Width - width));
        int y = Math.Clamp(
            desired.Y,
            workArea.Y,
            Math.Max(workArea.Y, workArea.Y + workArea.Height - height));

        return new RectInt32(x, y, width, height);
    }

    private static bool IsScrollBarSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is ScrollBar)
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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
