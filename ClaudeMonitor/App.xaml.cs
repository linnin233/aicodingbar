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
            MigrateConfig(_config); // 配置迁移：补充新增字段

            _engine = new StateEngine(_config);

            _server = new HttpStateServer(_engine, _config);
            _server.OnLog += ServerLog;
            await _server.StartAsync();

            _ = HookInstaller.EnsureInstalledAsync();

            // 任务栏文字叠加层（Win32 子窗口嵌入）
            _taskbarText = new NativeTaskbarText(_engine, _config);
            _taskbarText.OnDebugLog += ServerLog;
            _taskbarText.OnClicked += ShowDashboard;
            _taskbarText.OnRightClicked += ShowDashboard;
            _taskbarText.Show();

            // 系统托盘图标
            _trayIcon = new TrayIcon(_engine, () => _config);
            _trayIcon.OnShowDashboard += ShowDashboard;
            _trayIcon.OnExit += ExitApp;
            _trayIcon.Show();

            // 10s 间隔的 stale session 清理
            _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _cleanupTimer.Tick += (s, ev) =>
            {
                var (cleaned, transitioned) = _engine.CleanupDeadSessions();
                if (cleaned > 0) ServerLog($"[Cleanup] {cleaned} sessions removed, {transitioned} transitioned");
            };
            _cleanupTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex.Message}", "ClaudeMonitor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// 配置迁移：补充旧版本配置中缺失的新字段。
    /// 确保 v0.0.1 → v0.1.0 平滑升级，不需要用户删除 config.json。
    /// </summary>
    private static void MigrateConfig(ConfigManager config)
    {
        var mappings = config.Current.StateMapping;
        var defaults = Models.ConfigModel.DefaultMappings();
        bool changed = false;

        foreach (var kv in defaults)
        {
            if (!mappings.ContainsKey(kv.Key))
            {
                mappings[kv.Key] = kv.Value;
                changed = true;
            }
            else
            {
                var existing = mappings[kv.Key];
                var def = kv.Value;

                // 补充缺失字段（旧 config 没有 StateKind / Priority / AutoReturnMs）
                if (string.IsNullOrEmpty(existing.StateKind))
                {
                    existing.StateKind = def.StateKind;
                    changed = true;
                }
                if (existing.Priority == 0 && def.Priority != 0)
                {
                    existing.Priority = def.Priority;
                    changed = true;
                }
                if (existing.AutoReturnMs == 0 && def.AutoReturnMs != 0)
                {
                    existing.AutoReturnMs = def.AutoReturnMs;
                    changed = true;
                }
            }
        }

        // 补充 Taskbar 新字段
        if (config.Current.Taskbar.AutoSwitchThreshold == 0)
        {
            config.Current.Taskbar.AutoSwitchThreshold = 7;
            changed = true;
        }

        if (changed)
        {
            config.Save();
            System.Diagnostics.Debug.WriteLine("[Config] Migrated config to v0.1.0");
        }
    }

    private void ServerLog(string msg)
    {
        lock (_logQueue) { _logQueue.Add(msg); }

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
        try { _engine?.Dispose(); } catch { }
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
