using Microsoft.UI.Xaml;

namespace TaskbarQuota
{
    /// <summary>Applies an app theme override to the shell's root element.</summary>
    public static class ThemeService
    {
        private static FrameworkElement? _root;

        public static ElementTheme Current { get; private set; } = ElementTheme.Default;

        public static void Register(FrameworkElement root)
        {
            _root = root;
            _root.RequestedTheme = Current;
        }

        public static void Apply(ElementTheme theme)
        {
            Current = theme;
            if (_root != null) _root.RequestedTheme = theme;
        }
    }
}
