using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AiCodingBar.Config;
using AiCodingBar.Models;
using AiCodingBar.Server;
using GdiColor = System.Drawing.Color;

namespace AiCodingBar.Taskbar;

public class NativeTaskbarText : IDisposable
{
    private readonly StateEngine _engine;
    private readonly ConfigManager _config;

    private IntPtr _hwnd;

    // 双行渲染数据
    private string _line1Text = "--";
    private string _line2Text = "--";
    private List<(string Text, GdiColor ForeColor)> _line1Segs = new();
    private List<(string Text, GdiColor ForeColor)> _line2Segs = new();

    private bool _disposed;
    private Font? _gdiFont;
    private Brush? _textBrush;
    private Brush? _bgBrush;
    private int _windowWidth = 80;
    private int _windowHeight = 24;
    private int _taskbarHeight;
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
            if (session.Status == "attention" || session.IsBlocking)
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

        // 获取 taskbar 实际高度
        GetWindowRect(_taskbarHwnd, out var rcTb);
        _taskbarHeight = rcTb.Bottom - rcTb.Top;

        BuildDisplayLines();
        CreateGdiResources();
        MeasureSize();

        var className = $"AB_NTT_{Environment.TickCount % 10000}";
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

        if (SetParent(_hwnd, _taskbarHwnd) != IntPtr.Zero)
        {
            _isParented = true;
            OnDebugLog?.Invoke($"[NTT] SetParent ok, taskbarH={_taskbarHeight}");
        }
        else
        {
            OnDebugLog?.Invoke("[NTT] SetParent failed");
        }

        Reposition();
        SetTimer(_hwnd, TIMER_REPOSITION, REPOSITION_MS, IntPtr.Zero);
        ShowWindow(_hwnd, 8);
        OnDebugLog?.Invoke($"[NTT] Window ready: hwnd=0x{_hwnd:X} parented={_isParented}");
    }

    /// <summary>
    /// 双行模式：窗口填满整个 taskbar 高度 (y=0)，不再垂直居中。
    /// 单行模式：退化为原来居中逻辑。
    /// </summary>
    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _taskbarHwnd == IntPtr.Zero) return;

        var hNotify = FindWindowEx(_taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (hNotify == IntPtr.Zero) return;

        GetWindowRect(_taskbarHwnd, out var rcTb);
        GetWindowRect(hNotify, out var rcNt);

        var pt = new POINT { X = rcNt.Left, Y = rcNt.Top };
        ScreenToClient(_taskbarHwnd, ref pt);

        // 刷新 taskbar 高度（Win11 任务栏高度可能动态变化）
        _taskbarHeight = rcTb.Bottom - rcTb.Top;

        int x = pt.X - _windowWidth - 2;
        if (x < 0) x = 2;

        if (_config.Current.Taskbar.ShowLine2)
        {
            _windowHeight = _taskbarHeight; // 填满整条 taskbar
            SetWindowPos(_hwnd, IntPtr.Zero, x, 0, _windowWidth, _windowHeight, SWP_NOACTIVATE);
        }
        else
        {
            _windowHeight = Math.Max(22, (int)(_gdiFont?.GetHeight() ?? 14) + 6);
            int y = (_taskbarHeight - _windowHeight) / 2;
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, _windowWidth, _windowHeight, SWP_NOACTIVATE);
        }
    }

    private void MeasureSize()
    {
        try
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            if (_gdiFont != null)
            {
                var w1 = g.MeasureString(_line1Text, _gdiFont).Width;
                var w2 = g.MeasureString(_line2Text, _gdiFont).Width;
                _windowWidth = (int)Math.Ceiling(Math.Max(w1, w2)) + 14;
            }
        }
        catch { _windowWidth = Math.Max(_line1Text.Length, _line2Text.Length) * 10 + 14; }

        if (_windowWidth < 40) _windowWidth = 40;
    }

    private void CreateGdiResources()
    {
        _gdiFont?.Dispose();
        _textBrush?.Dispose();
        _bgBrush?.Dispose();

        var isDark = IsDarkMode();
        var fontName = !string.IsNullOrEmpty(_config.Current.Taskbar.FontName)
            ? _config.Current.Taskbar.FontName
            : "Microsoft YaHei UI";

        float fontSize;
        if (_config.Current.Taskbar.AutoFontSize && _config.Current.Taskbar.ShowLine2 && _taskbarHeight > 0)
        {
            // 双行模式：每个行高 = taskbarHeight / 2，字体 = 行高 * 0.55
            fontSize = (_taskbarHeight / 2f) * 0.55f;
            if (fontSize < 7f) fontSize = 7f;
            if (fontSize > 11f) fontSize = 11f;
        }
        else if (_config.Current.Taskbar.FontSize > 0)
        {
            fontSize = _config.Current.Taskbar.FontSize;
        }
        else
        {
            fontSize = 9f;
        }

        _gdiFont = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        _textBrush = new SolidBrush(isDark ? GdiColor.White : GdiColor.FromArgb(0x1A, 0x1A, 0x1A));
        _bgBrush = new SolidBrush(isDark
            ? GdiColor.FromArgb(30, 30, 30)
            : GdiColor.FromArgb(240, 240, 240));
    }

    public void Refresh()
    {
        var old1 = _line1Text;
        var old2 = _line2Text;
        BuildDisplayLines();
        if (_line1Text != old1 || _line2Text != old2)
        {
            CreateGdiResources();
            MeasureSize();
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
                Reposition();
        }

        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void StartFlash()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        _flashTogglesRemaining = 4;
        SetTimer(_hwnd, TIMER_FLASH, 100, IntPtr.Zero);
        OnDebugLog?.Invoke("[NTT] Flash started");
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

    // ═══════════════════════════════════════════
    // 双行显示构建
    // ═══════════════════════════════════════════

    private void BuildDisplayLines()
    {
        _line1Segs.Clear();
        _line2Segs.Clear();

        var sessions = _engine.Sessions.Values
            .Where(s => s.StatePriority > 1) // 过滤 idle(1) 和 sleeping(0) — 只显示活跃状态
            .OrderBy(s => s.SortIndex)
            .ToList();

        if (sessions.Count == 0)
        {
            _line1Segs.Add(("--", GdiColor.White));
            _line1Text = "--";
            _line2Segs.Add(("--", GdiColor.White));
            _line2Text = "--";
            return;
        }

        // Line 1: aggregate 聚合摘要 — 按 (agent, state) 分组统计
        _line1Text = BuildAggregateLine(sessions);

        // Line 2: compact 逐条详情 — 含 agent 前缀 + 持续时间 + 工具名
        _line2Text = BuildCompactLine(sessions);
    }

    /// <summary>
    /// Line 1 — 聚合格式（同方案B）：
    /// "C 思考:2 工作:1  |  O 思考:1 工作:1"
    /// agent 间用灰色 "|" 分隔，状态名带颜色，数字白色
    /// </summary>
    private string BuildAggregateLine(List<SessionState> sessions)
    {
        var sb = new System.Text.StringBuilder();

        // 按 agent 分组
        var agentGroups = sessions.GroupBy(s => s.AgentId).OrderBy(g => g.Key);

        bool firstAgent = true;
        foreach (var agentGroup in agentGroups)
        {
            if (!firstAgent)
            {
                _line1Segs.Add((" |", GdiColor.Gray));
                sb.Append(" |");
            }
            firstAgent = false;

            var abbr = agentGroup.First().AgentAbbr;
            _line1Segs.Add(($" {abbr}", GdiColor.White));
            sb.Append($" {abbr}");

            // 按状态优先级排序
            var stateCounts = agentGroup
                .GroupBy(s => s.Status)
                .Select(g => new { Status = g.Key, Count = g.Count(), Priority = g.First().StatePriority })
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.Status);

            foreach (var sc in stateCounts)
            {
                var mapping = _config.Current.StateMapping.Values.FirstOrDefault(m => m.State == sc.Status);
                var name = mapping?.Abbr ?? sc.Status;
                _line1Segs.Add(($" {name}:", GetStatusColor(sc.Status)));
                _line1Segs.Add((sc.Count.ToString(), GdiColor.White));
                sb.Append($" {name}:{sc.Count}");
            }
        }

        if (sb.Length == 0)
        {
            _line1Segs.Add(("--", GdiColor.White));
            return "--";
        }
        return sb.ToString().TrimStart();
    }

    /// <summary>
    /// Line 2 — 紧凑格式（同方案B）：
    /// "C1:思考[15s]|C2:工作[Bash]|C3:空闲|O1:思考[1m]|O2:工作"
    /// 含 agent 缩写前缀、持续时间、工具类别
    /// </summary>
    private string BuildCompactLine(List<SessionState> sessions)
    {
        var sb = new System.Text.StringBuilder();
        var showDuration = _config.Current.Taskbar.ShowDuration;
        var maxSessions = _config.Current.Taskbar.Line2MaxSessions;
        var displaySessions = sessions.Take(maxSessions).ToList();
        var truncated = sessions.Count > maxSessions;

        for (int i = 0; i < displaySessions.Count; i++)
        {
            if (i > 0)
            {
                _line2Segs.Add(("|", GdiColor.Gray));
                sb.Append('|');
            }
            var s = displaySessions[i];
            var mapping = _config.Current.StateMapping.Values.FirstOrDefault(m => m.State == s.Status);
            var name = mapping?.Abbr ?? s.Status;

            // 格式: {Abbr}{Idx}:{Name}
            _line2Segs.Add(($"{s.AgentAbbr}{s.SortIndex}:", GdiColor.White));
            _line2Segs.Add((name, GetStatusColor(s.Status)));
            sb.Append(s.AgentAbbr);
            sb.Append(s.SortIndex);
            sb.Append(':');
            sb.Append(name);

            // 持续时间
            if (showDuration)
            {
                var dur = FormatDuration(s.Duration);
                _line2Segs.Add((dur, GdiColor.Gray));
                sb.Append(dur);
            }

            // 阻塞状态 + 工具类别
            if (s.IsBlocking && !string.IsNullOrEmpty(s.ToolName))
            {
                var cat = SessionState.GetToolCategory(s.ToolName);
                if (!string.IsNullOrEmpty(cat) && cat != name)
                {
                    _line2Segs.Add(($":{cat}", GdiColor.Gray));
                    sb.Append($":{cat}");
                }
            }
        }

        if (truncated)
        {
            _line2Segs.Add(($" +{sessions.Count - maxSessions}", GdiColor.Gray));
            sb.Append($" +{sessions.Count - maxSessions}");
        }

        if (sb.Length == 0)
        {
            _line2Segs.Add(("--", GdiColor.White));
            return "--";
        }
        return sb.ToString();
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

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"[{d.Hours}h{d.Minutes}m]";
        if (d.TotalMinutes >= 1) return $"[{d.Minutes}m]";
        return $"[{d.Seconds}s]";
    }

    // ═══════════════════════════════════════════
    // WndProc
    // ═══════════════════════════════════════════

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

    /// <summary>
    /// 双行渲染：上排 (Line 1) + 下排 (Line 2)，各自在半区内水平居中
    /// </summary>
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

            if (_gdiFont == null) return;

            bool isDualLine = _config.Current.Taskbar.ShowLine2;
            float fontHeight = _gdiFont.GetHeight(g);

            if (isDualLine)
            {
                float halfH = h / 2f;

                // Line 1 — 上半区居中
                DrawSegmentsInRegion(g, _line1Segs, 0, halfH, fontHeight);

                // Line 2 — 下半区居中，前面加灰色分隔线
                using var sepPen = new Pen(GdiColor.FromArgb(60, 60, 60));
                g.DrawLine(sepPen, 4, halfH, w - 4, halfH);

                DrawSegmentsInRegion(g, _line2Segs, halfH, h, fontHeight);
            }
            else
            {
                // 单行：所有 segments 合并绘制
                DrawSegmentsInRegion(g, _line1Segs, 0, h, fontHeight);
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

    private void DrawSegmentsInRegion(Graphics g, List<(string Text, GdiColor Color)> segs,
        float regionTop, float regionBottom, float fontHeight)
    {
        if (segs.Count == 0) return;
        float regionH = regionBottom - regionTop;

        float totalWidth = 0;
        foreach (var seg in segs)
            totalWidth += g.MeasureString(seg.Text, _gdiFont!, int.MaxValue,
                StringFormat.GenericTypographic).Width;

        float x = Math.Max(2, (g.VisibleClipBounds.Width - totalWidth) / 2);
        float y = regionTop + Math.Max(0, (regionH - fontHeight) / 2);

        foreach (var seg in segs)
        {
            using var brush = new SolidBrush(seg.Color);
            g.DrawString(seg.Text, _gdiFont!, brush, x, y);
            x += g.MeasureString(seg.Text, _gdiFont!, int.MaxValue,
                StringFormat.GenericTypographic).Width;
        }
    }

    // ═══════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════

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

    // ==================== P/Invoke ====================
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
