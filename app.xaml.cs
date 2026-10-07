using System.Windows;

namespace BFV_FPS_Monitor;

public partial class App : Application
{
    private MonitorEngine? _engine;
    private AppSettings? _settings;

    public static MonitorEngine Engine { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 全局兑底：任何未处理异常只提示不闪退（磁贴/OSD 出错时用户可回主窗关掉开关）
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                MessageBox.Show(Localization.F("App.Crash", args.Exception.Message),
                    Localization.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            }
            catch { args.Handled = false; }
        };

        _settings = AppSettings.Load();
        Settings = _settings;
        Localization.Init(_settings.Language);   // 先安置语言字典，再创建主窗（XAML DynamicResource 才能解析）
        _engine = new MonitorEngine(_settings);
        Engine = _engine;
        _engine.Start();

        var win = new MainWindow(_settings);
        MainWindow = win;
        win.Show();
    }

    private void App_OnExit(object sender, ExitEventArgs e)
    {
        _engine?.Dispose();
    }
}
