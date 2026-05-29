using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TaskbarMonitor.Dashboard;

public partial class DebugLog : UserControl
{
    private readonly StateEngine _engine;
    private readonly Config _config;
    private readonly TaskbarWindow? _taskbar;
    private readonly StringBuilder _buffer = new();
    private bool _autoScroll = true;

    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x16, 0xa3, 0x4a));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xdc, 0x26, 0x26));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(0xd9, 0x77, 0x04));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(0x6b, 0x6b, 0x70));
    private static readonly SolidColorBrush CyanBrush = new(Color.FromRgb(0x02, 0x84, 0xc7));
    private static readonly SolidColorBrush AccentBrush = new(Color.FromRgb(0xd9, 0x77, 0x57));

    public DebugLog(StateEngine engine, Config config, TaskbarWindow? taskbar)
    {
        InitializeComponent();
        _engine = engine;
        _config = config;
        _taskbar = taskbar;

        Log("=== TaskbarMonitor Debug Log ===", GrayBrush);
        Log($"HTTP Server: http://127.0.0.1:{ConfigManager.ReadRuntimePort()} (port from runtime.json)", CyanBrush);
        Log($"Config dir: ~/.clawd-monitor/", GrayBrush);
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
                Log($"Simulate failed: {ex.Message}", RedBrush);
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

    public void ApplyLanguage(string lang)
    {
        var zh = lang == "zh";
        TitleText.Text = zh ? "调试日志" : "Debug Log";
        SubText.Text = zh ? "实时事件日志和测试工具" : "Real-time event log and testing tools";
        ClearBtn.Content = zh ? "清空" : "Clear";
        TestBtn.Content = zh ? "模拟事件" : "Simulate Event";
        AutoScrollCheck.Content = zh ? " 自动滚动" : " Auto-scroll";
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var zh = _config.Language == "zh";
        var port = ConfigManager.ReadRuntimePort();
        var hookStr = HookInstaller.IsInstalled()
            ? (zh ? "已安装" : "installed")
            : (zh ? "未安装" : "not installed");
        StatusText.Text = zh
            ? (port > 0 ? $"端口 {port} | 会话: {_engine.Sessions.Count} | Hook: {hookStr}" : $"未运行 | 会话: {_engine.Sessions.Count}")
            : (port > 0 ? $"Port {port} | Sessions: {_engine.Sessions.Count} | Hook: {hookStr}" : $"Not running | Sessions: {_engine.Sessions.Count}");
    }

    public void Log(string msg, SolidColorBrush? color = null)
    {
        _buffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        Dispatcher.Invoke(() =>
        {
            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };
            var run = new Run($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            run.Foreground = color ?? GrayBrush;
            para.Inlines.Add(run);
            LogBox.Document.Blocks.Add(para);

            if (_autoScroll)
                LogBox.ScrollToEnd();
        });
    }
}
