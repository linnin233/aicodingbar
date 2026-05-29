// taskbar-monitor — Integrated Claude Code session monitor for Windows taskbar
// Combines: HTTP hook server + Win32 taskbar child window + session state engine
//
// Build:  dotnet build -c Release
// Run:    dotnet run
// Publish: dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
//
// Data flow:
//   Claude Code hook → POST http://127.0.0.1:{port}/state → ProcessEvent()
//   → updates session state → InvalidateRect → WM_PATIN → colored text in taskbar

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using GdiColor = System.Drawing.Color;

namespace TaskbarMonitor;

#region Models

public class SessionState
{
    public string SessionId { get; init; } = string.Empty;
    public string Status { get; set; } = "idle";
    public string LastEvent { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public string? Cwd { get; set; }
    public string? SessionTitle { get; set; }
    public int? SourcePid { get; set; }
    public int? AgentPid { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime LastUpdateAt { get; set; } = DateTime.Now;
    public int SortIndex { get; set; }
}

public class StateMapping
{
    public string State { get; set; }
    public string Name { get; set; }
    public string Abbr { get; set; }
    public string Color { get; set; }

    public StateMapping() { State = "idle"; Name = ""; Abbr = "IDLE"; Color = "#888888"; }
    public StateMapping(string state, string name, string abbr, string color)
    {
        State = state; Name = name; Abbr = abbr; Color = color;
    }
}

public class ServerConfig
{
    public int StartPort { get; set; } = 23400;
    public int EndPort { get; set; } = 23404;
}

public class TaskbarConfig
{
    public string Mode { get; set; } = "compact";
    public int AutoSwitchThreshold { get; set; } = 7;
    public bool ShowZeroCounts { get; set; } = false;
    public string FontName { get; set; } = "Microsoft YaHei UI";
    public float FontSize { get; set; } = 11f;
    public int Spacing { get; set; } = 4;
    public int PaddingX { get; set; } = 0;
    public int PaddingY { get; set; } = 0;
}

public class Config
{
    public ServerConfig Server { get; set; } = new();
    public TaskbarConfig Taskbar { get; set; } = new();
    public string Language { get; set; } = "zh";
    public Dictionary<string, StateMapping> StateMapping { get; set; } = DefaultMappings();

    public static Dictionary<string, StateMapping> DefaultMappings() => new()
    {
        ["SessionStart"]        = new("idle",         "空闲", "空闲", "#888888"),
        ["SessionEnd"]          = new("sleeping",     "休眠", "休眠", "#555555"),
        ["UserPromptSubmit"]    = new("thinking",     "思考", "思考", "#E8A000"),
        ["PreToolUse"]          = new("working",      "工作", "工作", "#0080E0"),
        ["PostToolUseFailure"]  = new("error",        "错误", "错误", "#E04040"),
        ["Stop"]                = new("complete",     "完成", "完成", "#00C030"),
        ["StopFailure"]         = new("error",        "错误", "错误", "#E04040"),
        ["SubagentStart"]       = new("juggling",     "调度", "调度", "#9050C0"),
        ["SubagentStop"]        = new("working",      "工作", "工作", "#0080E0"),
        ["PreCompact"]          = new("sweeping",     "清理", "清理", "#A06030"),
        ["PostCompact"]         = new("complete",     "完成", "完成", "#00C030"),
        ["Notification"]        = new("notification", "通知", "通知", "#E06090"),
        ["Elicitation"]         = new("notification", "通知", "通知", "#E06090"),
        ["WorktreeCreate"]      = new("carrying",     "执行", "执行", "#6090C0"),
    };
}

#endregion

#region Config Manager

public static class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clawd-monitor");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    private static readonly string RuntimePath = Path.Combine(ConfigDir, "runtime.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<Config>(json, JsonOpts);
                if (cfg != null)
                {
                    if (cfg.StateMapping == null || cfg.StateMapping.Count == 0)
                        cfg.StateMapping = Config.DefaultMappings();
                    MigrateStateMapping(cfg);
                    return cfg;
                }
            }
        }
        catch { }
        return new Config();
    }

    // Fix known issues in saved configs from older versions
    private static void MigrateStateMapping(Config cfg)
    {
        var defaults = Config.DefaultMappings();
        bool changed = false;

        // v1: Stop used to map to "attention" — should be "complete"
        if (cfg.StateMapping.TryGetValue("Stop", out var stop) && stop.State == "attention")
        {
            cfg.StateMapping["Stop"] = defaults["Stop"];
            changed = true;
        }

        // v1: PostCompact used to map to "attention" — should be "complete"
        if (cfg.StateMapping.TryGetValue("PostCompact", out var postCompact) && postCompact.State == "attention")
        {
            cfg.StateMapping["PostCompact"] = defaults["PostCompact"];
            changed = true;
        }

        // v1: PostToolUse used to exist — remove it (no-op event, should not change state)
        if (cfg.StateMapping.Remove("PostToolUse"))
            changed = true;

        if (changed)
            Save(cfg);
    }

    public static void Save(Config config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }

    public static void WriteRuntimePort(int port)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(new { port }, JsonOpts);
        File.WriteAllText(RuntimePath, json);
    }

    public static int ReadRuntimePort()
    {
        try
        {
            if (File.Exists(RuntimePath))
            {
                var json = File.ReadAllText(RuntimePath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("port", out var port))
                    return port.GetInt32();
            }
        }
        catch { }
        return -1;
    }
}

#endregion

#region State Engine

public class StateEngine
{
    private readonly Config _config;
    private int _nextSortIndex;

    public ConcurrentDictionary<string, SessionState> Sessions { get; } = new();

    public event Action<SessionState>? OnSessionUpdated;
    public event Action<SessionState>? OnSessionRemoved;
    public event Action? OnAnyChange;

    public StateEngine(Config config) => _config = config;

    public SessionState? ProcessEvent(JsonElement payload)
    {
        try
        {
            var eventName = payload.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "";
            var sessionId = payload.TryGetProperty("session_id", out var sid)
                ? sid.GetString() ?? "default" : "default";

            // clawd-on-desk pattern: SessionEnd immediately removes
            if (eventName == "SessionEnd")
            {
                RemoveSession(sessionId);
                return null;
            }

            var mapping = _config.StateMapping.GetValueOrDefault(eventName);
            if (mapping == null) return null;

            var state = Sessions.GetOrAdd(sessionId, _ =>
            {
                var s = new SessionState
                {
                    SessionId = sessionId,
                    SortIndex = Interlocked.Increment(ref _nextSortIndex),
                    StartedAt = DateTime.Now,
                };
                return s;
            });

            state.Status = mapping.State;
            state.LastEvent = eventName;
            state.LastUpdateAt = DateTime.Now;

            if (payload.TryGetProperty("tool_name", out var tn))
            {
                state.ToolName = tn.GetString();
                // Interactive tools that wait for user input → override to attention
                if (eventName == "PreToolUse" && IsInteractiveTool(state.ToolName))
                    state.Status = "attention";
            }
            if (payload.TryGetProperty("cwd", out var cwd))
                state.Cwd = cwd.GetString();
            if (payload.TryGetProperty("session_title", out var title))
                state.SessionTitle = title.GetString();
            if (payload.TryGetProperty("source_pid", out var spid) && spid.TryGetInt32(out var sp))
                state.SourcePid = sp;
            if (payload.TryGetProperty("agent_pid", out var apid) && apid.TryGetInt32(out var ap))
                state.AgentPid = ap;

            OnSessionUpdated?.Invoke(state);
            OnAnyChange?.Invoke();
            return state;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StateEngine] ProcessEvent error: {ex.Message}");
            return null;
        }
    }

    public void RemoveSession(string sessionId)
    {
        if (Sessions.TryRemove(sessionId, out var session))
        {
            OnSessionRemoved?.Invoke(session);
            OnAnyChange?.Invoke();
        }
    }

    public void CleanupDeadSessions()
    {
        foreach (var (id, session) in Sessions)
        {
            var pid = session.AgentPid ?? session.SourcePid;
            if (pid != null && !IsProcessAlive(pid.Value))
                RemoveSession(id);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try { var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static bool IsInteractiveTool(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return false;
        return toolName switch
        {
            "AskUserQuestion" => true,
            "mcp__permission__approval_prompt" => true,
            _ => false,
        };
    }
}

#endregion

#region HTTP Server

public class HookServer
{
    private readonly StateEngine _engine;
    private readonly Config _config;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _actualPort;

    public int Port => _actualPort;
    public event Action<string>? OnLog;

    public HookServer(StateEngine engine, Config config)
    {
        _engine = engine;
        _config = config;
    }

    public async Task StartAsync()
    {
        _actualPort = await FindAvailablePort();
        ConfigManager.WriteRuntimePort(_actualPort);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_actualPort}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_cts.Token));
        OnLog?.Invoke($"Hook server started on port {_actualPort}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch { if (!ct.IsCancellationRequested) continue; }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        try
        {
            if (req.HttpMethod == "GET" && req.Url!.AbsolutePath == "/state")
            {
                var body = Encoding.UTF8.GetBytes(
                    $"{{\"ok\":true,\"app\":\"taskbar-monitor\",\"sessions\":{_engine.Sessions.Count},\"port\":{_actualPort}}}");
                resp.ContentType = "application/json";
                resp.OutputStream.Write(body);
                resp.StatusCode = 200;
            }
            else if (req.HttpMethod == "POST" && req.Url!.AbsolutePath == "/state")
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var json = reader.ReadToEnd();
                var doc = JsonDocument.Parse(json);
                var result = _engine.ProcessEvent(doc.RootElement);
                resp.StatusCode = 200;

                var evt = doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : "?";
                var sid = doc.RootElement.TryGetProperty("session_id", out var s) ? s.GetString() : "?";
                OnLog?.Invoke($"POST /state event={evt} session={sid} -> {result?.Status ?? "(no-op)"}");
            }
            else
            {
                resp.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            OnLog?.Invoke($"HTTP error: {ex.Message}");
        }
        finally
        {
            resp.Close();
        }
    }

    private async Task<int> FindAvailablePort()
    {
        for (int port = _config.Server.StartPort; port <= _config.Server.EndPort; port++)
        {
            if (await IsPortAvailable(port))
                return port;
        }
        return _config.Server.StartPort;
    }

    private static Task<bool> IsPortAvailable(int port)
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            listener.Stop();
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }
}

#endregion

#region Taskbar Window (Win32 native child window)

public class TaskbarWindow : IDisposable
{
    private readonly StateEngine _engine;
    private readonly Config _config;

    private IntPtr _hwnd;
    private string _displayText = "--";
    private List<(string Text, GdiColor Color)> _segments = new();
    private bool _disposed;
    private Font? _gdiFont;
    private Brush? _bgBrush;
    private int _windowWidth = 80;
    private int _windowHeight = 24;
    private int _flashRemaining;
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

    public event Action? OnClicked;

    private static WndProcDelegate? _wndProcDelegate;
    private static readonly Dictionary<IntPtr, TaskbarWindow> _windows = new();

    public TaskbarWindow(StateEngine engine, Config config)
    {
        _engine = engine;
        _config = config;
        _engine.OnAnyChange += Refresh;
        _engine.OnSessionUpdated += session =>
        {
            if (session.Status == "attention") StartFlash();
        };
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
    }

    public void Show()
    {
        if (_hwnd != IntPtr.Zero) { Console.WriteLine("[Taskbar] Show: already created"); return; }

        _taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (_taskbarHwnd == IntPtr.Zero) { Console.WriteLine("[Taskbar] Shell_TrayWnd not found"); return; }
        Console.WriteLine($"[Taskbar] Shell_TrayWnd found: 0x{_taskbarHwnd:X}");

        _displayText = BuildDisplayText();
        CreateGdiResources();
        MeasureSize();
        Console.WriteLine($"[Taskbar] Display text: '{_displayText}', size: {_windowWidth}x{_windowHeight}");

        var className = $"TBM_{Environment.TickCount % 100000}";
        _wndProcDelegate = WndProcStatic;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = _wndProcDelegate,
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
            hCursor = LoadCursor(IntPtr.Zero, 32512),
        };

        var atom = RegisterClassEx(ref wc);
        if (atom == 0) { Console.WriteLine($"[Taskbar] RegisterClassEx failed: {Marshal.GetLastWin32Error()}"); return; }
        Console.WriteLine($"[Taskbar] RegisterClassEx ok: class={className}, atom={atom}");

        _hwnd = CreateWindowEx(
            WS_EX_TOOLWINDOW, className, "",
            WS_POPUP,
            0, 0, _windowWidth, _windowHeight,
            IntPtr.Zero, IntPtr.Zero,
            GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) { Console.WriteLine($"[Taskbar] CreateWindowEx failed: {Marshal.GetLastWin32Error()}"); return; }

        lock (_windows) _windows[_hwnd] = this;

        if (SetParent(_hwnd, _taskbarHwnd) != IntPtr.Zero)
            _isParented = true;

        Reposition();
        SetTimer(_hwnd, TIMER_REPOSITION, REPOSITION_MS, IntPtr.Zero);
        ShowWindow(_hwnd, 8);
        Console.WriteLine($"[Taskbar] Window ready: 0x{_hwnd:X} parented={_isParented}");
    }

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _taskbarHwnd == IntPtr.Zero) return;
        var hNotify = FindWindowEx(_taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (hNotify == IntPtr.Zero) return;

        GetWindowRect(_taskbarHwnd, out var rcTb);
        GetWindowRect(hNotify, out var rcNt);

        var pt = new POINT { X = rcNt.Left, Y = rcNt.Top };
        ScreenToClient(_taskbarHwnd, ref pt);

        int x = pt.X - _windowWidth - 2;
        int y = (rcTb.Bottom - rcTb.Top - _windowHeight) / 2;
        if (x < 0) x = 2;

        SetWindowPos(_hwnd, IntPtr.Zero, x, y, _windowWidth, _windowHeight, SWP_NOACTIVATE);
    }

    private static Size MeasureText(Font font, string text)
    {
        return TextRenderer.MeasureText(text, font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    private void MeasureSize()
    {
        var padX = _config.Taskbar.PaddingX;
        var padY = _config.Taskbar.PaddingY;
        try
        {
            if (_gdiFont != null)
            {
                int totalWidth = 0;
                for (int i = 0; i < _segments.Count; i++)
                {
                    totalWidth += MeasureText(_gdiFont, _segments[i].Text).Width;
                    if (i < _segments.Count - 1) totalWidth += _config.Taskbar.Spacing;
                }
                var textH = MeasureText(_gdiFont, "Ay").Height;
                _windowWidth = totalWidth + padX * 2;
                _windowHeight = textH + padY * 2;
            }
        }
        catch { _windowWidth = _displayText.Length * 10 + padX * 2; }

        if (_windowWidth < 20) _windowWidth = 20;
        _windowHeight = Math.Max(14, _windowHeight);
    }

    private void CreateGdiResources()
    {
        _gdiFont?.Dispose();
        _bgBrush?.Dispose();

        var isDark = IsDarkMode();
        var fontSize = _config.Taskbar.FontSize > 0 ? _config.Taskbar.FontSize : 11f;
        var fontName = !string.IsNullOrEmpty(_config.Taskbar.FontName) ? _config.Taskbar.FontName : "Microsoft YaHei UI";

        _gdiFont = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        _bgBrush = new SolidBrush(isDark
            ? GdiColor.FromArgb(30, 30, 30)
            : GdiColor.FromArgb(240, 240, 240));
    }

    public void Refresh()
    {
        var newText = BuildDisplayText();
        _displayText = newText;
        CreateGdiResources();
        MeasureSize();
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            Reposition();
            InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void StartFlash()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        _flashRemaining = 4;
        SetTimer(_hwnd, TIMER_FLASH, 100, IntPtr.Zero);
    }

    private async void RecreateAsync()
    {
        if (_hwnd != IntPtr.Zero)
        {
            lock (_windows) _windows.Remove(_hwnd);
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
            lock (_windows) _windows.Remove(_hwnd);
            KillTimer(_hwnd, TIMER_REPOSITION);
            KillTimer(_hwnd, TIMER_FLASH);
            if (IsWindow(_hwnd)) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _gdiFont?.Dispose();
        _bgBrush?.Dispose();
    }

    // ---- Display text builders ----

    public string BuildDisplayText() => _config.Taskbar.Mode switch
    {
        "aggregate" => BuildAggregateText(),
        "highlight" => BuildHighlightText(),
        _ => BuildCompactText(),
    };

    // Status → label map (avoids ambiguous StateMapping lookup where
    // multiple events share the same state but have different labels)
    private static readonly Dictionary<string, (string Zh, string En)> StatusLabels = new()
    {
        ["idle"]         = ("空闲", "idle"),
        ["sleeping"]     = ("休眠", "sleeping"),
        ["thinking"]     = ("思考", "thinking"),
        ["working"]      = ("工作", "working"),
        ["error"]        = ("错误", "error"),
        ["attention"]    = ("提问", "attention"),
        ["juggling"]     = ("调度", "juggling"),
        ["sweeping"]     = ("清理", "sweeping"),
        ["notification"] = ("通知", "notification"),
        ["carrying"]     = ("执行", "carrying"),
        ["complete"]     = ("完成", "complete"),
    };

    private string GetStatusLabel(string status)
    {
        if (StatusLabels.TryGetValue(status, out var labels))
            return _config.Language == "zh" ? labels.Zh : labels.En;
        // Fallback: try StateMapping
        var mapping = _config.StateMapping.Values.FirstOrDefault(m => m.State == status);
        return !string.IsNullOrEmpty(mapping?.Name) ? mapping!.Name : (mapping?.Abbr ?? status);
    }

    private GdiColor GetColor(string state)
    {
        var mapping = _config.StateMapping.Values.FirstOrDefault(m => m.State == state);
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
        if (sessions.Count > _config.Taskbar.AutoSwitchThreshold) return BuildAggregateText();

        var sb = new StringBuilder();
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i > 0) { _segments.Add(("|", GdiColor.Gray)); sb.Append('|'); }
            var s = sessions[i];
            var name = GetStatusLabel(s.Status);
            _segments.Add(($"{s.SortIndex}:", GdiColor.White));
            _segments.Add((name, GetColor(s.Status)));
            sb.Append(s.SortIndex); sb.Append(':'); sb.Append(name);
        }
        return sb.ToString();
    }

    private string BuildAggregateText()
    {
        _segments.Clear();
        var counts = _engine.Sessions.Values.GroupBy(s => s.Status)
            .ToDictionary(g => g.Key, g => g.Count());
        var entries = _config.StateMapping.Values.DistinctBy(m => m.State).ToList();

        var sb = new StringBuilder();
        var first = true;
        foreach (var mapping in entries)
        {
            var count = counts.GetValueOrDefault(mapping.State, 0);
            if (count == 0 && !_config.Taskbar.ShowZeroCounts) continue;
            if (!first) { _segments.Add(("|", GdiColor.Gray)); sb.Append('|'); }
            first = false;
            var name = GetStatusLabel(mapping.State);
            _segments.Add(($"{name}:", GetColor(mapping.State)));
            _segments.Add((count.ToString(), GdiColor.White));
            sb.Append(name); sb.Append(':'); sb.Append(count);
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
        var name = GetStatusLabel(latest.Status);
        var extra = sessions.Count > 1 ? $" +{sessions.Count - 1}" : "";
        _segments.Add((name, GetColor(latest.Status)));
        _segments.Add(($" S{latest.SortIndex}{extra}", GdiColor.White));
        return $"{name} S{latest.SortIndex}{extra}";
    }

    // ---- WndProc ----

    private static IntPtr WndProcStatic(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        TaskbarWindow? self;
        lock (_windows) _windows.TryGetValue(hWnd, out self);
        if (self == null) return DefWindowProc(hWnd, msg, wParam, lParam);
        return self.WndProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT: Paint(hWnd); return IntPtr.Zero;
            case WM_ERASEBKGND: return (IntPtr)1;
            case WM_LBUTTONDOWN: OnClicked?.Invoke(); return IntPtr.Zero;
            case WM_RBUTTONUP: OnClicked?.Invoke(); return IntPtr.Zero;
            case WM_TIMER:
                if (wParam == (IntPtr)TIMER_REPOSITION)
                {
                    Reposition();
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                if (wParam == (IntPtr)TIMER_FLASH)
                {
                    if (_flashRemaining <= 0)
                    {
                        KillTimer(_hwnd, TIMER_FLASH);
                        ShowWindow(_hwnd, 8);
                        return IntPtr.Zero;
                    }
                    _flashRemaining--;
                    ShowWindow(_hwnd, (_flashRemaining % 2) == 0 ? 8 : 0);
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;
            default:
                if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
                {
                    Console.WriteLine("[Taskbar] Explorer restarted, recreating window");
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
                lock (_windows) _windows.Remove(hWnd);
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
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                var spacing = _config.Taskbar.Spacing;

                int totalWidth = 0;
                for (int i = 0; i < _segments.Count; i++)
                {
                    totalWidth += TextRenderer.MeasureText(_segments[i].Text, _gdiFont, Size.Empty, flags).Width;
                    if (i < _segments.Count - 1) totalWidth += spacing;
                }

                int x = Math.Max(0, (w - totalWidth) / 2);
                var textH = TextRenderer.MeasureText("Ay", _gdiFont, Size.Empty, flags).Height;
                int y = (h - textH) / 2;

                for (int i = 0; i < _segments.Count; i++)
                {
                    var seg = _segments[i];
                    TextRenderer.DrawText(g, seg.Text, _gdiFont, new System.Drawing.Rectangle(x, y, w - x, h), seg.Color, GdiColor.Transparent, flags);
                    x += TextRenderer.MeasureText(seg.Text, _gdiFont, Size.Empty, flags).Width;
                    if (i < _segments.Count - 1) x += spacing;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Taskbar] Paint error: {ex.Message}");
        }
        finally
        {
            EndPaint(hWnd, ref ps);
        }
    }

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

    // ---- Win32 P/Invoke ----

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate? lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore, fIncUpdate;
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
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string lpString);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
}

#endregion

#region Entry Point

class Program
{
    private static Mutex? _mutex;
    private static Config? _config;
    private static StateEngine? _engine;
    private static HookServer? _server;
    private static TaskbarWindow? _taskbar;
    private static System.Threading.Timer? _cleanupTimer;
    private static NotifyIcon? _trayIcon;
    private static ToolStripMenuItem? _sessionCountItem;
    private static MainWindow? _dashboard;
    private static System.Windows.Threading.Dispatcher? _wpfDispatcher;
    private static Thread? _wpfThread;
    private const uint WM_QUIT = 0x0012;

    static async Task<int> Main(string[] args)
    {
        // Single instance
        _mutex = new Mutex(true, @"Global\ClaudeMonitor", out var owned);
        if (!owned)
        {
            Console.WriteLine("taskbar-monitor is already running.");
            return 1;
        }

        Console.WriteLine("=== taskbar-monitor ===");
        Console.WriteLine("Integrated Claude Code session monitor for Windows taskbar");
        Console.WriteLine();

        // Ctrl+C handler: post WM_QUIT to main thread's message queue
        var mainThreadId = GetCurrentThreadId();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            PostThreadMessage(mainThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        // Load config
        _config = ConfigManager.Load();
        Console.WriteLine($"Config loaded. Mode={_config.Taskbar.Mode}, Font={_config.Taskbar.FontName} {_config.Taskbar.FontSize}pt");

        // State engine
        _engine = new StateEngine(_config);

        // HTTP hook server
        _server = new HookServer(_engine, _config);
        _server.OnLog += msg => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {msg}");
        await _server.StartAsync();
        Console.WriteLine($"Listening on http://127.0.0.1:{_server.Port}/state");
        Console.WriteLine($"Runtime port written to ~/.clawd-monitor/runtime.json");

        // Taskbar window
        _taskbar = new TaskbarWindow(_engine, _config);
        _taskbar.Show();
        Console.WriteLine("Taskbar window created. Click to show status.");

        // Tray icon
        InitTrayIcon();

        // Cleanup timer: check dead sessions every 10 seconds
        _cleanupTimer = new System.Threading.Timer(
            _ => _engine?.CleanupDeadSessions(),
            null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        // Ensure hooks are installed
        EnsureHooksInstalled();

        Console.WriteLine();
        Console.WriteLine("Running. Press Ctrl+C to exit.");
        Console.WriteLine("+/- : adjust font size   s : show config");
        Console.WriteLine();

        // Win32 message loop (blocks until window is destroyed)
        RunMessageLoop();

        // Cleanup
        _cleanupTimer?.Dispose();
        _taskbar?.Dispose();
        _trayIcon?.Dispose();
        _server?.Stop();
        _mutex?.ReleaseMutex();

        return 0;
    }

    private static void InitTrayIcon()
    {
        _sessionCountItem = new ToolStripMenuItem("Sessions: 0") { Enabled = false };

        // Font size menu
        var fontItem = new ToolStripMenuItem($"Font: {_config!.Taskbar.FontSize}pt");
        fontItem.Click += (s, e) => { }; // label only

        var fontUp = new ToolStripMenuItem("Font +");
        fontUp.Click += (s, e) => ChangeFontSize(1);
        var fontDown = new ToolStripMenuItem("Font -");
        fontDown.Click += (s, e) => ChangeFontSize(-1);

        // Mode sub-menu
        var modeMenu = new ToolStripMenuItem($"Mode: {_config.Taskbar.Mode}");
        foreach (var mode in new[] { "compact", "aggregate", "highlight" })
        {
            var m = new ToolStripMenuItem(mode);
            m.Click += (s, e) =>
            {
                _config.Taskbar.Mode = mode;
                ConfigManager.Save(_config);
                _taskbar!.Refresh();
                UpdateTrayMenu();
                Console.WriteLine($"Mode: {mode}");
            };
            modeMenu.DropDownItems.Add(m);
        }

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) =>
        {
            _trayIcon!.Visible = false;
            PostThreadMessage(GetCurrentThreadId(), WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        var dashboardItem = new ToolStripMenuItem("Dashboard");
        dashboardItem.Click += (s, e) => ShowDashboard();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_sessionCountItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(dashboardItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(fontItem);
        menu.Items.Add(fontUp);
        menu.Items.Add(fontDown);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(modeMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Text = "taskbar-monitor",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (s, e) => ShowDashboard();

        // Update session count periodically
        _engine!.OnAnyChange += UpdateTrayMenu;

        Console.WriteLine("Tray icon created.");
    }

    private static void UpdateTrayMenu()
    {
        if (_trayIcon == null || _config == null || _engine == null) return;
        try
        {
            var count = _engine.Sessions.Count;
            _sessionCountItem!.Text = $"Sessions: {count}";

            _trayIcon.Text = count > 0
                ? $"taskbar-monitor ({count} sessions)"
                : "taskbar-monitor";

            // Update font/mode labels (indices shifted after adding Dashboard item)
            var menu = _trayIcon.ContextMenuStrip;
            if (menu != null)
            {
                menu.Items[4].Text = $"Font: {_config.Taskbar.FontSize}pt"; // font label
                menu.Items[8].Text = $"Mode: {_config.Taskbar.Mode}";       // mode label
            }
        }
        catch { }
    }

    private static void ShowDashboard()
    {
        if (_engine == null || _config == null) return;

        if (_wpfThread == null || !_wpfThread.IsAlive)
        {
            var evt = new ManualResetEventSlim();
            _wpfThread = new Thread(() =>
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    _wpfDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    evt.Set();
                }));
                System.Windows.Threading.Dispatcher.Run();
            });
            _wpfThread.SetApartmentState(ApartmentState.STA);
            _wpfThread.IsBackground = true;
            _wpfThread.Name = "WPF-Thread";
            _wpfThread.Start();
            evt.Wait();
        }

        _wpfDispatcher!.Invoke(() =>
        {
            if (_dashboard == null)
            {
                _dashboard = new MainWindow(_engine, _config, _taskbar);
                _dashboard.Closed += (s, e) => _dashboard = null;
            }
            if (_dashboard.IsVisible)
                _dashboard.Activate();
            else
            {
                _dashboard.Show();
                _dashboard.Activate();
            }
        });
    }

    private static void RunMessageLoop()
    {
        // Message pump with console keyboard input handling
        while (true)
        {
            // Process Win32 messages (non-blocking)
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) // PM_REMOVE=1
            {
                if (msg.message == WM_QUIT) return;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            // Check console keyboard input
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    HandleKey(key);
                }
            }
            catch { /* no console available */ }

            // Small sleep to avoid busy-waiting
            Thread.Sleep(16);
        }
    }

    private static void HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.OemPlus:
            case ConsoleKey.Add:
                ChangeFontSize(1);
                break;
            case ConsoleKey.OemMinus:
            case ConsoleKey.Subtract:
                ChangeFontSize(-1);
                break;
            case ConsoleKey.S:
                ShowStatus();
                break;
        }
    }

    private static void ChangeFontSize(int delta)
    {
        if (_config == null || _taskbar == null) return;
        var newSize = _config.Taskbar.FontSize + delta;
        if (newSize < 6) newSize = 6;
        if (newSize > 24) newSize = 24;
        _config.Taskbar.FontSize = newSize;
        ConfigManager.Save(_config);
        _taskbar.Refresh();
        UpdateTrayMenu();
        Console.WriteLine($"Font size: {newSize}pt");
    }

    private static void ShowStatus()
    {
        if (_config == null || _server == null || _engine == null) return;
        Console.WriteLine($"--- Status ---");
        Console.WriteLine($"  Port: {_server.Port}");
        Console.WriteLine($"  Mode: {_config.Taskbar.Mode}");
        Console.WriteLine($"  Font: {_config.Taskbar.FontName} {_config.Taskbar.FontSize}pt");
        Console.WriteLine($"  Sessions: {_engine.Sessions.Count}");
        foreach (var s in _engine.Sessions.Values.OrderBy(s => s.SortIndex))
            Console.WriteLine($"    [{s.SortIndex}] {s.SessionId} -> {s.Status} (event: {s.LastEvent})");
        Console.WriteLine($"  Config: ~/.clawd-monitor/config.json");
        Console.WriteLine();
    }

    private static void EnsureHooksInstalled()
    {
        try
        {
            var hooksDir = FindHooksDir();
            if (hooksDir == null)
            {
                Console.WriteLine("Hooks directory not found, skipping auto-install.");
                return;
            }

            var installScript = Path.Combine(hooksDir, "install.js");
            if (!File.Exists(installScript))
            {
                Console.WriteLine($"install.js not found at {installScript}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{installScript}\" install",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(10000);
                var output = proc.StandardOutput.ReadToEnd();
                if (proc.ExitCode == 0)
                    Console.WriteLine("Hooks installed successfully.");
                else
                    Console.WriteLine($"Hook install: {output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Hook install skipped: {ex.Message}");
        }
    }

    private static string? FindHooksDir()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var hooksPath = Path.Combine(dir, "hooks");
            if (Directory.Exists(hooksPath) && File.Exists(Path.Combine(hooksPath, "install.js")))
                return hooksPath;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}

#endregion
