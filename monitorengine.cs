using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace BFV_FPS_Monitor;

/// <summary>
/// v2.0 通用监控引擎：自动识别游戏进程、PresentMon 动态采集（动态表头）、
/// 全套硬件传感器（CPU/GPU/内存/磁盘/电池）、OSD/CSV/快照数据出口、事件日志。
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private const string PresentMonExe = "PresentMon.exe";
    private const string PmSessionName = "GameMon_PM";   // 独立会话名：防与其他 PM 实例互踢
    private const int FrameWindowMax = 1000;
    private static readonly string CsvPath = Path.Combine(AppContext.BaseDirectory, "bfv_fps_log.csv");

    // ===== 设置 =====
    private readonly AppSettings _settings;

    // ===== PresentMon =====
    private Process? _pm;
    private int _pmTargetPid;
    private long _pmLineCount;
    private long _pmFrameCount;
    private int _pmStartFailures;
    private int _pmGeneration;       // PM 实例代际：StopPresentMon 时 +1，使旧实例的延迟重启链失效
    private bool _captureAll;        // 当前 PM 是否 captureall 模式
    private bool _pmForceCaptureAll; // 定向会话连续被拒后自动降级为全进程捕获（换游戏时重置）
    private int _colProcessId = -1;  // ProcessID 列索引（captureall 过滤用）
    private string _pmHeader = "";
    private int _colMsBetweenPresents = -1;
    private readonly ConcurrentQueue<double> _frameTimesMs = new();

    // ===== 硬件 =====
    private Computer? _computer;
    private PerformanceCounter? _cpuTotal;
    private bool _needCpuCounterRecreate;
    private readonly List<PhysicalDrive> _drives = new();
    private sealed class PhysicalDrive
    {
        public string Name = "";
        public IHardware? Hw;
        public ulong LastRead, LastWrite;
        public long LastStamp;
        public double ReadKbs = double.NaN, WriteKbs = double.NaN;
    }

    // ===== 网络 =====
    private ulong _lastRecv, _lastSent;
    private long _lastNetStamp;

    // ===== 游戏绑定 =====
    private readonly object _bindLock = new();
    private string _boundName = "";
    private int _boundPid;
    private string _lastGameName = "";   // 上轮判定结果（会话稳定性判定，防 PM 重启间隙误判为游戏切换）
    private int _lastGamePid;

    // ===== 游戏会话统计（对标游戏加加性能统计） =====
    private GameSession? _session;                      // 进行中的会话
    private DateTime _lastSessionSample = DateTime.MinValue;
    private double _sessionElapsed;                     // 会话累计秒（按采样间隔累加）
    private readonly List<double> _sessFps = new();
    private readonly List<double> _sessCpuLoad = new();
    private readonly List<double> _sessCpuTemp = new();
    private readonly List<double> _sessGpuLoad = new();
    private readonly List<double> _sessGpuTemp = new();
    private readonly List<double> _sessRam = new();
    private readonly List<double> _sessGpuMem = new();
    private readonly List<double> _sessDown = new();
    private readonly List<double> _sessUp = new();
    private readonly List<double> _sessCpuPower = new();
    private readonly List<double> _sessGpuPower = new();
    private long _sessFrameTotal;
    private double _sessNetDownMb, _sessNetUpMb;
    private long _sessLastFrames;

    /// <summary>会话结束（游戏退出）事件，UI 订阅弹性能报告。</summary>
    public event Action<GameSession>? SessionEnded;
    public GameSession? CurrentSession => _session;

    // ===== 后台 =====
    private Thread? _procThread;
    private Task? _hwTask;
    private readonly CancellationTokenSource _cts = new();

    // ===== 快照 =====
    private volatile Snapshot _snapshot = new();
    public Snapshot Current => _snapshot;

    // ===== CSV =====
    private StreamWriter? _csv;
    private readonly object _csvLock = new();
    private DateTime _lastCsvSec = DateTime.MinValue;

    // ===== 事件日志 =====
    private readonly ConcurrentQueue<string> _events = new();
    public IEnumerable<string> Events => _events.ToArray();
    private StreamWriter? _fileLog;
    private readonly object _fileLogLock = new();
    private StreamWriter? _pmRawLog;
    private string PmRawPath => System.IO.Path.Combine(AppContext.BaseDirectory, "pm_raw.csv");
    private string LogFilePath => System.IO.Path.Combine(AppContext.BaseDirectory, "engine.log");

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _events.Enqueue(line);
        try
        {
            lock (_fileLogLock)
            {
                if (_fileLog == null)
                {
                    // 启动新会话时滚动旧日志（保留 1 份历史）
                    try { if (System.IO.File.Exists(LogFilePath)) System.IO.File.Copy(LogFilePath, LogFilePath + ".1", true); } catch { }
                    _fileLog = new StreamWriter(LogFilePath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
                }
                _fileLog.WriteLine(line);
            }
        }
        catch { }
    }

    // ===== Win32 内存状态 =====
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public sealed class Snapshot
    {
        public string GameName = "";
        public int GamePid;
        public string GameSource = "";
        public bool PresentMonRunning;
        public double Fps, AvgFps, OnePercentLow, PointOnePercentLow, FrameTimeMs, MaxFrameMs;
        public int FrameCount;
        public double CpuLoad = double.NaN, CpuTemp = double.NaN, CpuMaxClock = double.NaN, CpuPower = double.NaN;
        public string CpuTempSource = "";
        public double RamUsedGb = double.NaN, RamTotalGb = double.NaN;
        public double GpuLoad = double.NaN, GpuTemp = double.NaN, GpuMemUsed = double.NaN, GpuMemTotal = double.NaN;
        public double GpuCoreClock = double.NaN, GpuMemClock = double.NaN, GpuPower = double.NaN, GpuFanRpm = double.NaN;
        public string GpuName = "";
        public double DiskReadKbs = double.NaN, DiskWriteKbs = double.NaN, DiskTemp = double.NaN;
        public double DownKbps = double.NaN, UpKbps = double.NaN;
        public double BatteryPct = double.NaN, BatteryPowerW = double.NaN;
        public bool BatteryCharging;
        public string AcOnline = "";
        public bool ExclusiveFullscreen;   // 游戏处于独占全屏（OSD 无法覆盖）
        public long PmLines;               // PresentMon stdout 总行数（诊断）
        public long PmFrames;              // 已解析的有效帧数（诊断）
        public string PmExitInfo = "";     // PM 退出信息
        public DateTime Time = DateTime.Now;

        public Snapshot Clone() => (Snapshot)MemberwiseClone();
    }

    public MonitorEngine(AppSettings settings)
    {
        _settings = settings;
    }

    // ===== 静态硬件档案（游戏加加风格，启动时采集一次） =====
    public sealed class StaticInfo
    {
        public string OsName = "";
        public string CpuName = "";
        public int CpuCores, CpuThreads;
        public string GpuName = "";
        public string GpuVram = "";
        public string Motherboard = "";
        public string DiskModel = "";
        public string DiskCapacity = "";
        public string DisplayInfo = "";
        public string RamInfo = "";
        public string RamSticks = "";
        public string BatteryInfo = "";
        public string MachineName = "";
    }
    private StaticInfo? _static;
    public StaticInfo? Static => _static;

    private static System.Management.ManagementObjectCollection GetWmi(System.Management.ManagementObjectSearcher searcher)
    {
        using (searcher) return searcher.Get();
    }

    private void CollectStaticInfo()
    {
        var si = new StaticInfo();
        try
        {
            si.MachineName = Environment.MachineName;
            si.OsName = $"{Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")})";

            var cpu = GetWmi(new ManagementObjectSearcher("root\\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor")).Cast<System.Management.ManagementObject>().FirstOrDefault();
            if (cpu != null)
            {
                si.CpuName = cpu["Name"]?.ToString() ?? "";
                si.CpuCores = Convert.ToInt32(cpu["NumberOfCores"] ?? 0);
                si.CpuThreads = Convert.ToInt32(cpu["NumberOfLogicalProcessors"] ?? 0);
            }

            var gpu = GetWmi(new ManagementObjectSearcher("root\\cimv2", "SELECT Name, AdapterRAM FROM Win32_VideoController")).Cast<System.Management.ManagementObject>().Where(m => m["Name"]?.ToString()?.Contains("Oray") != true).FirstOrDefault();
            if (gpu != null)
            {
                si.GpuName = gpu["Name"]?.ToString() ?? "";
                ulong ram = 0;
                try { ram = Convert.ToUInt64(gpu["AdapterRAM"] ?? 0); } catch { }
                if (ram > 0) si.GpuVram = $"{ram / 1073741824.0:F0} GB";
            }

            var board = GetWmi(new ManagementObjectSearcher("root\\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard")).Cast<System.Management.ManagementObject>().FirstOrDefault();
            if (board != null) si.Motherboard = $"{board["Manufacturer"]} {board["Product"]}";

            var disk = GetWmi(new ManagementObjectSearcher("root\\cimv2", "SELECT Model, Size FROM Win32_DiskDrive")).Cast<System.Management.ManagementObject>().OrderByDescending(m => Convert.ToUInt64(m["Size"] ?? 0)).FirstOrDefault();
            if (disk != null)
            {
                si.DiskModel = disk["Model"]?.ToString() ?? "";
                ulong sz = 0;
                try { sz = Convert.ToUInt64(disk["Size"] ?? 0); } catch { }
                if (sz > 0) si.DiskCapacity = $"{sz / 1073741824.0:F0} GB";
            }

            var disp = GetWmi(new ManagementObjectSearcher("root\\cimv2", "SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController")).Cast<System.Management.ManagementObject>().Where(m => m["Name"]?.ToString()?.Contains("Oray") != true).FirstOrDefault();
            if (disp != null)
            {
                int hw = 0, hh = 0, hr = 0;
                try { hw = Convert.ToInt32(disp["CurrentHorizontalResolution"] ?? 0); hh = Convert.ToInt32(disp["CurrentVerticalResolution"] ?? 0); hr = Convert.ToInt32(disp["CurrentRefreshRate"] ?? 0); } catch { }
                si.DisplayInfo = $"{disp["Name"]}" + (hw > 0 ? $"　{hw}×{hh} {hr}Hz" : "");
            }

            ulong totalPhys;
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem)) totalPhys = mem.ullTotalPhys;
            else totalPhys = 0;
            si.RamInfo = $"{totalPhys / 1073741824.0:F0} GB";

            var bat = GetWmi(new ManagementObjectSearcher("root\\cimv2", "SELECT Name, EstimatedChargeRemaining, DesignCapacity FROM Win32_Battery")).Cast<System.Management.ManagementObject>().FirstOrDefault();
            if (bat != null)
                si.BatteryInfo = (bat["Name"]?.ToString() ?? "") + (bat["DesignCapacity"] != null ? $"　设计容量 {bat["DesignCapacity"]} mWh" : "");

            // 内存条详情（设备定位器 → SMBIOS 内存设备）
            try
            {
                var sticks = GetWmi(new ManagementObjectSearcher("root\\cimv2", "SELECT Manufacturer, Capacity, Speed, PartNumber FROM Win32_PhysicalMemory")).Cast<System.Management.ManagementObject>().ToList();
                if (sticks.Count > 0)
                {
                    var parts = sticks.Select(s2 =>
                    {
                        ulong cap = 0; int spd = 0;
                        try { cap = Convert.ToUInt64(s2["Capacity"] ?? 0); spd = Convert.ToInt32(s2["Speed"] ?? 0); } catch { }
                        string mfr = (s2["Manufacturer"]?.ToString() ?? "").Trim();
                        return $"{mfr} {cap / 1073741824.0:F0}GB {spd}MHz";
                    });
                    si.RamSticks = string.Join("\n", parts);
                }
            }
            catch { }
        }
        catch { }
        _static = si;
    }

    public void Start()
    {
        InitCsv();
        InitHardware();
        InitNetwork();

        StartPrefetchSession();
        _procThread = new Thread(ProcessLoop) { IsBackground = true, Name = "game-proc-poll" };
        _procThread.Start();
        _hwTask = Task.Run(() => HardwareLoop(_cts.Token));
        Task.Run(CollectStaticInfo);
        Log("监控引擎启动");
    }

    // ================= 游戏进程绑定 =================

    private void ProcessLoop()
    {
        bool wasBound = false;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                string name = "";
                int pid = 0;
                string src = "";

                bool auto = _settings.AutoDetect;
                string pinned = _settings.PinnedProcess;
                if (!string.IsNullOrEmpty(pinned))
                {
                    var pinnedProc = Process.GetProcessesByName(pinned).FirstOrDefault();
                    if (pinnedProc != null) { name = pinnedProc.ProcessName; pid = pinnedProc.Id; src = "手动"; }
                }
                if (pid == 0 && auto)
                {
                    var hit = GameDetector.DetectGame();
                    if (hit != null) { name = hit.Name; pid = hit.Pid; src = hit.Source; }
                }

                lock (_bindLock) { _boundName = name; _boundPid = pid; }

                bool bound = pid != 0;
                bool sameGame = wasBound && _lastGameName == name && _lastGamePid == pid;
                if (bound && (!wasBound || !sameGame))
                {
                    if (wasBound) Log($"游戏切换：{name} (PID {pid})");
                    else Log($"检测到游戏：{name} (PID {pid}，{src})");
                    if (wasBound) EndSession(collect: true);
                    StartSession(name, pid);
                    _pmForceCaptureAll = false;
                    StartPresentMon(pid);
                    _lastGameName = name;
                    _lastGamePid = pid;
                }
                else if (!bound && wasBound)
                {
                    Log("游戏退出，停止帧采集（硬件监控继续）");
                    _frameTimesMs.Clear();
                    StopPresentMon(keepPrefetch: true);
                    _frameTimesMs.Clear();
                    EndSession(collect: true);
                    _lastGameName = "";
                    _lastGamePid = 0;
                }

                bool excl = false;
                try { if (pid != 0) excl = IsExclusiveFullscreen(pid); } catch { }
                var capturedPid = pid;
                UpdateSnap(s =>
                {
                    s.GameName = name;
                    s.GamePid = pid;
                    s.GameSource = src;
                    s.PresentMonRunning = _pm is { HasExited: false };
                    s.ExclusiveFullscreen = excl;
                    s.PmLines = _pmLineCount;
                    s.PmFrames = _pmFrameCount;
                    s.PmExitInfo = _pm is { HasExited: true } ? "PM已退出" : "";
                });
                wasBound = bound;
            }
            catch { }
            Thread.Sleep(1000);
        }
        StopPresentMon();
    }

    /// <summary>手动固定某进程（UI 调用；传空解除并回到自动）。</summary>
    public void PinProcess(string name)
    {
        _settings.PinnedProcess = name;
        _settings.Save();
        StopPresentMon();
        Log(string.IsNullOrEmpty(name) ? "解除手动固定，回到自动检测" : $"手动固定监控 {name}");
    }

    // ================= PresentMon =================

    /// <summary>预捕会话：监控启动即建立 ETW 全进程捕获（EA AntiCheat 等反作弊驱动加载后会全局拒绝新建 ETW 会话，
    /// 但对已存在的会话不干预——必须抢在游戏/EAC 之前建立，游戏出现后按 PID 过滤即可）。</summary>
    private void StartPrefetchSession()
    {
        try
        {
            if (_pm != null) return;
            StartPresentMon(0);   // pid=0 = 无绑定目标，captureall 模式下输出暂无过滤对象
        }
        catch (Exception ex) { Log("预捕会话启动失败: " + ex.Message); }
    }

    private void StartPresentMon(int pid)
    {
        try
        {
            // 已有存活的 captureall 实例（预捕/降级）→ 直接复用，仅切换过滤目标
            // （EA AntiCheat 等反作弊驱动加载后会全局拒绝新建 ETW 会话，已建会话不受影响，绝不能重启）
            if (pid != 0 && _pm is { HasExited: false } && _captureAll)
            {
                _pmTargetPid = pid;
                Interlocked.Exchange(ref _pmStartFailures, 0);
                Log($"复用全进程捕获会话，过滤目标 PID {pid}");
                UpdateSnap(s => s.PresentMonRunning = true);
                return;
            }

            StopPresentMon();

            string exe = Path.Combine(AppContext.BaseDirectory, PresentMonExe);
            if (!File.Exists(exe)) { Log("PresentMon.exe 缺失，帧采集不可用"); return; }

            bool prefetch = pid == 0;
            bool useCapAll = prefetch || _settings.PmCaptureAll || _pmForceCaptureAll;
            if (useCapAll) Log(prefetch ? "预捕会话启动（反作弊驱动加载前抢占 ETW）" : "PresentMon 使用全进程捕获模式（输出按游戏 PID 过滤）");
            // 清理残留 ETW 会话：PM 异常退出会泄漏同名会话，残留会导致后续 StartTrace 撞车（表现为 access denied）
            try
            {
                using var cleanup = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "logman.exe",
                    Arguments = $"stop {PmSessionName} -ets",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                cleanup?.WaitForExit(3000);
            }
            catch { }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                // 不传 -timed（其含义是 N 秒后停止采集，传 0 会秒退）；
                // -qpc 在 1.10.0 已改名 -qpc_time 且非必需，stdout 自带毫秒列
                Arguments = useCapAll
                    ? $"-captureall -session_name {PmSessionName} -output_stdout"
                    : $"-process_id {pid} -session_name {PmSessionName} -output_stdout",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            var pm = new Process { StartInfo = psi, EnableRaisingEvents = true };
            pm.OutputDataReceived += OnPmOutput;
            pm.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) Log("PM stderr: " + args.Data);
            };
            if (!pm.Start()) { pm.Dispose(); Log($"PresentMon 启动失败（PID {pid}）"); return; }
            Log($"PresentMon 已启动（PID {pid}，行数将实时写入 pm_raw.csv）");
            pm.BeginOutputReadLine();
            pm.BeginErrorReadLine();
            var capturedPid = pid;
            var capturedGen = Interlocked.Increment(ref _pmGeneration);
            pm.Exited += (_, _) =>
            {
                if (capturedGen != System.Threading.Volatile.Read(ref _pmGeneration)) return;   // 过期实例的延迟回调，忽略
                int fails = Interlocked.Increment(ref _pmStartFailures);
                if (fails >= 2 && pid != 0 && !_pmForceCaptureAll)
                {
                    _pmForceCaptureAll = true;
                    Log("定向追踪连续被拒，自动切换全进程捕获模式");
                }
                int delay = fails switch { <= 2 => 2000, <= 5 => 10000, <= 10 => 30000, _ => 60000 };
                Log($"PresentMon 进程退出（行数 {_pmLineCount}，帧数 {_pmFrameCount}，连续失败 {fails}），{delay / 1000} 秒后自动重启");
                Task.Delay(delay).ContinueWith(_ =>
                {
                    try
                    {
                        if (_cts.IsCancellationRequested) return;
                        if (capturedGen != System.Threading.Volatile.Read(ref _pmGeneration)) return;
                        if (capturedPid == 0)
                        {
                            // 预捕会话死亡：仅在无游戏绑定时重建（EAC 未加载时段才有窗口成功）
                            lock (_bindLock) { if (_boundPid == 0) StartPresentMon(0); }
                        }
                        else
                        {
                            lock (_bindLock) { if (_boundPid == capturedPid) StartPresentMon(capturedPid); }
                        }
                    }
                    catch { }
                });
            };
            _pm = pm;
            _pmTargetPid = pid;
            _pmHeader = "";
            _colMsBetweenPresents = -1;
            _colProcessId = -1;
            _captureAll = useCapAll;
            UpdateSnap(s => s.PresentMonRunning = true);
        }
        catch (Exception ex) { Log("PresentMon 异常: " + ex.Message); }
    }

    private void StopPresentMon(bool keepPrefetch = false)
    {
        try
        {
            var pm = _pm;
            if (keepPrefetch && pm is { HasExited: false })
            {
                // 预捕会话保持运行（反作弊加载后无法重建）：保留 _pm 引用供下次复用，仅清目标 PID
                _pmTargetPid = 0;
                Log("帧采集解绑（预捕会话保持待命）");
                UpdateSnap(s => { s.PresentMonRunning = true; });
                return;
            }
            _pm = null;
            _pmTargetPid = 0;
            Interlocked.Increment(ref _pmGeneration);   // 使旧实例的延迟重启链失效
            if (pm == null) { UpdateSnap(s => s.PresentMonRunning = false); return; }
            try { pm.OutputDataReceived -= OnPmOutput; } catch { }
            try { if (!pm.HasExited) pm.Kill(entireProcessTree: true); } catch { }
            pm.Dispose();
            Log($"PresentMon 已停止（总行数 {_pmLineCount}，有效帧 {_pmFrameCount}）");
            try { lock (_fileLogLock) { _pmRawLog?.Dispose(); _pmRawLog = null; } } catch { }
            UpdateSnap(s =>
            {
                s.PresentMonRunning = false;
                s.FrameCount = 0;
                s.Fps = s.AvgFps = s.OnePercentLow = s.PointOnePercentLow = 0;
                s.ExclusiveFullscreen = false;
            });
        }
        catch { }
    }

    private void OnPmOutput(object sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line)) return;
        Interlocked.Increment(ref _pmLineCount);

        // PM 原始输出落盘（诊断用，每次 PM 启动重建）
        try
        {
            lock (_fileLogLock)
            {
                if (_pmRawLog == null)
                    _pmRawLog = new StreamWriter(PmRawPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
                _pmRawLog.WriteLine(line);
            }
        }
        catch { }

        // 动态表头：首行为 CSV 列名，取 msBetweenPresents 列索引
        if (_pmHeader.Length == 0)
        {
            if (line.StartsWith("Application,") || line.Contains("SwapChainAddress"))
            {
                _pmHeader = line;
                var cols = line.Split(',');
                for (int i = 0; i < cols.Length; i++)
                {
                    if (cols[i].Trim().Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase))
                        _colMsBetweenPresents = i;
                }
                Interlocked.Exchange(ref _pmStartFailures, 0);
                Log(_colMsBetweenPresents >= 0
                    ? $"PresentMon 表头解析成功（{cols.Length} 列）"
                    : "PresentMon 表头无 msBetweenPresents 列");
            }
            return;
        }

        if (_colMsBetweenPresents < 0) return;
        var parts = line.Split(',');
        if (parts.Length <= _colMsBetweenPresents) return;

        // captureall 模式：PM 输出全部进程的帧，按 ProcessID 列过滤出绑定游戏
        if (_captureAll)
        {
            if (_colProcessId < 0)
            {
                var hcols = _pmHeader.Split(',');
                for (int i = 0; i < hcols.Length; i++)
                    if (hcols[i].Trim().Equals("ProcessID", StringComparison.OrdinalIgnoreCase))
                        _colProcessId = i;
            }
            if (_colProcessId < 0 || parts.Length <= _colProcessId) return;
            if (!int.TryParse(parts[_colProcessId].Trim(), out int rowPid) || rowPid != _pmTargetPid) return;
        }
        if (!double.TryParse(parts[_colMsBetweenPresents].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ft)) return;
        if (ft <= 0.2 || ft > 10000) return;

        Interlocked.Increment(ref _pmFrameCount);
        _frameTimesMs.Enqueue(ft);
        while (_frameTimesMs.Count > FrameWindowMax) _frameTimesMs.TryDequeue(out _);
        RecomputeStats(ft);
        WriteCsvFrame(ft);
    }

    private void RecomputeStats(double latestFrameMs)
    {
        double[] arr = _frameTimesMs.ToArray();
        int n = arr.Length;
        if (n == 0) return;

        double avgFt = 0, maxFt = 0;
        foreach (var ft in arr) { avgFt += ft; if (ft > maxFt) maxFt = ft; }
        avgFt /= n;

        var sorted = (double[])arr.Clone();
        Array.Sort(sorted);

        UpdateSnap(s =>
        {
            s.FrameTimeMs = latestFrameMs;
            s.Fps = 1000.0 / latestFrameMs;
            s.AvgFps = 1000.0 / avgFt;
            s.OnePercentLow = PercentileFps(sorted, 0.01);
            s.PointOnePercentLow = PercentileFps(sorted, 0.001);
            s.MaxFrameMs = maxFt;
            s.FrameCount = n;
        });
    }

    private static double PercentileFps(double[] sortedAsc, double fraction)
    {
        int take = Math.Clamp((int)Math.Round(sortedAsc.Length * fraction), 1, sortedAsc.Length);
        double sum = 0;
        for (int i = 0; i < take; i++) sum += sortedAsc[i];
        double avgFt = sum / take;
        return avgFt > 0 ? 1000.0 / avgFt : 0;
    }

    // ================= 硬件 =================

    private void InitHardware()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsStorageEnabled = true,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = true,
            };
            _computer.Open();

            foreach (var hw in _computer.Hardware)
                if (hw.HardwareType == HardwareType.Storage)
                    _drives.Add(new PhysicalDrive { Name = hw.Name, Hw = hw });

            _cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuTotal.NextValue();
        }
        catch (Exception ex) { Log("硬件初始化异常: " + ex.Message); }
    }

    private async Task HardwareLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { SampleHardware(); SampleNetwork(); SampleBattery(); }
            catch { }
            try { await Task.Delay(Math.Clamp(_settings.HwPollMs, 500, 10000), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void SampleHardware()
    {
        double cpuLoad = double.NaN;
        try
        {
            if (_cpuTotal == null || _needCpuCounterRecreate)
            {
                try { _cpuTotal?.Dispose(); } catch { }
                _cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _cpuTotal.NextValue();
                _needCpuCounterRecreate = false;
            }
            cpuLoad = _cpuTotal.NextValue();
            if (cpuLoad < 0 || cpuLoad > 100) { _needCpuCounterRecreate = true; cpuLoad = double.NaN; }
        }
        catch { _needCpuCounterRecreate = true; }

        double cpuTemp = double.NaN, cpuClock = double.NaN, cpuPower = double.NaN;
        string cpuTempSrc = "";
        double gpuLoad = double.NaN, gpuTemp = double.NaN, gpuMemUsed = double.NaN, gpuMemTotal = double.NaN;
        double gpuCore = double.NaN, gpuMem = double.NaN, gpuPow = double.NaN, gpuFan = double.NaN;
        string gpuName = "";
        double ramUsed = double.NaN, ramTotal = double.NaN;

        var computer = _computer;
        if (computer != null)
        {
            try
            {
                computer.Accept(new UpdateVisitor());
                foreach (var hw in computer.Hardware)
                {
                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            foreach (var s in hw.Sensors)
                            {
                                switch (s.SensorType)
                                {
                                    case SensorType.Temperature when s.Value.HasValue:
                                        double cv = s.Value.Value;
                                        if (cv <= 0 || cv >= 120) break;
                                        if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                            s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                            s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                                            s.Name.Contains("CCD", StringComparison.OrdinalIgnoreCase))
                                        { cpuTemp = cv; cpuTempSrc = "CPU 传感器"; }
                                        else if (double.IsNaN(cpuTemp)) { cpuTemp = cv; cpuTempSrc = "CPU 传感器"; }
                                        break;
                                    case SensorType.Clock when s.Value.HasValue:
                                        if (s.Value.Value > 100 && (s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase)))
                                            cpuClock = double.IsNaN(cpuClock) ? s.Value.Value : Math.Max(cpuClock, s.Value.Value);
                                        break;
                                    case SensorType.Power when s.Value.HasValue:
                                        if (s.Value.Value > 0.5 && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) cpuPower = s.Value.Value;
                                        break;
                                }
                            }
                            break;

                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                            foreach (var s in hw.Sensors)
                            {
                                switch (s.SensorType)
                                {
                                    case SensorType.Load when s.Value.HasValue && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase):
                                        gpuLoad = s.Value.Value; break;
                                    case SensorType.Temperature when s.Value.HasValue:
                                        double gv = s.Value.Value;
                                        if (gv <= 0 || gv >= 120) break;
                                        if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                                            gpuTemp = gv;
                                        else if (double.IsNaN(gpuTemp)) gpuTemp = gv;
                                        break;
                                    case SensorType.SmallData when s.Value.HasValue:
                                        if (s.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase)) gpuMemUsed = s.Value.Value;
                                        else if (s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase)) gpuMemTotal = s.Value.Value;
                                        else if (double.IsNaN(gpuMemUsed) && s.Name.Equals("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase)) gpuMemUsed = s.Value.Value;
                                        else if (double.IsNaN(gpuMemTotal) && s.Name.Equals("D3D Dedicated Memory Total", StringComparison.OrdinalIgnoreCase)) gpuMemTotal = s.Value.Value;
                                        break;
                                    case SensorType.Clock when s.Value.HasValue:
                                        if (s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) gpuCore = s.Value.Value;
                                        else if (s.Name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase)) gpuMem = s.Value.Value;
                                        break;
                                    case SensorType.Power when s.Value.HasValue:
                                        if (s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) && double.IsNaN(gpuPow)) gpuPow = s.Value.Value;
                                        break;
                                    case SensorType.Fan when s.Value.HasValue:
                                        if (double.IsNaN(gpuFan)) gpuFan = s.Value.Value;
                                        break;
                                }
                            }
                            if (string.IsNullOrEmpty(gpuName)) gpuName = hw.Name;
                            break;

                        case HardwareType.Memory:
                            foreach (var s in hw.Sensors)
                                if (s.SensorType == SensorType.Data && s.Value.HasValue &&
                                    s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase))
                                    ramUsed = s.Value.Value;
                            break;
                    }
                }
            }
            catch { }
        }

        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                ramTotal = mem.ullTotalPhys / 1073741824.0;
                if (double.IsNaN(ramUsed) || ramUsed <= 0)
                    ramUsed = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
            }
        }
        catch { }

        if (double.IsNaN(cpuTemp))
        {
            double acpi = ReadAcpiTempC();
            if (!double.IsNaN(acpi)) { cpuTemp = acpi; cpuTempSrc = "ACPI 热区"; }
        }

        double diskR = double.NaN, diskW = double.NaN, diskT = double.NaN;
        try
        {
            long now = Environment.TickCount64;
            foreach (var d in _drives)
            {
                if (d.Hw == null) continue;
                try { d.Hw.Update(); } catch { continue; }
                ulong rd = 0, wr = 0;
                foreach (var s in d.Hw.Sensors)
                {
                    if (s.SensorType == SensorType.Data && s.Value.HasValue)
                    {
                        if (s.Name.Equals("Total Data Read", StringComparison.OrdinalIgnoreCase)) rd = (ulong)(s.Value.Value * 1073741824.0);
                        else if (s.Name.Equals("Total Data Written", StringComparison.OrdinalIgnoreCase)) wr = (ulong)(s.Value.Value * 1073741824.0);
                    }
                    else if (s.SensorType == SensorType.Temperature && s.Value.HasValue && double.IsNaN(diskT) && s.Value.Value > 0 && s.Value.Value < 120)
                        diskT = s.Value.Value;
                }
                if (d.LastStamp != 0 && rd >= d.LastRead && wr >= d.LastWrite)
                {
                    double dt = (now - d.LastStamp) / 1000.0;
                    if (dt > 0.5)
                    {
                        d.ReadKbs = (rd - d.LastRead) / dt / 1024.0;
                        d.WriteKbs = (wr - d.LastWrite) / dt / 1024.0;
                    }
                }
                d.LastRead = rd; d.LastWrite = wr; d.LastStamp = now;
                if (!double.IsNaN(d.ReadKbs))
                {
                    diskR = double.IsNaN(diskR) ? d.ReadKbs : diskR + d.ReadKbs;
                    diskW = double.IsNaN(diskW) ? d.WriteKbs : diskW + d.WriteKbs;
                }
            }
        }
        catch { }

        UpdateSnap(s =>
        {
            s.CpuLoad = cpuLoad; s.CpuTemp = cpuTemp; s.CpuTempSource = cpuTempSrc;
            s.CpuMaxClock = cpuClock; s.CpuPower = cpuPower;
            s.GpuLoad = gpuLoad; s.GpuTemp = gpuTemp; s.GpuMemUsed = gpuMemUsed; s.GpuMemTotal = gpuMemTotal;
            s.GpuCoreClock = gpuCore; s.GpuMemClock = gpuMem; s.GpuPower = gpuPow; s.GpuFanRpm = gpuFan;
            s.GpuName = gpuName;
            s.RamUsedGb = ramUsed; s.RamTotalGb = ramTotal;
            s.DiskReadKbs = diskR; s.DiskWriteKbs = diskW; s.DiskTemp = diskT;
            s.Time = DateTime.Now;
        });
    }

    // ===== 会话采样（游戏加加性能统计） =====

    private void StartSession(string gameName, int pid)
    {
        _session = new GameSession
        {
            GameName = gameName,
            GamePid = pid,
            Start = DateTime.Now,
            Resolution = ResolveResolution(out var hz),
            RefreshRate = hz > 0 ? $"{hz}Hz" : ""
        };
        _sessFps.Clear(); _sessCpuLoad.Clear(); _sessCpuTemp.Clear();
        _sessGpuLoad.Clear(); _sessGpuTemp.Clear(); _sessRam.Clear(); _sessGpuMem.Clear();
        _sessDown.Clear(); _sessUp.Clear(); _sessCpuPower.Clear(); _sessGpuPower.Clear();
        _sessFrameTotal = 0; _sessNetDownMb = 0; _sessNetUpMb = 0; _sessLastFrames = 0;
        _sessionElapsed = 0;
        _lastSessionSample = DateTime.Now;
        Log($"会话开始：{gameName}，分辨率 {_session.Resolution}");
    }

    /// <summary>当前主屏分辨率（DEVMODE 真实像素）+ 刷新率。</summary>
    private string ResolveResolution(out int hz)
    {
        hz = 0;
        try
        {
            var m = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(@"\\.\DISPLAY1", ENUM_CURRENT_SETTINGS, ref m) && m.dmPelsWidth > 0)
            {
                hz = (int)m.dmDisplayFrequency;
                return $"{m.dmPelsWidth} × {m.dmPelsHeight}";
            }
        }
        catch { }
        try { return $"{(int)System.Windows.SystemParameters.PrimaryScreenWidth} × {(int)System.Windows.SystemParameters.PrimaryScreenHeight}"; }
        catch { return ""; }
    }

    private void EndSession(bool collect)
    {
        var s = _session;
        _session = null;
        if (s == null) return;
        s.End = DateTime.Now;
        if (collect) AggregateSession(s);
        try { SessionStore.Save(s); } catch (Exception ex) { Log("会话保存失败：" + ex.Message); }
        Log($"会话结束：{s.GameName}，时长 {s.DurationText}，平均 FPS {s.FpsAvg:F1}，已保存到 sessions/");
        Log($"会话事件分发（订阅者 {SessionEnded?.GetInvocationList().Length ?? 0} 个）");
        try { SessionEnded?.Invoke(s); } catch (Exception ex) { Log("会话事件分发异常：" + ex.Message); }
    }

    /// <summary>在 UI 定时器或硬件轮询线程里每秒调用：累积当前快照到会话。</summary>
    public void SampleSessionTick(Snapshot snap)
    {
        var s = _session;
        if (s == null) return;
        var now = DateTime.Now;
        var dt = (now - _lastSessionSample).TotalSeconds;
        if (dt < 1.0) return;
        _lastSessionSample = now;
        _sessionElapsed += dt;

        double fps = snap.FrameCount > 0 ? snap.AvgFps > 0 ? snap.AvgFps : snap.Fps : 0;
        // 帧增量计数
        if (snap.FrameCount > 0)
        {
            // FrameCount 是滑窗内帧数；用 PmFrames 总量算增量更准
            if (snap.PmFrames >= _sessLastFrames) _sessFrameTotal = snap.PmFrames;
            else _sessLastFrames = snap.PmFrames;
            _sessLastFrames = snap.PmFrames;
        }

        AddSample(_sessFps, fps);
        if (!double.IsNaN(snap.CpuLoad)) AddSample(_sessCpuLoad, snap.CpuLoad);
        if (!double.IsNaN(snap.CpuTemp)) AddSample(_sessCpuTemp, snap.CpuTemp);
        if (!double.IsNaN(snap.GpuLoad)) AddSample(_sessGpuLoad, snap.GpuLoad);
        if (!double.IsNaN(snap.GpuTemp)) AddSample(_sessGpuTemp, snap.GpuTemp);
        if (!double.IsNaN(snap.RamUsedGb)) AddSample(_sessRam, snap.RamUsedGb);
        if (!double.IsNaN(snap.GpuMemUsed)) AddSample(_sessGpuMem, snap.GpuMemUsed > 64 ? snap.GpuMemUsed / 1024.0 : snap.GpuMemUsed); // 快照显存单位 MB，转 GB（>64 判定为 MB 值域）
        if (!double.IsNaN(snap.DownKbps)) { AddSample(_sessDown, snap.DownKbps); _sessNetDownMb += snap.DownKbps * dt / 1024.0; }
        if (!double.IsNaN(snap.UpKbps)) { AddSample(_sessUp, snap.UpKbps); _sessNetUpMb += snap.UpKbps * dt / 1024.0; }
        if (!double.IsNaN(snap.CpuPower)) AddSample(_sessCpuPower, snap.CpuPower);
        if (!double.IsNaN(snap.GpuPower)) AddSample(_sessGpuPower, snap.GpuPower);

        // 每 2 秒落一个曲线采样点
        if (s.Samples.Count == 0 || _sessionElapsed - s.Samples[^1].T >= 2.0)
        {
            s.Samples.Add(new SessionSample
            {
                T = _sessionElapsed,
                Fps = fps,
                CpuLoad = Val(_sessCpuLoad),
                CpuTemp = Val(_sessCpuTemp),
                GpuLoad = Val(_sessGpuLoad),
                GpuTemp = Val(_sessGpuTemp),
                RamUsedGb = Val(_sessRam),
                GpuMemGb = Val(_sessGpuMem),
                DownKbps = Val(_sessDown),
                UpKbps = Val(_sessUp),
                CpuPowerW = Val(_sessCpuPower),
                GpuPowerW = Val(_sessGpuPower),
            });
        }
    }

    private static void AddSample(List<double> list, double v)
    {
        list.Add(v);
        if (list.Count > 86400) list.RemoveAt(0); // 24h 防爆
    }

    private static double Val(List<double> list) => list.Count > 0 ? list[^1] : 0;

    private void AggregateSession(GameSession s)
    {
        var snap = Current;
        s.FpsAvg = _sessFps.Count > 0 ? _sessFps.Average() : 0;
        s.FpsMax = _sessFps.Count > 0 ? _sessFps.Max() : 0;
        s.FpsMin = _sessFps.Count > 0 ? _sessFps.Where(x => x > 0).DefaultIfEmpty(0).Min() : 0;
        // 1% Low：最差 1% 帧的平均 FPS；无帧数据时 0
        if (_sessFps.Count(x => x > 0) >= 10)
        {
            var valid = _sessFps.Where(x => x > 0).OrderBy(x => x).ToList();
            int k = Math.Max(1, (int)Math.Ceiling(valid.Count * 0.01));
            s.FpsOneLow = valid.Take(k).Average();
            k = Math.Max(1, (int)Math.Ceiling(valid.Count * 0.001));
            s.FpsPointOneLow = valid.Take(k).Average();
        }
        s.FrameTotal = _sessFrameTotal;

        s.CpuLoadAvg = _sessCpuLoad.Count > 0 ? _sessCpuLoad.Average() : double.NaN;
        s.CpuLoadMax = _sessCpuLoad.Count > 0 ? _sessCpuLoad.Max() : double.NaN;
        s.CpuLoadMin = _sessCpuLoad.Count > 0 ? _sessCpuLoad.Min() : double.NaN;
        s.CpuTempAvg = _sessCpuTemp.Count > 0 ? _sessCpuTemp.Average() : double.NaN;
        s.CpuTempMax = _sessCpuTemp.Count > 0 ? _sessCpuTemp.Max() : double.NaN;
        s.CpuTempMin = _sessCpuTemp.Count > 0 ? _sessCpuTemp.Min() : double.NaN;

        s.GpuLoadAvg = _sessGpuLoad.Count > 0 ? _sessGpuLoad.Average() : double.NaN;
        s.GpuLoadMax = _sessGpuLoad.Count > 0 ? _sessGpuLoad.Max() : double.NaN;
        s.GpuLoadMin = _sessGpuLoad.Count > 0 ? _sessGpuLoad.Min() : double.NaN;
        s.GpuTempAvg = _sessGpuTemp.Count > 0 ? _sessGpuTemp.Average() : double.NaN;
        s.GpuTempMax = _sessGpuTemp.Count > 0 ? _sessGpuTemp.Max() : double.NaN;
        s.GpuTempMin = _sessGpuTemp.Count > 0 ? _sessGpuTemp.Min() : double.NaN;

        s.RamAvgGb = _sessRam.Count > 0 ? _sessRam.Average() : double.NaN;
        s.RamMaxGb = _sessRam.Count > 0 ? _sessRam.Max() : double.NaN;
        s.GpuMemAvgGb = _sessGpuMem.Count > 0 ? _sessGpuMem.Average() : double.NaN;
        s.GpuMemMaxGb = _sessGpuMem.Count > 0 ? _sessGpuMem.Max() : double.NaN;
        s.NetDownTotalMb = _sessNetDownMb;
        s.NetUpTotalMb = _sessNetUpMb;
        s.CpuPowerAvgW = _sessCpuPower.Count > 0 ? _sessCpuPower.Average() : double.NaN;
        s.GpuPowerAvgW = _sessGpuPower.Count > 0 ? _sessGpuPower.Average() : double.NaN;

        // 能耗估算：CPU 功率 + GPU 功率（无效值用整机估算：桌面基线 15W）
        double cpuW = double.IsNaN(s.CpuPowerAvgW) || s.CpuPowerAvgW <= 0 ? 15 : s.CpuPowerAvgW;
        double gpuW = double.IsNaN(s.GpuPowerAvgW) || s.GpuPowerAvgW <= 0 ? 20 : s.GpuPowerAvgW;
        s.EnergyKwh = (cpuW + gpuW) * s.Duration.TotalHours / 1000.0;
    }

    /// <summary>检测绑定游戏是否处于独占全屏（D3D exclusive fullscreen 会绕过桌面合成，OSD 无法覆盖）。</summary>
    private bool IsExclusiveFullscreen(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            IntPtr h = proc.MainWindowHandle;
            if (h == IntPtr.Zero) return false;

            // 独占全屏特征：无边框样式 + 严格覆盖整个显示器矩形 + 显示器模式被切换（分辨率等于游戏内分辨率）
            if ((GetWindowLongW(h, GWL_STYLE) & WS_CAPTION) != 0) return false;
            if (!GetWindowRectW(h, out RECT r)) return false;

            var mi = new MONITORINFO2 { cbSize = Marshal.SizeOf<MONITORINFO2>() };
            IntPtr mon = MonitorFromWindow(h, 1);
            if (!GetMonitorInfoW(mon, ref mi)) return false;

            bool coversMonitor = r.L == mi.rcMonitor.L && r.T == mi.rcMonitor.T &&
                                 r.R == mi.rcMonitor.R && r.B == mi.rcMonitor.B;
            if (!coversMonitor) return false;

            // 进一步：查询该显示器的当前模式是否被改（独占全屏通常切换分辨率）；无边框全屏不切模式
            var m = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettingsW(mi.szDevice, ENUM_CURRENT_SETTINGS, ref m);
        }
        catch { return false; }
    }

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll")] private static extern long GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool GetWindowRectW(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO2 info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE dm);

    private struct RECT { public int L, T, R, B; }
    private struct MONITORINFO2
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    private void SampleBattery()
    {
        double pct = double.NaN, pw = double.NaN;
        bool charging = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT DischargeRate, ChargeRate, RemainingCapacity, FullChargedCapacity FROM BatteryStatus");
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    try
                    {
                        if (o["DischargeRate"] != null)
                        {
                            int rate = Convert.ToInt32(o["DischargeRate"]);
                            charging = rate < 0;
                            pw = Math.Abs(rate);
                        }
                        if (o["ChargeRate"] != null)
                        {
                            int cr = Convert.ToInt32(o["ChargeRate"]);
                            if (cr > 0) { charging = true; pw = cr; }
                        }
                        if (o["RemainingCapacity"] != null && o["FullChargedCapacity"] != null)
                        {
                            double rem = Convert.ToDouble(o["RemainingCapacity"]);
                            double full = Convert.ToDouble(o["FullChargedCapacity"]);
                            if (full > 0) pct = rem / full * 100.0;
                        }
                    }
                    catch { }
                }
                break;
            }
        }
        catch { }

        string ac = "";
        try
        {
            using var bs = new ManagementObjectSearcher("root\\cimv2", "SELECT BatteryStatus FROM Win32_Battery");
            foreach (var o in bs.Get())
            {
                using (o)
                {
                    try
                    {
                        if (o["BatteryStatus"] != null)
                        {
                            int st = Convert.ToInt32(o["BatteryStatus"]);
                            ac = st == 2 ? "已接电源" : "使用电池";
                            if (st == 2) charging = true;
                        }
                    }
                    catch { }
                }
                break;
            }
        }
        catch { }
        if (string.IsNullOrEmpty(ac)) ac = charging ? "已接电源" : "使用电池";

        UpdateSnap(s =>
        {
            s.BatteryPct = pct;
            s.BatteryPowerW = pw;
            s.BatteryCharging = charging;
            s.AcOnline = ac;
        });
    }

    private static double ReadAcpiTempC()
    {
        try
        {
            double best = double.NaN;
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    if (o["CurrentTemperature"] == null) continue;
                    double c = Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15;
                    if (c <= 0 || c >= 120) continue;
                    if (double.IsNaN(best) || c > best) best = c;
                }
            }
            return best;
        }
        catch { return double.NaN; }
    }

    private void InitNetwork()
    {
        try { (_lastRecv, _lastSent) = GetNicBytes(); _lastNetStamp = Environment.TickCount64; } catch { }
    }

    private static (ulong recv, ulong sent) GetNicBytes()
    {
        ulong r = 0, t = 0;
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            var st = nic.GetIPv4Statistics();
            r += (ulong)Math.Max(0, st.BytesReceived);
            t += (ulong)Math.Max(0, st.BytesSent);
        }
        return (r, t);
    }

    private void SampleNetwork()
    {
        try
        {
            var (r, t) = GetNicBytes();
            long now = Environment.TickCount64;
            double dt = (now - _lastNetStamp) / 1000.0;
            if (dt > 0.2 && r >= _lastRecv && t >= _lastSent)
            {
                double down = (r - _lastRecv) / dt / 1024.0;
                double up = (t - _lastSent) / dt / 1024.0;
                UpdateSnap(s => { s.DownKbps = down; s.UpKbps = up; });
            }
            _lastRecv = r; _lastSent = t; _lastNetStamp = now;
        }
        catch { }
    }

    // ================= CSV =================

    private void InitCsv()
    {
        try
        {
            bool exists = File.Exists(CsvPath);
            _csv = new StreamWriter(CsvPath, append: true) { AutoFlush = true };
            if (!exists)
                _csv.WriteLine("Timestamp,Game,FrameTimeMs,FPS,AvgFps,OnePercentLow,ZeroPointOnePercentLow,GpuTemp,GpuLoad,GpuMemUsed,CpuLoad,CpuTemp,RamUsedGb,DownKbps,UpKbps");
        }
        catch { _csv = null; }
    }

    private void WriteCsvFrame(double frameMs)
    {
        if (_settings.CsvModeInt == 0) return;
        var snap = _snapshot;
        var csv = _csv;
        if (csv == null) return;
        if (_settings.CsvModeInt == 2)
        {
            var now = DateTime.Now;
            if ((now - _lastCsvSec).TotalMilliseconds < 950) return;
            _lastCsvSec = now;
        }
        try
        {
            lock (_csvLock)
            {
                csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff},{1},{2:F3},{3:F2},{4:F2},{5:F2},{6:F2},{7},{8},{9},{10},{11},{12},{13},{14}",
                    DateTime.Now, snap.GameName, frameMs, snap.Fps, snap.AvgFps, snap.OnePercentLow, snap.PointOnePercentLow,
                    F(snap.GpuTemp), F(snap.GpuLoad), F(snap.GpuMemUsed), F(snap.CpuLoad), F(snap.CpuTemp), F(snap.RamUsedGb), F(snap.DownKbps), F(snap.UpKbps)));
            }
        }
        catch { }
    }

    private static string F(double v) => double.IsNaN(v) ? "N/A" : v.ToString("F1", CultureInfo.InvariantCulture);

    // ================= 快照 =================

    private void UpdateSnap(Action<Snapshot> mutate)
    {
        var copy = _snapshot.Clone();
        mutate(copy);
        _snapshot = copy;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _procThread?.Join(2000); } catch { }
        try { _hwTask?.Wait(1000); } catch { }
        StopPresentMon();
        try { _cpuTotal?.Dispose(); } catch { }
        try { lock (_csvLock) { _csv?.Flush(); _csv?.Dispose(); } } catch { }
        _csv = null;
        try { _computer?.Close(); } catch { }
        _computer = null;
        _cts.Dispose();
    }
}

public sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Update();
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
