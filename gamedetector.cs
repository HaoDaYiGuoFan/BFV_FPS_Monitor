using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BFV_FPS_Monitor;

/// <summary>
/// 游戏进程识别：内置白名单 + 全屏窗口启发式 + 系统进程排除。
/// </summary>
public static class GameDetector
{
    public sealed record GameHit(string Name, int Pid, string Source);

    // ===== 主流游戏白名单（进程名，不含 .exe） =====
    private static readonly HashSet<string> White = new(StringComparer.OrdinalIgnoreCase)
    {
        // 战地系
        "bfv", "bf1", "bf2042", "bf4", "bf3", "bf2",
        // FPS / TPS
        "cs2", "csgo", "GTA5", "RDR2", "r5apex", "r5apex_dx12",
        "FortniteClient-Win64-Shipping", "VALORANT-Win64-Shipping", "TslGame",
        "Overwatch", "cod", "ModernWarfare", "HuntGame", "HLL", "SquadGame", "PostScriptum",
        "DeadByDaylight", "DyingLightGame", "DyingLightGame_x64_rwdi",
        // 开放世界 / RPG
        "Cyberpunk2077", "witcher3", "eldenring", "sekiro", "Starfield", "Fallout4",
        "SkyrimSE", "Palworld", "MonsterHunterWilds", "Nioh2", "ACValhalla", "ACMirage",
        "Odyssey", "Origins", "FC25", "FC24", "NFSUnboundGame", "NFS16",
        // 国区常见
        "dnf", "crossfire", "loall", "wutheringwaves", "YuanShen", "ZenlessZoneZero", "StarRail",
        "jx3", "tp3svr", "wuwa",
    };

    // ===== 排除（桌面/系统/启动器/办公） =====
    private static readonly HashSet<string> Exclude = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "System", "Idle", "Registry", "dwm", "winlogon",
        "textinputhost", "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost",
        "RuntimeBroker", "WidgetService", "Widgets", "msedgewebview2", "msedge", "chrome",
        "firefox", "QQ", "WeChat", "Weixin", "WeChatAppEx", "DingTalk", "Feishu", "Lark",
        "Telegram", "Discord", "steam", "steamwebhelper", "EADesktop", "EALauncher",
        "EpicGamesLauncher", "Battle.net", "Battle.net-Agent", "devenv", "idea64", "Code",
        "powershell", "pwsh", "cmd", "conhost", "WindowsTerminal", "OpenConsole",
        "BFV_FPS_Monitor", "PresentMon", "tabtip", "ShellHost", "smartscreen", "wudfhost",
        "svchost", "csrss", "services", "lsass", "wininit", "spoolsv", "taskhostw", "ctfmon",
        "SearchIndexer", "SecurityHealthService", "MsMpEng", "Everything", "Listary",
        "AutoClaw", "node", "dotnet", "lhm_probe", "mcporter",
        // 截图 / 贴图工具：贴图窗口无边框且常覆盖全屏，全屏启发式必误判为游戏
        "Snipaste", "PixPin", "ShareX", "Greenshot", "PicPick", "FSCapture",
    };

    /// <summary>
    /// 扫描一次系统进程，返回识别到的游戏（白名单优先，其次全屏启发式）；无游戏返回 null。
    /// 白名单支持前缀匹配：codwa 命中 cod 系条目（如 CoDWaW）。
    /// </summary>
    public static GameHit? DetectGame()
    {
        List<GameHit> hits = new();
        foreach (var p in Process.GetProcesses())
        {
            string? n = null;
            try
            {
                n = p.ProcessName;
                if (n.Length < 3 || Exclude.Contains(n)) continue;
                if (p.Id == Environment.ProcessId) continue;

                if (White.Contains(n) || MatchesWhite(n))
                {
                    hits.Add(new GameHit(n, p.Id, "白名单"));
                    continue;
                }
                if (IsFullscreenWindow(p))
                    hits.Add(new GameHit(n, p.Id, "全屏"));
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        return hits.FirstOrDefault(w => w.Source == "白名单") ?? hits.FirstOrDefault();
    }

    public static bool IsWhitelisted(string processName)
        => White.Contains(processName) || MatchesWhite(processName);

    /// <summary>前缀模糊匹配：进程名以任一白名单条目开头（条目 ≥3 字符，cod→CoDWaW）。</summary>
    private static bool MatchesWhite(string processName)
    {
        foreach (var w in White)
        {
            if (w.Length >= 3 && processName.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>枚举有可见主窗口的进程（供手动固定下拉框）。</summary>
    public static List<(string Name, int Pid)> ListWindowedProcesses()
    {
        var list = new List<(string Name, int Pid)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                if (n.Length < 2 || Exclude.Contains(n) || !seen.Add(n)) continue;
                try { if (p.MainWindowHandle == 0) continue; } catch { continue; }
                list.Add((n, p.Id));
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ===== Win32 全屏启发式 =====

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;

    [DllImport("user32.dll")] private static extern long GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    private struct RECT { public int L, T, R, B; }
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static bool IsFullscreenWindow(Process p)
    {
        try
        {
            IntPtr h = p.MainWindowHandle;
            if (h == IntPtr.Zero || !IsWindowVisible(h)) return false;
            if ((GetWindowLongW(h, GWL_STYLE) & WS_CAPTION) != 0) return false; // 带标题栏的普通窗口不算
            if (!GetWindowRect(h, out RECT r)) return false;

            IntPtr mon = MonitorFromWindow(h, 1 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(mon, ref mi)) return false;

            long iw = Math.Min((long)r.R, mi.rcMonitor.R) - Math.Max((long)r.L, mi.rcMonitor.L);
            long ih = Math.Min((long)r.B, mi.rcMonitor.B) - Math.Max((long)r.T, mi.rcMonitor.T);
            if (iw <= 0 || ih <= 0) return false;

            long mw = (long)mi.rcMonitor.R - mi.rcMonitor.L;
            long mh = (long)mi.rcMonitor.B - mi.rcMonitor.T;
            if (mw <= 0 || mh <= 0) return false;

            return (double)(iw * ih) / (mw * mh) >= 0.90;
        }
        catch { return false; }
    }
}
