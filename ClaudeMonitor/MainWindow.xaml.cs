using System.Windows;
using ClaudeMonitor.Config;
using ClaudeMonitor.Dashboard;
using ClaudeMonitor.Server;
using ClaudeMonitor.Taskbar;

namespace ClaudeMonitor;

public partial class MainWindow : Window
{
    private readonly SessionGrid _sessionGrid;
    private readonly ConfigPanel _configPanel;
    private readonly DebugLog _debugLog;
    private readonly NativeTaskbarText _taskbarText;
    private bool _pinned;

    public MainWindow(StateEngine engine, ConfigManager config, NativeTaskbarText taskbarText)
    {
        InitializeComponent();
        _taskbarText = taskbarText;

        _sessionGrid = new SessionGrid(engine);
        _sessionGrid.PinChanged += OnPinChanged;
        SessionTab.Content = _sessionGrid;

        _configPanel = new ConfigPanel(config, () =>
        {
            _taskbarText.Refresh();
        });
        ConfigTab.Content = _configPanel;

        _debugLog = new DebugLog(engine, config, taskbarText);
        DebugTab.Content = _debugLog;

        Deactivated += OnDeactivated;
    }

    public void LogDebug(string msg)
    {
        _debugLog.Log(msg);
    }

    private void OnPinChanged(bool pinned)
    {
        _pinned = pinned;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_pinned)
        {
            Hide();
        }
    }
}
