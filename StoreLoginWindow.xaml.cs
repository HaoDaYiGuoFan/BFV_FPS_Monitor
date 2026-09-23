using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace BFV_FPS_Monitor;

/// <summary>
/// 内嵌商店登录窗：WebView2 + 用户数据目录持久化（exe 目录 store_profile）。
/// 登录一次 Cookie 永久保存，之后打开即登录态，手动点"获取"完成领取。
/// 凭证不出本机；清除按钮可一键抹掉。
/// </summary>
public partial class StoreLoginWindow : Window
{
    public const string EpicUrl = "https://store.epicgames.com/zh-CN/free-games";
    public const string SteamUrl = "https://store.steampowered.com/";

    private readonly string _profileDir;

    public StoreLoginWindow()
    {
        InitializeComponent();
        _profileDir = Path.Combine(AppContext.BaseDirectory, "store_profile");
        Loaded += async (_, _) => await InitAsync().ConfigureAwait(true);
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _profileDir).ConfigureAwait(true);

            await Web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            var core = Web.CoreWebView2;

            // 伪装常规桌面 UA，避免 Epic 对内嵌 WebView 弹特殊提示
            core.Settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = true;

            core.DocumentTitleChanged += (_, _) =>
            {
                Title = "商店登录 · " + core.DocumentTitle;
                DetectLoginState();
            };
            core.NavigationCompleted += (_, _) => DetectLoginState();
            Web.SourceChanged += (_, _) => DetectLoginState();

            Web.Source = new Uri(EpicUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            StateText.Text = "缺少 WebView2 Runtime";
            StateText.Foreground = (System.Windows.Media.Brush)FindResource("AccentRed");
            MessageBox.Show(this,
                "缺少 Microsoft WebView2 运行时。\n请安装后重试：https://developer.microsoft.com/microsoft-edge/webview2/",
                "Game Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StateText.Text = "初始化失败: " + ex.Message;
        }
    }

    private void DetectLoginState()
    {
        try
        {
            var url = Web.Source?.ToString() ?? "";
            // 粗略状态提示（登录页 → 未登录；商店主页 → 可能已登录，以页面右上角头像为准）
            bool onLogin = url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                           url.Contains("authentication", StringComparison.OrdinalIgnoreCase);
            if (onLogin)
            {
                StateText.Text = "登录页 — 登录完成后 Cookie 将自动保存在本机";
                StateText.Foreground = (System.Windows.Media.Brush)FindResource("AccentOrange");
            }
            else
            {
                StateText.Text = "已会话保持 — 登录过一次后，直接在页面点「获取」即可入库";
                StateText.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");
            }
        }
        catch { }
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this,
            "将删除本工具保存的全部商店 Cookie 与会话（store_profile 目录）。\n继续？",
            "清除登录态", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        try
        {
            Web?.CoreWebView2?.Profile?.ClearBrowsingDataAsync();
            // 兜底：直接删目录（下次启动重建）
            Task.Delay(500).ContinueWith(_ =>
            {
                try { if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, true); } catch { }
            });
            StateText.Text = "登录态已清除";
            StateText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
            Web.Source = new Uri(EpicUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "清除失败：" + ex.Message, "Game Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenExternalBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = Web.Source?.ToString() ?? EpicUrl;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
