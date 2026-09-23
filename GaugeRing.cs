using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BFV_FPS_Monitor;

/// <summary>
/// 环形仪表：外圈进度弧 + 中央数值 + 下方标签（游戏加加性能报告风格）。
/// </summary>
public sealed class GaugeRing : FrameworkElement
{
    private double _value;          // 0..100
    private double _display;        // 中央显示值
    private string _label = "";
    private string _sub = "";
    private Color _color = Color.FromRgb(0x3D, 0x9B, 0xFF);

    public GaugeRing()
    {
        SnapsToDevicePixels = true;
    }

    public void Set(double value, double display, string label, string sub, Color color)
    {
        _value = Math.Clamp(value, 0, 100);
        _display = display;
        _label = label;
        _sub = sub;
        _color = color;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 30 || h < 30) return;

        double ringSize = Math.Min(w, h - 26);
        double cx = w / 2, cy = ringSize / 2 + 2;
        double r = ringSize / 2 - 5;
        if (r < 10) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ftNum = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var ftLbl = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // 背景环
        var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x8B, 0x97, 0xA8)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, new Point(cx, cy), r, r);

        // 进度弧（从顶部顺时针）
        if (_value > 0.5)
        {
            double sweep = _value / 100.0 * 359.9;
            var start = AnglePoint(cx, cy, r - 0.5, -90);
            var end = AnglePoint(cx, cy, r - 0.5, -90 + sweep);
            bool large = sweep > 180;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(start, false, false);
                g.ArcTo(end, new Size(r - 0.5, r - 0.5), 0, large, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            var colPen = new Pen(new SolidColorBrush(_color), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            colPen.Freeze();
            dc.DrawGeometry(null, colPen, geo);
        }

        // 中央数值
        string valText = _display >= 100 ? _display.ToString("F0", CultureInfo.InvariantCulture) : _display.ToString("F1", CultureInfo.InvariantCulture);
        double fs = valText.Length > 4 ? 15 : 19;
        var vt = new FormattedText(valText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ftNum, fs, new SolidColorBrush(_color), dpi);
        dc.DrawText(vt, new Point(cx - vt.Width / 2, cy - vt.Height / 2 - 2));

        // 标签
        if (!string.IsNullOrEmpty(_label))
        {
            var lt = new FormattedText(_label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ftLbl, 10.5,
                new SolidColorBrush(Color.FromRgb(0x8B, 0x97, 0xA8)), dpi);
            dc.DrawText(lt, new Point(cx - lt.Width / 2, cy + r - 2));
        }

        // 副标签（环下方）
        if (!string.IsNullOrEmpty(_sub))
        {
            var st = new FormattedText(_sub, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ftLbl, 10,
                new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF4)), dpi);
            dc.DrawText(st, new Point(cx - st.Width / 2, h - st.Height - 2));
        }
    }

    private static Point AnglePoint(double cx, double cy, double r, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }
}
