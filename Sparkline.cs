using System.Windows;
using System.Windows.Media;

namespace BFV_FPS_Monitor;

/// <summary>迷你走势线（无坐标轴），用于桌面磁贴：占用/温度曲线。</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly List<double> _data = new();
    private double _max = 100;
    private Pen _pen = new(Brushes.LimeGreen, 1.6);

    public void SetStyle(Brush line, double max)
    {
        _max = max <= 0 ? 100 : max;
        _pen = new Pen(line, 1.6) { LineJoin = PenLineJoin.Round };
        _pen.Freeze();
        InvalidateVisual();
    }

    public void Update(IEnumerable<double> values)
    {
        _data.Clear();
        _data.AddRange(values);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 6) return;

        var basePen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
        basePen.Freeze();
        dc.DrawLine(basePen, new Point(0, h - 0.5), new Point(w, h - 0.5));

        if (_data.Count < 2) return;
        int n = _data.Count;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double stepX = w / Math.Max(1, n - 1);
            bool started = false;
            for (int i = 0; i < n; i++)
            {
                double x = i * stepX;
                double y = h - Math.Clamp(_data[i] / _max, 0, 1) * (h - 2) - 1;
                if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; }
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, _pen, geo);
    }
}
