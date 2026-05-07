using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StockPicker.Models;

namespace StockPicker.Controls
{
    /// <summary>
    /// Renders a weekly closing-price area chart with gridlines and axis labels.
    /// Driven by the <see cref="Bars"/> dependency property.
    /// </summary>
    public partial class StockChartControl : UserControl
    {
        // ── Dependency properties ─────────────────────────────────────────────

        public static readonly DependencyProperty BarsProperty =
            DependencyProperty.Register(nameof(Bars), typeof(IReadOnlyList<WeeklyBar>),
                typeof(StockChartControl),
                new PropertyMetadata(null, (d, _) => ((StockChartControl)d).Redraw()));

        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.Register(nameof(IsLoading), typeof(bool),
                typeof(StockChartControl),
                new PropertyMetadata(false, (d, _) => ((StockChartControl)d).UpdateLoadingState()));

        /// <summary>The weekly bars to plot.</summary>
        public IReadOnlyList<WeeklyBar>? Bars
        {
            get => (IReadOnlyList<WeeklyBar>?)GetValue(BarsProperty);
            set => SetValue(BarsProperty, value);
        }

        /// <summary>When true shows a "Loading…" overlay instead of the chart.</summary>
        public bool IsLoading
        {
            get => (bool)GetValue(IsLoadingProperty);
            set => SetValue(IsLoadingProperty, value);
        }

        // ── Palette ───────────────────────────────────────────────────────────

        static readonly Color BgColor        = Color.FromRgb(0x1C, 0x1E, 0x2A);
        static readonly Color GridColor      = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
        static readonly Color AxisLabelColor = Color.FromRgb(0x88, 0x8C, 0xA0);
        static readonly Color LineColorUp    = Color.FromRgb(0x26, 0xC6, 0x8A); // green
        static readonly Color LineColorDown  = Color.FromRgb(0xEF, 0x53, 0x50); // red
        static readonly Color FillColorUp    = Color.FromArgb(0x33, 0x26, 0xC6, 0x8A);
        static readonly Color FillColorDown  = Color.FromArgb(0x33, 0xEF, 0x53, 0x50);

        // ── Layout constants (pixels) ─────────────────────────────────────────
        const double PadLeft   = 56;
        const double PadRight  = 12;
        const double PadTop    = 12;
        const double PadBottom = 28;

        public StockChartControl()
        {
            InitializeComponent();
            Loaded += (_, _) => Redraw(); // ensure we draw after first layout pass
        }

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        private void UpdateLoadingState()
        {
            LoadingText.Visibility = IsLoading ? Visibility.Visible : Visibility.Collapsed;
            ChartCanvas.Visibility = IsLoading ? Visibility.Collapsed : Visibility.Visible;
            // If loading just finished and we have bars but the canvas was hidden during
            // the previous Redraw call, re-draw now that the canvas is visible.
            if (!IsLoading && Bars != null)
                Redraw();
        }

        private void Redraw()
        {
            ChartCanvas.Children.Clear();

            var bars = Bars;
            if (bars == null || bars.Count < 2) return;

            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            double plotW = w - PadLeft - PadRight;
            double plotH = h - PadTop  - PadBottom;

            // Price range with a little padding
            double minP = (double)bars.Min(b => b.Low);
            double maxP = (double)bars.Max(b => b.High);
            if (maxP <= minP) maxP = minP + 1;
            double range = maxP - minP;
            minP -= range * 0.04;
            maxP += range * 0.04;
            range = maxP - minP;

            // Determine line colour based on whether we're up or down over the period
            bool isUp = bars[^1].Close >= bars[0].Close;
            var lineColor = isUp ? LineColorUp : LineColorDown;
            var fillColor = isUp ? FillColorUp : FillColorDown;

            // ── Gridlines + Y-axis labels ─────────────────────────────────────
            int gridLines = 4;
            for (int gi = 0; gi <= gridLines; gi++)
            {
                double fraction = (double)gi / gridLines;
                double py = PadTop + plotH * fraction;

                // Gridline — added exactly once
                var gl = new Line
                {
                    X1 = PadLeft, Y1 = py, X2 = w - PadRight, Y2 = py,
                    Stroke = new SolidColorBrush(GridColor),
                    StrokeThickness = 0.5
                };
                ChartCanvas.Children.Add(gl);

                // Y-axis price label
                double priceAtLine = maxP - range * fraction;
                var lbl = new TextBlock
                {
                    Text = FormatPrice(priceAtLine),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(AxisLabelColor),
                    Width = PadLeft - 4,
                    TextAlignment = TextAlignment.Right,
                };
                Canvas.SetLeft(lbl, 0);
                Canvas.SetTop(lbl, py - 6);
                ChartCanvas.Children.Add(lbl);
            }

            // ── X-axis month labels ───────────────────────────────────────────
            var monthsSeen = new System.Collections.Generic.HashSet<string>();
            int n = bars.Count;
            for (int i = 0; i < n; i++)
            {
                string monthKey = bars[i].WeekStart.ToString("MMM yy");
                if (monthsSeen.Add(monthKey))
                {
                    double px = PadLeft + (i / (double)(n - 1)) * plotW;
                    var lbl = new TextBlock
                    {
                        Text = bars[i].WeekStart.ToString("MMM"),
                        FontSize = 9,
                        Foreground = new SolidColorBrush(AxisLabelColor),
                    };
                    Canvas.SetLeft(lbl, px - 10);
                    Canvas.SetTop(lbl, h - PadBottom + 4);
                    ChartCanvas.Children.Add(lbl);
                }
            }

            // ── Compute close-price points ────────────────────────────────────
            var pts = new PointCollection(n);
            for (int i = 0; i < n; i++)
            {
                double px = PadLeft + (i / (double)(n - 1)) * plotW;
                double py = PadTop  + (1.0 - ((double)bars[i].Close - minP) / range) * plotH;
                pts.Add(new Point(px, py));
            }

            // ── Filled area polygon ───────────────────────────────────────────
            var fillPts = new PointCollection(pts);
            fillPts.Add(new Point(pts[^1].X, PadTop + plotH));  // bottom-right
            fillPts.Add(new Point(pts[0].X,  PadTop + plotH));  // bottom-left
            var fill = new Polygon
            {
                Points = fillPts,
                Fill = new SolidColorBrush(fillColor),
                Stroke = Brushes.Transparent,
            };
            ChartCanvas.Children.Add(fill);

            // ── Price line ────────────────────────────────────────────────────
            var line = new Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush(lineColor),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
            };
            ChartCanvas.Children.Add(line);

            // ── Last-price dot ────────────────────────────────────────────────
            var lastPt = pts[^1];
            var dot = new Ellipse
            {
                Width = 6, Height = 6,
                Fill = new SolidColorBrush(lineColor),
            };
            Canvas.SetLeft(dot, lastPt.X - 3);
            Canvas.SetTop(dot,  lastPt.Y - 3);
            ChartCanvas.Children.Add(dot);
        }

        private static string FormatPrice(double price)
            => price >= 1000 ? $"{price:N0}" : $"{price:N2}";
    }
}
