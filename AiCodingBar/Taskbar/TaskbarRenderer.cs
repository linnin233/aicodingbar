using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiCodingBar.Config;
using AiCodingBar.Models;
using AiCodingBar.Native;
using AiCodingBar.Server;

namespace AiCodingBar.Taskbar;

public class TaskbarRenderer : IDisposable
{
    private readonly StateEngine _engine;
    private readonly ConfigManager _config;
    private Window? _overlayWindow;
    private TextBlock? _textBlock;
    private System.Windows.Threading.DispatcherTimer? _positionTimer;
    private System.Windows.Threading.DispatcherTimer? _recreateTimer;

    public TaskbarRenderer(StateEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
        _engine.OnAnyChange += Refresh;
    }

    public void Show()
    {
        if (_overlayWindow != null) return;

        var isDark = TaskbarInterop.IsDarkMode();
        var fgColor = isDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
        var bgBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x60, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));

        _textBlock = new TextBlock
        {
            FontFamily = new FontFamily(_config.Current.Taskbar.FontName),
            FontSize = _config.Current.Taskbar.FontSize,
            Foreground = new SolidColorBrush(fgColor),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Text = BuildDisplayText(),
        };

        var border = new Border
        {
            Background = bgBrush,
            CornerRadius = new CornerRadius(3),
            Child = _textBlock,
        };

        _overlayWindow = new Window
        {
            Content = border,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false,
            IsHitTestVisible = true,
        };

        _overlayWindow.MouseLeftButtonDown += (s, e) => OnClicked?.Invoke();
        var contextMenu = BuildContextMenu();
        _overlayWindow.MouseRightButtonDown += (s, e) =>
        {
            contextMenu.IsOpen = true;
        };

        _overlayWindow.Loaded += (s, e) =>
        {
            PositionWindow();
        };

        _overlayWindow.Show();

        // Reposition every second (taskbar may resize / explorer restart)
        _positionTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _positionTimer.Tick += (s, e) => PositionWindow();
        _positionTimer.Start();

        // Recreate window every 5s if it was destroyed by explorer restart
        _recreateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _recreateTimer.Tick += (s, e) =>
        {
            if (_overlayWindow == null || !_overlayWindow.IsVisible)
            {
                _overlayWindow?.Close();
                _overlayWindow = null;
                Show();
            }
        };
        _recreateTimer.Start();
    }

    public void Hide()
    {
        _positionTimer?.Stop();
        _recreateTimer?.Stop();
        _overlayWindow?.Close();
        _overlayWindow = null;
    }

    public event Action? OnClicked;

    public void Refresh()
    {
        if (_textBlock == null) return;
        _textBlock.Dispatcher.Invoke(() =>
        {
            _textBlock.Text = BuildDisplayText();
            _overlayWindow?.InvalidateVisual();
        });
    }

    public string BuildDisplayText()
    {
        var mode = _config.Current.Taskbar.Mode;
        return mode switch
        {
            "aggregate" => BuildAggregateText(),
            "highlight" => BuildHighlightText(),
            _ => BuildCompactText(),
        };
    }

    private string BuildCompactText()
    {
        var sessions = _engine.Sessions.Values
            .OrderBy(s => s.SortIndex)
            .ToList();

        if (sessions.Count == 0) return "--";

        var threshold = _config.Current.Taskbar.AutoSwitchThreshold;
        if (sessions.Count > threshold) return BuildAggregateText();

        var sb = new StringBuilder();
        foreach (var s in sessions)
        {
            if (sb.Length > 0) sb.Append(' ');
            var mapping = _config.Current.StateMapping.Values
                .FirstOrDefault(m => m.State == s.Status);
            var abbr = mapping?.Abbr ?? s.Status.ToUpperInvariant()[..Math.Min(3, s.Status.Length)];
            sb.Append($"{s.SortIndex}:{abbr}");
        }
        return sb.ToString();
    }

    private string BuildAggregateText()
    {
        var counts = _engine.Sessions.Values
            .GroupBy(s => s.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        var showZeros = _config.Current.Taskbar.ShowZeroCounts;
        var sb = new StringBuilder();
        foreach (var mapping in _config.Current.StateMapping.Values.DistinctBy(m => m.State))
        {
            var count = counts.GetValueOrDefault(mapping.State, 0);
            if (count == 0 && !showZeros) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append($"{mapping.Abbr}:{count}");
        }
        return sb.Length > 0 ? sb.ToString() : "--";
    }

    private string BuildHighlightText()
    {
        var sessions = _engine.Sessions.Values
            .OrderByDescending(s => s.LastUpdateAt)
            .ToList();

        if (sessions.Count == 0) return "--";

        var latest = sessions[0];
        var mapping = _config.Current.StateMapping.Values
            .FirstOrDefault(m => m.State == latest.Status);
        var abbr = mapping?.Abbr ?? latest.Status.ToUpperInvariant();

        var extra = sessions.Count > 1 ? $" +{sessions.Count - 1}" : "";
        return $"{abbr} S{latest.SortIndex}{extra}";
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        void AddModeItem(string mode, string label)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = _config.Current.Taskbar.Mode == mode,
            };
            item.Click += (s, e) =>
            {
                _config.Current.Taskbar.Mode = mode;
                _config.Save();
                Refresh();
            };
            menu.Items.Add(item);
        }

        AddModeItem("compact", "紧凑模式");
        AddModeItem("aggregate", "聚合模式");
        AddModeItem("highlight", "高亮模式");

        menu.Items.Add(new Separator());
        var dashboardItem = new MenuItem { Header = "打开 Dashboard" };
        dashboardItem.Click += (s, e) => OnClicked?.Invoke();
        menu.Items.Add(dashboardItem);

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (s, e) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        return menu;
    }

    public event Action<string>? OnDebugLog;

    private void PositionWindow()
    {
        if (_overlayWindow == null) return;
        if (_overlayWindow.ActualWidth < 1 || _overlayWindow.ActualHeight < 1) return;

        var taskbarRect = TaskbarInterop.GetTaskbarRect();
        OnDebugLog?.Invoke($"Taskbar rect: X={taskbarRect.X} Y={taskbarRect.Y} W={taskbarRect.Width} H={taskbarRect.Height}");

        if (taskbarRect.Width == 0 || taskbarRect.Height == 0)
        {
            // Fallback: bottom-right of primary screen
            var screen = System.Windows.SystemParameters.WorkArea;
            _overlayWindow.Left = screen.Right - _overlayWindow.ActualWidth - 8;
            _overlayWindow.Top = screen.Bottom - _overlayWindow.ActualHeight - 4;
            OnDebugLog?.Invoke($"Fallback position: L={_overlayWindow.Left} T={_overlayWindow.Top} W={_overlayWindow.ActualWidth} H={_overlayWindow.ActualHeight}");
            return;
        }

        var notifyRect = TaskbarInterop.GetNotificationAreaRect();
        OnDebugLog?.Invoke($"Notify rect: X={notifyRect.X} Y={notifyRect.Y} W={notifyRect.Width} H={notifyRect.Height}");

        var rightEdge = notifyRect.X > 0 ? notifyRect.X - 4 : taskbarRect.X + taskbarRect.Width - 200;
        _overlayWindow.Left = Math.Min(rightEdge, taskbarRect.X + taskbarRect.Width - 8) - _overlayWindow.ActualWidth;
        _overlayWindow.Top = taskbarRect.Y + (taskbarRect.Height - _overlayWindow.ActualHeight) / 2;
        _overlayWindow.Height = taskbarRect.Height;

        OnDebugLog?.Invoke($"Overlay position: L={_overlayWindow.Left} T={_overlayWindow.Top} W={_overlayWindow.ActualWidth} H={_overlayWindow.ActualHeight}");
    }

    public void Dispose()
    {
        _engine.OnAnyChange -= Refresh;
        Hide();
    }
}
