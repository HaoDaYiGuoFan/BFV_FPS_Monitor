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
/// 游戏加加风格 OSD 悬浮条：项目勾选 / 位置 / 字号 / 透明度可配置。
/// 锁定时鼠标穿透，Ctrl+Alt+O 解锁拖动，位置记忆。
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppSettings _settings;
    private bool _locked = true;
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _pin = new() { Interval = TimeSpan.FromSeconds(1) };

    private static readonly Brush BGray = Freeze(0xFF8B97A8);
    private static readonly Brush BGreen = Freeze(0xFF3DDC84);
    private static readonly Brush BYellow = Freeze(0xFFFFD24D);
    private static readonly Brush BRed = Freeze(0xFFFF5A5A);
    private static readonly Brush BBlue = Freeze(0xFF4DA3FF);
    private static readonly Brush BWhite = Freeze(0xFFE8EDF4);

    private static SolidColorBrush Freeze(uint rgb)
    {
        var br = new SolidColorBrush(Color.FromArgb((byte)(rgb >> 24), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        br.Freeze();
        return br;
    }

    // ===== Win32 =====
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 1;
    private const uint MOD_CONTROL = 0x2, MOD_ALT = 0x1, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    private IntPtr _hwnd;
    public bool IsLocked => _locked;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Cursor = Cursors.SizeAll;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _ui.Stop(); _pin.Stop();
            try { UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyConfig();
        LoadPosition();

        var src = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwnd = src.Handle;
        src.AddHook(WndProc);
        RegisterHotKey(_hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4F); // Ctrl+Alt+O

        ApplyLockStyle();

        _ui.Tick += (_, _) => UpdateBar();
        _ui.Start();
        _pin.Tick += (_, _) => ForceTopmost(); // 游戏运行时每秒原生重申置顶（全屏窗口会压过普通置顶）
        _pin.Start();
        UpdateBar();
    }

    private void ForceTopmost()
    {
        try
        {
            Topmost = true;
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ToggleLock();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ===== 配置应用 =====

    public void ApplyConfig()
    {
        SegFps.Visibility = _settings.OsdItems.Contains("FPS") ? Visibility.Visible : Visibility.Collapsed;
        SegCpuL.Visibility = _settings.OsdItems.Contains("CPUL") ? Visibility.Visible : Visibility.Collapsed;
        SegCpuT.Visibility = _settings.OsdItems.Contains("CPUT") ? Visibility.Visible : Visibility.Collapsed;
        SegGpuL.Visibility = _settings.OsdItems.Contains("GPUL") ? Visibility.Visible : Visibility.Collapsed;
        SegGpuT.Visibility = _settings.OsdItems.Contains("GPUT") ? Visibility.Visible : Visibility.Collapsed;
        SegNet.Visibility = _settings.OsdItems.Contains("NET") ? Visibility.Visible : Visibility.Collapsed;

        double fs = Math.Clamp(_settings.OsdFontSize, 10, 22);
        foreach (var tb in new[] { FpsVal, CpuLoadVal, CpuTempVal, GpuLoadVal, GpuTempVal, NetVal })
            tb.FontSize = fs;
        foreach (var b in new[] { SegFps, SegCpuL, SegCpuT, SegGpuL, SegGpuT, SegNet })
        {
            var lbl = ((StackPanel)b.Child).Children.OfType<TextBlock>().FirstOrDefault();
            if (lbl != null) lbl.FontSize = Math.Max(10, fs - 1);
        }

        byte a = (byte)Math.Clamp(_settings.OsdBgOpacity * 2.55, 30, 255);
        foreach (var b in new[] { SegFps, SegCpuL, SegCpuT, SegGpuL, SegGpuT, SegNet })
            b.Background = new SolidColorBrush(Color.FromArgb(a, 0x10, 0x14, 0x18));

        MoveToCorner();
    }

    private void MoveToCorner()
    {
        if (!_settings.OsdItems.Any()) return;
        double workW = SystemParameters.WorkArea.Width;
        switch (_settings.OsdCorner)
        {
            case "C": Left = (workW - ActualWidth) / 2; Top = 16; break;
            case "R": Left = workW - ActualWidth - 24; Top = 16; break;
            case "L": Left = 16; Top = 16; break;
            case "CUS": break; // 用户拖动过的自由位置，不动
            default: Left = 16; Top = 16; break;
        }
    }

    // ===== 锁定 / 拖动 =====

    public void ToggleLock()
    {
        _locked = !_locked;
        ApplyLockStyle();
        SavePosition();
    }

    private void ApplyLockStyle()
    {
        if (_hwnd == IntPtr.Zero) return;
        long v = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        v |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (_locked) v |= WS_EX_TRANSPARENT;
        else v &= ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)v);
        Cursor = _locked ? Cursors.Arrow : Cursors.SizeAll;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        try { DragMove(); _settings.OsdCorner = "CUS"; SavePosition(); } catch { }
    }

    // ===== 位置记忆 =====

    private sealed record Cfg(double Left, double Top, bool Locked);
    private string CfgPath => Path.Combine(AppContext.BaseDirectory, "overlay.json");

    private void LoadPosition()
    {
        try
        {
            if (!File.Exists(CfgPath)) { MoveToCorner(); return; }
            var cfg = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(CfgPath));
            if (cfg == null) { MoveToCorner(); return; }
            Left = cfg.Left; Top = cfg.Top;
            _locked = cfg.Locked;
        }
        catch { MoveToCorner(); }
    }

    private void SavePosition()
    {
        try { File.WriteAllText(CfgPath, JsonSerializer.Serialize(new Cfg(Left, Top, _locked), new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    // ===== 数据 =====

    private void UpdateBar()
    {
        var s = App.Engine?.Current;
        if (s == null) return;

        bool has = s.FrameCount > 0;
        FpsVal.Text = has ? s.Fps.ToString("F0", CultureInfo.InvariantCulture) : "--";
        FpsVal.Foreground = has ? FpsBrush(s.Fps) : BGray;

        CpuLoadVal.Text = Pct(s.CpuLoad);
        CpuLoadVal.Foreground = Ramp(s.CpuLoad, 50, 75);

        CpuTempVal.Text = Tc(s.CpuTemp);
        CpuTempVal.Foreground = RampTemp(s.CpuTemp);

        GpuLoadVal.Text = Pct(s.GpuLoad);
        GpuLoadVal.Foreground = Ramp(s.GpuLoad, 50, 75);

        GpuTempVal.Text = Tc(s.GpuTemp);
        GpuTempVal.Foreground = RampTemp(s.GpuTemp);

        NetVal.Text = double.IsNaN(s.DownKbps) ? "--"
            : (s.DownKbps >= 1024 ? (s.DownKbps / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "M" : s.DownKbps.ToString("F0", CultureInfo.InvariantCulture) + "K");
        NetVal.Foreground = BBlue;

        // 位置跟随（窗口尺寸随显隐段变化，模式为 L/C/R 时保持对齐）
        if (_settings.OsdCorner is "L" or "C" or "R") MoveToCorner();
    }

    private static string Pct(double v) => double.IsNaN(v) ? "--%" : v.ToString("F0", CultureInfo.InvariantCulture) + "%";
    private static string Tc(double v) => double.IsNaN(v) ? "--°C" : v.ToString("F0", CultureInfo.InvariantCulture) + "°C";
    private static Brush Ramp(double v, double warn, double danger) => double.IsNaN(v) ? BGray : v >= danger ? BRed : v >= warn ? BYellow : BGreen;
    private static Brush RampTemp(double v) => double.IsNaN(v) ? BGray : v >= 80 ? BRed : v >= 50 ? BYellow : BGreen;
    private static Brush FpsBrush(double v) => v >= 60 ? BGreen : v >= 30 ? BYellow : BRed;
}
