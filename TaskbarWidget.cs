using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BFV_FPS_Monitor;

/// <summary>
/// 任务栏迷你监控条（输入法状态条风格）：默认嵌入任务栏内部，
/// 拖出任务栏变自由浮窗、拖回自动吸附；跟随任务栏移动/尺寸变化；位置记忆。
/// </summary>
public sealed class TaskbarWidget : Window
{
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IntPtr _hwnd;
    private double _dpiScale = 1.0;

    private TextBlock? _cpuVal, _cpuTemp, _gpuVal, _gpuTemp, _netVal;
    private Border? _root;

    // 停靠状态
    private bool _docked = true;
    private double _dockOffsetFromLeft = 280; // 磁贴中心相对任务栏左缘的 DIP 偏移
    private double _natW, _natH;

    private static readonly Brush Bg = Solid(0xF0101418);
    private static readonly Brush Gray = Solid(0xFF8B97A8);
    private static readonly Brush Ink = Solid(0xFFE8EDF4);
    private static readonly Brush Green = Solid(0xFF3DDC84);
    private static readonly Brush Blue = Solid(0xFF4DA3FF);
    private static readonly Brush Orange = Solid(0xFFFFB454);

    private static SolidColorBrush Solid(uint rgb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(rgb >> 24), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    public TaskbarWidget()
    {
        Title = Localization.T("Tbw.Title");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true; // 任务栏本体置顶，嵌入显示必须同层
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Cursor = Cursors.SizeAll;
        MouseLeftButtonDown += Tile_MouseLeftButtonDown;

        _root = new Border
        {
            Background = Bg,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12, 6, 12, 6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        TextBlock Lbl(string t) => new() { Text = t, Foreground = Gray, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        TextBlock Val(Brush c, string init = "--")
        {
            var tb = new TextBlock { Text = init, Foreground = c, FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            return tb;
        }
        TextBlock Gap() => new() { Text = "  |  ", Foreground = Solid(0x508B97A8), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };

        sp.Children.Add(Lbl("CPU"));
        _cpuVal = Val(Green);
        sp.Children.Add(_cpuVal);
        _cpuTemp = Val(Orange);
        sp.Children.Add(_cpuTemp);
        sp.Children.Add(Gap());
        sp.Children.Add(Lbl("GPU"));
        _gpuVal = Val(Blue);
        sp.Children.Add(_gpuVal);
        _gpuTemp = Val(Orange);
        sp.Children.Add(_gpuTemp);
        sp.Children.Add(Gap());
        _netVal = Val(Ink, "↓-- ↑--");
        sp.Children.Add(_netVal);

        _root.Child = sp;
        Content = _root;

        Loaded += async (_, _) =>
        {
            var src = (HwndSource)PresentationSource.FromVisual(this)!;
            _hwnd = src.Handle;
            long v = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(v | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

            _dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _natW = ActualWidth;
            _natH = ActualHeight;

            LoadPos();
            _ui.Tick += (_, _) => { MaintainDock(); UpdateValues(); };
            _ui.Start();
            UpdateValues();
            await Task.Delay(1);
        };
        Closed += (_, _) => _ui.Stop();
    }

    // ===== 任务栏停靠 =====

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    private struct RECT { public int L, T, R, B; }

    /// <summary>Shell_TrayWnd（主任务栏）矩形，转 DIP；找不到返回 null。</summary>
    private Rect? GetTaskbarRectDip()
    {
        try
        {
            var h = FindWindowW("Shell_TrayWnd", null);
            if (h == IntPtr.Zero) return null;
            if (!GetWindowRect(h, out RECT r)) return null;
            if (r.R - r.L <= 0 || r.B - r.T <= 0) return null;
            double s = _dpiScale <= 0 ? 1.0 : _dpiScale;
            return new Rect(r.L / s, r.T / s, (r.R - r.L) / s, (r.B - r.T) / s);
        }
        catch { return null; }
    }

    /// <summary>拖动结束后：中心落在任务栏（±24DIP 容差）内则吸附停靠。</summary>
    private void SnapToTaskbarIfNeeded()
    {
        var tb = GetTaskbarRectDip();
        if (tb == null) { _docked = false; return; }
        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
        const double tol = 24;
        if (cx >= tb.Value.X - tol && cx <= tb.Value.Right + tol &&
            cy >= tb.Value.Y - tol && cy <= tb.Value.Bottom + tol)
        {
            _docked = true;
            _dockOffsetFromLeft = Math.Clamp(cx - tb.Value.X, 60, tb.Value.Width - 60);
            SnapIntoTaskbar(tb.Value);
        }
        else
        {
            _docked = false;
            Height = _natH;
            SizeToContent = SizeToContent.WidthAndHeight;
        }
    }

    private void SnapIntoTaskbar(Rect tb)
    {
        if (_root == null) return;
        double h = Math.Max(24, tb.Height - 2);
        SizeToContent = SizeToContent.Manual;
        Width = _natW;
        Height = h;
        // 垂直居中：压缩上下 padding
        double padV = Math.Max(2, (h - _natH + 12) / 2);
        _root.Padding = new Thickness(12, padV, 12, padV);
        Left = Math.Max(tb.Left, tb.Left + _dockOffsetFromLeft - ActualWidth / 2);
        Top = tb.Top + (tb.Height - h) / 2;
        // 压到任务栏之上
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>停靠时每 tick 跟随任务栏（移动/自动隐藏/分辨率变化）。</summary>
    private void MaintainDock()
    {
        if (!_docked) return;
        var tb = GetTaskbarRectDip();
        if (tb == null) return;
        SnapIntoTaskbar(tb.Value);
    }

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
            SnapToTaskbarIfNeeded();
            SavePos();
        }
        catch { }
    }

    // ===== 数据 =====

    private void UpdateValues()
    {
        var s = App.Engine?.Current;
        if (s == null) return;
        var ci = CultureInfo.InvariantCulture;

        if (_cpuVal != null) _cpuVal.Text = double.IsNaN(s.CpuLoad) ? "--%" : s.CpuLoad.ToString("F0", ci) + "%";
        if (_cpuTemp != null) _cpuTemp.Text = double.IsNaN(s.CpuTemp) ? "--°C" : s.CpuTemp.ToString("F0", ci) + "°C";
        if (_gpuVal != null) _gpuVal.Text = double.IsNaN(s.GpuLoad) ? "--%" : s.GpuLoad.ToString("F0", ci) + "%";
        if (_gpuTemp != null) _gpuTemp.Text = double.IsNaN(s.GpuTemp) ? "--°C" : s.GpuTemp.ToString("F0", ci) + "°C";
        if (_netVal != null)
        {
            string Rate(double k)
            {
                if (double.IsNaN(k)) return "--";
                return k >= 1024 ? (k / 1024.0).ToString("F1", ci) + "M" : k.ToString("F0", ci) + "K";
            }
            _netVal.Text = $"↓{Rate(s.DownKbps)} ↑{Rate(s.UpKbps)}";
        }
    }

    // ===== 位置记忆（含停靠状态） =====

    private sealed record Cfg(double Left, double Top, bool Docked, double DockOffset);
    private string PosPath => Path.Combine(AppContext.BaseDirectory, "taskbar_widget_pos.json");

    private void LoadPos()
    {
        try
        {
            if (File.Exists(PosPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(PosPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("Left", out var l) && root.TryGetProperty("Top", out var t))
                {
                    _docked = root.TryGetProperty("Docked", out var d) && d.GetBoolean();
                    _dockOffsetFromLeft = root.TryGetProperty("DockOffset", out var o) ? o.GetDouble() : 280;
                    if (_docked)
                    {
                        var tb = GetTaskbarRectDip();
                        if (tb != null) { SnapIntoTaskbar(tb.Value); return; }
                        _docked = false;
                    }
                    Left = l.GetDouble();
                    Top = t.GetDouble();
                    return;
                }
            }
        }
        catch { }
        ResetPosition();
    }

    /// <summary>默认：嵌入任务栏左侧（同输入法状态条位置）。</summary>
    public void ResetPosition()
    {
        var tb = GetTaskbarRectDip();
        if (tb != null)
        {
            _docked = true;
            _dockOffsetFromLeft = Math.Min(280, tb.Value.Width * 0.2);
            SnapIntoTaskbar(tb.Value);
        }
        else
        {
            _docked = false;
            Left = SystemParameters.WorkArea.Right - ActualWidth - 24;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 4;
        }
        SavePos();
    }

    private void SavePos()
    {
        try
        {
            File.WriteAllText(PosPath, JsonSerializer.Serialize(new Cfg(Left, Top, _docked, _dockOffsetFromLeft), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>语言切换后刷新标题等文本。</summary>
    public void ApplyLanguage()
    {
        try { Title = Localization.T("Tbw.Title"); } catch { }
    }
}
