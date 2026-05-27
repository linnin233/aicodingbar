using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClaudeMonitor.Config;
using ClaudeMonitor.Server;
using GdiColor = System.Drawing.Color;

namespace ClaudeMonitor.Taskbar;

public class NativeTaskbarText : IDisposable
{
    private readonly StateEngine _engine;
    private readonly ConfigManager _config;

    private IntPtr _hwnd;
    private string _displayText = "--";
    private List<(string Text, GdiColor ForeColor)> _segments = new();
    private bool _disposed;
    private Font? _gdiFont;
    private Brush? _textBrush;
    private Brush? _bgBrush;
    private int _windowWidth = 80;
    private int _windowHeight = 24;
    private int _flashTogglesRemaining;
    private IntPtr _taskbarHwnd;
    private uint _taskbarCreatedMsg;
    private bool _isParented;

    private const int TIMER_REPOSITION = 1;
    private const int TIMER_FLASH = 2;
    private const int REPOSITION_MS = 500;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int CS_HREDRAW = 0x0002;
    private const int CS_VREDRAW = 0x0001;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_DESTROY = 0x0002;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_TIMER = 0x0113;
    private const uint SWP_NOACTIVATE = 0x0010;

    private static WndProcDelegate? _wndProcDelegate;
    private static readonly Dictionary<IntPtr, NativeTaskbarText> _windows = new();

    public event Action? OnClicked;
    public event Action? OnRightClicked;
    public event Action<string>? OnDebugLog;

    public NativeTaskbarText(StateEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
        _engine.OnAnyChange += Refresh;
        _engine.OnSessionUpdated += (session) =>
        {
            if (session.Status == "attention")
                StartFlash();
        };
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
    }

    public void Show()
    {
        if (_hwnd != IntPtr.Zero) return;

        _taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (_taskbarHwnd == IntPtr.Zero)
        {
            OnDebugLog?.Invoke("[NTT] Shell_TrayWnd not found");
            return;
        }

        _displayText = BuildDisplayText();
        CreateGdiResources();
        MeasureSize();

        var className = $"CM_NTT_{Environment.TickCount % 10000}";
        _wndProcDelegate = StaticWndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = _wndProcDelegate,
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
            hbrBackground = IntPtr.Zero,
            hCursor = LoadCursor(IntPtr.Zero, 32512),
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            OnDebugLog?.Invoke($"[NTT] RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        // taskbar-hello pattern: WS_POPUP + WS_EX_TOOLWINDOW, then SetParent
        _hwnd = CreateWindowEx(
            WS_EX_TOOLWINDOW,
            className, "",
            WS_POPUP,
            0, 0, _windowWidth, _windowHeight,
            IntPtr.Zero, IntPtr.Zero,
            GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            OnDebugLog?.Invoke($"[NTT] CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        lock (_windows) { _windows[_hwnd] = this; }

        // Embed into taskbar (taskbar-hello: SetParent to Shell_TrayWnd directly)
        if (SetParent(_hwnd, _taskbarHwnd) != IntPtr.Zero)
        {
            _isParented = true;
            OnDebugLog?.Invoke($"[NTT] SetParent ok, parent=Shell_TrayWnd (0x{_taskbarHwnd:X})");
        }
        else
        {
            OnDebugLog?.Invoke("[NTT] SetParent failed");
        }

        Reposition();
        SetTimer(_hwnd, TIMER_REPOSITION, REPOSITION_MS, IntPtr.Zero);
        ShowWindow(_hwnd, 8); // SW_SHOWNOACTIVATE
        OnDebugLog?.Invoke($"[NTT] Window ready: hwnd=0x{_hwnd:X} parented={_isParented}");
    }

    /// <summary>
    /// Reposition window to the left of TrayNotifyWnd, centered vertically.
    /// taskbar-hello pattern: ScreenToClient on Shell_TrayWnd, position at TrayNotifyWnd.left - width - 2.
    /// Called every 500ms to handle tray icon count changes.
    /// </summary>
    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _taskbarHwnd == IntPtr.Zero) return;

        var hNotify = FindWindowEx(_taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (hNotify == IntPtr.Zero) return;

        // Get screen rects
        GetWindowRect(_taskbarHwnd, out var rcTb);
        GetWindowRect(hNotify, out var rcNt);

        // Convert TrayNotifyWnd left edge to taskbar client coords
        var pt = new POINT { X = rcNt.Left, Y = rcNt.Top };
        ScreenToClient(_taskbarHwnd, ref pt);

        int x = pt.X - _windowWidth - 2;
        int y = (rcTb.Bottom - rcTb.Top - _windowHeight) / 2;
        if (x < 0) x = 2;

        SetWindowPos(_hwnd, IntPtr.Zero, x, y, _windowWidth, _windowHeight, SWP_NOACTIVATE);
    }

    private void MeasureSize()
    {
        try
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            if (_gdiFont != null)
            {
                var sz = g.MeasureString(_displayText, _gdiFont);
                _windowWidth = (int)Math.Ceiling(sz.Width) + 14;
            }
        }
        catch { _windowWidth = _displayText.Length * 10 + 14; }

        if (_windowWidth < 40) _windowWidth = 40;
        _windowHeight = Math.Max(22, (int)(_gdiFont?.GetHeight() ?? 14) + 6);
    }

    private void CreateGdiResources()
    {
        _gdiFont?.Dispose();
        _textBrush?.Dispose();
        _bgBrush?.Dispose();

        var isDark = IsDarkMode();
        var fontSize = _config.Current.Taskbar.FontSize > 0 ? _config.Current.Taskbar.FontSize : 9f;
        var fontName = !string.IsNullOrEmpty(_config.Current.Taskbar.FontName)
            ? _config.Current.Taskbar.FontName
            : "Microsoft YaHei UI";

        _gdiFont = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        _textBrush = new SolidBrush(isDark ? GdiColor.White : System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1A));
        _bgBrush = new SolidBrush(isDark
            ? System.Drawing.Color.FromArgb(30, 30, 30)
            : System.Drawing.Color.FromArgb(240, 240, 240));
    }

    public void Refresh()
    {
        var newText = BuildDisplayText();
        if (newText != _displayText)
        {
            _displayText = newText;
            CreateGdiResources();
            MeasureSize();
            // Width changed, reposition next timer tick will handle it
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            {
                Reposition();
            }
        }

        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void StartFlash()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        _flashTogglesRemaining = 4; // 2 flashes = 4 toggles
        SetTimer(_hwnd, TIMER_FLASH, 100, IntPtr.Zero);
        OnDebugLog?.Invoke("[NTT] Flash started (attention)");
    }

    private async void RecreateAsync()
    {
        if (_hwnd != IntPtr.Zero)
        {
            lock (_windows) { _windows.Remove(_hwnd); }
            KillTimer(_hwnd, TIMER_REPOSITION);
            KillTimer(_hwnd, TIMER_FLASH);
            if (IsWindow(_hwnd)) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _isParented = false;
        _taskbarHwnd = IntPtr.Zero;

        await Task.Delay(500);
        Show();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.OnAnyChange -= Refresh;
        if (_hwnd != IntPtr.Zero)
        {
            lock (_windows) { _windows.Remove(_hwnd); }
            KillTimer(_hwnd, TIMER_REPOSITION);
            KillTimer(_hwnd, TIMER_FLASH);
            if (IsWindow(_hwnd)) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _gdiFont?.Dispose();
        _textBrush?.Dispose();
        _bgBrush?.Dispose();
    }

    // ---- Display text ----
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

    private GdiColor GetStatusColor(string state)
    {
        var mapping = _config.Current.StateMapping.Values.FirstOrDefault(m => m.State == state);
        if (mapping != null && !string.IsNullOrEmpty(mapping.Color))
        {
            try { return ColorTranslator.FromHtml(mapping.Color); }
            catch { }
        }
        return GdiColor.Gray;
    }

    private string BuildCompactText()
    {
        _segments.Clear();
        var sessions = _engine.Sessions.Values.OrderBy(s => s.SortIndex).ToList();
        if (sessions.Count == 0)
        {
            _segments.Add(("Claude --", GdiColor.White));
            return "Claude --";
        }
        if (sessions.Count > _config.Current.Taskbar.AutoSwitchThreshold) return BuildAggregateText();

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i > 0)
            {
                _segments.Add(("|", GdiColor.Gray));
                sb.Append('|');
            }
            var s = sessions[i];
            var mapping = _config.Current.StateMapping.Values.FirstOrDefault(m => m.State == s.Status);
            var name = !string.IsNullOrEmpty(mapping?.Name) ? mapping!.Name : (mapping?.Abbr ?? s.Status);
            _segments.Add(($"{s.SortIndex}:", GdiColor.White));
            _segments.Add((name, GetStatusColor(s.Status)));
            sb.Append(s.SortIndex);
            sb.Append(':');
            sb.Append(name);
        }
        return sb.ToString();
    }

    private string BuildAggregateText()
    {
        _segments.Clear();
        var counts = _engine.Sessions.Values.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count());
        var showZeros = _config.Current.Taskbar.ShowZeroCounts;
        var entries = _config.Current.StateMapping.Values.DistinctBy(m => m.State).ToList();

        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var mapping in entries)
        {
            var count = counts.GetValueOrDefault(mapping.State, 0);
            if (count == 0 && !showZeros) continue;
            if (!first)
            {
                _segments.Add(("|", GdiColor.Gray));
                sb.Append('|');
            }
            first = false;
            var name = !string.IsNullOrEmpty(mapping.Name) ? mapping.Name : mapping.Abbr;
            _segments.Add(($"{name}:", GetStatusColor(mapping.State)));
            _segments.Add((count.ToString(), GdiColor.White));
            sb.Append(name);
            sb.Append(':');
            sb.Append(count);
        }
        if (sb.Length == 0)
        {
            _segments.Add(("Claude --", GdiColor.White));
            return "Claude --";
        }
        return sb.ToString();
    }

    private string BuildHighlightText()
    {
        _segments.Clear();
        var sessions = _engine.Sessions.Values.OrderByDescending(s => s.LastUpdateAt).ToList();
        if (sessions.Count == 0)
        {
            _segments.Add(("Claude --", GdiColor.White));
            return "Claude --";
        }
        var latest = sessions[0];
        var mapping = _config.Current.StateMapping.Values.FirstOrDefault(m => m.State == latest.Status);
        var name = !string.IsNullOrEmpty(mapping?.Name) ? mapping!.Name : (mapping?.Abbr ?? latest.Status);
        var extra = sessions.Count > 1 ? $" +{sessions.Count - 1}" : "";
        _segments.Add((name, GetStatusColor(latest.Status)));
        _segments.Add(($" S{latest.SortIndex}{extra}", GdiColor.White));
        return $"{name} S{latest.SortIndex}{extra}";
    }

    // ---- WndProc ----
    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        NativeTaskbarText? self;
        lock (_windows) { _windows.TryGetValue(hWnd, out self); }
        if (self == null) return DefWindowProc(hWnd, msg, wParam, lParam);
        return self.WndProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint(hWnd);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return (IntPtr)1;
            case WM_LBUTTONDOWN:
                OnClicked?.Invoke();
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                OnRightClicked?.Invoke();
                return IntPtr.Zero;
            case WM_TIMER:
                if (wParam == (IntPtr)TIMER_REPOSITION)
                {
                    Reposition();
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                if (wParam == (IntPtr)TIMER_FLASH)
                {
                    if (_flashTogglesRemaining <= 0)
                    {
                        KillTimer(_hwnd, TIMER_FLASH);
                        ShowWindow(_hwnd, 8);
                        return IntPtr.Zero;
                    }
                    _flashTogglesRemaining--;
                    ShowWindow(_hwnd, (_flashTogglesRemaining % 2) == 0 ? 8 : 0);
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;

            default:
                if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
                {
                    OnDebugLog?.Invoke("[NTT] TaskbarCreated received, recreating window");
                    RecreateAsync();
                    return IntPtr.Zero;
                }
                break;
            case WM_SETTINGCHANGE:
                CreateGdiResources();
                Reposition();
                InvalidateRect(_hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case WM_DESTROY:
                lock (_windows) { _windows.Remove(hWnd); }
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Paint(IntPtr hWnd)
    {
        PAINTSTRUCT ps = default;
        var hdc = BeginPaint(hWnd, ref ps);
        try
        {
            if (hdc == IntPtr.Zero) return;
            GetClientRect(hWnd, out var cr);
            var w = cr.Right - cr.Left;
            var h = cr.Bottom - cr.Top;
            if (w <= 0 || h <= 0) return;

            using var g = Graphics.FromHdc(hdc);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            if (_bgBrush != null) g.FillRectangle(_bgBrush, 0, 0, w, h);

            if (_gdiFont != null && _segments.Count > 0)
            {
                float totalWidth = 0;
                foreach (var seg in _segments)
                    totalWidth += g.MeasureString(seg.Text, _gdiFont, int.MaxValue,
                        StringFormat.GenericTypographic).Width;

                float x = Math.Max(0, (w - totalWidth) / 2);
                foreach (var seg in _segments)
                {
                    using var brush = new SolidBrush(seg.ForeColor);
                    g.DrawString(seg.Text, _gdiFont, brush, x, (h - _gdiFont.GetHeight(g)) / 2);
                    x += g.MeasureString(seg.Text, _gdiFont, int.MaxValue,
                        StringFormat.GenericTypographic).Width;
                }
            }
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[NTT] Paint error: {ex.Message}");
        }
        finally
        {
            EndPaint(hWnd, ref ps);
        }
    }

    // ---- Helpers ----
    private static bool IsDarkMode()
    {
        try
        {
            var v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is 0;
        }
        catch { return false; }
    }

    // ===== P/Invoke =====
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate? lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
}
