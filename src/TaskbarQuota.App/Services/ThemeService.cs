using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace TaskbarQuota
{
    /// <summary>
    /// Applies an app theme override to every registered window root (main window, flyout, floating HUD).
    /// Multiple roots are required: a single root meant creating the floating window stole theme from the
    /// main window, and disposing the floating window left theme updates targeting a dead element.
    /// </summary>
    public static class ThemeService
    {
        private static readonly object Gate = new();
        private static readonly List<WeakReference<FrameworkElement>> Roots = new();

        public static ElementTheme Current { get; private set; } = ElementTheme.Default;

        public static void Register(FrameworkElement root)
        {
            lock (Gate)
            {
                PruneUnlocked();
                for (int i = 0; i < Roots.Count; i++)
                {
                    if (Roots[i].TryGetTarget(out var existing) && ReferenceEquals(existing, root))
                    {
                        root.RequestedTheme = Current;
                        return;
                    }
                }

                Roots.Add(new WeakReference<FrameworkElement>(root));
                root.RequestedTheme = Current;
            }
        }

        public static void Unregister(FrameworkElement root)
        {
            lock (Gate)
            {
                Roots.RemoveAll(wr =>
                    !wr.TryGetTarget(out var target) || ReferenceEquals(target, root));
            }
        }

        public static void Apply(ElementTheme theme)
        {
            Current = theme;
            lock (Gate)
            {
                PruneUnlocked();
                foreach (var wr in Roots)
                {
                    if (wr.TryGetTarget(out var root))
                        root.RequestedTheme = theme;
                }
            }
        }

        /// <summary>
        /// Whether chrome should use light (dark text) or dark (light text) colors for the given element,
        /// honoring its ActualTheme, then the app override, then the system theme.
        /// </summary>
        public static bool IsLightChrome(FrameworkElement element)
        {
            var actual = element.ActualTheme;
            if (actual == ElementTheme.Light)
                return true;
            if (actual == ElementTheme.Dark)
                return false;

            if (Current == ElementTheme.Light)
                return true;
            if (Current == ElementTheme.Dark)
                return false;

            return Interop.SystemInfos.IsSystemLightThemeUsed() == true;
        }

        private static void PruneUnlocked()
        {
            Roots.RemoveAll(wr => !wr.TryGetTarget(out _));
        }
    }
}
