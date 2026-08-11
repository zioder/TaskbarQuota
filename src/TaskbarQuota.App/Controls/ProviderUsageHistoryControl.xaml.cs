using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TaskbarQuota.ViewModels;
using Windows.UI;

namespace TaskbarQuota.Controls
{
    public sealed partial class ProviderUsageHistoryControl : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(
                nameof(ViewModel),
                typeof(ProviderUsageHistoryViewModel),
                typeof(ProviderUsageHistoryControl),
                new PropertyMetadata(ProviderUsageHistoryViewModel.Empty, OnViewModelChanged));

        public ProviderUsageHistoryViewModel ViewModel
        {
            get => (ProviderUsageHistoryViewModel?)GetValue(ViewModelProperty) ?? ProviderUsageHistoryViewModel.Empty;
            set => SetValue(ViewModelProperty, value);
        }

        public ProviderUsageHistoryControl()
        {
            InitializeComponent();
            Loaded += (_, _) => RenderTrend();
            ActualThemeChanged += (_, _) => RenderTrend();
        }

        public Visibility BoolToVisibility(bool value)
            => value ? Visibility.Visible : Visibility.Collapsed;

        public Visibility InvertBoolToVisibility(bool value)
            => value ? Visibility.Collapsed : Visibility.Visible;

        private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            if (sender is ProviderUsageHistoryControl control && control.IsLoaded)
                control.RenderTrend();
        }

        private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => RenderTrend();

        private void RenderTrend()
        {
            if (!IsLoaded || !ViewModel.HasTrend || TrendCanvas.ActualWidth <= 0)
                return;

            TrendCanvas.Children.Clear();
            var points = ViewModel.TrendPoints;
            if (points.Count == 0)
                return;

            var maxTokens = points.Max(point => point.Tokens);
            if (maxTokens == 0)
                return;

            var width = TrendCanvas.ActualWidth;
            var height = TrendCanvas.ActualHeight > 0 ? TrendCanvas.ActualHeight : 52;
            var slot = width / points.Count;
            var barWidth = Math.Clamp(slot * 0.58, 2, 12);
            var fill = ResolveBrush("AccentFillColorDefaultBrush", Colors.DodgerBlue);
            for (int index = 0; index < points.Count; index++)
            {
                var point = points[index];
                var barHeight = point.Tokens == 0
                    ? 1
                    : Math.Max(2, point.Tokens / (double)maxTokens * (height - 4));
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = barHeight,
                    Fill = fill,
                    RadiusX = 1,
                    RadiusY = 1,
                    Opacity = point.Tokens == 0 ? 0.28 : 1,
                };
                ToolTipService.SetToolTip(bar, point.TooltipText);
                Canvas.SetLeft(bar, index * slot + (slot - barWidth) / 2);
                Canvas.SetTop(bar, height - barHeight);
                TrendCanvas.Children.Add(bar);
            }
        }

        private Brush ResolveBrush(string key, Color fallback)
            => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
                ? brush
                : new SolidColorBrush(fallback);
    }
}
