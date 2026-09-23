using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BFV_FPS_Monitor;

/// <summary>一次游戏会话的性能快照点（秒级聚合，2s 间隔落盘）。</summary>
public sealed class SessionSample
{
    public double T { get; set; }        // 会话内秒数
    public double Fps { get; set; }
    public double CpuLoad { get; set; }
    public double CpuTemp { get; set; }
    public double GpuLoad { get; set; }
    public double GpuTemp { get; set; }
    public double RamUsedGb { get; set; }
    public double GpuMemGb { get; set; }
    public double DownKbps { get; set; }
    public double UpKbps { get; set; }
    public double CpuPowerW { get; set; }
    public double GpuPowerW { get; set; }

    public SessionSample Clone() => (SessionSample)MemberwiseClone();
}

/// <summary>一次完整游戏会话的统计记录（对齐游戏加加性能统计）。</summary>
public sealed class GameSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string GameName { get; set; } = "";
    public int GamePid { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Resolution { get; set; } = "";
    public string RefreshRate { get; set; } = "";

    // FPS 统计
    public double FpsAvg { get; set; }
    public double FpsMax { get; set; }
    public double FpsMin { get; set; }
    public double FpsOneLow { get; set; }
    public double FpsPointOneLow { get; set; }
    public long FrameTotal { get; set; }

    // CPU 统计
    public double CpuLoadAvg { get; set; }
    public double CpuLoadMax { get; set; }
    public double CpuLoadMin { get; set; }
    public double CpuTempAvg { get; set; }
    public double CpuTempMax { get; set; }
    public double CpuTempMin { get; set; }

    // GPU 统计
    public double GpuLoadAvg { get; set; }
    public double GpuLoadMax { get; set; }
    public double GpuLoadMin { get; set; }
    public double GpuTempAvg { get; set; }
    public double GpuTempMax { get; set; }
    public double GpuTempMin { get; set; }

    // 内存 / 网络 / 功耗
    public double RamAvgGb { get; set; }
    public double RamMaxGb { get; set; }
    public double GpuMemAvgGb { get; set; }
    public double GpuMemMaxGb { get; set; }
    public double NetDownTotalMb { get; set; }
    public double NetUpTotalMb { get; set; }
    public double CpuPowerAvgW { get; set; }
    public double GpuPowerAvgW { get; set; }

    /// <summary>会话能量估算 kWh（功率 W × 时长 / 3.6e6）</summary>
    public double EnergyKwh { get; set; }
    /// <summary>CO₂ 排放估算 g（按 0.556 kg/kWh 全国电网平均排放因子）</summary>
    public double Co2Grams => EnergyKwh * 556.0;

    public List<SessionSample> Samples { get; set; } = new();

    [JsonIgnore]
    public TimeSpan Duration => End - Start;
    [JsonIgnore]
    public string DurationText =>
        Duration.TotalHours >= 1
            ? $"{(int)Duration.TotalHours}小时{Duration.Minutes}分{Duration.Seconds}秒"
            : $"{Duration.Minutes}分{Duration.Seconds}秒";
}

/// <summary>会话存储：sessions/ 目录，每会话一个 JSON。</summary>
public static class SessionStore
{
    private static string Dir_ => System.IO.Path.Combine(AppContext.BaseDirectory, "sessions");

    public static void Save(GameSession s)
    {
        Directory.CreateDirectory(Dir_);
        var path = System.IO.Path.Combine(Dir_, $"{s.Start:yyyyMMdd_HHmmss}_{s.GameName}_{s.Id}.json");
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        File.WriteAllText(path, json);
    }

    public static List<GameSession> LoadAll()
    {
        var list = new List<GameSession>();
        try
        {
            if (!Directory.Exists(Dir_)) return list;
            foreach (var f in Directory.GetFiles(Dir_, "*.json").OrderByDescending(f => f))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<GameSession>(File.ReadAllText(f), new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
                    if (s != null) list.Add(s);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public static void Delete(GameSession s)
    {
        try
        {
            var path = System.IO.Path.Combine(Dir_, $"{s.Start:yyyyMMdd_HHmmss}_{s.GameName}_{s.Id}.json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
