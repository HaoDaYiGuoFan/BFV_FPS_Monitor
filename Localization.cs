using System.Globalization;
using System.Windows;

namespace BFV_FPS_Monitor;

/// <summary>
/// 界面本地化（运行时中/英切换）。
/// 字符串资源存放在 Lang.zh-CN.xaml / Lang.en-US.xaml（ResourceDictionary），
/// 由本类在 Application.Resources.MergedDictionaries 中维护"当前语言字典"。
/// XAML 静态文本用 {DynamicResource Key}（切语言时自动跟随）；
/// 代码动态文本用 Localization.T(key) / F(key, args)。
/// </summary>
public static class Localization
{
    public const string ZhCN = "zh-CN";
    public const string EnUS = "en-US";

    private static readonly string[] LangFiles = { "Lang.zh-CN.xaml", "Lang.en-US.xaml" };

    public static string Current { get; private set; } = ZhCN;

    /// <summary>语言切换后广播；订阅者对 C# 动态文本做重绘。</summary>
    public static event Action? LanguageChanged;

    /// <summary>启动时调用：按已保存设置安置语言字典（不广播）。</summary>
    public static void Init(string lang) => Set(lang, notify: false);

    /// <summary>切换语言：替换应用级字典 + 广播刷新事件。</summary>
    public static void Set(string lang, bool notify = true)
    {
        if (lang is not (ZhCN or EnUS)) lang = ZhCN;
        if (Current == lang && IsActive(lang)) { if (notify) LanguageChanged?.Invoke(); return; }
        Current = lang;
        SwapActive();
        if (notify) LanguageChanged?.Invoke();
    }

    public static void ApplyActive()
    {
        SwapActive();
    }

    private static bool IsActive(string lang)
    {
        var app = Application.Current;
        if (app == null) return false;
        string file = "Lang." + lang + ".xaml";
        foreach (var d in app.Resources.MergedDictionaries)
            if (IsLangDict(d, file)) return true;
        return false;
    }

    /// <summary>参数形式兼容相对 URI（"Lang.zh-CN.xaml"）与解析后的绝对 pack URI。</summary>
    private static bool IsLangDict(ResourceDictionary d, string file)
    {
        if (d.Source == null) return false;
        string s = d.Source.OriginalString;
        return s.Equals(file, StringComparison.OrdinalIgnoreCase)
            || s.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase);
    }

    private static void SwapActive()
    {
        var app = Application.Current;
        if (app == null) return;
        // 先移除全部语言字典（App.xaml 中静态挂载的那个也会被摘除）
        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var d = app.Resources.MergedDictionaries[i];
            bool isLang = false;
            foreach (var f in LangFiles)
                if (IsLangDict(d, f)) { isLang = true; break; }
            if (isLang) app.Resources.MergedDictionaries.RemoveAt(i);
        }
        // 再挂当前语言字典到首位
        var active = new ResourceDictionary
        {
            Source = new Uri("Lang." + Current + ".xaml", UriKind.Relative),
        };
        app.Resources.MergedDictionaries.Insert(0, active);
    }

    /// <summary>取当前语言字符串；key 不存在时原样返回 key。</summary>
    public static string T(string key)
    {
        var app = Application.Current;
        if (app != null)
        {
            try
            {
                if (app.TryFindResource(key) is string s) return s;
            }
            catch { }
        }
        return key;
    }

    /// <summary>取带格式参数的字符串（string.Format，CultureInvariant 保留数值格式）。</summary>
    public static string F(string key, params object?[]? args)
    {
        var s = T(key);
        if (args is not { Length: > 0 }) return s;
        try { return string.Format(CultureInfo.InvariantCulture, s, args); }
        catch { return s; }
    }

    /// <summary>游戏识别来源映射（"白名单"/"全屏"/"手动" → 本地化）。</summary>
    public static string GameSource(string? src) => src switch
    {
        "白名单" => T("Src.White"),
        "全屏" => T("Src.Fullscreen"),
        "手动" => T("Src.Manual"),
        _ => string.IsNullOrEmpty(src) ? "" : src,
    };

    /// <summary>电源状态映射（"已接电源"/"使用电池" → 本地化）。</summary>
    public static string BatteryState(string? ac)
    {
        if (string.IsNullOrEmpty(ac)) return "";
        return ac switch
        {
            "已接电源" => T("Batt.Ac"),
            "使用电池" => T("Batt.Dc"),
            _ => ac,
        };
    }

    /// <summary>CPU 温度传感器来源映射（"CPU 传感器"/"ACPI 热区" → 本地化）。</summary>
    public static string CpuTempSource(string? src) => src switch
    {
        "CPU 传感器" => T("Src.CpuSensor"),
        "ACPI 热区" => T("Src.AcpiZone"),
        _ => string.IsNullOrEmpty(src) ? "" : src,
    };
}