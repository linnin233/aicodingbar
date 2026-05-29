using System.Windows;
using System.Windows.Controls;
using AiCodingBar.Config;
using AiCodingBar.Dashboard;
using AiCodingBar.Server;
using AiCodingBar.Taskbar;

namespace AiCodingBar;

public partial class MainWindow : Window
{
    private readonly NativeTaskbarText _taskbarText;
    private readonly ConfigManager _config;
    private readonly StateEngine _engine;
    private bool _pinned;

    private readonly SessionGrid _sessionGrid;
    private readonly DisplayTab _displayTab;
    private readonly AgentsTab _agentsTab;
    private readonly ConfigPanel _statesTab;
    private readonly ServerTab _serverTab;
    private readonly DebugLog _debugLog;

    public MainWindow(StateEngine engine, ConfigManager config, NativeTaskbarText taskbarText)
    {
        InitializeComponent();
        _engine = engine;
        _config = config;
        _taskbarText = taskbarText;

        // 创建所有 Tab 内容
        _sessionGrid = new SessionGrid(engine);
        _sessionGrid.PinChanged += OnPinChanged;

        _displayTab = new DisplayTab(config, taskbarText);

        _agentsTab = new AgentsTab(config, taskbarText);

        _statesTab = new ConfigPanel(config, () => taskbarText.Refresh());

        _serverTab = new ServerTab(config);

        _debugLog = new DebugLog(engine, config, taskbarText);

        // 默认选中 Sessions
        SidebarList.SelectedIndex = 0;

        // 窗口失焦自动隐藏（除非 pin 住）
        Deactivated += Window_Deactivated;
    }

    public void LogDebug(string msg)
    {
        _debugLog.Log(msg);
    }

    private void Sidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContentStack.Children.Clear();

        var item = SidebarList.SelectedItem as ListBoxItem;
        var tag = item?.Tag?.ToString() ?? "sessions";

        switch (tag)
        {
            case "sessions":
                ContentStack.Children.Add(_sessionGrid);
                break;
            case "display":
                ContentStack.Children.Add(_displayTab);
                break;
            case "agents":
                ContentStack.Children.Add(_agentsTab);
                break;
            case "states":
                ContentStack.Children.Add(_statesTab);
                break;
            case "server":
                ContentStack.Children.Add(_serverTab);
                break;
            case "logs":
                ContentStack.Children.Add(_debugLog);
                break;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_pinned)
            Hide();
    }

    private void OnPinChanged(bool pinned)
    {
        _pinned = pinned;
    }
}
