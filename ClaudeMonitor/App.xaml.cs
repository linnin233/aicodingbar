using System.Windows;
using System.Windows.Threading;
using ClaudeMonitor.Config;
using ClaudeMonitor.Server;
using ClaudeMonitor.Taskbar;

namespace ClaudeMonitor;

public partial class App : Application
{
    private static Mutex? _appMutex;
    private static bool _ownsMutex;

    private ConfigManager? _config;
    private StateEngine? _engine;
    private HttpStateServer? _server;
    private NativeTaskbarText? _taskbarText;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _cleanupTimer;
    private readonly List<string> _logQueue = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _appMutex = new Mutex(true, @"Global\ClaudeMonitor", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("ClaudeMonitor 已在运行中", "ClaudeMonitor",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _config = new ConfigManager();
            _config.Load();

            _engine = new StateEngine(_config);

            _server = new HttpStateServer(_engine, _config);
            _server.OnLog += ServerLog;
            await _server.StartAsync();

            _ = HookInstaller.EnsureInstalledAsync();

            // Taskbar overlay (text display — native child window, TrafficMonitor approach)
            _taskbarText = new NativeTaskbarText(_engine, _config);
            _taskbarText.OnDebugLog += ServerLog;
            _taskbarText.OnClicked += ShowDashboard;
            _taskbarText.OnRightClicked += ShowDashboard;
            _taskbarText.Show();

            // System tray icon (always visible, fallback entry point)
            _trayIcon = new TrayIcon(_engine, () => _config);
            _trayIcon.OnShowDashboard += ShowDashboard;
            _trayIcon.OnExit += ExitApp;
            _trayIcon.Show();

            _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _cleanupTimer.Tick += (s, ev) => _engine.CleanupDeadSessions();
            _cleanupTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex.Message}", "ClaudeMonitor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void ServerLog(string msg)
    {
        // Queue logs until debug panel is available
        lock (_logQueue) { _logQueue.Add(msg); }

        // Forward to main window if open
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow != null)
            {
                _mainWindow.LogDebug(msg);
            }
        });
    }

    private void ShowDashboard()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(_engine!, _config!, _taskbarText!);
            _mainWindow.Closed += (s, e) => _mainWindow = null;

            // Replay queued logs
            lock (_logQueue)
            {
                foreach (var msg in _logQueue)
                    _mainWindow.LogDebug(msg);
                _logQueue.Clear();
            }
        }

        if (_mainWindow.IsVisible)
        {
            _mainWindow.Activate();
        }
        else
        {
            _mainWindow.Show();
            _mainWindow.Activate();
        }
    }

    private void ExitApp()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cleanupTimer?.Stop();
        try { _server?.Stop(); } catch { }
        try { _taskbarText?.Dispose(); } catch { }
        try { _trayIcon?.Dispose(); } catch { }
        try { _mainWindow?.Close(); } catch { }
        if (_ownsMutex)
        {
            try { _appMutex?.ReleaseMutex(); } catch { }
        }
        base.OnExit(e);
    }
}
