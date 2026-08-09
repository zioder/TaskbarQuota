using TaskbarQuota.Services;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
public class WidgetSurfaceModeTests
{
    /// <summary>
    /// WidgetSettingsService is process-global. Capture and restore surface/opacity so these tests
    /// cannot leak into others (or observe another test's mutations under parallel xUnit).
    /// </summary>
    private static void WithIsolatedSurfaceSettings(Action<string> body)
    {
        var previousSurface = WidgetSettingsService.CurrentSurface;
        var previousOpacity = WidgetSettingsService.FloatingOpacity;
        var directory = Path.Combine(Path.GetTempPath(), "taskbarquota-surface-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var storageOverride = AppStorage.OverrideAppDataDirectoryForTesting(directory);
            WidgetSettingsService.ReloadSurfaceSettingsForTesting();
            body(directory);
        }
        finally
        {
            WidgetSettingsService.RestoreSurfaceSettingsForTesting(previousSurface, previousOpacity);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Default_surface_is_taskbar()
    {
        WithIsolatedSurfaceSettings(directory =>
        {
            Assert.Equal(WidgetSurfaceMode.Taskbar, WidgetSettingsService.CurrentSurface);
        });
    }

    [Fact]
    public void ApplySurface_switches_to_floating_and_back()
    {
        WithIsolatedSurfaceSettings(directory =>
        {
            WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Taskbar);
            Assert.Equal(WidgetSurfaceMode.Taskbar, WidgetSettingsService.CurrentSurface);

            WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Floating);
            Assert.Equal(WidgetSurfaceMode.Floating, WidgetSettingsService.CurrentSurface);
            Assert.Equal(int.MaxValue, PinBudgetService.AvailableLogicalWidth);
            Assert.Equal("1", File.ReadAllText(Path.Combine(directory, "widget-surface-mode.txt")));

            WidgetSettingsService.ReloadSurfaceSettingsForTesting();
            Assert.Equal(WidgetSurfaceMode.Floating, WidgetSettingsService.CurrentSurface);

            WidgetSettingsService.ApplySurface(WidgetSurfaceMode.Taskbar);
            Assert.Equal(WidgetSurfaceMode.Taskbar, WidgetSettingsService.CurrentSurface);
        });
    }

    [Fact]
    public void ApplySurface_same_value_is_noop()
    {
        WithIsolatedSurfaceSettings(_ =>
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
        WithIsolatedSurfaceSettings(directory =>
        {
            WidgetSettingsService.ApplyFloatingOpacity(0.5);
            Assert.Equal(0.5, WidgetSettingsService.FloatingOpacity, precision: 2);
            Assert.Equal("50", File.ReadAllText(Path.Combine(directory, "floating-opacity.txt")));

            WidgetSettingsService.ReloadSurfaceSettingsForTesting();
            Assert.Equal(0.5, WidgetSettingsService.FloatingOpacity, precision: 2);

            WidgetSettingsService.ApplyFloatingOpacity(0.1); // below min
            Assert.Equal(WidgetSettingsService.FloatingOpacityMin, WidgetSettingsService.FloatingOpacity, precision: 2);

            WidgetSettingsService.ApplyFloatingOpacity(1.5); // above max
            Assert.Equal(WidgetSettingsService.FloatingOpacityMax, WidgetSettingsService.FloatingOpacity, precision: 2);

            WidgetSettingsService.ApplyFloatingOpacity(WidgetSettingsService.FloatingOpacityDefault);
        });
    }

    [Fact]
    public void StepOpacityFromWheel_steps_by_five_percent_and_clamps()
    {
        Assert.Equal(0.55, FloatingUsageWindow.StepOpacityFromWheel(0.50, +120), precision: 2);
        Assert.Equal(0.45, FloatingUsageWindow.StepOpacityFromWheel(0.50, -120), precision: 2);
        Assert.Equal(WidgetSettingsService.FloatingOpacityMax,
            FloatingUsageWindow.StepOpacityFromWheel(0.98, +120), precision: 2);
        Assert.Equal(WidgetSettingsService.FloatingOpacityMin,
            FloatingUsageWindow.StepOpacityFromWheel(0.36, -120), precision: 2);
        // Sub-notch deltas still count as one step (precision touchpad).
        Assert.Equal(0.55, FloatingUsageWindow.StepOpacityFromWheel(0.50, +40), precision: 2);
    }

    [Fact]
    public void ComputeAcrylicMaterial_clamps_and_preserves_native_material_at_maximum()
    {
        var minimum = FloatingUsageWindow.ComputeAcrylicMaterial(0);
        var middle = FloatingUsageWindow.ComputeAcrylicMaterial(0.675);
        var maximum = FloatingUsageWindow.ComputeAcrylicMaterial(2);

        Assert.Equal(0.25f, minimum.TintOpacity, precision: 3);
        Assert.Equal(0.45f, minimum.LuminosityOpacity, precision: 3);
        Assert.InRange(middle.TintOpacity, minimum.TintOpacity, maximum.TintOpacity);
        Assert.InRange(middle.LuminosityOpacity, minimum.LuminosityOpacity, maximum.LuminosityOpacity);
        Assert.Equal(0.80f, maximum.TintOpacity, precision: 3);
        Assert.Equal(0.80f, maximum.LuminosityOpacity, precision: 3);
        Assert.True(maximum.TintOpacity < 1, "Maximum strength must still reveal the native Acrylic material.");
    }
}
