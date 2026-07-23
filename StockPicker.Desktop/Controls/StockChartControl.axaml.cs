using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using StockPicker.Models;

namespace StockPicker.Desktop.Controls;

/// <summary>
/// Renders a weekly closing-price area chart with gridlines and axis labels.
/// Driven by the <see cref="Bars"/> styled property.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION (Avalonia port of <c>StockPicker/Controls/StockChartControl.xaml(.cs)</c>):
/// <list type="bullet">
///   <item>WPF <c>DependencyProperty</c> + <c>PropertyChangedCallback→Redraw</c> →
///     Avalonia <c>StyledProperty</c> registered with <c>AffectsRender</c>. Any change to
///     <see cref="Bars"/> or <see cref="IsLoading"/> invalidates the visual and re-invokes
///     <see cref="Render"/>; a size change re-renders automatically, so the WPF
///     <c>SizeChanged</c>/<c>Loaded</c> redraw wiring is no longer needed.</item>
///   <item>The original built <c>System.Windows.Shapes</c> objects (Line/Polygon/Polyline/
///     Ellipse/TextBlock) into a <c>Canvas</c>. This port draws directly in
///     <see cref="Render"/> via <c>DrawingContext</c> (DrawLine / DrawGeometry / DrawEllipse
///     / DrawText). This is the lowest-risk, most idiomatic Avalonia approach: it removes all
///     per-child object churn and event wiring while reproducing the exact same geometry and
///     palette. The loading overlay stays in XAML (drawn on top of the Render output).</item>
///   <item>Type map: <c>System.Windows.Media.Color/SolidColorBrush</c> →
///     <c>Avalonia.Media.*</c> (same <c>FromRgb</c>/<c>FromArgb</c>); axis labels →
///     <c>FormattedText</c>; the area polygon and price polyline → <c>StreamGeometry</c>;
///     the last-price dot → <c>DrawEllipse</c>.</item>
/// </list>
/// </remarks>
public partial class StockChartControl : UserControl
{
    // ── Styled properties ─────────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<WeeklyBar>?> BarsProperty =
        AvaloniaProperty.Register<StockChartControl, IReadOnlyList<WeeklyBar>?>(nameof(Bars));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<StockChartControl, bool>(nameof(IsLoading));

    /// <summary>The weekly bars to plot.</summary>
    public IReadOnlyList<WeeklyBar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>When true shows a "Loading…" overlay instead of the chart.</summary>
    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    // ── Palette (1:1 with the WPF original) ───────────────────────────────

    static readonly Color BgColor        = Color.FromRgb(0x1C, 0x1E, 0x2A);
    static readonly Color GridColor      = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
    static readonly Color AxisLabelColor = Color.FromRgb(0x88, 0x8C, 0xA0);
    static readonly Color LineColorUp    = Color.FromRgb(0x26, 0xC6, 0x8A); // green
    static readonly Color LineColorDown  = Color.FromRgb(0xEF, 0x53, 0x50); // red
    static readonly Color FillColorUp    = Color.FromArgb(0x33, 0x26, 0xC6, 0x8A);
    static readonly Color FillColorDown  = Color.FromArgb(0x33, 0xEF, 0x53, 0x50);

    static readonly IBrush BgBrush        = new SolidColorBrush(BgColor);
    static readonly IBrush GridBrush      = new SolidColorBrush(GridColor);
    static readonly IBrush AxisLabelBrush = new SolidColorBrush(AxisLabelColor);
    static readonly Typeface LabelTypeface = new Typeface("Segoe UI");

    // ── Layout constants (pixels) ─────────────────────────────────────────
    const double PadLeft   = 56;
    const double PadRight  = 12;
    const double PadTop    = 12;
    const double PadBottom = 28;

    static StockChartControl()
    {
        // WPF PropertyChangedCallback→Redraw replacement: invalidate + re-render on change.
        AffectsRender<StockChartControl>(BarsProperty, IsLoadingProperty);
    }

    public StockChartControl()
    {
        InitializeComponent();
        UpdateLoadingState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLoadingProperty)
            UpdateLoadingState();
    }

    private void UpdateLoadingState()
    {
        // LoadingText is the x:Named overlay from the .axaml; may be null before load.
        if (LoadingText is not null)
            LoadingText.IsVisible = IsLoading;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;

        // Background (the WPF Canvas had Background="#1C1E2A").
        context.FillRectangle(BgBrush, new Rect(0, 0, w, h));

        // While loading, only paint the background; the XAML overlay shows on top.
        if (IsLoading) return;

        var bars = Bars;
        if (bars == null || bars.Count < 2) return;
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
        var lineBrush = new SolidColorBrush(lineColor);
        var fillBrush = new SolidColorBrush(fillColor);

        // ── Gridlines + Y-axis labels ─────────────────────────────────────
        var gridPen = new Pen(GridBrush, 0.5);
        int gridLines = 4;
        for (int gi = 0; gi <= gridLines; gi++)
        {
            double fraction = (double)gi / gridLines;
            double py = PadTop + plotH * fraction;

            context.DrawLine(gridPen, new Point(PadLeft, py), new Point(w - PadRight, py));

            // Y-axis price label, right-aligned within the left gutter (PadLeft - 4)
            double priceAtLine = maxP - range * fraction;
            var ft = new FormattedText(
                FormatPrice(priceAtLine), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelTypeface, 9, AxisLabelBrush)
            {
                TextAlignment = TextAlignment.Right,
                MaxTextWidth  = PadLeft - 4,
            };
            context.DrawText(ft, new Point(0, py - 6));
        }

        // ── X-axis month labels ───────────────────────────────────────────
        var monthsSeen = new HashSet<string>();
        int n = bars.Count;
        for (int i = 0; i < n; i++)
        {
            string monthKey = bars[i].WeekStart.ToString("MMM yy");
            if (monthsSeen.Add(monthKey))
            {
                double px = PadLeft + (i / (double)(n - 1)) * plotW;
                var ft = new FormattedText(
                    bars[i].WeekStart.ToString("MMM"), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, LabelTypeface, 9, AxisLabelBrush);
                context.DrawText(ft, new Point(px - 10, h - PadBottom + 4));
            }
        }

        // ── Compute close-price points ────────────────────────────────────
        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double px = PadLeft + (i / (double)(n - 1)) * plotW;
            double py = PadTop  + (1.0 - ((double)bars[i].Close - minP) / range) * plotH;
            pts[i] = new Point(px, py);
        }

        // ── Filled area polygon ───────────────────────────────────────────
        var fillGeo = new StreamGeometry();
        using (var gctx = fillGeo.Open())
        {
            gctx.BeginFigure(pts[0], isFilled: true);
            for (int i = 1; i < n; i++) gctx.LineTo(pts[i]);
            gctx.LineTo(new Point(pts[^1].X, PadTop + plotH)); // bottom-right
            gctx.LineTo(new Point(pts[0].X,  PadTop + plotH)); // bottom-left
            gctx.EndFigure(isClosed: true);
        }
        context.DrawGeometry(fillBrush, null, fillGeo);

        // ── Price line ────────────────────────────────────────────────────
        var lineGeo = new StreamGeometry();
        using (var gctx = lineGeo.Open())
        {
            gctx.BeginFigure(pts[0], isFilled: false);
            for (int i = 1; i < n; i++) gctx.LineTo(pts[i]);
            gctx.EndFigure(isClosed: false);
        }
        var linePen = new Pen(lineBrush, 1.5) { LineJoin = PenLineJoin.Round };
        context.DrawGeometry(null, linePen, lineGeo);

        // ── Last-price dot ────────────────────────────────────────────────
        var lastPt = pts[^1];
        context.DrawEllipse(lineBrush, null, lastPt, 3, 3);
    }

    private static string FormatPrice(double price)
        => price >= 1000 ? $"{price:N0}" : $"{price:N2}";
}
