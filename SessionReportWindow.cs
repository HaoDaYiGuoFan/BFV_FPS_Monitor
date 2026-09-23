using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BFV_FPS_Monitor;

/// <summary>
/// 游戏会话性能报告（对标游戏加加"性能报告 1.0"）：
/// 顶部游戏信息条 + 环形仪表总览（FPS/CPU/GPU/内存）+ FPS 折线图 + 硬件状态区块。
/// </summary>
public class SessionReportWindow : Window
{
    private readonly GameSession _s;
    private ChartPlot _chart = null!;
    private ComboBox _metricCombo = null!;
    private TextBlock _legendText = null!;

    public SessionReportWindow(GameSession s)
    {
        _s = s;
        try
        {
            BuildWindow(s);
        }
        catch (Exception ex)
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "report_ctor_error.txt"), ex.ToString(), System.Text.Encoding.UTF8); } catch { }
            throw;
        }
    }

    private void BuildWindow(GameSession s)
    {
        Title = "性能报告";
        Width = 1080;
        Height = 760;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)FindResource("BgBase");
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")); } catch { }

        Build();
    }

    private void Build()
    {
        var root = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 头部信息
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 仪表区
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 图表
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 硬件状态

        // ===== 头部：游戏名 + 时间 + 分辨率 =====
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 10) };
        head.Children.Add(new TextBlock
        {
            Text = _s.GameName,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(Muted($"　开始 {_s.Start:HH:mm:ss} · 结束 {_s.End:HH:mm:ss} · 时长 {_s.DurationText} · 分辨率 {_s.Resolution}"));
        var exportBtn = Btn("导出 JSON", ExportClick);
        exportBtn.HorizontalAlignment = HorizontalAlignment.Right;
        exportBtn.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(exportBtn, Dock.Right);
        var headDock = new DockPanel();
        headDock.Children.Add(exportBtn);
        headDock.Children.Add(head);
        root.Children.Add(headDock);
        Grid.SetRow(headDock, 0);

        // ===== 仪表区：FPS | CPU占用 | CPU温度 | GPU占用 | GPU温度 | 内存 =====
        var gauges = new UniformGrid { Columns = 6, Margin = new Thickness(0, 4, 0, 8), Height = 150 };
        gauges.Children.Add(MakeGauge(_s.FpsAvg, _s.FpsAvg, "平均 FPS", $"最低 {_s.FpsMin:F0} / 最高 {_s.FpsMax:F0}", ColorFromKey("AccentOrange")));
        gauges.Children.Add(MakeGauge(_s.CpuLoadAvg, _s.CpuLoadAvg, "CPU 占用 %", $"最低 {_s.CpuLoadMin:F0} / 最高 {_s.CpuLoadMax:F0}", ColorFromKey("AccentGreen")));
        gauges.Children.Add(MakeGauge(_s.CpuTempMax > 0 ? _s.CpuTempAvg : 0, _s.CpuTempAvg, "CPU 温度 °C", $"最低 {_s.CpuTempMin:F0} / 最高 {_s.CpuTempMax:F0}", ColorFromKey("AccentBlue")));
        gauges.Children.Add(MakeGauge(_s.GpuLoadAvg, _s.GpuLoadAvg, "GPU 占用 %", $"最低 {_s.GpuLoadMin:F0} / 最高 {_s.GpuLoadMax:F0}", ColorFromKey("AccentPurple")));
        gauges.Children.Add(MakeGauge(_s.GpuTempMax > 0 ? _s.GpuTempAvg : 0, _s.GpuTempAvg, "GPU 温度 °C", $"最低 {_s.GpuTempMin:F0} / 最高 {_s.GpuTempMax:F0}", ColorFromKey("AccentBlue")));
        double ramPct = _s.RamAvgGb > 0 && App.Engine?.Static?.RamInfo != null ? _s.RamAvgGb / Math.Max(_s.RamMaxGb, 0.1) * 100 : 0;
        gauges.Children.Add(MakeGauge(ramPct, _s.RamAvgGb, "内存 GB", $"峰值 {_s.RamMaxGb:F1} GB", ColorFromKey("AccentOrange")));
        root.Children.Add(gauges);
        Grid.SetRow(gauges, 1);

        // ===== FPS 相关统计条（Avg/Max/Min/1%Low/0.1%Low） =====
        var statRow = new UniformGrid { Columns = 5, Margin = new Thickness(0, 2, 0, 6) };
        AddStat(statRow, "FPS 相关 · 平均", _s.FpsAvg, ColorFromKey("AccentOrange"));
        AddStat(statRow, "最大", _s.FpsMax, ColorFromKey("AccentBlue"));
        AddStat(statRow, "最小", _s.FpsMin, ColorFromKey("AccentRed"));
        AddStat(statRow, "1% Low", _s.FpsOneLow, ColorFromKey("AccentGreen"));
        AddStat(statRow, "0.1% Low", _s.FpsPointOneLow, ColorFromKey("AccentOrange"));
        root.Children.Add(statRow);
        Grid.SetRow(statRow, 2);

        // ===== 折线图 + 硬件状态（占剩余空间） =====
        var bottom = new Grid();
        bottom.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var chartCard = Card();
        var chartHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var combo = new ComboBox { Style = (Style)FindResource("DarkCombo"), Width = 170, MinHeight = 28 };
        foreach (var m in new[] { "FPS", "CPU 占用 %", "CPU 温度 °C", "GPU 占用 %", "GPU 温度 °C", "内存 GB", "下载 Kbps", "上传 Kbps" })
            combo.Items.Add(m);
        combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) => RedrawChart();
        _metricCombo = combo;
        DockPanel.SetDock(combo, Dock.Left);
        chartHead.Children.Add(combo);
        _legendText = Muted("");
        _legendText.HorizontalAlignment = HorizontalAlignment.Right;
        _legendText.VerticalAlignment = VerticalAlignment.Center;
        chartHead.Children.Add(_legendText);
        var chartGrid = new Grid();
        chartGrid.Children.Add(chartHead);
        _chart = new ChartPlot();
        chartGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(chartHead, 0);
        Grid.SetRow(_chart, 1);
        chartGrid.Children.Add(_chart);
        chartCard.Child = chartGrid;
        Grid.SetRow(chartCard, 0);
        bottom.Children.Add(chartCard);

        // 硬件状态区块
        var hw = Card();
        var hwGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        hwGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hwGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var st = App.Engine?.Static;
        var leftCol = new StackPanel();
        AddHwLine(leftCol, "处理器", st?.CpuName ?? "—");
        AddHwLine(leftCol, "显卡", st?.GpuName ?? "—");
        AddHwLine(leftCol, "内存", st?.RamInfo ?? "—");
        AddHwLine(leftCol, "系统", st?.OsName ?? "—");
        var rightCol = new StackPanel();
        AddHwLine(rightCol, "主板", st?.Motherboard ?? "—");
        AddHwLine(rightCol, "硬盘", st?.DiskModel ?? "—");
        AddHwLine(rightCol, "显示器", st?.DisplayInfo ?? "—");
        AddHwLine(rightCol, "功耗/能耗", $"CPU {_s.CpuPowerAvgW:F0}W · GPU {_s.GpuPowerAvgW:F0}W · 本次约 {_s.EnergyKwh * 1000:F1} Wh · CO₂ ≈ {_s.Co2Grams:F1} g");
        Grid.SetColumn(leftCol, 0);
        Grid.SetColumn(rightCol, 1);
        hwGrid.Children.Add(leftCol);
        hwGrid.Children.Add(rightCol);
        hw.Child = hwGrid;
        Grid.SetRow(hw, 1);
        bottom.Children.Add(hw);

        root.Children.Add(bottom);
        Grid.SetRow(bottom, 3);
        Content = root;
    }

    private void RedrawChart()
    {
        if (_chart == null || _metricCombo == null) return;
        var m = _metricCombo.SelectedItem as string ?? "FPS";
        var pts = _s.Samples;
        if (pts.Count == 0)
        {
            _chart.SetSeries("", Brushes.Orange, 100, Math.Max(30, (int)_s.Duration.TotalSeconds));
            _chart.UpdateValues(Array.Empty<double>());
            return;
        }
        double max = 100;
        Brush line = Brushes.Orange;
        var vals = new List<double>();
        switch (m)
        {
            case "FPS":
                vals = pts.Select(p => p.Fps).ToList(); max = Math.Max(60, _s.FpsMax * 1.15); line = Brushes.Orange; break;
            case "CPU 占用 %":
                vals = pts.Select(p => p.CpuLoad).ToList(); max = 100; line = (Brush)FindResource("AccentGreen"); break;
            case "CPU 温度 °C":
                vals = pts.Select(p => p.CpuTemp).ToList(); max = 100; line = (Brush)FindResource("AccentBlue"); break;
            case "GPU 占用 %":
                vals = pts.Select(p => p.GpuLoad).ToList(); max = 100; line = (Brush)FindResource("AccentPurple"); break;
            case "GPU 温度 °C":
                vals = pts.Select(p => p.GpuTemp).ToList(); max = 100; line = (Brush)FindResource("AccentBlue"); break;
            case "内存 GB":
                vals = pts.Select(p => p.RamUsedGb).ToList(); max = Math.Max(4, _s.RamMaxGb * 1.2); line = Brushes.Gold; break;
            case "下载 Kbps":
                vals = pts.Select(p => p.DownKbps).ToList(); max = Math.Max(1024, pts.Count > 0 ? pts.Max(p => p.DownKbps) * 1.2 : 1024); line = Brushes.DodgerBlue; break;
            case "上传 Kbps":
                vals = pts.Select(p => p.UpKbps).ToList(); max = Math.Max(512, pts.Count > 0 ? pts.Max(p => p.UpKbps) * 1.2 : 512); line = (Brush)FindResource("AccentPurple"); break;
        }
        int span = Math.Max(30, (int)_s.Duration.TotalSeconds);
        _chart.SetSeries("", line, max, span);
        _chart.UpdateValues(vals);
        var lo = vals.Where(x => x > 0).DefaultIfEmpty(0).ToList();
        _legendText.Text = vals.Count > 0
            ? $"最大 {vals.Max():F0} · 最小 {lo.Min():F0} · 平均 {vals.Where(x => x > 0).DefaultIfEmpty(0).Average():F1} · 采样 {vals.Count} 点"
            : "无采样数据";
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, $"report_{_s.GameName}_{_s.Start:yyyyMMdd_HHmmss}.json");
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(_s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            MessageBox.Show(this, $"已导出：{path}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ===== 工具方法 =====

    private GaugeRing MakeGauge(double pct, double display, string label, string sub, Color color)
    {
        var g = new GaugeRing();
        g.Set(pct, display, label, sub, color);
        return g;
    }

    private void AddStat(UniformGrid grid, string label, double v, Color c)
    {
        var b = new Border
        {
            Background = (Brush)FindResource("BgPanel"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 6, 10, 8),
        };
        var sp = new StackPanel();
        sp.Children.Add(Muted(label));
        sp.Children.Add(new TextBlock
        {
            Text = v.ToString("F0", CultureInfo.InvariantCulture),
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(c),
        });
        b.Child = sp;
        grid.Children.Add(b);
    }

    private static void AddHwLine(StackPanel sp, string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = 11.5, Foreground = (Brush)Application.Current.FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock { Text = value, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(l, 0);
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        sp.Children.Add(g);
    }

    private Border Card() => new()
    {
        Background = (Brush)FindResource("BgPanel"),
        BorderBrush = (Brush)FindResource("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 10, 14, 10),
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static TextBlock Muted(string t) => new()
    {
        Text = t,
        FontSize = 11.5,
        Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Button Btn(string text, RoutedEventHandler onClick) => new()
    {
        Content = text,
        Style = (Style)FindResource("OverlayToggle"),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private void Btn_Click(object sender, RoutedEventArgs e) { }

    private Color ColorFromKey(string key)
    {
        if (TryFindResource(key) is SolidColorBrush scb) return scb.Color;
        return Colors.DodgerBlue;
    }
}
