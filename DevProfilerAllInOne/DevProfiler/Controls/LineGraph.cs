using System.Windows;
using System.Windows.Media;

namespace DevProfiler.Controls;

public sealed class LineGraph : FrameworkElement
{
    private IReadOnlyList<double> _values = [];
    private string _title = string.Empty;
    private Brush _lineBrush = Brushes.Cyan;
    private string _unit = string.Empty;

    public void SetData(string title, IEnumerable<double> values, Brush lineBrush, string unit)
    {
        _title = title;
        _values = values.TakeLast(300).ToArray();
        _lineBrush = lineBrush;
        _unit = unit;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 20 || height <= 20)
            return;

        var background = new SolidColorBrush(Color.FromRgb(28, 33, 40));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(48, 54, 61)), 1);
        drawingContext.DrawRectangle(background, border, new Rect(0, 0, width, height));

        var title = new FormattedText(
            _title,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            12,
            _lineBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(title, new Point(8, 6));

        if (_values.Count == 0)
            return;

        double left = 8;
        double top = 28;
        double right = width - 8;
        double bottom = height - 20;
        double min = Math.Min(0, _values.Min());
        double max = _values.Max();
        if (Math.Abs(max - min) < 0.0001)
            max = min + 1;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = 0; index < _values.Count; index++)
            {
                double x = _values.Count == 1 ? left : left + (right - left) * index / (_values.Count - 1d);
                double y = bottom - (_values[index] - min) / (max - min) * (bottom - top);
                if (index == 0) context.BeginFigure(new Point(x, y), false, false);
                else context.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(_lineBrush, 1.5), geometry);

        string label = $"max {max:N1}{_unit}";
        var maximum = new FormattedText(
            label,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            10,
            new SolidColorBrush(Color.FromRgb(139, 148, 158)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(maximum, new Point(Math.Max(8, width - maximum.Width - 8), 7));
    }
}
