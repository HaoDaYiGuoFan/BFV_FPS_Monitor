using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BFV_FPS_Monitor;

/// <summary>
/// 轻量折线图：Y 轴数值刻度 + X 轴时间刻度（-span 秒 → 0）+ 网格线 + 渐变填充折线。
/// 纯 DrawingContext 自绘，无第三方依赖。
/// </summary>
public sealed class ChartPlot : FrameworkElement
{
    private readonly List<double> _data = new();
    private double _max = 100;
    private double _spanSeconds = 120;
    private string _unit = "";
    private Brush _lineColor = Brushes.LimeGreen;

    public ChartPlot()
    {
        SnapsToDevicePixels = true;
    }

    public void SetSeries(string unit, Brush line, double max, int seconds = 120)
    {
        _unit = unit;
        _lineColor = line;
        _max = max <= 0 ? 100 : max;
        _spanSeconds = seconds;
        InvalidateVisual();
    }

    public void UpdateValues(IEnumerable<double> values, double maxOverride = 0)
    {
        _data.Clear();
        _data.AddRange(values);
        if (maxOverride > 0) _max = maxOverride;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 30) return;

        // 布局常量
        const double padL = 44, padR = 8, padT = 8, padB = 20;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        if (plotW < 10 || plotH < 10) return;

        var ink = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF4)), 1); ink.Freeze();
        var dimBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x97, 0xA8)); dimBrush.Freeze();
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x2E, 0x8B, 0x97, 0xA8)), 1); gridPen.Freeze();
        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x8B, 0x97, 0xA8)), 1); axisPen.Freeze();
        var ftNum = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ftLbl = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // ===== 网格 + Y 轴刻度（5 档：0..max） =====
        int ySteps = 5;
        for (int i = 0; i <= ySteps; i++)
        {
            double frac = i / (double)ySteps;
            double y = padT + plotH * (1 - frac);
            double val = _max * frac;

            dc.DrawLine(gridPen, new Point(padL, y), new Point(padL + plotW, y));

            string label = _max >= 10 ? val.ToString("F0", CultureInfo.InvariantCulture) : val.ToString("F1", CultureInfo.InvariantCulture);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ftNum, 10, dimBrush, dpi);
            dc.DrawText(text, new Point(padL - text.Width - 4, y - text.Height / 2));
        }

        // ===== X 轴时间刻度（-120s ... 0s，6 档） =====
        int xSteps = 6;
        for (int i = 0; i <= xSteps; i++)
        {
            double frac = i / (double)xSteps;
            double x = padL + plotW * frac;
            dc.DrawLine(gridPen, new Point(x, padT), new Point(x, padT + plotH));

            int secAgo = (int)Math.Round(_spanSeconds * (1 - frac));
            string label = secAgo == 0 ? "now" : $"-{secAgo}s";
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ftNum, 9.5, dimBrush, dpi);
            dc.DrawText(text, new Point(x - text.Width / 2, padT + plotH + 4));
        }

        // 轴线
        dc.DrawLine(axisPen, new Point(padL, padT), new Point(padL, padT + plotH));
        dc.DrawLine(axisPen, new Point(padL, padT + plotH), new Point(padL + plotW, padT + plotH));

        // ===== 数据折线 =====
        if (_data.Count >= 2)
        {
            int n = _data.Count;
            double stepX = plotW / (_spanSeconds - 1);   // 数据按 1s 采样对齐真实时间轴
            double effStep = n >= _spanSeconds ? plotW / (n - 1) : stepX;
            double x0 = padL + plotW;                     // 最右 = 最新

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool started = false;
                for (int i = 0; i < n; i++)
                {
                    double x = x0 - i * effStep;
                    double y = padT + plotH * (1 - Math.Clamp(_data[n - 1 - i] / _max, 0, 1));
                    if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; }
                    else ctx.LineTo(new Point(x, y), true, false);
                }
            }
            geo.Freeze();

            // 渐变填充
            var scb = _lineColor as SolidColorBrush;
            var lc = scb != null ? scb.Color : Colors.LimeGreen;

            var fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x66, lc.R, lc.G, lc.B), 0),
                    new GradientStop(Color.FromArgb(0x00, lc.R, lc.G, lc.B), 1),
                },
            };
            fill.Freeze();
            var fillGeo = Geometry.Combine(geo, Geometry.Parse($"M0,0 L{plotW},0 L{plotW},{plotH} L0,{plotH} Z"), GeometryCombineMode.Intersect, Transform.Identity);
            dc.DrawGeometry(fill, null, fillGeo);

            var linePen = new Pen(_lineColor, 1.8) { LineJoin = PenLineJoin.Round }; linePen.Freeze();
            dc.DrawGeometry(null, linePen, geo);
        }
        else
        {
            var text = new FormattedText(Localization.T("Chart.Waiting"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ftLbl, 11, dimBrush, dpi);
            dc.DrawText(text, new Point(padL + plotW / 2 - text.Width / 2, padT + plotH / 2 - text.Height / 2));
        }
    }
}
