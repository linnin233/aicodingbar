using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeMonitor.Config;
using ClaudeMonitor.Server;

namespace ClaudeMonitor.Taskbar;

public class SimpleTaskbarText : IDisposable
{
    private readonly StateEngine _engine;
    private readonly ConfigManager _config;

    private Form? _form;
    private string _displayText = "--";
    private bool _disposed;

    public event Action? OnClicked;
    public event Action<string>? OnDebugLog;

    public SimpleTaskbarText(StateEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
        _engine.OnAnyChange += Refresh;
    }

    public void Show()
    {
        if (_form != null) return;

        var taskbarRect = GetTaskbarRect();
        OnDebugLog?.Invoke($"Taskbar: L={taskbarRect.Left} T={taskbarRect.Top} R={taskbarRect.Right} B={taskbarRect.Bottom}");

        if (taskbarRect.Width == 0)
        {
            OnDebugLog?.Invoke("Taskbar rect is empty, using fallback");
            var screen = Screen.PrimaryScreen;
            if (screen == null)
            {
                OnDebugLog?.Invoke("No screen found");
                return;
            }
            taskbarRect = new System.Drawing.Rectangle(
                screen.WorkingArea.Right - 200,
                screen.WorkingArea.Bottom - 40,
                200, 40);
        }

        var notifyRect = GetNotificationAreaRect();
        OnDebugLog?.Invoke($"Notify: L={notifyRect.Left} T={notifyRect.Top} R={notifyRect.Right}");

        _displayText = BuildDisplayText();
        var isDark = IsDarkMode();

        // Measure text
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        var font = CreateFont();
        var textSize = g.MeasureString(_displayText, font);
        var w = (int)Math.Ceiling(textSize.Width) + 16;
        var h = taskbarRect.Height > 0 ? taskbarRect.Height : 40;

        // Position to the left of notification area
        var rightEdge = notifyRect.Left > 0 ? notifyRect.Left - 2 : taskbarRect.Right - 200;
        var x = rightEdge - w;
        var y = taskbarRect.Top;

        // DIAGNOSTIC: use a bright visible color and position to confirm rendering
        var diagX = 100;
        var diagY = 100;
        OnDebugLog?.Invoke($"DIAG: using test position ({diagX},{diagY}) with red bg to verify form visibility");

        _form = new Form
        {
            Text = "CM",
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            TopMost = true,
            Width = 200,
            Height = 30,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(diagX, diagY),
            BackColor = System.Drawing.Color.Red,
            Opacity = 1.0,
        };

        var label = new Label
        {
            Text = "TEST-" + _displayText,
            Font = font,
            ForeColor = System.Drawing.Color.White,
            BackColor = System.Drawing.Color.Red,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
        };

        label.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) OnClicked?.Invoke();
        };

        _form.Controls.Add(label);

        // Prevent form from getting focus
        _form.Load += (s, e) =>
        {
            var exStyle = (int)GetWindowLong(_form.Handle, GWL_EXSTYLE);
            SetWindowLong(_form.Handle, GWL_EXSTYLE, (IntPtr)(exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            // Force Z-order above taskbar
            SetWindowPos(_form.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        };

        _form.Show();
        // Explicitly bring to front after showing
        SetWindowPos(_form.Handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        OnDebugLog?.Invoke($"Form shown: x={x} y={y} w={w} h={h} text='{_displayText}' handle={_form.Handle}");
    }

    private Font CreateFont()
    {
        var fontSize = _config.Current.Taskbar.FontSize > 0 ? _config.Current.Taskbar.FontSize : 9f;
        var fontName = !string.IsNullOrEmpty(_config.Current.Taskbar.FontName)
            ? _config.Current.Taskbar.FontName
            : "Microsoft YaHei UI";
        return new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
    }

    public void Refresh()
    {
        var newText = BuildDisplayText();
        if (newText != _displayText)
        {
            _displayText = newText;
        }

        if (_form != null && !_form.IsDisposed)
        {
            _form.BeginInvoke(() =>
            {
                if (_form.Controls.Count > 0 && _form.Controls[0] is Label label)
                {
                    label.Text = _displayText;
                }
            });
        }
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
        var parts = new List<string>();
        foreach (var s in sessions)
        {
            var mapping = _config.Current.StateMapping.Values
                .FirstOrDefault(m => m.State == s.Status);
            var abbr = mapping?.Abbr ?? s.Status.ToUpperInvariant()[..Math.Min(3, s.Status.Length)];
            parts.Add($"{s.SortIndex}:{abbr}");
        }
        return string.Join(" ", parts);
    }

    private string BuildAggregateText()
    {
        var counts = _engine.Sessions.Values
            .GroupBy(s => s.Status)
            .ToDictionary(g => g.Key, g => g.Count());
        var showZeros = _config.Current.Taskbar.ShowZeroCounts;
        var parts = new List<string>();
        foreach (var mapping in _config.Current.StateMapping.Values.DistinctBy(m => m.State))
        {
            var count = counts.GetValueOrDefault(mapping.State, 0);
            if (count == 0 && !showZeros) continue;
            parts.Add($"{mapping.Abbr}:{count}");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : "--";
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.OnAnyChange -= Refresh;
        try
        {
            if (_form != null && !_form.IsDisposed)
            {
                _form.Invoke(() => { _form.Close(); _form.Dispose(); });
            }
        }
        catch { }
    }

    // ===== P/Invoke for taskbar info =====

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private static System.Drawing.Rectangle GetTaskbarRect()
    {
        var hwnd = FindWindow("Shell_TrayWnd", null);
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
            return new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return System.Drawing.Rectangle.Empty;
    }

    private static System.Drawing.Rectangle GetNotificationAreaRect()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return System.Drawing.Rectangle.Empty;
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return System.Drawing.Rectangle.Empty;
        if (GetWindowRect(notify, out var rect))
            return new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return System.Drawing.Rectangle.Empty;
    }

    private static bool IsDarkMode()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int v && v == 0;
        }
        catch { return false; }
    }
}
