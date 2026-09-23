using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BFV_FPS_Monitor;

/// <summary>
/// 桌面监控磁贴（游戏加加风格）：三种风格（紧凑横条 / 卡片网格 / 任务栏细条），
/// 鼠标穿透可开关，主窗口最小化时自动折叠为任务栏角落小条，位置记忆。
/// </summary>
public sealed class TileWindow : Window
{
    private readonly AppSettings _settings;
    private bool _mini;
    private bool _built;
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IntPtr _hwnd;

    // 动态元素引用
    private StackPanel? _host;
    private readonly Dictionary<string, TextBlock> _txt = new();
    private readonly Dictionary<string, ProgressBar> _bar = new();
    private readonly Dictionary<string, Sparkline> _spark = new();
    private readonly Queue<double> _cpuLoadHist = new();
    private readonly Queue<double> _cpuTempHist = new();
    private readonly Queue<double> _gpuLoadHist = new();
    private readonly Queue<double> _gpuTempHist = new();
    private int _sparkTick;

    private static readonly Brush Bg = Solid(0xE6101418);
    private static readonly Brush Chip = Solid(0xFF232C3A);
    private static readonly Brush Gray = Solid(0xFF8B97A8);
    private static readonly Brush Ink = Solid(0xFFE8EDF4);
    private static readonly Brush Green = Solid(0xFF3DDC84);
    private static readonly Brush Blue = Solid(0xFF4DA3FF);
    private static readonly Brush Orange = Solid(0xFFFF9F43);
    private static readonly Brush Purple = Solid(0xFFB57BFF);
    private static readonly Brush Red = Solid(0xFFFF5A5A);

    private static SolidColorBrush Solid(uint rgb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(rgb >> 24), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    // ===== Win32 穿透 =====
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    public bool IsMini => _mini;

    /// <summary>安全获取全局 HBarTemplate（App.xaml 资源；找不到时返回 null 用默认模板）。</summary>
    private static ControlTemplate? HBarTemplateSafe
    {
        get
        {
            try { return Application.Current.TryFindResource("HBarTemplate") as ControlTemplate; }
            catch { return null; }
        }
    }
    public TileWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "桌面监控磁贴";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = false; // 不置顶：与普通窗口同级，不遮挡其他应用（用户要求）
        ShowInTaskbar = _settings.TileShowInTaskbar;
        ShowActivated = false;
        Width = 640; Height = 44;
        Left = 24; Top = 120;
        Cursor = Cursors.SizeAll;
        MouseLeftButtonDown += Tile_MouseLeftButtonDown;
        Loaded += async (_, _) =>
        {
            var src = (HwndSource)PresentationSource.FromVisual(this)!;
            _hwnd = src.Handle;
            ApplyExStyle();
            Rebuild();
            LoadPos();
            _ui.Tick += (_, _) => UpdateValues();
            _ui.Start();
            UpdateValues();
            await Task.Delay(1);
        };
        Closed += (_, _) => _ui.Stop();
    }

    // ===== 风格构建 =====

    public void Rebuild()
    {
        try
        {
            Content = null;
            _txt.Clear();
            _bar.Clear();
            _spark.Clear();
            _host = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };

            switch (_settings.TileStyle)
            {
                case "Card": BuildCard(); Width = 420; Height = 190; break;
                case "Taskbar": BuildTaskbar(); Width = 660; Height = 40; break;
                case "GamePP": BuildGamePP(); Width = 260; Height = 470; break;
                default: BuildBar(); Width = 660; Height = 44; break;
            }

            if (_mini) BuildMini();
            Content = _host;
            _built = true;
            UpdateValues();
        }
        catch (Exception ex)
        {
            // 兑底：任何风格构建失败都退回最简横条，磁贴不死
            try
            {
                _txt.Clear(); _bar.Clear(); _spark.Clear();
                _host = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
                _host.Children.Add(new TextBlock { Text = "磁贴渲染异常: " + ex.Message, Foreground = Gray, FontSize = 12 });
                Content = _host;
                Width = 420; Height = 40;
            }
            catch { }
        }
        ApplyExStyle();
    }

    private Border Chip_(string key, string label, Brush accent, double labelW = 40)
    {
        var b = new Border { Background = Bg, CornerRadius = new CornerRadius(4), Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(10, 5, 10, 5) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = label, Foreground = Gray, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = labelW });
        var v = new TextBlock { Foreground = accent, FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), Text = "--" };
        sp.Children.Add(v);
        _txt[key] = v;
        b.Child = sp;
        return b;
    }

    private void BuildBar()
    {
        _host!.Children.Add(Chip_("cpu", "CPU", Green));
        _host.Children.Add(Chip_("gpu", "GPU", Blue));
        _host.Children.Add(Chip_("vram", "显存", Purple, 34));
        _host.Children.Add(Chip_("ram", "内存", Green, 34));
        _host.Children.Add(Chip_("net", "网络", Blue, 34));
    }

    private Border Card_(string key, string title, Brush accent)
    {
        var b = new Border { Background = Bg, CornerRadius = new CornerRadius(8), Margin = new Thickness(4), Padding = new Thickness(12, 8, 12, 8), Width = 200 };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Foreground = Gray, FontSize = 11, FontWeight = FontWeights.SemiBold });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var v = new TextBlock { Text = "--", Foreground = accent, FontSize = 22, FontWeight = FontWeights.Bold };
        row.Children.Add(v);
        var sub = new TextBlock { Text = "", Foreground = Ink, FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 4) };
        row.Children.Add(sub);
        sp.Children.Add(row);
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Foreground = accent, Background = Chip, Margin = new Thickness(0, 6, 0, 0), Template = HBarTemplateSafe };
        sp.Children.Add(bar);
        var sub2 = new TextBlock { Text = "", Foreground = Gray, FontSize = 10.5, Margin = new Thickness(0, 5, 0, 0) };
        sp.Children.Add(sub2);
        _txt[key] = v;
        _txt[key + ".sub"] = sub;
        _txt[key + ".sub2"] = sub2;
        _bar[key] = bar;
        b.Child = sp;
        return b;
    }

    private void BuildCard()
    {
        var grid = new UniformGrid { Columns = 2 };
        grid.Children.Add(Card_("cpu", "CPU 处理器", Green));
        grid.Children.Add(Card_("gpu", "GPU 显卡", Blue));
        grid.Children.Add(Card_("mem", "内存 / 显存", Purple));
        grid.Children.Add(Card_("net", "网络速率", Orange));
        _host!.Children.Add(grid);
    }

    private void BuildTaskbar()
    {
        var b = new Border { Background = Bg, CornerRadius = new CornerRadius(6), Margin = new Thickness(2), Padding = new Thickness(12, 6, 12, 6) };
        var dock = new DockPanel();
        var net = new TextBlock { Foreground = Ink, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Text = "↓-- ↑--" };
        _txt["net"] = net;
        DockPanel.SetDock(net, Dock.Right);
        dock.Children.Add(net);

        dock.Children.Add(MiniBar("cpu", "CPU", Green));
        dock.Children.Add(MiniBar("gpu", "GPU", Blue));
        dock.Children.Add(MiniBar("ram", "内存", Purple));

        b.Child = dock;
        _host!.Children.Add(b);
    }

    // ===== 游戏加加风格：竖向渐变面板 + 曲线 =====

    private void BuildGamePP()
    {
        var panel = new Border
        {
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xE0, 0x24, 0x3B, 0x5E), 0),
                    new GradientStop(Color.FromArgb(0xD8, 0x3B, 0x2A, 0x54), 0.55),
                    new GradientStop(Color.FromArgb(0xCC, 0x18, 0x2A, 0x44), 1),
                },
            },
        };
        panel.Effect = null;

        var sp = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        sp.Children.Add(GamePPHeader());

        // CPU
        sp.Children.Add(GamePPSectionHeader("CPU", "cpu", Green));
        sp.Children.Add(GamePPSpark("cpu.load", Green, 100));
        sp.Children.Add(GamePPValueRow("cpu.load", "cpu.temp", Green, Orange));

        // GPU
        sp.Children.Add(GamePPSectionHeader("GPU", "gpu", Blue));
        sp.Children.Add(GamePPSpark("gpu.load", Blue, 100));
        sp.Children.Add(GamePPValueRow("gpu.load", "gpu.temp", Blue, Orange));

        // 内存 / 显存
        sp.Children.Add(GamePPBar("内存", "ram", "ram", Green));
        sp.Children.Add(GamePPBar("显 存", "vram", "vrambar", Blue));

        // 网络
        sp.Children.Add(GamePPNetRow());

        panel.Child = sp;
        _host!.Children.Add(panel);
    }

    private UIElement GamePPHeader()
    {
        var b = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(0, 0, 0, 8) };
        b.Child = new TextBlock { Text = "Game Monitor 桌面监控", Foreground = Ink, FontSize = 13, FontWeight = FontWeights.Bold };
        return b;
    }

    private UIElement GamePPSectionHeader(string title, string key, Brush accent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        var icon = new Border { Background = accent, CornerRadius = new CornerRadius(3), Width = 16, Height = 16, Child = new TextBlock { Text = title[0].ToString(), Foreground = Brushes.Black, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = " " + title, Foreground = Ink, FontSize = 14, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        var meta = new TextBlock { Text = "--", Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        _txt[key + ".meta"] = meta;
        row.Children.Add(meta);
        return row;
    }

    private UIElement GamePPSpark(string key, Brush accent, double max)
    {
        var host = new Border { Height = 52, Margin = new Thickness(0, 2, 0, 4) };
        var spark = new Sparkline();
        spark.SetStyle(accent, max);
        host.Child = spark;
        _spark[key] = spark;
        return host;
    }

    private UIElement GamePPValueRow(string loadKey, string tempKey, Brush loadColor, Brush tempColor)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lv = new TextBlock { Text = "-- %", Foreground = loadColor, FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        var tv = new TextBlock { Text = "-- °C", Foreground = tempColor, FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) };
        _txt[loadKey + ".val"] = lv;
        _txt[tempKey + ".val"] = tv;
        Grid.SetColumn(tv, 0);
        Grid.SetColumn(lv, 1);
        grid.Children.Add(tv);
        grid.Children.Add(lv);
        return grid;
    }

    private UIElement GamePPBar(string label, string key, string barKey, Brush accent)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, Foreground = Ink, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var bar = new ProgressBar { Height = 10, Minimum = 0, Maximum = 100, Foreground = accent, Background = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), Template = HBarTemplateSafe, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        var v = new TextBlock { Text = "--", Foreground = Ink, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(v, 2);
        row.Children.Add(v);
        _bar[barKey] = bar;
        _txt[key] = v;
        return row;
    }

    private UIElement GamePPNetRow()
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = "网 络", Foreground = Ink, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var down = new TextBlock { Text = "↓ --", Foreground = Green, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
        var up = new TextBlock { Text = "↑ --", Foreground = Blue, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        _txt["net.down"] = down;
        _txt["net.up"] = up;
        Grid.SetColumn(down, 1);
        Grid.SetColumn(up, 2);
        row.Children.Add(down);
        row.Children.Add(up);
        return row;
    }

    private StackPanel MiniBar(string key, string label, Brush accent)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        sp.Children.Add(new TextBlock { Text = label, Foreground = Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var bar = new ProgressBar { Width = 90, Height = 6, Minimum = 0, Maximum = 100, Foreground = accent, Background = Chip, Margin = new Thickness(7, 0, 0, 0), Template = HBarTemplateSafe, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(bar);
        var v = new TextBlock { Text = "--%", Foreground = Ink, FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 34 };
        sp.Children.Add(v);
        _bar[key] = bar;
        _txt[key] = v;
        return sp;
    }

    private void BuildMini()
    {
        var b = new Border { Background = Bg, CornerRadius = new CornerRadius(4), Margin = new Thickness(2), Padding = new Thickness(10, 4, 10, 4) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        TextBlock mk(string k, string label, Brush c)
        {
            var t = new TextBlock { Foreground = Ink, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            _txt[k] = t;
            sp.Children.Add(new TextBlock { Text = label + " ", Foreground = Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(t);
            return t;
        }
        mk("cpu", "CPU", Green);
        sp.Children.Add(new TextBlock { Text = "  ", FontSize = 11 });
        mk("gpu", "GPU", Blue);
        sp.Children.Add(new TextBlock { Text = "  ", FontSize = 11 });
        mk("ram", "内存", Purple);
        b.Child = sp;
        _host!.Children.Add(b);
        Width = 250; Height = 30;
    }

    // ===== 数据刷新 =====

    private void UpdateValues()
    {
        var s = App.Engine?.Current;
        if (s == null || !_built) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        string Pct(double v) => double.IsNaN(v) ? "--%" : v.ToString("F0", ci) + "%";
        string Ghz(double v) => double.IsNaN(v) || v <= 100 ? "--" : (v / 1000).ToString("F2", ci) + "GHz";
        string W_(double v) => double.IsNaN(v) || v <= 0 ? "--W" : v.ToString("F1", ci) + "W";

        if (_txt.TryGetValue("cpu", out var t)) t.Text = Pct(s.CpuLoad) + (double.IsNaN(s.CpuMaxClock) || s.CpuMaxClock <= 100 ? "" : " · " + Ghz(s.CpuMaxClock)) + (double.IsNaN(s.CpuPower) || s.CpuPower <= 0 ? "" : " · " + W_(s.CpuPower));
        if (_txt.TryGetValue("gpu", out var g)) g.Text = Pct(s.GpuLoad) + (double.IsNaN(s.GpuCoreClock) || s.GpuCoreClock <= 100 ? "" : " · " + Ghz(s.GpuCoreClock)) + (double.IsNaN(s.GpuPower) || s.GpuPower <= 0 ? "" : " · " + W_(s.GpuPower));
        if (_txt.TryGetValue("vram", out var v))
            v.Text = double.IsNaN(s.GpuMemUsed) ? "--" : (double.IsNaN(s.GpuMemTotal) ? $"{s.GpuMemUsed / 1024.0:F1}G" : $"{s.GpuMemUsed / 1024.0:F1}/{s.GpuMemTotal / 1024.0:F1}G");
        if (_txt.TryGetValue("ram", out var r))
            r.Text = double.IsNaN(s.RamUsedGb) ? "--" : (double.IsNaN(s.RamTotalGb) ? $"{s.RamUsedGb:F1}GB" : $"{s.RamUsedGb:F1}/{s.RamTotalGb:F0}GB");
        if (_txt.TryGetValue("net", out var n))
            n.Text = $"↓{Rate(s.DownKbps)} ↑{Rate(s.UpKbps)}";

        // 卡片
        if (_txt.TryGetValue("cpu.sub", out var cs)) cs.Text = $"{Ghz(s.CpuMaxClock)} · {W_(s.CpuPower)}";
        if (_txt.TryGetValue("gpu.sub", out var gs)) gs.Text = $"{Ghz(s.GpuCoreClock)} · {W_(s.GpuPower)}";
        if (_txt.TryGetValue("mem.sub", out var ms)) ms.Text = double.IsNaN(s.RamUsedGb) ? "--" : $"内存 {s.RamUsedGb:F1}/{(double.IsNaN(s.RamTotalGb) ? 0 : s.RamTotalGb):F0}GB";
        if (_txt.TryGetValue("mem.sub2", out var ms2)) ms2.Text = double.IsNaN(s.GpuMemUsed) ? "显存 --" : $"显存 {s.GpuMemUsed:F0}/{(double.IsNaN(s.GpuMemTotal) ? 0 : s.GpuMemTotal):F0}MB";
        if (_txt.TryGetValue("net.sub", out var ns)) ns.Text = $"↓ {Rate(s.DownKbps)}   ↑ {Rate(s.UpKbps)}";
        if (_txt.TryGetValue("net.sub2", out var ns2)) ns2.Text = "全部网卡 IPv4 · 1s 采样";

        if (_bar.TryGetValue("cpu", out var cb)) cb.Value = double.IsNaN(s.CpuLoad) ? 0 : Math.Clamp(s.CpuLoad, 0, 100);
        if (_bar.TryGetValue("gpu", out var gb)) gb.Value = double.IsNaN(s.GpuLoad) ? 0 : Math.Clamp(s.GpuLoad, 0, 100);
        if (_bar.TryGetValue("mem", out var mb)) mb.Value = double.IsNaN(s.RamUsedGb) || double.IsNaN(s.RamTotalGb) || s.RamTotalGb <= 0 ? 0 : Math.Clamp(s.RamUsedGb / s.RamTotalGb * 100, 0, 100);

        // mini
        if (_mini)
        {
            if (_txt.TryGetValue("cpu", out var mc)) mc.Text = Pct(s.CpuLoad);
            if (_txt.TryGetValue("gpu", out var mg)) mg.Text = Pct(s.GpuLoad);
            if (_txt.TryGetValue("ram", out var mr)) mr.Text = Pct(double.IsNaN(s.RamUsedGb) || double.IsNaN(s.RamTotalGb) || s.RamTotalGb <= 0 ? double.NaN : s.RamUsedGb / s.RamTotalGb * 100);
        }

        // GamePP 风格曲线缓存（每 500ms 一点，120 点 = 60s）+ 数值（仅 GamePP 风格有这些元素）
        if (_settings.TileStyle == "GamePP")
        {
            _sparkTick++;
            if (_sparkTick % 2 == 0)
            {
                Push(_cpuLoadHist, s.CpuLoad);
                Push(_cpuTempHist, s.CpuTemp);
                Push(_gpuLoadHist, s.GpuLoad);
                Push(_gpuTempHist, s.GpuTemp);
                if (_spark.TryGetValue("cpu.load", out var csl)) csl.Update(_cpuLoadHist);
                if (_spark.TryGetValue("gpu.load", out var gsl)) gsl.Update(_gpuLoadHist);
            }

            if (_txt.TryGetValue("cpu.meta", out var cm)) cm.Text = $"⚡ {W_(s.CpuPower)}  ⧗ {Ghz(s.CpuMaxClock)}";
            if (_txt.TryGetValue("gpu.meta", out var gm)) gm.Text = $"⧗ {Ghz(s.GpuCoreClock)}  ⏲ {Rpm(s.GpuFanRpm)}";
            if (_txt.TryGetValue("cpu.load.val", out var clv)) clv.Text = Pct(s.CpuLoad);
            if (_txt.TryGetValue("cpu.temp.val", out var ctv)) ctv.Text = Temp_(s.CpuTemp);
            if (_txt.TryGetValue("gpu.load.val", out var glv)) glv.Text = Pct(s.GpuLoad);
            if (_txt.TryGetValue("gpu.temp.val", out var gtv)) gtv.Text = Temp_(s.GpuTemp);
            if (_txt.TryGetValue("ram", out var rv)) rv.Text = double.IsNaN(s.RamUsedGb) ? "--" : $"{s.RamUsedGb:F1}G / {(double.IsNaN(s.RamTotalGb) ? 0 : s.RamTotalGb):F0}G";
            if (_bar.TryGetValue("ram", out var rb2)) rb2.Value = double.IsNaN(s.RamUsedGb) || double.IsNaN(s.RamTotalGb) || s.RamTotalGb <= 0 ? 0 : Math.Clamp(s.RamUsedGb / s.RamTotalGb * 100, 0, 100);
            if (_txt.TryGetValue("vram", out var vv)) vv.Text = double.IsNaN(s.GpuMemUsed) ? "--" : $"{s.GpuMemUsed / 1024.0:F1}G / {(double.IsNaN(s.GpuMemTotal) ? 0 : s.GpuMemTotal / 1024.0):F1}G";
            if (_bar.TryGetValue("vrambar", out var vb2)) vb2.Value = double.IsNaN(s.GpuMemUsed) || double.IsNaN(s.GpuMemTotal) || s.GpuMemTotal <= 0 ? 0 : Math.Clamp(s.GpuMemUsed / s.GpuMemTotal * 100, 0, 100);
            if (_txt.TryGetValue("net.down", out var nd)) nd.Text = "↓ " + Rate(s.DownKbps) + "/s";
            if (_txt.TryGetValue("net.up", out var nu)) nu.Text = "↑ " + Rate(s.UpKbps) + "/s";
        }

        static string Temp_(double v) => double.IsNaN(v) ? "--°C" : v.ToString("F0") + "°C";
        static string Rpm(double v) => double.IsNaN(v) || v <= 0 ? "0 RPM" : v.ToString("F0") + " RPM";
        static void Push(Queue<double> q, double v) { if (double.IsNaN(v)) v = 0; q.Enqueue(v); while (q.Count > 120) q.Dequeue(); }
        static string Rate(double kbps)
        {
            if (double.IsNaN(kbps)) return "--";
            return kbps >= 1024 ? (kbps / 1024.0).ToString("F1") + "M" : kbps.ToString("F0") + "K";
        }
    }

    // ===== 折叠 / 恢复 =====

    public void SetMini(bool mini)
    {
        if (_mini == mini) return;
        _mini = mini;
        SavePos();
        Rebuild();
        if (mini)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 12;
            Top = wa.Bottom + 2;
        }
        else LoadPos();
    }

    // ===== 位置 =====

    private string PosPath => Path.Combine(AppContext.BaseDirectory, "tile_pos.json");

    private void LoadPos()
    {
        try
        {
            if (File.Exists(PosPath))
            {
                var cfg = JsonSerializer.Deserialize<double[]>(File.ReadAllText(PosPath));
                if (cfg is { Length: 2 } && cfg[0] > -100 && cfg[1] > -100) { Left = cfg[0]; Top = cfg[1]; return; }
            }
        }
        catch { }
        // 默认：右上角偏下
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = 60;
    }

    public void ResetPosition()
    {
        try { if (File.Exists(PosPath)) File.Delete(PosPath); } catch { }
        _mini = false;
        Rebuild();
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = 60;
    }

    private void SavePos()
    {
        try { File.WriteAllText(PosPath, JsonSerializer.Serialize(new[] { Left, Top })); } catch { }
    }

    // ===== 交互 =====

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.TileClickThrough) return;
        try
        {
            if (e.ClickCount == 2 && _mini) { SetMini(false); return; }
            DragMove();
            SavePos();
        }
        catch { }
    }

    public void ApplyExStyle()
    {
        if (_hwnd == IntPtr.Zero) return;
        long v = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        v |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (_settings.TileClickThrough && !_mini) v |= WS_EX_TRANSPARENT;
        else v &= ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)v);
        Cursor = _settings.TileClickThrough ? Cursors.Arrow : Cursors.SizeAll;
    }

    public void ApplyConfig()
    {
        ShowInTaskbar = _settings.TileShowInTaskbar;
        Rebuild();
    }
}
