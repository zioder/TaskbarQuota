using TaskbarQuota.Services;

namespace TaskbarQuota.Tests;

public class WidgetSurfaceModeTests
{
    /// <summary>
    /// WidgetSettingsService is process-global. Capture and restore surface/opacity so these tests
    /// cannot leak into others (or observe another test's mutations under parallel xUnit).
    /// </summary>
    private static void WithIsolatedSurfaceSettings(Action body)
    {
        var previousSurface = WidgetSettingsService.CurrentSurface;
        var previousOpacity = WidgetSettingsService.FloatingOpacity;
        try
        {
            body();
        }
        finally
        {
            WidgetSettingsService.ApplySurface(previousSurface);
            WidgetSettingsService.ApplyFloatingOpacity(previousOpacity);
        }
    }

    [Fact]
    public void Default_surface_is_taskbar()
    {
        WithIsolatedSurfaceSettings(() =>
        {
            WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Taskbar);
            Assert.Equal(WidgetSurfaceMode.Taskbar, WidgetSettingsService.CurrentSurface);
        });
    }

    [Fact]
    public void ApplySurface_switches_to_floating_and_back()
    {
        WithIsolatedSurfaceSettings(() =>
        {
            WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Taskbar);
            Assert.Equal(WidgetSurfaceMode.Taskbar, WidgetSettingsService.CurrentSurface);

            WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Floating);
            Assert.Equal(WidgetSurfaceMode.Floating, WidgetSettingsService.CurrentSurface);
            Assert.Equal(PinBudgetService.FloatingAvailableLogicalWidth, PinBudgetService.AvailableLogicalWidth);

            WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Taskbar);
            Assert.Equal(WidgetSurfaceMode.Taskbar, WidgetSettingsService.CurrentSurface);
        });
    }

    [Fact]
    public void ApplySurface_same_value_is_noop()
    {
        WithIsolatedSurfaceSettings(() =>
        {
            WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Taskbar);
            int changed = 0;
            void Handler(object? s, EventArgs e) => changed++;
            WidgetSettingsService.Changed += Handler;
            try
            {
                WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Taskbar);
                Assert.Equal(0, changed);
            }
            finally
            {
                WidgetSettingsService.Changed -= Handler;
            }
        });
    }

    [Fact]
    public void ApplyFloatingOpacity_clamps_and_round_trips()
    {
        WithIsolatedSurfaceSettings(() =>
        {
            WidgetSettingsService.ApplyFloatingOpacity(0.5);
            Assert.Equal(0.5, WidgetSettingsService.FloatingOpacity, precision: 2);

            WidgetSettingsService.ApplyFloatingOpacity(0.1); // below min
            Assert.Equal(WidgetSettingsService.FloatingOpacityMin, WidgetSettingsService.FloatingOpacity, precision: 2);

            WidgetSettingsService.ApplyFloatingOpacity(1.5); // above max
            Assert.Equal(WidgetSettingsService.FloatingOpacityMax, WidgetSettingsService.FloatingOpacity, precision: 2);

            WidgetSettingsService.ApplyFloatingOpacity(WidgetSettingsService.FloatingOpacityDefault);
        });
    }
}
