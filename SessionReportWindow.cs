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
    private readonly Action _localizeHandler;

    /// <summary>图表指标 key 顺序与组合框一致。</summary>
    private static readonly string[] MetricKeys = { "Metric.Fps", "Chart.CpuLoad", "Report.CpuTemp", "Chart.GpuLoad", "Report.GpuTemp", "Metric.Ram", "Metric.Down", "Metric.Up" };

    public SessionReportWindow(GameSession s)
    {
        _s = s;
        _localizeHandler = () => { try { Build(); } catch { } };
        Localization.LanguageChanged += _localizeHandler;
        Closed += (_, _) => Localization.LanguageChanged -= _localizeHandler;
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
        Title = Localization.T("Report.Title");
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
        head.Children.Add(Muted(Localization.F("Report.Head", _s.Start.ToString("HH:mm:ss", CultureInfo.InvariantCulture), _s.End.ToString("HH:mm:ss", CultureInfo.InvariantCulture), _s.DurationText, _s.Resolution)));
        var exportBtn = Btn(Localization.T("Report.Export"), ExportClick);
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
        gauges.Children.Add(MakeGauge(_s.FpsAvg, _s.FpsAvg, Localization.T("Report.AvgFps"), Localization.F("Report.MinMax", _s.FpsMin.ToString("F0", CultureInfo.InvariantCulture), _s.FpsMax.ToString("F0", CultureInfo.InvariantCulture)), ColorFromKey("AccentOrange")));
        gauges.Children.Add(MakeGauge(_s.CpuLoadAvg, _s.CpuLoadAvg, Localization.T("Report.CpuPct"), Localization.F("Report.MinMax", _s.CpuLoadMin.ToString("F0", CultureInfo.InvariantCulture), _s.CpuLoadMax.ToString("F0", CultureInfo.InvariantCulture)), ColorFromKey("AccentGreen")));
        gauges.Children.Add(MakeGauge(_s.CpuTempMax > 0 ? _s.CpuTempAvg : 0, _s.CpuTempAvg, Localization.T("Report.CpuTemp"), Localization.F("Report.MinMax", _s.CpuTempMin.ToString("F0", CultureInfo.InvariantCulture), _s.CpuTempMax.ToString("F0", CultureInfo.InvariantCulture)), ColorFromKey("AccentBlue")));
        gauges.Children.Add(MakeGauge(_s.GpuLoadAvg, _s.GpuLoadAvg, Localization.T("Report.GpuPct"), Localization.F("Report.MinMax", _s.GpuLoadMin.ToString("F0", CultureInfo.InvariantCulture), _s.GpuLoadMax.ToString("F0", CultureInfo.InvariantCulture)), ColorFromKey("AccentPurple")));
        gauges.Children.Add(MakeGauge(_s.GpuTempMax > 0 ? _s.GpuTempAvg : 0, _s.GpuTempAvg, Localization.T("Report.GpuTemp"), Localization.F("Report.MinMax", _s.GpuTempMin.ToString("F0", CultureInfo.InvariantCulture), _s.GpuTempMax.ToString("F0", CultureInfo.InvariantCulture)), ColorFromKey("AccentBlue")));
        double ramPct = _s.RamAvgGb > 0 && App.Engine?.Static?.RamInfo != null ? _s.RamAvgGb / Math.Max(_s.RamMaxGb, 0.1) * 100 : 0;
        gauges.Children.Add(MakeGauge(ramPct, _s.RamAvgGb, Localization.T("Report.RamGb"), Localization.F("Report.RamPeak", _s.RamMaxGb.ToString("F1", CultureInfo.InvariantCulture)), ColorFromKey("AccentOrange")));
        root.Children.Add(gauges);
        Grid.SetRow(gauges, 1);

        // ===== FPS 相关统计条（Avg/Max/Min/1%Low/0.1%Low） =====
        var statRow = new UniformGrid { Columns = 5, Margin = new Thickness(0, 2, 0, 6) };
        AddStat(statRow, Localization.T("Report.FpsAvg"), _s.FpsAvg, ColorFromKey("AccentOrange"));
        AddStat(statRow, Localization.T("Report.Max"), _s.FpsMax, ColorFromKey("AccentBlue"));
        AddStat(statRow, Localization.T("Report.Min"), _s.FpsMin, ColorFromKey("AccentRed"));
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
        foreach (var k in MetricKeys)
        {
            // DynamicResource 引用：语言切换时 ComboBoxItem 文本实时跟随，无需重建窗口。
            var ci = new ComboBoxItem();
            ci.SetResourceReference(ContentControl.ContentProperty, k);
            combo.Items.Add(ci);
        }
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
        AddHwLine(leftCol, Localization.T("St.Cpu"), st?.CpuName ?? "—");
        AddHwLine(leftCol, Localization.T("St.Gpu"), st?.GpuName ?? "—");
        AddHwLine(leftCol, Localization.T("St.Ram"), st?.RamInfo ?? "—");
        AddHwLine(leftCol, Localization.T("St.Os"), st?.OsDisplay ?? "—");
        var rightCol = new StackPanel();
        AddHwLine(rightCol, Localization.T("St.Mb"), st?.Motherboard ?? "—");
        AddHwLine(rightCol, Localization.T("St.Disk"), st?.DiskModel ?? "—");
        AddHwLine(rightCol, Localization.T("St.Display"), st?.DisplayInfo ?? "—");
        AddHwLine(rightCol, Localization.T("Report.Power"), Localization.F("Report.PowerVal",
            _s.CpuPowerAvgW.ToString("F0", CultureInfo.InvariantCulture),
            _s.GpuPowerAvgW.ToString("F0", CultureInfo.InvariantCulture),
            (_s.EnergyKwh * 1000.0).ToString("F1", CultureInfo.InvariantCulture),
            _s.Co2Grams.ToString("F1", CultureInfo.InvariantCulture)));
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
        int idx = _metricCombo.SelectedIndex;
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
        switch (idx)
        {
            case 0: // FPS
                vals = pts.Select(p => p.Fps).ToList(); max = Math.Max(60, _s.FpsMax * 1.15); line = Brushes.Orange; break;
            case 1: // CPU 占用 %
                vals = pts.Select(p => p.CpuLoad).ToList(); max = 100; line = (Brush)FindResource("AccentGreen"); break;
            case 2: // CPU 温度 °C
                vals = pts.Select(p => p.CpuTemp).ToList(); max = 100; line = (Brush)FindResource("AccentBlue"); break;
            case 3: // GPU 占用 %
                vals = pts.Select(p => p.GpuLoad).ToList(); max = 100; line = (Brush)FindResource("AccentPurple"); break;
            case 4: // GPU 温度 °C
                vals = pts.Select(p => p.GpuTemp).ToList(); max = 100; line = (Brush)FindResource("AccentBlue"); break;
            case 5: // 内存 GB
                vals = pts.Select(p => p.RamUsedGb).ToList(); max = Math.Max(4, _s.RamMaxGb * 1.2); line = Brushes.Gold; break;
            case 6: // 下载 Kbps
                vals = pts.Select(p => p.DownKbps).ToList(); max = Math.Max(1024, pts.Count > 0 ? pts.Max(p => p.DownKbps) * 1.2 : 1024); line = Brushes.DodgerBlue; break;
            case 7: // 上传 Kbps
                vals = pts.Select(p => p.UpKbps).ToList(); max = Math.Max(512, pts.Count > 0 ? pts.Max(p => p.UpKbps) * 1.2 : 512); line = (Brush)FindResource("AccentPurple"); break;
        }
        int span = Math.Max(30, (int)_s.Duration.TotalSeconds);
        _chart.SetSeries("", line, max, span);
        _chart.UpdateValues(vals);
        var lo = vals.Where(x => x > 0).DefaultIfEmpty(0).ToList();
        var avg = vals.Where(x => x > 0).DefaultIfEmpty(0).Average();
        _legendText.Text = vals.Count > 0
            ? Localization.F("Chart.Legend", vals.Max().ToString("F0", CultureInfo.InvariantCulture), lo.Min().ToString("F0", CultureInfo.InvariantCulture), avg.ToString("F1", CultureInfo.InvariantCulture), vals.Count)
            : Localization.T("Chart.NoData");
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, $"report_{_s.GameName}_{_s.Start:yyyyMMdd_HHmmss}.json");
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(_s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            MessageBox.Show(this, Localization.F("Export.Done", path), Localization.T("Export.Ok"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Localization.T("Export.Fail"), MessageBoxButton.OK, MessageBoxImage.Error); }
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
