using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Controls
{
    public sealed partial class ProviderAvatar : UserControl
    {
        public static readonly DependencyProperty ProviderIdProperty =
            DependencyProperty.Register(
                nameof(ProviderId),
                typeof(ProviderId),
                typeof(ProviderAvatar),
                new PropertyMetadata(default(ProviderId), OnVisualPropertyChanged));

        public static readonly DependencyProperty InitialProperty =
            DependencyProperty.Register(
                nameof(Initial),
                typeof(string),
                typeof(ProviderAvatar),
                new PropertyMetadata("?", OnVisualPropertyChanged));

        public static readonly DependencyProperty ForegroundBrushProperty =
            DependencyProperty.Register(
                nameof(ForegroundBrush),
                typeof(Brush),
                typeof(ProviderAvatar),
                new PropertyMetadata(null, OnVisualPropertyChanged));

        public ProviderAvatar()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
        }

        public ProviderId ProviderId
        {
            get => (ProviderId)GetValue(ProviderIdProperty);
            set => SetValue(ProviderIdProperty, value);
        }

        public string Initial
        {
            get => (string)GetValue(InitialProperty);
            set => SetValue(InitialProperty, value);
        }

        public Brush ForegroundBrush
        {
            get => (Brush)GetValue(ForegroundBrushProperty);
            set => SetValue(ForegroundBrushProperty, value);
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProviderAvatar avatar)
                avatar.Refresh();
        }

        private void Refresh()
        {
            var foreground = ForegroundBrush ?? new SolidColorBrush(Microsoft.UI.Colors.White);
            InitialText.Text = Initial;
            InitialText.Foreground = foreground;

            if (ProviderGlyphRenderer.TryApply(GlyphPath, ProviderId, foreground))
            {
                GlyphBox.Visibility = Visibility.Visible;
                InitialText.Visibility = Visibility.Collapsed;
            }
            else
            {
                GlyphBox.Visibility = Visibility.Collapsed;
                InitialText.Visibility = Visibility.Visible;
            }
        }
    }
}
