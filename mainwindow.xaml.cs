using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace BFV_FPS_Monitor;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Brush _green, _orange, _red, _blue, _gray;

    // 曲线缓存（120 点）
    private readonly Queue<double> _fpsHist = new();
    private readonly Queue<double> _gpuHist = new();
    private readonly Queue<double> _cpuHist = new();
    private readonly Queue<double> _cpuTempHist = new();
    private readonly Queue<double> _gpuTempHist = new();
    private readonly Queue<double> _ramHist = new();
    private readonly Queue<double> _gpuMemHist = new();
    private int _histTick;

    // 传感器详情行（name → value TextBlock）
    private readonly Dictionary<string, TextBlock> _detailValues = new();
    private OverlayWindow? _overlay;
    private TileWindow? _tile;
    private TaskbarWidget? _tbWidget;
    private readonly List<Window> _reportWindows = new();

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        try { VerChip.Text = "V" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "1.0.0.1"); } catch { }
        InitCharts();
        Loaded += (_, _) => InitTileFromSettings();
        StateChanged += MainWindow_StateChanged;
        _green = (Brush)FindResource("AccentGreen");
        _orange = (Brush)FindResource("AccentOrange");
        _red = (Brush)FindResource("AccentRed");
        _blue = (Brush)FindResource("AccentBlue");
        _gray = (Brush)FindResource("TextSecondary");

        _timer.Tick += (_, _) => UpdateUi();
        _timer.Start();
        UpdateUi();

        InitSettingsUi();
        BuildDetailsPanel();
        RefreshSessions();
        if (App.Engine != null) App.Engine.SessionEnded += OnSessionEnded;
        Localization.LanguageChanged += () => { try { ApplyLanguage(); } catch { } };
        Closed += (_, _) => { CloseOverlay(); _tile?.Close(); _tbWidget?.Close(); };
    }

    // ================= 设置初始化 =================

    private void InitCharts()
    {
        var g = (Brush)FindResource("AccentGreen");
        var o = (Brush)FindResource("AccentOrange");
        var b = (Brush)FindResource("AccentBlue");
        var r = (Brush)FindResource("AccentRed");
        var p = (Brush)FindResource("AccentPurple");
        double hz = DetectRefreshRate();
        _fpsCeiling = Math.Max(60, Math.Ceiling(hz / 30.0) * 30 + 10); // 60/90/120/144→150...：不超过刷新率太多
        FpsChart.SetSeries("FPS", g, _fpsCeiling);
        CpuChart.SetSeries("%", o, 100);
        GpuChart.SetSeries("%", b, 100);
        CpuTempChart.SetSeries("°C", r, 100);
        GpuTempChart.SetSeries("°C", p, 100);
        RamChart.SetSeries("%", g, 100);
        GpuMemChart.SetSeries("%", b, 100);
    }

    private double _fpsCeiling = 130;

    /// <summary>检测主显示器当前刷新率（Win32_VideoController，排除虚拟显示器）。</summary>
    private static double DetectRefreshRate()
    {
        try
        {
            double best = 0;
            using (var s = new System.Management.ManagementObjectSearcher("root\\cimv2", "SELECT CurrentRefreshRate, Name FROM Win32_VideoController"))
            foreach (var o in s.Get())
            {
                using (var m = (System.Management.ManagementObject)o)
                {
                    string n = m["Name"]?.ToString() ?? "";
                    if (n.Contains("Oray") || n.Contains("Basic Display")) continue;
                    try { double v = Convert.ToDouble(m["CurrentRefreshRate"] ?? 0); if (v > best) best = v; } catch { }
                }
            }
            if (best < 30) best = 60; // 读不到时保底 60
            return best;
        }
        catch { return 60; }
    }

    private void InitSettingsUi()
    {
        AutoDetectChk.Checked += (_, _) => { _settings.AutoDetect = true; _settings.Save(); };
        AutoDetectChk.Unchecked += (_, _) => { _settings.AutoDetect = false; _settings.Save(); };
        AutoDetectChk.IsChecked = _settings.AutoDetect;

        OsdFps.IsChecked = _settings.OsdItems.Contains("FPS");
        OsdCpuL.IsChecked = _settings.OsdItems.Contains("CPUL");
        OsdCpuT.IsChecked = _settings.OsdItems.Contains("CPUT");
        OsdGpuL.IsChecked = _settings.OsdItems.Contains("GPUL");
        OsdGpuT.IsChecked = _settings.OsdItems.Contains("GPUT");
        OsdNet.IsChecked = _settings.OsdItems.Contains("NET");
        foreach (var cb in new[] { OsdFps, OsdCpuL, OsdCpuT, OsdGpuL, OsdGpuT, OsdNet })
            cb.Checked += (_, _) => SaveOsdItems();
        foreach (var cb in new[] { OsdFps, OsdCpuL, OsdCpuT, OsdGpuL, OsdGpuT, OsdNet })
            cb.Unchecked += (_, _) => SaveOsdItems();

        OsdCornerCombo.SelectedIndex = _settings.OsdCorner switch { "C" => 1, "R" => 2, _ => 0 };
        OsdFontSlider.Value = _settings.OsdFontSize;
        OsdOpacitySlider.Value = _settings.OsdBgOpacity;
        OsdFontVal.Text = _settings.OsdFontSize.ToString(CultureInfo.InvariantCulture);
        OsdOpacityVal.Text = _settings.OsdBgOpacity + "%";

        HwPollSlider.Value = _settings.HwPollMs;
        HwPollVal.Text = _settings.HwPollMs + "ms";

        TileStyleCombo.SelectedIndex = _settings.TileStyle switch { "Bar" => 1, "Card" => 2, "Taskbar" => 3, _ => 0 };
        TileClickThroughChk.IsChecked = _settings.TileClickThrough;
        TileTaskbarChk.IsChecked = _settings.TileShowInTaskbar;
        TileMiniChk.IsChecked = _settings.TileMiniOnMinimize;

        CsvModeCombo.SelectedIndex = Math.Clamp(_settings.CsvModeInt, 0, 2);

        LanguageCombo.SelectedIndex = _settings.Language == Localization.EnUS ? 1 : 0;

        AutostartChk.IsChecked = _settings.Autostart;
        SessionPopupChk.IsChecked = _settings.SessionReportPopup;

        RefreshProcList();
        if (!string.IsNullOrEmpty(_settings.PinnedProcess))
            ProcCombo.SelectedItem = _settings.PinnedProcess;
    }

    private void SaveOsdItems()
    {
        var list = new List<string>();
        if (OsdFps.IsChecked == true) list.Add("FPS");
        if (OsdCpuL.IsChecked == true) list.Add("CPUL");
        if (OsdCpuT.IsChecked == true) list.Add("CPUT");
        if (OsdGpuL.IsChecked == true) list.Add("GPUL");
        if (OsdGpuT.IsChecked == true) list.Add("GPUT");
        if (OsdNet.IsChecked == true) list.Add("NET");
        _settings.OsdItems = list;
        _settings.Save();
        _overlay?.ApplyConfig();
    }

    private void OsdSetting_Changed(object sender, SelectionChangedEventArgs e) { SaveOsdLook(); }
    private void OsdSetting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { SaveOsdLook(); }

    private void SaveOsdLook()
    {
        if (OsdCornerCombo == null || OsdFontSlider == null || OsdOpacitySlider == null
            || OsdFontVal == null || OsdOpacityVal == null || _settings == null) return;
        if (OsdCornerCombo.SelectedIndex >= 0)
            _settings.OsdCorner = OsdCornerCombo.SelectedIndex switch { 1 => "C", 2 => "R", _ => "L" };
        _settings.OsdFontSize = (int)OsdFontSlider.Value;
        _settings.OsdBgOpacity = (int)OsdOpacitySlider.Value;
        OsdFontVal.Text = _settings.OsdFontSize.ToString(CultureInfo.InvariantCulture);
        OsdOpacityVal.Text = _settings.OsdBgOpacity + "%";
        _settings.Save();
        _overlay?.ApplyConfig();
    }

    private void HwPoll_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HwPollVal == null) return;
        _settings.HwPollMs = (int)HwPollSlider.Value;
        HwPollVal.Text = _settings.HwPollMs + "ms";
        _settings.Save();
    }

    private void CsvMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CsvModeCombo.SelectedIndex < 0) return;
        _settings.CsvModeInt = CsvModeCombo.SelectedIndex;
        _settings.Save();
    }

    private void SessionPopup_Changed(object sender, RoutedEventArgs e)
    {
        if (SessionPopupChk.IsChecked == null) return;
        _settings.SessionReportPopup = SessionPopupChk.IsChecked == true;
        _settings.Save();
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Autostart = AutostartChk.IsChecked == true;
        _settings.Save();
        try
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, true);
            if (rk == null) return;
            if (_settings.Autostart)
                rk.SetValue("GameMonitor", Process.GetCurrentProcess().MainModule?.FileName ?? "", Microsoft.Win32.RegistryValueKind.String);
            else if (rk.GetValue("GameMonitor") != null)
                rk.DeleteValue("GameMonitor");
        }
        catch { }
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo == null || LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string lang) return;
        if (lang == Localization.Current) return;
        _settings.Language = lang;
        _settings.Save();
        Localization.Set(lang);
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ProcCombo.SelectedItem is string s && !string.IsNullOrEmpty(s))
            App.Engine.PinProcess(s);
        else
            StatusText.Text = Localization.T("Main.PinNone");
    }

    private void UnpinBtn_Click(object sender, RoutedEventArgs e)
    {
        App.Engine.PinProcess("");
        ProcCombo.SelectedIndex = -1;
    }

    private void RefreshProcBtn_Click(object sender, RoutedEventArgs e) => RefreshProcList();

    private void RefreshProcList()
    {
        var cur = ProcCombo.SelectedItem as string;
        var names = GameDetector.ListWindowedProcesses().Select(x => x.Name).Distinct().ToList();
        ProcCombo.ItemsSource = names;
        if (!string.IsNullOrEmpty(cur) && names.Contains(cur))
            ProcCombo.SelectedItem = cur;
    }

    private void SnapshotBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = App.Engine.Current;
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Localization.F("Snap.Header", s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sb.AppendLine(Localization.F("Snap.Game", s.GameName, s.GamePid, Localization.GameSource(s.GameSource), s.PresentMonRunning ? Localization.T("Snap.PmRunning") : Localization.T("Snap.PmStopped")));
            sb.AppendLine(Localization.F("Snap.Fps", s.Fps.ToString("F1", CultureInfo.InvariantCulture), s.AvgFps.ToString("F1", CultureInfo.InvariantCulture), s.OnePercentLow.ToString("F1", CultureInfo.InvariantCulture), s.PointOnePercentLow.ToString("F1", CultureInfo.InvariantCulture), s.MaxFrameMs.ToString("F1", CultureInfo.InvariantCulture)));
            sb.AppendLine(Localization.F("Snap.Cpu", Pct(s.CpuLoad), Temp(s.CpuTemp), Mhz(s.CpuMaxClock), W(s.CpuPower)));
            sb.AppendLine(Localization.F("Snap.Ram", Gb(s.RamUsedGb), Gb(s.RamTotalGb)));
            sb.AppendLine(Localization.F("Snap.Gpu", s.GpuName, Pct(s.GpuLoad), Temp(s.GpuTemp), Mhz(s.GpuCoreClock), W(s.GpuPower), Rpm(s.GpuFanRpm)));
            sb.AppendLine(Localization.F("Snap.Vram", Mb(s.GpuMemUsed), Mb(s.GpuMemTotal)));
            sb.AppendLine(Localization.F("Snap.Disk", Kbs(s.DiskReadKbs), Kbs(s.DiskWriteKbs), Temp(s.DiskTemp)));
            sb.AppendLine(Localization.F("Snap.Net", Kbs(s.DownKbps), Kbs(s.UpKbps)));
            sb.AppendLine(Localization.F("Snap.Batt", Pct(s.BatteryPct), W(s.BatteryPowerW), Localization.BatteryState(s.AcOnline)));
            File.WriteAllText(path, sb.ToString());
            StatusText.Text = Localization.F("Status.SnapshotSaved", path);
        }
        catch (Exception ex) { StatusText.Text = Localization.F("Status.SnapshotFail", ex.Message); }
    }

    // ================= 性能统计（游戏加加风格） =================

    private GameSession? _pendingReport;
    private void OnSessionEnded(GameSession s)
    {
        try
        {
            // 过短会话（误判进程 / 快速重启游戏）只入库不弹报告，避免报告窗口堆积
            if (s.Duration.TotalSeconds >= 60) _pendingReport = s;
        }
        catch { }
    }

    private string _lastReportId = "";
    private void ShowPendingReport()
    {
        var s = _pendingReport;
        if (s == null) return;
        _pendingReport = null;
        if (s.Id == _lastReportId) return;   // 同一会话不重复弹
        _lastReportId = s.Id;
        try
        {
            RefreshSessions();
            if (_settings.SessionReportPopup)
            {
                try
                {
                    var w = new SessionReportWindow(s);
                    w.Closed += (_, _) => _reportWindows.Remove(w);
                    _reportWindows.Add(w);
                    w.Show();
                    w.Activate();
                }
                catch (Exception exInner)
                {
                    try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "popup_error.txt"), exInner.ToString(), System.Text.Encoding.UTF8); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "popup_error.txt"), ex.ToString(), System.Text.Encoding.UTF8); } catch { }
            try { StatusText.Text = Localization.F("Report.PopupFail", ex.Message); } catch { }
        }
    }
    private void SessionsRefreshBtn_Click(object sender, RoutedEventArgs e) => RefreshSessions();

    private void RefreshSessions()
    {
        if (SessionsHost == null) return;
        try
        {
            var sessions = SessionStore.LoadAll();
            SessionSummaryText.Text = sessions.Count > 0
                ? Localization.F("Ses.Summary", sessions.Count, sessions[0].GameName, sessions[0].Start.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))
                : Localization.T("Ses.Empty");
            SessionsHost.Children.Clear();
            foreach (var s in sessions.Take(50))
            {
                var card = BuildSessionCard(s);
                SessionsHost.Children.Add(card);
            }
        }
        catch (Exception ex) { SessionSummaryText.Text = Localization.F("Ses.Fail", ex.Message); }
    }

    private Border BuildSessionCard(GameSession s)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("BgPanel"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 行 1：游戏名 + 时间 + 时长
        var head = new DockPanel();
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = s.GameName, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = Localization.F("Ses.Head", s.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), s.End.ToString("HH:mm:ss", CultureInfo.InvariantCulture), s.DurationText, s.Resolution), FontSize = 11, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(left);
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        var delBtn = new Button { Content = Localization.T("Ses.Delete"), Style = (Style)FindResource("OverlayToggle") };
        delBtn.Click += (_, _) => { SessionStore.Delete(s); RefreshSessions(); };
        var detail2Btn = new Button { Content = Localization.T("Ses.Detail2"), Style = (Style)FindResource("OverlayToggle"), Margin = new Thickness(0, 0, 6, 0) };
        detail2Btn.Click += (_, _) => new SessionDetailWindow(s).Show();
        var detailBtn = new Button { Content = Localization.T("Ses.Detail"), Style = (Style)FindResource("OverlayToggle"), Margin = new Thickness(0, 0, 6, 0) };
        detailBtn.Click += (_, _) => new SessionReportWindow(s).Show();
        btns.Children.Add(delBtn);
        btns.Children.Add(detail2Btn);
        btns.Children.Add(detailBtn);
        DockPanel.SetDock(btns, Dock.Right);
        head.Children.Add(btns);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // 行 2：四指标横排（FPS/CPU/GPU/内存）
        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 10, 0, 4) };
        AddCardStat(stats, Localization.T("Ses.AvgFps"), s.FpsAvg > 0 ? s.FpsAvg.ToString("F1", CultureInfo.InvariantCulture) : "--", "AccentOrange");
        AddCardStat(stats, Localization.T("Ses.CpuAvgPeak"), double.IsNaN(s.CpuLoadAvg) ? "--" : $"{s.CpuLoadAvg:F0}% / {s.CpuLoadMax:F0}%", "AccentGreen");
        AddCardStat(stats, Localization.T("Ses.GpuAvgPeak"), double.IsNaN(s.GpuLoadAvg) ? "--" : $"{s.GpuLoadAvg:F0}% / {s.GpuLoadMax:F0}%", "AccentPurple");
        AddCardStat(stats, Localization.T("Ses.RamPeak"), s.RamMaxGb > 0 ? $"{s.RamMaxGb:F1} GB" : "--", "AccentBlue");
        Grid.SetRow(stats, 1);
        root.Children.Add(stats);

        // 行 3：1% Low / 0.1% Low / 帧总数
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foot.Children.Add(new TextBlock { Text = Localization.F("Ses.Foot", s.FpsOneLow.ToString("F1", CultureInfo.InvariantCulture), s.FpsPointOneLow.ToString("F1", CultureInfo.InvariantCulture), s.FrameTotal.ToString("N0", CultureInfo.InvariantCulture)), FontSize = 11, Foreground = (Brush)FindResource("TextSecondary") });
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        card.Child = root;
        return card;
    }

    private void AddCardStat(UniformGrid grid, string label, string value, string colorKey)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = (Brush)FindResource("TextSecondary") });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 21, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource(colorKey) });
        grid.Children.Add(sp);
    }

    // ================= Tab 切换 =================

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview == null) return;
        var rb = (RadioButton)sender;
        string tab = rb.Tag?.ToString() ?? "";
        PageOverview.Visibility = tab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PageDetails.Visibility = tab == "details" ? Visibility.Visible : Visibility.Collapsed;
        PageCharts.Visibility = tab == "charts" ? Visibility.Visible : Visibility.Collapsed;
        PageSessions.Visibility = tab == "sessions" ? Visibility.Visible : Visibility.Collapsed;
        PageFreeGames.Visibility = tab == "freegames" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "sessions") RefreshSessions();
        if (tab == "freegames" && _freeGames == null) _ = LoadFreeGamesAsync();
    }

    /// <summary>语言切换后重绘 C# 动态构造的界面部分。</summary>
    private void ApplyLanguage()
    {
        try { UpdateUi(); } catch { }
        try { BuildDetailsPanel(); } catch { }
        RefreshSessions();
        if (_freeGames != null) { try { RenderFreeGames(); } catch { } }
        if (_tile != null) { try { _tile.Rebuild(); } catch { } }
        if (_tbWidget != null) { try { _tbWidget.ApplyLanguage(); } catch { } }
    }

    // ================= 喜加一（限免情报） =================

    private List<FreeGame>? _freeGames;
    private readonly HttpClient _imgHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private async Task LoadFreeGamesAsync()
    {
        try
        {
            FreeStatusText.Text = Localization.T("Free.Loading");
            FreeRefreshBtn.IsEnabled = false;
            _freeGames = new List<FreeGame>();
            RenderFreeGames();

            // 渐进式：Epic 快（~2s）先渲染；Steam 慢/不可达时限时 8s 失败即跳过
            var epicTask = FreeGameService.FetchEpicAsync(System.Threading.CancellationToken.None);
            var steamTask = FreeGameService.FetchSteamAsync(System.Threading.CancellationToken.None);

            var gogTask = FreeGameService.FetchCheapSharkDealsAsync(System.Threading.CancellationToken.None, storeId: 7, platform: "GOG", maxPrice: 0, top: 8);
            var humbleTask = FreeGameService.FetchCheapSharkDealsAsync(System.Threading.CancellationToken.None, storeId: 11, platform: "Humble", maxPrice: 0, top: 8);

            string notes = "";
            try
            {
                var epic = await epicTask.ConfigureAwait(true);
                _freeGames.AddRange(epic);
                RenderFreeGames();
                if (epic.Count == 0) notes += Localization.T("Free.EpicEmpty");
            }
            catch (Exception ex)
            {
                notes += Localization.F("Free.EpicFailed", ex.Message);
                RenderFreeGames();
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var steamTask2 = Task.Run(() => FreeGameService.FetchSteamAsync(cts.Token));
                var steam = await steamTask2.ConfigureAwait(true);
                _freeGames.AddRange(steam);
                RenderFreeGames();
                if (steam.Count == 0) notes += Localization.T("Free.SteamEmpty");
            }
            catch (Exception ex)
            {
                notes += Localization.T("Free.SteamUnreachable");
            }

            try
            {
                var gog = await gogTask.ConfigureAwait(true);
                if (gog.Count > 0) { _freeGames.AddRange(gog); RenderFreeGames(); }
            }
            catch { notes += Localization.T("Free.GogFailed"); }

            try
            {
                var hum = await humbleTask.ConfigureAwait(true);
                if (hum.Count > 0) { _freeGames.AddRange(hum); RenderFreeGames(); }
            }
            catch { notes += Localization.T("Free.HumbleFailed"); }

            FreeStatusText.Text = Localization.F("Free.Updated", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), (notes.Length == 0 ? Localization.T("Free.AllOk") : notes));
        }
        catch (Exception ex)
        {
            FreeStatusText.Text = Localization.F("Free.FetchFailed", ex.Message);
        }
        finally
        {
            FreeRefreshBtn.IsEnabled = true;
        }
    }

    private void RenderFreeGames()
    {
        FreeGamesHost.Children.Clear();
        var list = _freeGames ?? new List<FreeGame>();
        int active = list.Count(g => !g.Upcoming);
        FreeCountText.Text = list.Count == 0 ? Localization.T("Free.Empty") : Localization.F("Free.Count", list.Count, active, list.Count - active);

        foreach (var g in list)
        {
            var card = BuildFreeGameCard(g);
            FreeGamesHost.Children.Add(card);
            _ = LoadCoverAsync(g.ImageUrl, card);
        }
        if (list.Count == 0)
        {
            FreeGamesHost.Children.Add(new TextBlock
            {
                Text = Localization.T("Free.NoData"),
                Foreground = (Brush)FindResource("TextSecondary"),
                FontSize = 12.5,
                Margin = new Thickness(2, 8, 0, 0),
            });
        }
    }

    private Border BuildFreeGameCard(FreeGame g)
    {
        var card = new Border
        {
            Style = (Style)FindResource("PanelCard"),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(14, 12, 14, 12),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 封面占位
        var coverHost = new Border
        {
            Background = (Brush)FindResource("BgChip"),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock { Text = Localization.T("Free.CoverLoading"), Foreground = (Brush)FindResource("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 },
        };
        Grid.SetColumn(coverHost, 0);
        grid.Children.Add(coverHost);
        card.Tag = coverHost; // 异步封面回填用

        // 中部信息
        var mid = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = g.Title, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
        mid.Children.Add(new TextBlock
        {
            Text = Localization.F("Free.Platform", g.Platform) + (string.IsNullOrEmpty(g.Seller) ? "" : $"　·　{g.Seller}"),
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 6, 0, 0),
        });

        string priceTxt = g.FmtPrice.Length > 0 ? g.FmtPrice : (g.OriginPriceCents > 0 ? "¥" + (g.OriginPriceCents / 100.0).ToString("F2", CultureInfo.InvariantCulture) : "");
        string timeTxt;
        if (g.Upcoming && g.StartLocal.HasValue)
            timeTxt = Localization.F("Free.Upcoming", g.StartLocal.Value.ToString("MM-dd HH:mm"));
        else if (g.EndLocal.HasValue)
        {
            var left = g.EndLocal.Value - DateTime.Now;
            timeTxt = left > TimeSpan.Zero
                ? Localization.F("Free.Deadline", g.EndLocal.Value.ToString("MM-dd HH:mm"), (int)left.TotalDays, left.Hours)
                : Localization.T("Free.Ended");
        }
        else if (g.Platform is "GOG" or "Humble")
            timeTxt = Localization.T("Free.DealRunning");
        else
            timeTxt = Localization.T("Free.FreeRunning");

        mid.Children.Add(new TextBlock { Text = timeTxt, FontSize = 12, Foreground = g.Upcoming ? (Brush)FindResource("AccentBlue") : (Brush)FindResource("AccentGreen"), Margin = new Thickness(0, 6, 0, 0) });
        if (priceTxt.Length > 0)
        {
            var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            priceRow.Children.Add(new Border
            {
                Background = (Brush)FindResource("AccentGreen"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2, 7, 2),
                Child = new TextBlock { Text = g.Seller.StartsWith("-") ? g.Seller : "-100%", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Brushes.Black },
            });
            priceRow.Children.Add(new TextBlock { Text = priceTxt, FontSize = 12.5, Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextDecorations = TextDecorations.Strikethrough });
            mid.Children.Add(priceRow);
        }
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        // 右侧按钮：内嵌登录领取（WebView2 Cookie 持久化）+ 系统浏览器兜底
        bool isDeal = g.Platform is "GOG" or "Humble";
        var btn = new Button
        {
            Content = isDeal ? Localization.T("Free.Buy") : Localization.T("Free.Claim"),
            Style = (Style)FindResource("OverlayToggle"),
            Background = (Brush)FindResource("AccentBlue"),
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(18, 9, 18, 9),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Localization.T("Tip.Claim"),
        };
        btn.Click += (_, _) =>
        {
            if (isDeal)
            {
                try { Process.Start(new ProcessStartInfo(g.Url) { UseShellExecute = true }); } catch { }
            }
            else
            {
                OpenStoreLogin();
            }
        };
        var btnHost = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        btnHost.Children.Add(btn);
        var extLink = new TextBlock
        {
            Text = Localization.T("Free.ExternalOpen"),
            FontSize = 10.5,
            Foreground = (Brush)FindResource("AccentBlue"),
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        extLink.MouseLeftButtonDown += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(g.Url) { UseShellExecute = true }); }
            catch (Exception ex) { FreeStatusText.Text = Localization.F("Free.OpenBrowserFailed", ex.Message); }
        };
        btnHost.Children.Add(extLink);
        Grid.SetColumn(btnHost, 2);
        grid.Children.Add(btnHost);

        card.Child = grid;
        return card;
    }

    private void OpenStoreLogin()
    {
        try
        {
            var win = new StoreLoginWindow();
            win.Show();
        }
        catch (Exception ex)
        {
            FreeStatusText.Text = Localization.F("Free.OpenEmbedFailed", ex.Message);
        }
    }

    private void FreeLoginBtn_Click(object sender, RoutedEventArgs e) => OpenStoreLogin();

    private void FreePlatformBtn_Click(object sender, RoutedEventArgs e)
    {
        // 预留：切 Epic / Steam 商店页（当前窗口默认打开 Epic 限免页，可导航）
        OpenStoreLogin();
    }

    private async Task LoadCoverAsync(string url, Border card)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var bytes = await _imgHttp.GetByteArrayAsync(url).ConfigureAwait(true);
            using var ms = new MemoryStream(bytes);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.DecodePixelWidth = 360;
            img.EndInit();
            img.Freeze();
            if (card.Tag is Border host)
            {
                host.Child = new System.Windows.Controls.Image { Source = img, Stretch = Stretch.UniformToFill, MaxHeight = 92 };
            }
        }
        catch { }
    }

    private async void FreeRefreshBtn_Click(object sender, RoutedEventArgs e) => await LoadFreeGamesAsync();

    // ================= 硬件详情面板 =================

    private void BuildDetailsPanel()
    {
        // 重建前清空旧内容（语言切换时会整块重建）
        DetailGrid.Children.Clear();
        TempBarsHost.Children.Clear();
        StGrid.Children.Clear();
        _detailValues.Clear();
        _tempBars.Clear();
        while (StGrid.RowDefinitions.Count > 0) StGrid.RowDefinitions.RemoveAt(0);
        while (DetailGrid.RowDefinitions.Count > 0) DetailGrid.RowDefinitions.RemoveAt(0);

        BuildStaticPanel();
        BuildTempBars();
        AddDetailSection(Localization.T("Sec.Cpu"), new[]
        {
            (Localization.T("D.CpuLoad"), "cpu.load"), (Localization.T("D.CpuTemp"), "cpu.temp"), (Localization.T("D.CpuClk"), "cpu.clk"), (Localization.T("D.CpuPwr"), "cpu.pwr"),
        });
        AddDetailSection(Localization.T("Sec.Gpu"), new[]
        {
            (Localization.T("D.GpuLoad"), "gpu.load"), (Localization.T("D.GpuTemp"), "gpu.temp"),
            (Localization.T("D.GpuCoreClk"), "gpu.coreclk"), (Localization.T("D.GpuMemClk"), "gpu.memclk"), (Localization.T("D.GpuPwr"), "gpu.pwr"), (Localization.T("D.GpuFan"), "gpu.fan"),
            (Localization.T("D.GpuMemUsed"), "gpu.mem"), (Localization.T("D.GpuMemTotal"), "gpu.memtotal"),
        });
        AddDetailSection(Localization.T("Sec.Misc"), new[]
        {
            (Localization.T("D.RamUsed"), "ram.used"), (Localization.T("D.RamTotal"), "ram.total"), (Localization.T("D.RamPct"), "ram.pct"),
            (Localization.T("D.DiskRead"), "disk.read"), (Localization.T("D.DiskWrite"), "disk.write"), (Localization.T("D.DiskTemp"), "disk.temp"),
            (Localization.T("D.NetDown"), "net.down"), (Localization.T("D.NetUp"), "net.up"),
            (Localization.T("D.BatPct"), "bat.pct"), (Localization.T("D.BatPw"), "bat.pw"), (Localization.T("D.BatAc"), "bat.ac"),
        });
    }

    // ===== 游戏加加风格：左侧静态硬件档案 =====

    private void BuildStaticPanel()
    {
        try
        {
            var si = App.Engine.Static;
            if (si == null) return;
            StOsText.Text = si.OsDisplay;
            StMachineText.Text = si.MachineName;
            var rows = new List<(string Icon, string Label, string Value)>
            {
                ("Ⓒ", Localization.T("St.Cpu"), si.CpuName + (si.CpuThreads > 0 ? Localization.F("St.Cores", si.CpuCores, si.CpuThreads) : "")),
                ("Ⓖ", Localization.T("St.Gpu"), si.GpuName + (string.IsNullOrEmpty(si.GpuVram) ? "" : Localization.F("St.Vram", si.GpuVram))),
                ("Ⓜ", Localization.T("St.Mb"), si.Motherboard),
                ("Ⓓ", Localization.T("St.Disk"), si.DiskModel + (string.IsNullOrEmpty(si.DiskCapacity) ? "" : $"　{si.DiskCapacity}")),
                ("Ⓢ", Localization.T("St.Display"), si.DisplayInfo),
                ("Ⓡ", Localization.T("St.Ram"), si.RamInfo),
                ("Ⓑ", Localization.T("St.Battery"), string.IsNullOrEmpty(si.BatteryDisplay) ? Localization.T("St.NoBattery") : si.BatteryDisplay),
            };
            int r = 0;
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Value)) continue;
                StGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var lbl = new TextBlock { Text = row.Label, FontSize = 12.5, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 6) };
                var val = new TextBlock { Text = row.Value, FontSize = 12.5, Foreground = (Brush)FindResource("TextPrimary"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 6) };
                Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0);
                Grid.SetRow(val, r); Grid.SetColumn(val, 1);
                StGrid.Children.Add(lbl);
                StGrid.Children.Add(val);
                r++;
            }
            if (!string.IsNullOrEmpty(si.RamSticks))
            {
                StGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var lbl = new TextBlock { Text = "", FontSize = 12.5, Foreground = (Brush)FindResource("TextSecondary") };
                var val = new TextBlock { Text = si.RamSticks, FontSize = 11.5, Foreground = (Brush)FindResource("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
                Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0);
                Grid.SetRow(val, r); Grid.SetColumn(val, 1);
                StGrid.Children.Add(lbl);
                StGrid.Children.Add(val);
            }
        }
        catch { }
    }

    // ===== 游戏加加风格：右侧温度条（标签 + 进度条 + 当前值） =====

    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Val)> _tempBars = new();

    private void BuildTempBars()
    {
        AddTempBar("cpu.temp", Localization.T("Tb.CpuTemp"), 100, _orange);
        AddTempBar("gpu.temp", Localization.T("Tb.GpuTemp"), 100, _green);
        AddTempBar("disk.temp", Localization.T("Tb.DiskTemp"), 100, _green);
        AddTempBar("ram.pct", Localization.T("Tb.RamPct"), 100, _blue);
        AddTempBar("gpu.load", Localization.T("Tb.GpuLoad"), 100, _blue);
    }

    private void AddTempBar(string key, string label, double max, Brush color)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        var lbl = new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        var bar = new ProgressBar { Height = 7, Minimum = 0, Maximum = max, Foreground = color, Background = (Brush)FindResource("BgChip"), Template = (ControlTemplate)FindResource("HBarTemplate") };
        var val = new TextBlock { Text = "N/A", FontSize = 12, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(lbl, 0); Grid.SetColumn(bar, 1); Grid.SetColumn(val, 2);
        grid.Children.Add(lbl); grid.Children.Add(bar); grid.Children.Add(val);
        TempBarsHost.Children.Add(grid);
        _tempBars[key] = (bar, val);
    }

    private void UpdateTempBars(MonitorEngine.Snapshot s)
    {
        Tb("cpu.temp", s.CpuTemp, "F0", "°C");
        Tb("gpu.temp", s.GpuTemp, "F0", "°C");
        Tb("disk.temp", s.DiskTemp, "F0", "°C");
        Tb("ram.pct", double.IsNaN(s.RamUsedGb) || double.IsNaN(s.RamTotalGb) || s.RamTotalGb <= 0 ? double.NaN : s.RamUsedGb / s.RamTotalGb * 100.0, "F0", "%");
        Tb("gpu.load", s.GpuLoad, "F0", "%");
    }

    private void Tb(string key, double v, string fmt, string unit)
    {
        if (!_tempBars.TryGetValue(key, out var t)) return;
        if (double.IsNaN(v)) { t.Val.Text = "N/A"; t.Bar.Value = 0; }
        else { t.Val.Text = v.ToString(fmt, CultureInfo.InvariantCulture) + unit; t.Bar.Value = Math.Clamp(v, 0, t.Bar.Maximum); }
    }

    private void AddDetailSection(string title, (string Label, string Key)[] rows)
    {
        var host = DetailGrid;
        var title2 = new TextBlock { Text = title, Style = (Style)FindResource("PanelTitle"), Margin = new Thickness(0, 12, 0, 2) };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(title2, host.RowDefinitions.Count - 1); Grid.SetColumnSpan(title2, 2);
        host.Children.Add(title2);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < rows.Length; i++)
        {
            var lbl = new TextBlock { Text = rows[i].Label, FontSize = 12.5, Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 4, 0, 4) };
            var val = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextPrimary"), Margin = new Thickness(0, 4, 0, 4), Text = "N/A" };
            Grid.SetRow(lbl, i); Grid.SetColumn(lbl, 0);
            Grid.SetRow(val, i); Grid.SetColumn(val, 1);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            val.VerticalAlignment = VerticalAlignment.Center;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(lbl);
            grid.Children.Add(val);
            _detailValues[rows[i].Key] = val;
        }
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(grid, host.RowDefinitions.Count - 1); Grid.SetColumnSpan(grid, 2);
        host.Children.Add(grid);
    }

    private void UpdateDetails(MonitorEngine.Snapshot s)
    {
        Dv("cpu.load", Pct(s.CpuLoad));
        Dv("cpu.temp", Temp(s.CpuTemp) + SrcSuffix(s.CpuTempSource));
        Dv("cpu.clk", Mhz(s.CpuMaxClock));
        Dv("cpu.pwr", W(s.CpuPower));
        Dv("ram.used", Gb(s.RamUsedGb));
        Dv("ram.total", Gb(s.RamTotalGb));
        Dv("ram.pct", double.IsNaN(s.RamUsedGb) || double.IsNaN(s.RamTotalGb) || s.RamTotalGb <= 0 ? "N/A" : (s.RamUsedGb / s.RamTotalGb * 100.0).ToString("F0", CultureInfo.InvariantCulture) + " %");
        Dv("gpu.load", Pct(s.GpuLoad));
        Dv("gpu.temp", Temp(s.GpuTemp));
        Dv("gpu.mem", Mb(s.GpuMemUsed));
        Dv("gpu.memtotal", Mb(s.GpuMemTotal));
        Dv("gpu.coreclk", Mhz(s.GpuCoreClock));
        Dv("gpu.memclk", Mhz(s.GpuMemClock));
        Dv("gpu.pwr", W(s.GpuPower));
        Dv("gpu.fan", Rpm(s.GpuFanRpm));
        Dv("disk.read", Kbs(s.DiskReadKbs));
        Dv("disk.write", Kbs(s.DiskWriteKbs));
        Dv("disk.temp", Temp(s.DiskTemp));
        Dv("net.down", Kbs(s.DownKbps));
        Dv("net.up", Kbs(s.UpKbps));
        Dv("bat.pct", Pct(s.BatteryPct));
        Dv("bat.pw", W(s.BatteryPowerW));
        Dv("bat.ac", string.IsNullOrEmpty(s.AcOnline) ? "N/A" : s.AcOnline);
        UpdateTempBars(s);
    }

    private void Dv(string key, string text)
    {
        if (_detailValues.TryGetValue(key, out var tb)) tb.Text = text;
    }

    private static string SrcSuffix(string src) => string.IsNullOrEmpty(src) ? "" : "　(" + src + ")";

    // ================= 曲线 =================

    private void PushHist(Queue<double> q, double v)
    {
        if (double.IsNaN(v)) v = 0;
        q.Enqueue(v);
        while (q.Count > 120) q.Dequeue();
    }

    private static void RenderChart(ChartPlot chart, Queue<double> data, double max)
    {
        chart.UpdateValues(data, max);
    }

    // ================= 主刷新 =================

    private void UpdateUi()
    {
        var s = App.Engine?.Current;
        if (s == null) return;

        // 会话采样（每秒一次，内部自限频）
        try { App.Engine?.SampleSessionTick(s); } catch { }

        // 游戏退出后的性能报告弹窗（UI 线程安全路径）
        try { ShowPendingReport(); } catch { }

        // 顶栏徽章 + 独占全屏警告
        bool game = !string.IsNullOrEmpty(s.GameName);
        ChipGameDot.Fill = game ? _green : _gray;
        ChipGameText.Text = game ? Localization.F("Main.GameChip", s.GameName, s.GamePid, Localization.GameSource(s.GameSource)) : Localization.T("Main.NoGame");
        ChipGameText.Foreground = game ? (Brush)FindResource("TextPrimary") : _gray;
        FpsCardTitle.Text = game ? $"FPS · {s.GameName}" : Localization.T("Ov.FpsTitle");
        StatusText.Text = s.ExclusiveFullscreen
            ? Localization.T("Status.ExclFullscreen")
            : Localization.F("Status.Running", _settings.HwPollMs, s.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        // GPU
        GpuTempText.Text = F(s.GpuTemp, "F0");
        GpuTempText.Foreground = ColorFor(s.GpuTemp, 70, 85);
        SetLoadBar(GpuLoadBar, GpuLoadText, s.GpuLoad);
        GpuLoadPctText.Text = double.IsNaN(s.GpuLoad) ? "-- %" : s.GpuLoad.ToString("F0", CultureInfo.InvariantCulture) + " %";
        if (!double.IsNaN(s.GpuMemUsed))
        {
            double total = double.IsNaN(s.GpuMemTotal) ? 0 : s.GpuMemTotal;
            if (total > 0)
            {
                GpuMemBar.Maximum = total;
                GpuMemBar.Value = Math.Min(s.GpuMemUsed, total);
                double ratio = s.GpuMemUsed / total;
                GpuMemBar.Foreground = ratio > 0.9 ? _red : (ratio > 0.75 ? _orange : _green);
                GpuMemText.Text = $"{s.GpuMemUsed:F0} / {total:F0} MB";
            }
            else { GpuMemBar.Value = 0; GpuMemText.Text = $"{s.GpuMemUsed:F0} MB"; }
        }
        else { GpuMemBar.Value = 0; GpuMemText.Text = "N/A"; }
        GpuNameText.Text = string.IsNullOrEmpty(s.GpuName) ? "—" : s.GpuName;
        GpuClockText.Text = double.IsNaN(s.GpuCoreClock) ? "" : s.GpuCoreClock.ToString("F0", CultureInfo.InvariantCulture) + " MHz";

        // CPU
        CpuTempText.Text = F(s.CpuTemp, "F0");
        CpuTempText.Foreground = ColorFor(s.CpuTemp, 75, 90);
        CpuTempText.ToolTip = string.IsNullOrEmpty(s.CpuTempSource) ? null : Localization.F("Cpu.TempSrc", Localization.CpuTempSource(s.CpuTempSource));
        SetLoadBar(CpuLoadBar, CpuLoadText, s.CpuLoad);
        CpuLoadPctText.Text = double.IsNaN(s.CpuLoad) ? "-- %" : s.CpuLoad.ToString("F0", CultureInfo.InvariantCulture) + " %";
        if (!double.IsNaN(s.RamUsedGb) && !double.IsNaN(s.RamTotalGb) && s.RamTotalGb > 0)
        {
            RamBar.Maximum = s.RamTotalGb;
            RamBar.Value = Math.Min(s.RamUsedGb, s.RamTotalGb);
            double ratio = s.RamUsedGb / s.RamTotalGb;
            RamBar.Foreground = ratio > 0.9 ? _red : (ratio > 0.8 ? _orange : _green);
            RamText.Text = $"{s.RamUsedGb:F1} / {s.RamTotalGb:F0} GB";
        }
        else { RamBar.Value = 0; RamText.Text = "N/A"; }
        CpuPowerText.Text = double.IsNaN(s.CpuPower) ? "" : Localization.F("Cpu.PwrVal", s.CpuPower.ToString("F1", CultureInfo.InvariantCulture));
        CpuClkText.Text = double.IsNaN(s.CpuMaxClock) ? "" : s.CpuMaxClock.ToString("F0", CultureInfo.InvariantCulture) + " MHz";

        // FPS
        bool hasFrames = s.FrameCount > 0;
        FpsText.Text = hasFrames ? s.Fps.ToString("F0", CultureInfo.InvariantCulture) : "--";
        FrameTimeText.Text = hasFrames ? $"{s.FrameTimeMs:F2} ms" : "N/A";
        FrameCountText.Text = s.FrameCount.ToString(CultureInfo.InvariantCulture);
        SnapTimeText.Text = s.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        // 统计
        AvgFpsText.Text = hasFrames ? s.AvgFps.ToString("F1", CultureInfo.InvariantCulture) : "--";
        P1LowText.Text = hasFrames ? s.OnePercentLow.ToString("F1", CultureInfo.InvariantCulture) : "--";
        P01LowText.Text = hasFrames ? s.PointOnePercentLow.ToString("F1", CultureInfo.InvariantCulture) : "--";
        FpsNowText.Text = hasFrames ? s.Fps.ToString("F1", CultureInfo.InvariantCulture) : "--";

        // 运行状态
        if (!game)
            RunStateText.Text = Localization.T("Run.Waiting2");
        else if (!s.PresentMonRunning)
            RunStateText.Text = Localization.F("Run.Binding", s.GameName) + (string.IsNullOrEmpty(s.PmExitInfo) ? "" : Localization.F("Run.PmExitInfo", s.PmExitInfo switch
            {
                "PM已退出" => Localization.T("Pm.Exited"),
                "PM被拒" => Localization.T("Pm.Denied"),
                _ => s.PmExitInfo,
            }));
        else if (!hasFrames)
            RunStateText.Text = Localization.F("Run.WaitingFrames", s.GameName, s.PmLines, s.PmFrames);
        else
            RunStateText.Text = Localization.F("Run.Collecting", s.GameName, s.FrameCount, s.MaxFrameMs.ToString("F1", CultureInfo.InvariantCulture));

        // 网络/磁盘/电池
        NetDownText.Text = FormatRate(s.DownKbps);
        NetUpText.Text = FormatRate(s.UpKbps);
        DiskText.Text = double.IsNaN(s.DiskReadKbs) && double.IsNaN(s.DiskWriteKbs)
            ? "N/A"
            : Localization.F("Cpu.DiskRW", Kbs(s.DiskReadKbs), Kbs(s.DiskWriteKbs));
        DiskTempText.Text = Temp(s.DiskTemp);
        BatteryText.Text = double.IsNaN(s.BatteryPct)
            ? "N/A"
            : $"{s.BatteryPct:F0}% {s.AcOnline}" + (double.IsNaN(s.BatteryPowerW) ? "" : $" {s.BatteryPowerW:F0}W");

        // 详情页 + 曲线
        UpdateDetails(s);
        _histTick++;
        if (_histTick % 5 == 0) // 每 1s 采一个点
        {
            PushHist(_fpsHist, s.Fps);
            PushHist(_gpuHist, s.GpuLoad);
            PushHist(_cpuHist, s.CpuLoad);
            PushHist(_cpuTempHist, s.CpuTemp);
            PushHist(_gpuTempHist, s.GpuTemp);
            PushHist(_ramHist, double.IsNaN(s.RamUsedGb) || double.IsNaN(s.RamTotalGb) || s.RamTotalGb <= 0 ? double.NaN : s.RamUsedGb / s.RamTotalGb * 100.0);
            PushHist(_gpuMemHist, double.IsNaN(s.GpuMemUsed) || double.IsNaN(s.GpuMemTotal) || s.GpuMemTotal <= 0 ? double.NaN : s.GpuMemUsed / s.GpuMemTotal * 100.0);
        }
        if (PageCharts.Visibility == Visibility.Visible)
        {
            // FPS 区间：屏幕刷新率基准与实际峰值取大者，超峰时自动扩量
            double fpsPeak = _fpsHist.Count > 0 ? _fpsHist.Max() : 0;
            double fpsMax = Math.Max(_fpsCeiling, Math.Ceiling(fpsPeak / 30.0) * 30 + 10);
            RenderChart(FpsChart, _fpsHist, fpsMax);
            RenderChart(GpuChart, _gpuHist, 100);
            RenderChart(CpuChart, _cpuHist, 100);
            double cpuTempMax = _cpuTempHist.Count > 0 ? Math.Max(60, Math.Ceiling(_cpuTempHist.Max() / 20.0) * 20) : 100;
            RenderChart(CpuTempChart, _cpuTempHist, cpuTempMax);
            double gpuTempMax = _gpuTempHist.Count > 0 ? Math.Max(60, Math.Ceiling(_gpuTempHist.Max() / 20.0) * 20) : 100;
            RenderChart(GpuTempChart, _gpuTempHist, gpuTempMax);
            RenderChart(RamChart, _ramHist, 100);
            RenderChart(GpuMemChart, _gpuMemHist, 100);
            FpsRangeText.Text = _fpsHist.Count > 0
                ? Localization.F("Chart.RangePeak", fpsPeak.ToString("F0", CultureInfo.InvariantCulture), DetectRefreshRate().ToString("F0", CultureInfo.InvariantCulture), fpsMax.ToString("F0", CultureInfo.InvariantCulture))
                : Localization.F("Chart.RangeRefresh", DetectRefreshRate().ToString("F0", CultureInfo.InvariantCulture), fpsMax.ToString("F0", CultureInfo.InvariantCulture));
        }

        StatusText.Text = Localization.F("Status.Running", _settings.HwPollMs, s.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }

    // ================= 桌面监控磁贴 =================

    // ================= 桌面监控磁贴 =================

    private void TileStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TileStyleCombo == null || TileStyleCombo.SelectedIndex < 0 || _settings == null) return;
        _settings.TileStyle = TileStyleCombo.SelectedIndex switch { 1 => "Bar", 2 => "Card", 3 => "Taskbar", _ => "GamePP" };
        _settings.Save();
        _tile?.ApplyConfig();
    }

    private void TileOpt_Changed(object sender, RoutedEventArgs e)
    {
        if (TileClickThroughChk == null || _settings == null) return;
        _settings.TileClickThrough = TileClickThroughChk.IsChecked == true;
        bool wantTaskbar = TileTaskbarChk.IsChecked == true;
        _settings.TileShowInTaskbar = wantTaskbar;
        _settings.TileMiniOnMinimize = TileMiniChk.IsChecked == true;
        _settings.Save();
        if (_tile != null)
        {
            _tile.ApplyConfig();
            _tile.ApplyExStyle();
        }
        // 任务栏迷你监控条跟随勾选
        if (wantTaskbar && _tbWidget == null)
        {
            _tbWidget = new TaskbarWidget();
            _tbWidget.Show();
        }
        else if (!wantTaskbar && _tbWidget != null)
        {
            _tbWidget.Close();
            _tbWidget = null;
        }
    }

    private void TileResetPosBtn_Click(object sender, RoutedEventArgs e) => _tile?.ResetPosition();

    private void InitTileFromSettings()
    {
        try
        {
            if (_settings.TileOn)
            {
                _tile = new TileWindow(_settings);
                _tile.Show();
                TileBtn.Content = Localization.T("Tile.BtnOn");
            }
            if (_settings.TileShowInTaskbar)
            {
                _tbWidget = new TaskbarWidget();
                _tbWidget.Show();
            }
        }
        catch (Exception ex)
        {
            _settings.TileOn = false;
            _settings.Save();
            MessageBox.Show(this, Localization.F("Tile.FailStart", ex.Message), Localization.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TileBtn_Click(object sender, RoutedEventArgs e) => ToggleTile();

    private void ToggleTile()
    {
        if (_tile == null)
        {
            _tile = new TileWindow(_settings);
            _tile.Show();
            TileBtn.Content = Localization.T("Tile.BtnOn");
            _settings.TileOn = true;
        }
        else
        {
            _tile.Close();
            _tile = null;
            TileBtn.Content = Localization.T("Tile.BtnOff");
            _settings.TileOn = false;
            _settings.Save();
        }
    }

    private void MainWindow_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.TileMiniOnMinimize)
            _tile?.SetMini(true);
    }

    // ================= Overlay =================

    private void OverlayBtn_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void ToggleOverlay()
    {
        if (_overlay == null)
        {
            _overlay = new OverlayWindow(_settings);
            _overlay.Show();
            OverlayBtn.Content = Localization.T("Overlay.BtnOn");
            _settings.OsdOn = true;
        }
        else CloseOverlay();
    }

    private void CloseOverlay()
    {
        if (_overlay == null) return;
        try { _overlay.Close(); } catch { }
        _overlay = null;
        OverlayBtn.Content = Localization.T("Overlay.BtnOff");
        _settings.OsdOn = false;
        _settings.Save();
    }

    // ================= 辅助 =================

    private static string F(double v, string fmt) => double.IsNaN(v) ? "--" : v.ToString(fmt, CultureInfo.InvariantCulture);
    private static string Pct(double v) => double.IsNaN(v) ? "N/A" : v.ToString("F0", CultureInfo.InvariantCulture) + " %";
    private static string Temp(double v) => double.IsNaN(v) ? "N/A" : v.ToString("F0", CultureInfo.InvariantCulture) + " °C";
    private static string Mhz(double v) => double.IsNaN(v) ? "N/A" : v.ToString("F0", CultureInfo.InvariantCulture) + " MHz";
    private static string W(double v) => double.IsNaN(v) ? "N/A" : v.ToString("F1", CultureInfo.InvariantCulture) + " W";
    private static string Rpm(double v) => double.IsNaN(v) ? "N/A" : v.ToString("F0", CultureInfo.InvariantCulture) + " RPM";
    private static string Gb(double v) => double.IsNaN(v) ? "N/A" : v.ToString("F1", CultureInfo.InvariantCulture) + " GB";
    private static string Mb(double v) => double.IsNaN(v) ? "N/A" : v.ToString("F0", CultureInfo.InvariantCulture) + " MB";
    private static string Kbs(double v) => double.IsNaN(v) ? "N/A" : FormatRate(v);

    private static string FormatRate(double kbps)
    {
        if (double.IsNaN(kbps)) return "--";
        return kbps >= 1024 ? $"{kbps / 1024.0:F2} MB/s" : $"{kbps:F0} KB/s";
    }

    private Brush ColorFor(double v, double warn, double danger)
    {
        if (double.IsNaN(v)) return _gray;
        if (v >= danger) return _red;
        if (v >= warn) return _orange;
        return _green;
    }

    private void SetLoadBar(ProgressBar bar, TextBlock label, double load)
    {
        if (double.IsNaN(load)) { bar.Value = 0; label.Text = "N/A"; }
        else
        {
            bar.Value = Math.Clamp(load, 0, 100);
            label.Text = $"{load:F0} %";
            bar.Foreground = load > 92 ? _red : (load > 80 ? _orange : _green);
        }
    }
}
