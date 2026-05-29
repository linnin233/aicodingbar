using System.Windows;

namespace TaskbarMonitor;

public partial class MainWindow : Window
{
    private readonly StateEngine _engine;
    private readonly Config _config;
    private readonly TaskbarWindow? _taskbar;
    private readonly Dashboard.SessionGrid _sessionGrid;
    private readonly Dashboard.ConfigPanel _configPanel;
    private readonly Dashboard.DebugLog _debugLog;

    public MainWindow(StateEngine engine, Config config, TaskbarWindow? taskbar)
    {
        InitializeComponent();
        _engine = engine;
        _config = config;
        _taskbar = taskbar;

        _sessionGrid = new Dashboard.SessionGrid(engine);

        _configPanel = new Dashboard.ConfigPanel(config, () =>
        {
            _taskbar?.Refresh();
            ApplyLanguage();
            _sessionGrid.ApplyMode(_config.Taskbar.Mode);
        });

        _debugLog = new Dashboard.DebugLog(engine, config, taskbar);

        ApplyLanguage();
        ShowPage(_sessionGrid);
    }

    public void ApplyLanguage()
    {
        var zh = _config.Language == "zh";
        NavSessions.Content = zh ? "  会话列表" : "  Sessions";
        NavConfig.Content = zh ? "  配置" : "  Config";
        NavDebug.Content = zh ? "  调试日志" : "  Debug Log";

        var port = ConfigManager.ReadRuntimePort();
        ServerStatusText.Text = zh
            ? $"服务: {(port > 0 ? $"端口 {port}" : "未运行")}"
            : $"Server: {(port > 0 ? $"port {port}" : "not running")}";
        HookStatusText.Text = zh
            ? $"Hook: {(HookInstaller.IsInstalled() ? "已安装" : "未安装")}"
            : $"Hook: {(HookInstaller.IsInstalled() ? "installed" : "not installed")}";

        _sessionGrid.ApplyLanguage(_config.Language);
        _configPanel.ApplyLanguage(_config.Language);
        _debugLog.ApplyLanguage(_config.Language);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ContentArea == null) return;
        if (sender == NavSessions) ShowPage(_sessionGrid);
        else if (sender == NavConfig) ShowPage(_configPanel);
        else if (sender == NavDebug) ShowPage(_debugLog);
    }

    private void ShowPage(System.Windows.UIElement page)
    {
        ContentArea.Children.Clear();
        ContentArea.Children.Add(page);
    }

    public void LogDebug(string msg) => _debugLog.Log(msg);
}
