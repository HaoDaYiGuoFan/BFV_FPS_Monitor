using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BFV_FPS_Monitor;

/// <summary>
/// 性能报告 2.0（对标游戏加加"详情 2.0"）：
/// FPS 相关五大指标卡 + CPU/GPU 区块（温度条 + 小指标墙）+ 内存/硬盘 + 多指标折线图 + 时间轴回放滑条。
/// </summary>
public class SessionDetailWindow : Window
{
    private readonly GameSession _s;
    private ChartPlot _chart = null!;
    private ComboBox _metricCombo = null!;
    private Slider _timeline = null!;
    private TextBlock _timelineLabel = null!;
    private TextBlock _legendText = null!;
    private readonly Action _localizeHandler;

    /// <summary>图表指标 key 顺序与组合框一致。</summary>
    private static readonly string[] MetricKeys = { "Metric.Fps", "Chart.CpuLoad", "Report.CpuTemp", "Chart.GpuLoad", "Report.GpuTemp", "Metric.Ram", "Metric.Down", "Metric.Up" };

    public SessionDetailWindow(GameSession s)
    {
        _s = s;
        _localizeHandler = () => { try { Build(); } catch { } };
        Localization.LanguageChanged += _localizeHandler;
        Closed += (_, _) => Localization.LanguageChanged -= _localizeHandler;
        Title = Localization.T("Detail.Title");
        Width = 1200;
        Height = 860;
        MinWidth = 980;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)FindResource("BgBase");
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")); } catch { }
        Build();
    }

    private void Build()
    {
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };

        // ===== 头部信息条 =====
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var headLeft = new StackPanel { Orientation = Orientation.Horizontal };
        headLeft.Children.Add(new TextBlock { Text = _s.GameName, FontSize = 19, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
        headLeft.Children.Add(Muted(Localization.F("Detail.Head", _s.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), _s.End.ToString("HH:mm:ss", CultureInfo.InvariantCulture), _s.DurationText, _s.Resolution)));
        head.Children.Add(headLeft);
        var exportBtn = new Button { Content = Localization.T("Report.Export"), Style = (Style)FindResource("OverlayToggle"), Padding = new Thickness(12, 5, 12, 5) };
        exportBtn.Click += (_, _) =>
        {
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, $"report_{_s.GameName}_{_s.Start:yyyyMMdd_HHmmss}.json");
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(_s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                MessageBox.Show(this, Localization.F("Export.Done", path), Localization.T("Export.Ok"));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Localization.T("Export.Fail")); }
        };
        DockPanel.SetDock(exportBtn, Dock.Right);
        exportBtn.HorizontalAlignment = HorizontalAlignment.Right;
        exportBtn.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(exportBtn);
        root.Children.Add(head);

        // ===== FPS 相关五大卡 =====
        var fpsSection = SectionTitle(Localization.T("Detail.FpsSection"));
        root.Children.Add(fpsSection);
        var fpsGrid = new UniformGrid { Columns = 5, Margin = new Thickness(0, 4, 0, 10) };
        AddBigStat(fpsGrid, Localization.T("Detail.Avg"), _s.FpsAvg.ToString("F1", CultureInfo.InvariantCulture), "AccentOrange");
        AddBigStat(fpsGrid, Localization.T("Detail.Max"), _s.FpsMax.ToString("F0", CultureInfo.InvariantCulture), "AccentBlue");
        AddBigStat(fpsGrid, Localization.T("Detail.Min"), _s.FpsMin.ToString("F0", CultureInfo.InvariantCulture), "AccentRed");
        AddBigStat(fpsGrid, "1% Low", _s.FpsOneLow.ToString("F1", CultureInfo.InvariantCulture), "AccentGreen");
        AddBigStat(fpsGrid, "0.1% Low", _s.FpsPointOneLow.ToString("F1", CultureInfo.InvariantCulture), "AccentOrange");
        root.Children.Add(fpsGrid);

        var st = App.Engine?.Static;

        // ===== CPU 区块 =====
        root.Children.Add(SectionTitle(Localization.F("Detail.CpuSection", st?.CpuName ?? "—")));
        var cpuGrid = new Grid { Margin = new Thickness(0, 2, 0, 10) };
        cpuGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cpuGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cpuLeft = new StackPanel();
        AddMini(cpuLeft, Localization.T("Detail.Cores"), $"{st?.CpuCores ?? 0} / {st?.CpuThreads ?? 0}");
        AddMini(cpuLeft, Localization.T("Detail.AvgLoad"), Pct(_s.CpuLoadAvg));
        AddMini(cpuLeft, Localization.T("Detail.MinMaxLoad"), $"{Pct(_s.CpuLoadMin)} / {Pct(_s.CpuLoadMax)}");
        AddMini(cpuLeft, Localization.T("Detail.AvgPwr"), W(_s.CpuPowerAvgW));
        var cpuRight = new StackPanel();
        AddMini(cpuRight, Localization.T("Detail.AvgTemp"), T(_s.CpuTempAvg));
        AddMini(cpuRight, Localization.T("Detail.MinMaxTemp"), $"{T(_s.CpuTempMin)} / {T(_s.CpuTempMax)}");
        AddMini(cpuRight, Localization.T("Detail.TotalFrames"), _s.FrameTotal.ToString("N0", CultureInfo.InvariantCulture));
        AddMini(cpuRight, Localization.T("Detail.NetTrafficL"), Localization.F("Detail.NetTraffic", _s.NetDownTotalMb.ToString("F1", CultureInfo.InvariantCulture), _s.NetUpTotalMb.ToString("F1", CultureInfo.InvariantCulture)));
        Grid.SetColumn(cpuLeft, 0);
        Grid.SetColumn(cpuRight, 1);
        var cpuCard = Card();
        var cpuWrap = new Grid();
        cpuWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cpuWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cpuWrap.Children.Add(cpuLeft);
        Grid.SetColumn(cpuRight, 1);
        cpuWrap.Children.Add(cpuRight);
        cpuCard.Child = cpuWrap;
        root.Children.Add(cpuCard);

        // ===== GPU 区块 =====
        root.Children.Add(SectionTitle(Localization.F("Detail.GpuSection", st?.GpuName ?? "—")));
        var gpuCard = Card();
        var gpuWrap = new Grid();
        gpuWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gpuWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gpuLeft = new StackPanel();
        AddMini(gpuLeft, Localization.T("Detail.VramTotal"), st?.GpuVram ?? "—");
        AddMini(gpuLeft, Localization.T("Detail.AvgLoad"), Pct(_s.GpuLoadAvg));
        AddMini(gpuLeft, Localization.T("Detail.MinMaxLoad"), $"{Pct(_s.GpuLoadMin)} / {Pct(_s.GpuLoadMax)}");
        AddMini(gpuLeft, Localization.T("Detail.AvgPwr"), W(_s.GpuPowerAvgW));
        var gpuRight = new StackPanel();
        AddMini(gpuRight, Localization.T("Detail.AvgTemp"), T(_s.GpuTempAvg));
        AddMini(gpuRight, Localization.T("Detail.MinMaxTemp"), $"{T(_s.GpuTempMin)} / {T(_s.GpuTempMax)}");
        AddMini(gpuRight, Localization.T("Detail.AvgVram"), _s.GpuMemAvgGb > 0 ? $"{_s.GpuMemAvgGb:F2} GB" : "—");
        AddMini(gpuRight, Localization.T("Detail.PeakVram"), _s.GpuMemMaxGb > 0 ? $"{_s.GpuMemMaxGb:F2} GB" : "—");
        gpuWrap.Children.Add(gpuLeft);
        Grid.SetColumn(gpuRight, 1);
        gpuWrap.Children.Add(gpuRight);
        gpuCard.Child = gpuWrap;
        root.Children.Add(gpuCard);

        // ===== 内存 / 硬盘 =====
        root.Children.Add(SectionTitle(Localization.T("Detail.MemDisk")));
        var memCard = Card();
        var memWrap = new Grid();
        memWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        memWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var memLeft = new StackPanel();
        AddMini(memLeft, Localization.T("Detail.RamSticks"), st?.RamInfo ?? "—");
        AddMini(memLeft, Localization.T("Detail.AvgPeak"), _s.RamAvgGb > 0 ? $"{_s.RamAvgGb:F1} / {_s.RamMaxGb:F1} GB" : "—");
        var memRight = new StackPanel();
        AddMini(memRight, Localization.T("St.Disk"), $"{st?.DiskModel ?? "—"} {st?.DiskCapacity ?? ""}");
        AddMini(memRight, Localization.T("Detail.Energy"), Localization.F("Detail.EnergyVal", (_s.EnergyKwh * 1000.0).ToString("F1", CultureInfo.InvariantCulture), _s.Co2Grams.ToString("F1", CultureInfo.InvariantCulture)));
        memWrap.Children.Add(memLeft);
        Grid.SetColumn(memRight, 1);
        memWrap.Children.Add(memRight);
        memCard.Child = memWrap;
        root.Children.Add(memCard);

        // ===== 多指标折线 =====
        root.Children.Add(SectionTitle(Localization.T("Detail.Chart")));
        var chartCard = Card();
        var chartRoot = new Grid { Height = 260 };
        chartRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chartRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chartRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var chartHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var combo = new ComboBox { Style = (Style)FindResource("DarkCombo"), Width = 180, MinHeight = 28 };
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
        chartHead.Children.Add(combo);
        _legendText = Muted("");
        _legendText.HorizontalAlignment = HorizontalAlignment.Right;
        _legendText.VerticalAlignment = VerticalAlignment.Center;
        chartHead.Children.Add(_legendText);
        Grid.SetRow(chartHead, 0);
        chartRoot.Children.Add(chartHead);

        _chart = new ChartPlot();
        Grid.SetRow(_chart, 1);
        chartRoot.Children.Add(_chart);

        // 时间轴回放条
        var tl = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _timeline = new Slider
        {
            Minimum = 0,
            Maximum = Math.Max(1, _s.Samples.Count - 1),
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            Value = Math.Max(0, _s.Samples.Count - 1),
        };
        _timelineLabel = Muted("");
        _timelineLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _timelineLabel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_timelineLabel, Dock.Right);
        tl.Children.Add(_timelineLabel);
        tl.Children.Add(_timeline);
        Grid.SetRow(tl, 2);
        chartRoot.Children.Add(tl);
        _timeline.ValueChanged += (_, _) =>
        {
            int idx = (int)_timeline.Value;
            if (_s.Samples.Count > 0 && idx >= 0 && idx < _s.Samples.Count)
            {
                var p = _s.Samples[idx];
                _timelineLabel.Text = Localization.F("Detail.Timeline",
                    p.T.ToString("F0", CultureInfo.InvariantCulture),
                    p.Fps.ToString("F0", CultureInfo.InvariantCulture),
                    p.CpuLoad.ToString("F0", CultureInfo.InvariantCulture),
                    p.CpuTemp.ToString("F0", CultureInfo.InvariantCulture),
                    p.GpuLoad.ToString("F0", CultureInfo.InvariantCulture),
                    p.GpuTemp.ToString("F0", CultureInfo.InvariantCulture));
            }
        };
        chartCard.Child = chartRoot;
        root.Children.Add(chartCard);

        sv.Content = root;
        Content = sv;
        Loaded += (_, _) => RedrawChart();
    }

    private void RedrawChart()
    {
        if (_chart == null || _metricCombo == null) return;
        int idx = _metricCombo.SelectedIndex;
        var pts = _s.Samples;
        var vals = new List<double>();
        double max = 100;
        Brush line = Brushes.Orange;
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
        var pos = vals.Where(x => x > 0).ToList();
        _legendText.Text = vals.Count > 0
            ? Localization.F("Chart.Legend", vals.Max().ToString("F0", CultureInfo.InvariantCulture), pos.DefaultIfEmpty(0).Min().ToString("F0", CultureInfo.InvariantCulture), pos.DefaultIfEmpty(0).Average().ToString("F1", CultureInfo.InvariantCulture), vals.Count)
            : Localization.T("Chart.NoData");
    }

    // ===== 工具 =====

    private TextBlock SectionTitle(string t) => new()
    {
        Text = "★ " + t,
        FontSize = 13,
        FontWeight = FontWeights.Bold,
        Foreground = (Brush)FindResource("TextSecondary"),
        Margin = new Thickness(2, 6, 0, 6),
    };

    private void AddBigStat(UniformGrid grid, string label, string value, string colorKey)
    {
        var b = new Border
        {
            Background = (Brush)FindResource("BgPanel"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 8, 12, 10),
        };
        var sp = new StackPanel();
        sp.Children.Add(Muted(label));
        sp.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 30,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource(colorKey),
        });
        b.Child = sp;
        grid.Children.Add(b);
    }

    private void AddMini(StackPanel sp, string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 8, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = 11.5, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(l, 0);
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        var bd = new Border { Background = (Brush)FindResource("BgPanelLight"), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 2, 0, 2) };
        bd.Child = g;
        sp.Children.Add(bd);
    }

    private Border Card() => new()
    {
        Background = (Brush)FindResource("BgPanel"),
        BorderBrush = (Brush)FindResource("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 10, 14, 10),
        Margin = new Thickness(0, 0, 0, 4),
    };

    private TextBlock Muted(string t) => new()
    {
        Text = t,
        FontSize = 11.5,
        Foreground = (Brush)FindResource("TextSecondary"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static string Pct(double v) => double.IsNaN(v) ? "—" : $"{v:F0} %";
    private static string T(double v) => double.IsNaN(v) || v <= 0 ? "—" : $"{v:F0} °C";
    private static string W(double v) => double.IsNaN(v) || v <= 0 ? "—" : $"{v:F1} W";
}
