using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using System.Windows.Input;
using TaskbarQuota.Services;
using TaskbarQuota.ViewModels;
using Windows.UI.ViewManagement;
using Windows.UI;

namespace TaskbarQuota.Controls
{
    public sealed partial class TotalSpendControl : UserControl
    {
        private static readonly Duration SettleDuration = new(TimeSpan.FromMilliseconds(420));

        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(
                nameof(ViewModel),
                typeof(TotalSpendViewModel),
                typeof(TotalSpendControl),
                new PropertyMetadata(null, OnViewModelChanged));

        public TotalSpendViewModel? ViewModel
        {
            get => (TotalSpendViewModel?)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public static readonly DependencyProperty RefreshCommandProperty =
            DependencyProperty.Register(
                nameof(RefreshCommand),
                typeof(ICommand),
                typeof(TotalSpendControl),
                new PropertyMetadata(null));

        public ICommand? RefreshCommand
        {
            get => (ICommand?)GetValue(RefreshCommandProperty);
            set => SetValue(RefreshCommandProperty, value);
        }

        public TotalSpendControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            ActualThemeChanged += (_, _) =>
            {
                RenderRing();
                RenderChart();
            };
        }

        private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            if (sender is not TotalSpendControl control)
                return;
            if (args.OldValue is TotalSpendViewModel oldViewModel)
            {
                oldViewModel.PropertyChanged -= control.ViewModel_PropertyChanged;
                oldViewModel.ProviderSlices.CollectionChanged -= control.ProviderSlices_CollectionChanged;
            }
            if (args.NewValue is TotalSpendViewModel newViewModel && control.IsLoaded)
            {
                newViewModel.PropertyChanged += control.ViewModel_PropertyChanged;
                newViewModel.ProviderSlices.CollectionChanged += control.ProviderSlices_CollectionChanged;
            }
            if (control.IsLoaded)
            {
                control.SyncSelectionsFromViewModel();
                control.RenderRing(animate: true);
                control.RenderChart(animate: true);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                viewModel.PropertyChanged += ViewModel_PropertyChanged;
                viewModel.ProviderSlices.CollectionChanged -= ProviderSlices_CollectionChanged;
                viewModel.ProviderSlices.CollectionChanged += ProviderSlices_CollectionChanged;
            }
            SyncSelectionsFromViewModel();
            RenderRing(animate: true);
            RenderChart(animate: true);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                viewModel.ProviderSlices.CollectionChanged -= ProviderSlices_CollectionChanged;
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TotalSpendViewModel.ChartDays)
                or nameof(TotalSpendViewModel.SelectedMetric))
                RenderChart(animate: true);
            if (e.PropertyName is nameof(TotalSpendViewModel.SelectedMetric)
                or nameof(TotalSpendViewModel.RingCenterValue))
                RenderRing(animate: true);
            // First time data arrives the whole card flips from collapsed to visible; stagger the
            // containers so they settle in instead of popping.
            if (e.PropertyName == nameof(TotalSpendViewModel.HasSpendData)
                && sender is TotalSpendViewModel { HasSpendData: true })
            {
                PlaySettleAnimation(Container1Card);
                PlaySettleAnimation(Container2Card, beginDelayMs: 90);
                PlaySettleAnimation(Container3Card, beginDelayMs: 180);
            }
        }

        private void ProviderSlices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => RenderRing(animate: true);

        private void SyncSelectionsFromViewModel()
        {
            if (ViewModel is not { } viewModel)
                return;
            SelectTag(PeriodBar, viewModel.SelectedPeriod);
            SelectTag(MetricBar, viewModel.SelectedMetric);
            SelectTag(BreakdownBar, viewModel.SelectedBreakdown);
        }

        private void CostShareButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            var flyout = new MenuFlyout();
            flyout.Items.Add(CreateShareItem("Summary", 1));
            flyout.Items.Add(CreateShareItem("Summary and daily cost", 2));
            flyout.Items.Add(CreateShareItem("Everything", 3));
            flyout.ShowAt(button);
        }

        private MenuFlyoutItem CreateShareItem(string text, int containers)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (_, _) => await ShareContainersAsync(containers);
            return item;
        }

        private async Task ShareContainersAsync(int containers)
        {
            // Collapse the containers that were not selected so the share image shows exactly the
            // requested group, and hide the header actions so the card reads as a clean shareable card.
            var restored = new List<FrameworkElement>();
            if (containers < 3 && Container3Card.Visibility == Visibility.Visible)
            {
                Container3Card.Visibility = Visibility.Collapsed;
                restored.Add(Container3Card);
            }
            if (containers < 2 && Container2Card.Visibility == Visibility.Visible)
            {
                Container2Card.Visibility = Visibility.Collapsed;
                restored.Add(Container2Card);
            }

            bool buttonsVisible = CostRefreshButton.Visibility == Visibility.Visible;
            if (buttonsVisible)
            {
                CostRefreshButton.Visibility = Visibility.Collapsed;
                CostShareButton.Visibility = Visibility.Collapsed;
            }

            bool copied = false;
            try
            {
                RootCard.UpdateLayout();
                copied = await ShareCardHelper.CopyElementToClipboardAsync(RootCard);
            }
            finally
            {
                foreach (var element in restored)
                    element.Visibility = Visibility.Visible;
                if (buttonsVisible)
                {
                    CostRefreshButton.Visibility = Visibility.Visible;
                    CostShareButton.Visibility = Visibility.Visible;
                }
                RootCard.UpdateLayout();
            }

            ShareCardHelper.ShowTransientTip(
                ShareTip,
                copied ? "Image copied to clipboard" : "Couldn't copy the image",
                CostShareButton);
        }

        private static void SelectTag(SelectorBar selector, string tag)
        {
            selector.SelectedItem = selector.Items.FirstOrDefault(item => Equals(item.Tag, tag))
                ?? selector.Items.FirstOrDefault();
        }

        private void PeriodBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is string period && ViewModel is { } viewModel)
                viewModel.SelectedPeriod = period;
        }

        private void MetricBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is string metric && ViewModel is { } viewModel)
                viewModel.SelectedMetric = metric;
        }

        private void BreakdownBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is string breakdown && ViewModel is { } viewModel)
                viewModel.SelectedBreakdown = breakdown;
        }

        private void UsageChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => RenderChart();

        private void SpendRingCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => RenderRing();

        private void RenderRing(bool animate = false)
        {
            if (!IsLoaded || ViewModel is not { } viewModel || SpendRingCanvas.ActualWidth <= 0)
                return;

            SpendRingCanvas.Children.Clear();
            if (animate)
                PlaySettleAnimation(SpendRingCanvas);
            var slices = viewModel.ProviderSlices.Where(slice => slice.Value > 0).ToArray();
            var arcs = SpendRingLayout.BuildArcs(slices.Select(slice => slice.Value).ToArray());
            if (arcs.Count == 0)
                return;

            var size = Math.Min(SpendRingCanvas.ActualWidth, SpendRingCanvas.ActualHeight);
            var center = size / 2;
            var outerRadius = Math.Max(12, center - 4);
            var innerRadius = Math.Max(4, outerRadius - 24);
            var offsetX = (SpendRingCanvas.ActualWidth - size) / 2;
            var offsetY = (SpendRingCanvas.ActualHeight - size) / 2;
            const double fullCircle = Math.PI * 2;
            const double gapRadians = 0.035;

            for (var index = 0; index < arcs.Count; index++)
            {
                var arc = arcs[index];
                var start = -Math.PI / 2 + arc.Start * fullCircle + gapRadians / 2;
                var end = -Math.PI / 2 + arc.End * fullCircle - gapRadians / 2;
                if (end <= start)
                    continue;

                var segment = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = CreateRingSegmentGeometry(center, outerRadius, innerRadius, start, end),
                    Fill = slices[index].DotBrush,
                };
                ToolTipService.SetToolTip(
                    segment,
                    $"{slices[index].ProviderName}: {slices[index].SummaryValueText} ({slices[index].ShareText})");
                Canvas.SetLeft(segment, offsetX);
                Canvas.SetTop(segment, offsetY);
                SpendRingCanvas.Children.Add(segment);
            }
        }

        private static Geometry CreateRingSegmentGeometry(
            double center,
            double outerRadius,
            double innerRadius,
            double start,
            double end)
        {
            var span = end - start;
            var outerStart = PolarPoint(center, outerRadius, start);
            var outerEnd = PolarPoint(center, outerRadius, end);
            var innerEnd = PolarPoint(center, innerRadius, end);
            var innerStart = PolarPoint(center, innerRadius, start);
            var figure = new PathFigure { StartPoint = outerStart, IsClosed = true };
            figure.Segments.Add(new ArcSegment
            {
                Point = outerEnd,
                Size = new Windows.Foundation.Size(outerRadius, outerRadius),
                IsLargeArc = span > Math.PI,
                SweepDirection = SweepDirection.Clockwise,
            });
            figure.Segments.Add(new LineSegment { Point = innerEnd });
            figure.Segments.Add(new ArcSegment
            {
                Point = innerStart,
                Size = new Windows.Foundation.Size(innerRadius, innerRadius),
                IsLargeArc = span > Math.PI,
                SweepDirection = SweepDirection.Counterclockwise,
            });
            return new PathGeometry { Figures = { figure } };
        }

        private static Windows.Foundation.Point PolarPoint(double center, double radius, double angle)
            => new(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));

        /// <summary>
        /// Quick fade-and-rise used when data changes, so redrawn visuals settle in instead of
        /// snapping. Respects the Windows "show animations" setting.
        /// </summary>
        private void PlaySettleAnimation(UIElement element, double beginDelayMs = 0)
        {
            if (!new UISettings().AnimationsEnabled)
            {
                element.Opacity = 1;
                element.RenderTransform = null;
                return;
            }

            var transform = new TranslateTransform { Y = 10 };
            element.RenderTransform = transform;
            element.Opacity = 0;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var beginTime = TimeSpan.FromMilliseconds(beginDelayMs);

            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = SettleDuration,
                BeginTime = beginTime,
                EasingFunction = ease,
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));

            var rise = new DoubleAnimation
            {
                To = 0,
                Duration = SettleDuration,
                BeginTime = beginTime,
                EasingFunction = ease,
            };
            Storyboard.SetTarget(rise, transform);
            Storyboard.SetTargetProperty(rise, nameof(TranslateTransform.Y));

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(rise);
            storyboard.Begin();
        }

        private void RenderChart(bool animate = false)
        {
            if (!IsLoaded || ViewModel is not { } viewModel || UsageChartCanvas.ActualWidth <= 0)
                return;

            UsageChartCanvas.Children.Clear();
            if (animate)
                PlaySettleAnimation(UsageChartCanvas);
            var points = viewModel.ChartDays;
            var width = UsageChartCanvas.ActualWidth;
            var height = UsageChartCanvas.ActualHeight > 0 ? UsageChartCanvas.ActualHeight : 176;
            const double plotLeft = 52;
            const double plotRight = 8;
            const double plotTop = 8;
            const double labelHeight = 28;
            var plotHeight = Math.Max(40, height - plotTop - labelHeight);
            var plotWidth = Math.Max(1, width - plotLeft - plotRight);
            var values = points.Select(point => viewModel.SelectedMetric == "tokens" ? point.TotalTokens : point.CostUsd).ToArray();
            var maxValue = values.Length == 0 ? 0 : values.Max();

            DrawGridLine(plotLeft, plotTop, plotWidth, FormatAxis(maxValue, viewModel.SelectedMetric));
            DrawGridLine(plotLeft, plotTop + plotHeight / 2, plotWidth, FormatAxis(maxValue / 2, viewModel.SelectedMetric));
            DrawGridLine(plotLeft, plotTop + plotHeight, plotWidth, "0");

            if (points.Count == 0 || maxValue <= 0)
            {
                var empty = new TextBlock
                {
                    Text = "No transcript activity in this window.",
                    Foreground = ResolveBrush("TextFillColorSecondaryBrush", Colors.Gray),
                    Style = Application.Current.Resources["BodyTextBlockStyle"] as Style,
                };
                Canvas.SetLeft(empty, plotLeft + 12);
                Canvas.SetTop(empty, plotTop + plotHeight / 2 - 10);
                UsageChartCanvas.Children.Add(empty);
                return;
            }

            var groupWidth = plotWidth / points.Count;
            var barWidth = Math.Clamp(groupWidth * 0.72, 1, 24);
            for (var index = 0; index < points.Count; index++)
            {
                var point = points[index];
                var x = plotLeft + index * groupWidth + (groupWidth - barWidth) / 2;
                var bottom = plotTop + plotHeight;
                var dayTooltip = BuildDayTooltip(point);

                foreach (var provider in point.Providers.OrderBy(provider => provider.ProviderId))
                {
                    var value = viewModel.SelectedMetric == "tokens" ? provider.Tokens : provider.CostUsd ?? 0;
                    if (value <= 0)
                        continue;
                    var segmentHeight = Math.Max(1, value / maxValue * plotHeight);
                    bottom -= segmentHeight;
                    var segment = new Rectangle
                    {
                        Width = barWidth,
                        Height = segmentHeight,
                        Fill = new SolidColorBrush(TotalSpendSliceViewModel.ProviderColor(provider.ProviderId)),
                        RadiusX = Math.Min(2, barWidth / 2),
                        RadiusY = Math.Min(2, barWidth / 2),
                    };
                    ToolTipService.SetToolTip(segment, dayTooltip);
                    Canvas.SetLeft(segment, x);
                    Canvas.SetTop(segment, bottom);
                    UsageChartCanvas.Children.Add(segment);
                }

                if (ShouldLabel(index, points.Count))
                {
                    var label = new TextBlock
                    {
                        Text = point.Date.ToString("MMM d", CultureInfo.CurrentCulture),
                        Foreground = ResolveBrush("TextFillColorSecondaryBrush", Colors.Gray),
                        Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
                    };
                    label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    var labelX = Math.Clamp(x + barWidth / 2 - label.DesiredSize.Width / 2, plotLeft, Math.Max(plotLeft, width - label.DesiredSize.Width));
                    Canvas.SetLeft(label, labelX);
                    Canvas.SetTop(label, plotTop + plotHeight + 6);
                    UsageChartCanvas.Children.Add(label);
                }
            }
        }

        private void DrawGridLine(double left, double top, double width, string labelText)
        {
            var line = new Line
            {
                X1 = left,
                X2 = left + width,
                Y1 = top,
                Y2 = top,
                Stroke = ResolveBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(80, 128, 128, 128)),
                StrokeThickness = 1,
            };
            UsageChartCanvas.Children.Add(line);

            var label = new TextBlock
            {
                Text = labelText,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush", Colors.Gray),
                Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, Math.Max(0, top - 8));
            UsageChartCanvas.Children.Add(label);
        }

        private Brush ResolveBrush(string key, Color fallback)
            => Resources.TryGetValue(key, out var local) && local is Brush localBrush
                ? localBrush
                : Application.Current.Resources.TryGetValue(key, out var app) && app is Brush appBrush
                    ? appBrush
                    : new SolidColorBrush(fallback);

        private static bool ShouldLabel(int index, int count)
        {
            if (count <= 7)
                return true;
            var step = count <= 30 ? 5 : 15;
            return index == 0 || index == count - 1 || index % step == 0;
        }

        private static string FormatAxis(double value, string metric)
        {
            if (metric == "cost")
                return value >= 10 ? $"${value:F0}" : $"${value:F2}";
            if (value >= 1_000_000_000) return $"{value / 1_000_000_000:F1}B";
            if (value >= 1_000_000) return $"{value / 1_000_000:F1}M";
            if (value >= 1_000) return $"{value / 1_000:F1}K";
            return $"{value:F0}";
        }

        private static string BuildDayTooltip(UsageChartDayViewModel point)
        {
            var providers = string.Join(Environment.NewLine, point.Providers.Select(provider =>
                $"{provider.ProviderName}: {provider.Tokens:N0} tokens · {(provider.CostUsd.HasValue ? $"${provider.CostUsd.Value:F2}" : "cost unavailable")}"));
            var totalCost = $"${point.CostUsd:F2}{(point.CostComplete ? string.Empty : "*")}";
            return $"{point.Date:dddd, MMM d}{Environment.NewLine}{point.TotalTokens:N0} tokens · {totalCost}{(providers.Length > 0 ? Environment.NewLine + providers : string.Empty)}";
        }

    }
}
