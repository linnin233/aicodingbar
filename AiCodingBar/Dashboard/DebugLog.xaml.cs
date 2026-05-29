using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AiCodingBar.Config;
using AiCodingBar.Server;
using AiCodingBar.Taskbar;

namespace AiCodingBar.Dashboard;

public partial class DebugLog : UserControl
{
    private readonly StateEngine _engine;
    private readonly ConfigManager _config;
    private readonly NativeTaskbarText? _taskbar;
    private readonly StringBuilder _buffer = new();
    private bool _autoScroll = true;

    private static readonly SolidColorBrush GreenBrush = new(Colors.LightGreen);
    private static readonly SolidColorBrush RedBrush = new(Colors.IndianRed);
    private static readonly SolidColorBrush YellowBrush = new(Colors.Khaki);
    private static readonly SolidColorBrush GrayBrush = new(Colors.Gray);
    private static readonly SolidColorBrush CyanBrush = new(Colors.Cyan);

    public DebugLog(StateEngine engine, ConfigManager config, NativeTaskbarText? taskbar)
    {
        InitializeComponent();
        _engine = engine;
        _config = config;
        _taskbar = taskbar;

        Log("=== AiCodingBar Debug Log ===", GrayBrush);
        Log($"HTTP Server: http://127.0.0.1:{ConfigManager.ReadRuntimePort()} (port from runtime.json)", CyanBrush);
        Log($"Config dir: ~/.aicoding-bar/", GrayBrush);
        Log($"Hook installed: {HookInstaller.IsInstalled()}", HookInstaller.IsInstalled() ? GreenBrush : YellowBrush);
        Log($"Sessions tracked: {_engine.Sessions.Count}", GrayBrush);
        Log("---", GrayBrush);

        _engine.OnSessionUpdated += (session) =>
        {
            Dispatcher.Invoke(() =>
                Log($"Session UPDATED: [{session.SortIndex}] {session.SessionId} -> {session.Status} (event: {session.LastEvent})", GreenBrush));
        };
        _engine.OnSessionRemoved += (session) =>
        {
            Dispatcher.Invoke(() =>
                Log($"Session REMOVED: [{session.SortIndex}] {session.SessionId}", RedBrush));
        };

        if (_taskbar != null)
        {
            _taskbar.OnClicked -= RefreshStatus;
            _taskbar.OnClicked += RefreshStatus;
        }

        ClearBtn.Click += (s, e) =>
        {
            LogBox.Document.Blocks.Clear();
            _buffer.Clear();
        };

        TestBtn.Click += (s, e) =>
        {
            try
            {
                var id = Guid.NewGuid().ToString("N")[..8];
                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var json = $$"""
                {
                    "event": "SessionStart",
                    "session_id": "test-{{id}}",
                    "cwd": "d:\\test",
                    "session_title": "Test Session",
                    "source_pid": {{pid}}
                }
                """;
                Log($"Test JSON: {json}", CyanBrush);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var result = _engine.ProcessEvent(doc.RootElement);
                Log($"ProcessEvent result: session={result?.SessionId}, status={result?.Status}", GreenBrush);
            }
            catch (Exception ex)
            {
                Log($"模拟事件失败: {ex.Message}", RedBrush);
                Log($"StackTrace: {ex.StackTrace}", RedBrush);
            }
        };

        AutoScrollCheck.Checked += (s, e) => _autoScroll = true;
        AutoScrollCheck.Unchecked += (s, e) => _autoScroll = false;

        SendBtn.Click += async (s, e) =>
        {
            var url = QuickInput.Text.Trim();
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = await client.GetStringAsync(url);
                Log($"GET {url} -> {resp}", GreenBrush);
            }
            catch (Exception ex)
            {
                Log($"GET {url} -> ERROR: {ex.Message}", RedBrush);
            }
        };

        RefreshStatus();
    }

    public void Log(string msg, SolidColorBrush? color = null)
    {
        _buffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        Dispatcher.Invoke(() =>
        {
            var para = new Paragraph { Margin = new Thickness(0) };
            var run = new Run($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            run.Foreground = color ?? GrayBrush;
            para.Inlines.Add(run);
            LogBox.Document.Blocks.Add(para);

            if (_autoScroll)
                LogBox.ScrollToEnd();
        });
    }

    private void RefreshStatus()
    {
        var port = ConfigManager.ReadRuntimePort();
        StatusText.Text = port > 0
            ? $"服务器状态: 端口 {port} | Sessions: {_engine.Sessions.Count} | Hook: {(HookInstaller.IsInstalled() ? "已安装" : "未安装")}"
            : $"服务器状态: 未运行 | Sessions: {_engine.Sessions.Count}";
    }

    /// <summary>
    /// Called externally to log raw HTTP request info
    /// </summary>
    public void LogRaw(string msg, SolidColorBrush? color = null)
    {
        Log(msg, color);
    }
}
