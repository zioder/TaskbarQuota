using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Controls
{
    /// <summary>
    /// A Fluent donut chart rendered from <see cref="Path"/> arc segments (no third-party charting).
    /// Each segment is drawn as a stroked open arc with a small gap between neighbours, matching the
    /// system's rounded, layered aesthetic. A single full segment renders as a closed ring so a
    /// one-provider window doesn't show a spurious gap.
    /// </summary>
    public sealed partial class CostDonut : UserControl
    {
        private const double Thickness = 20;
        private const double GapDegrees = 4;   // visual gap between multi-segment arcs

        public CostDonut() => InitializeComponent();

        public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
            nameof(Segments), typeof(IEnumerable<CostSegmentViewModel>), typeof(CostDonut),
            new PropertyMetadata(null, OnSegmentsChanged));

        public IEnumerable<CostSegmentViewModel>? Segments
        {
            get => (IEnumerable<CostSegmentViewModel>?)GetValue(SegmentsProperty);
            set => SetValue(SegmentsProperty, value);
        }

        public static readonly DependencyProperty CenterTextProperty = DependencyProperty.Register(
            nameof(CenterText), typeof(string), typeof(CostDonut),
            new PropertyMetadata("$0.00", (d, _) => ((CostDonut)d).CenterLabel.Text = ((CostDonut)d).CenterText ?? ""));

        public string? CenterText
        {
            get => (string?)GetValue(CenterTextProperty);
            set => SetValue(CenterTextProperty, value);
        }

        public static readonly DependencyProperty CenterSubtitleProperty = DependencyProperty.Register(
            nameof(CenterSubtitle), typeof(string), typeof(CostDonut),
            new PropertyMetadata("API value", (d, _) => ((CostDonut)d).CenterSubLabel.Text = ((CostDonut)d).CenterSubtitle ?? ""));

        public string? CenterSubtitle
        {
            get => (string?)GetValue(CenterSubtitleProperty);
            set => SetValue(CenterSubtitleProperty, value);
        }

        private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var donut = (CostDonut)d;
            if (e.OldValue is INotifyCollectionChanged oldColl)
                oldColl.CollectionChanged -= donut.OnCollectionChanged;
            if (e.NewValue is INotifyCollectionChanged newColl)
                newColl.CollectionChanged += donut.OnCollectionChanged;
            donut.Redraw();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        private void Redraw()
        {
            ArcCanvas.Children.Clear();

            double size = Math.Min(Root.ActualWidth, Root.ActualHeight);
            if (size <= Thickness * 2) return;

            double radius = (size - Thickness) / 2;
            var center = new Point(Root.ActualWidth / 2, Root.ActualHeight / 2);

            var segments = Segments?.Where(s => s.Fraction > 0).ToList() ?? new();

            // Track background ring so a partially-filled window still reads as a full dial.
            ArcCanvas.Children.Add(FullRing(center, radius, TrackColor()));

            if (segments.Count == 0) return;

            if (segments.Count == 1)
            {
                ArcCanvas.Children.Add(FullRing(center, radius, segments[0].Color));
                return;
            }

            double startAngle = -90; // 12 o'clock
            foreach (var seg in segments)
            {
                double sweep = seg.Fraction * 360;
                double drawSweep = Math.Max(0, sweep - GapDegrees);
                if (drawSweep <= 0.1) { startAngle += sweep; continue; }

                ArcCanvas.Children.Add(Arc(center, radius, startAngle + GapDegrees / 2,
                    startAngle + GapDegrees / 2 + drawSweep, seg.Color));
                startAngle += sweep;
            }
        }

        private static Path Arc(Point center, double radius, double startDeg, double endDeg, Color color)
        {
            Point start = OnCircle(center, radius, startDeg);
            Point end = OnCircle(center, radius, endDeg);
            bool large = (endDeg - startDeg) > 180;

            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = large,
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = Thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
        }

        private static Path FullRing(Point center, double radius, Color color) => new()
        {
            Data = new EllipseGeometry { Center = center, RadiusX = radius, RadiusY = radius },
            Stroke = new SolidColorBrush(color),
            StrokeThickness = Thickness,
        };

        private static Point OnCircle(Point center, double radius, double degrees)
        {
            double rad = degrees * Math.PI / 180;
            return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
        }

        private Color TrackColor()
        {
            // Subtle neutral track that works in both themes.
            if (Application.Current.Resources.TryGetValue("ControlStrokeColorDefault", out var v) && v is Color c)
                return Color.FromArgb(90, c.R, c.G, c.B);
            return Color.FromArgb(40, 128, 128, 128);
        }
    }
}
