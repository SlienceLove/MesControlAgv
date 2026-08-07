using System.Windows;
using System.Windows.Media;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Controls;

public sealed class KpiDonutChart : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices), typeof(IReadOnlyList<KpiStatusSlice>), typeof(KpiDonutChart),
        new FrameworkPropertyMetadata(Array.Empty<KpiStatusSlice>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<KpiStatusSlice> Slices
    {
        get => (IReadOnlyList<KpiStatusSlice>)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 8);
        if (radius <= 4) return;
        var innerRadius = radius * 0.58;
        var total = Slices.Sum(slice => Math.Max(0, slice.Value));
        if (total == 0)
        {
            drawingContext.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(220, 226, 234)), radius * 0.28), center, radius * 0.78, radius * 0.78);
            return;
        }

        var angle = -90d;
        foreach (var slice in Slices.Where(slice => slice.Value > 0))
        {
            var sweep = slice.Value * 360d / total;
            drawingContext.DrawGeometry(new SolidColorBrush((Color)ColorConverter.ConvertFromString(slice.Color)), null, CreateSegment(center, radius, innerRadius, angle, sweep));
            angle += sweep;
        }
    }

    private static Geometry CreateSegment(Point center, double outerRadius, double innerRadius, double startAngle, double sweepAngle)
    {
        var startOuter = PointAt(center, outerRadius, startAngle);
        var endOuter = PointAt(center, outerRadius, startAngle + sweepAngle);
        var endInner = PointAt(center, innerRadius, startAngle + sweepAngle);
        var startInner = PointAt(center, innerRadius, startAngle);
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(startOuter, true, true);
        context.ArcTo(endOuter, new Size(outerRadius, outerRadius), 0, sweepAngle > 180, SweepDirection.Clockwise, true, false);
        context.LineTo(endInner, true, false);
        context.ArcTo(startInner, new Size(innerRadius, innerRadius), 0, sweepAngle > 180, SweepDirection.Counterclockwise, true, false);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointAt(Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}

public sealed class KpiTrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<Services.KpiTaskTrendPoint>), typeof(KpiTrendChart),
        new FrameworkPropertyMetadata(Array.Empty<Services.KpiTaskTrendPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Services.KpiTaskTrendPoint> Points
    {
        get => (IReadOnlyList<Services.KpiTaskTrendPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var padding = new Thickness(16, 12, 16, 24);
        var width = Math.Max(0, ActualWidth - padding.Left - padding.Right);
        var height = Math.Max(0, ActualHeight - padding.Top - padding.Bottom);
        if (width <= 1 || height <= 1) return;
        var background = new SolidColorBrush(Color.FromRgb(250, 251, 253));
        drawingContext.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var values = Points.SelectMany(point => new[] { point.Created, point.Completed }).ToList();
        var max = Math.Max(1, values.DefaultIfEmpty(0).Max());
        var chart = new Rect(padding.Left, padding.Top, width, height);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(225, 230, 237)), 1);
        for (var row = 0; row <= 4; row++)
        {
            var y = chart.Top + chart.Height * row / 4;
            drawingContext.DrawLine(gridPen, new Point(chart.Left, y), new Point(chart.Right, y));
        }
        DrawSeries(drawingContext, chart, max, Points.Select(point => point.Created).ToList(), Color.FromRgb(47, 128, 237));
        DrawSeries(drawingContext, chart, max, Points.Select(point => point.Completed).ToList(), Color.FromRgb(39, 174, 96));
    }

    private static void DrawSeries(DrawingContext dc, Rect chart, int max, IReadOnlyList<int> values, Color color)
    {
        if (values.Count == 0) return;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        for (var index = 0; index < values.Count; index++)
        {
            var x = values.Count == 1 ? chart.Left : chart.Left + chart.Width * index / (values.Count - 1);
            var y = chart.Bottom - chart.Height * values[index] / max;
            if (index == 0) context.BeginFigure(new Point(x, y), false, false);
            else context.LineTo(new Point(x, y), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2), geometry);
        var brush = new SolidColorBrush(color);
        for (var index = 0; index < values.Count; index++)
        {
            var x = values.Count == 1 ? chart.Left : chart.Left + chart.Width * index / (values.Count - 1);
            var y = chart.Bottom - chart.Height * values[index] / max;
            dc.DrawEllipse(brush, null, new Point(x, y), 3, 3);
        }
    }
}
