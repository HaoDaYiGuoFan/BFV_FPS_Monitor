using System.IO;
using System.Text.Json;

namespace BFV_FPS_Monitor;

/// <summary>全局设置（settings.json 持久化，exe 目录）。</summary>
public sealed class AppSettings
{
    // 进程监控
    public bool AutoDetect { get; set; } = true;
    public string PinnedProcess { get; set; } = "";

    // 采集
    public int HwPollMs { get; set; } = 1000;
    /// <summary>0=关闭 1=帧级 2=秒级</summary>
    public int CsvModeInt { get; set; } = 1;

    // OSD
    public bool OsdOn { get; set; }
    public List<string> OsdItems { get; set; } = new() { "FPS", "CPUL", "CPUT", "GPUL", "GPUT" };
    /// <summary>横向模式：L=左上 C=上部居中 R=右上（自定义拖动后自动切 CUS）</summary>
    public string OsdCorner { get; set; } = "L";
    public int OsdFontSize { get; set; } = 14;
    public int OsdBgOpacity { get; set; } = 90;

    // 系统
    public bool Autostart { get; set; }

    // 桌面监控磁贴
    public bool TileOn { get; set; }
    public string TileStyle { get; set; } = "Bar";       // Bar=紧凑横条 Card=卡片网格 Taskbar=任务栏细条
    public bool TileClickThrough { get; set; }
    public bool TileShowInTaskbar { get; set; }
    public bool TileMiniOnMinimize { get; set; } = true;   // 主窗最小化时折叠为任务栏小条

    // 性能统计
    public bool SessionReportPopup { get; set; } = true;   // 游戏退出后自动弹性能报告

    // PresentMon 采集模式
    /// <summary>true=-captureall 全进程捕获（目标进程打开句柄被安全软件拦截时的备选，CPU 略高）</summary>
    public bool PmCaptureAll { get; set; }

    // ===== 持久化 =====
    private static string Path_ => System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(Path_, JsonSerializer.Serialize(this, JsonOpt)); } catch { }
    }
}
