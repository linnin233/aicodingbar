# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ClaudeMonitor is a Windows taskbar-resident Claude Code session monitor. It embeds a colored text overlay into the Windows taskbar (via raw Win32 child window) showing real-time status of active Claude Code sessions. It also provides a WPF dashboard with session grid, config editing, and debug logging.

GitHub: https://github.com/linnin233/claude-monitor (v0.0.1-Beta released)

## Build & Run

```bash
# Build (debug)
cd ClaudeMonitor && dotnet build

# Build (release)
cd ClaudeMonitor && dotnet build -c Release

# Run
cd ClaudeMonitor && dotnet run

# Publish single-file executable (self-contained, ~147MB)
cd ClaudeMonitor && dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Requires .NET 8.0 SDK and Windows. The project uses both WPF and WinForms (`UseWPF=true`, `UseWindowsForms=true`).

## Architecture

```
ClaudeMonitor/          # .NET 8.0 WPF application
├── App.xaml.cs         # Entry point: initializes all services, wires them together
├── MainWindow.xaml     # Dashboard window (SessionGrid | ConfigPanel | DebugLog tabs)
├── Server/
│   ├── HttpStateServer.cs   # HttpListener on 127.0.0.1:23333-23337, receives POST /state
│   ├── StateEngine.cs       # Event→state mapping, ConcurrentDictionary<string, SessionState>
│   ├── HookInstaller.cs     # Runs hooks/install.js via node, reads ~/.claude/settings.json
│   └── ProcessHelper.cs     # IsProcessAlive(pid) — used by CleanupDeadSessions
├── Models/
│   ├── SessionState.cs      # SessionId, Status, ToolName, Cwd, SourcePid, SortIndex, etc.
│   └── ConfigModel.cs       # Config structure: ServerConfig, TaskbarConfig, StateMapping
├── Config/
│   └── ConfigManager.cs     # JSON read/write to ~/.clawd-monitor/config.json + runtime.json
├── Taskbar/
│   ├── NativeTaskbarText.cs # PRODUCTION: raw Win32 child window embedded in taskbar
│   ├── TaskbarRenderer.cs   # LEGACY: WPF Window version (not wired, kept for reference)
│   ├── SimpleTaskbarText.cs # LEGACY: WinForms version (not wired, kept for reference)
│   └── TrayIcon.cs          # System tray NotifyIcon with context menu + mode switching
├── Dashboard/
│   ├── SessionGrid.xaml/.cs     # DataGrid showing all sessions with filtering (all/active)
│   ├── ConfigPanel.xaml/.cs     # Event→state mapping editor, mode selector, threshold config
│   └── DebugLog.xaml/.cs        # RichTextBox log viewer + simulated event injection for testing
├── Native/
│   └── TaskbarInterop.cs    # P/Invoke helpers (used by legacy implementations, not NativeTaskbarText)
├── GlobalUsings.cs          # Resolves WPF vs WinForms type conflicts (Color, Application, etc.)
└── AssemblyInfo.cs
hooks/                  # Node.js scripts injected into Claude Code
├── install.js          # Reads ~/.claude/settings.json, adds command hooks for all events
│                         Marks with __claude_monitor__ key. install | uninstall commands.
└── claude-status-hook.js  # Reads hook JSON from stdin, POSTs to ClaudeMonitor HTTP server
                              Port discovered from ~/.clawd-monitor/runtime.json
```

## Data Flow

```
Claude Code event
  → claude-status-hook.js <EventName> (stdin JSON from Claude Code)
  → POST http://127.0.0.1:{port}/state
  → HttpStateServer.HandleRequest()
  → StateEngine.ProcessEvent(payload)
  → maps event → state via ConfigModel.StateMapping
  → updates ConcurrentDictionary<string, SessionState>
  → fires OnSessionUpdated / OnAnyChange
  → NativeTaskbarText.Refresh() redraws taskbar
  → SessionGrid updates DataGrid
```

## NativeTaskbarText — The Core Display Logic

This is the most important and complex file. It uses the "taskbar-hello" pattern (proven working C++ reference at `D:\code\taskbar-hello\src\main.cpp`):

1. **Window creation**: `RegisterClassEx` + `CreateWindowEx` with `WS_EX_TOOLWINDOW | WS_POPUP`
2. **Embedding**: `SetParent(_hwnd, _taskbarHwnd)` where `_taskbarHwnd = FindWindow("Shell_TrayWnd", null)`
3. **Positioning**: `Reposition()` every 500ms via `SetTimer(TIMER_REPOSITION)`:
   - Finds `TrayNotifyWnd` inside `Shell_TrayWnd`
   - Converts its screen coords to taskbar client coords via `ScreenToClient`
   - Positions window at `TrayNotifyWnd.left - width - 2`, vertically centered
4. **Rendering**: GDI+ via `Graphics.FromHdc(hdc)` in `WM_PAINT` handler. Renders colored text segments.
5. **Explorer restart**: Registers `WM_TASKBARCREATED`, calls `RecreateAsync()` on receipt.
6. **Flash animation**: On "attention" status, toggles window visibility 4 times (2 flashes).

Key constants: `WS_EX_TOOLWINDOW = 0x80`, `WS_POPUP = 0x80000000`, `TIMER_REPOSITION = 1`, `REPOSITION_MS = 500`

## State Machine

Events map to display states via `ConfigModel.DefaultMappings()`:

| Event | State | Display Name | Color |
|-------|-------|-------------|-------|
| SessionStart | idle | 空闲 | #888888 (gray) |
| UserPromptSubmit | thinking | 思考 | #E8A000 (yellow) |
| PreToolUse / PostToolUse | working | 工作 | #0080E0 (blue) |
| PostToolUseFailure | error | 错误 | #E04040 (red) |
| Stop | attention | 完成 | #00C030 (green) |
| StopFailure | error | 错误 | #E04040 (red) |
| SubagentStart | juggling | 调度 | #9050C0 (purple) |
| SubagentStop | working | 工作 | #0080E0 (blue) |
| PreCompact | sweeping | 清理 | #A06030 (brown) |
| PostCompact | attention | 完成 | #00C030 (green) |
| Notification | notification | 通知 | #E06090 (pink) |
| Elicitation | notification | 通知 | #E06090 (pink) |
| WorktreeCreate | carrying | 执行 | #6090C0 (light blue) |
| **SessionEnd** | **(immediate removal)** | — | — |

**SessionEnd** is special — it bypasses the mapping and calls `RemoveSession(sessionId)` directly (clawd-on-desk pattern). This ensures closed sessions disappear immediately rather than lingering as "sleeping".

## Three Display Modes

- **compact** (default): `1:思考|2:工作` — shows each session by SortIndex. Auto-switches to aggregate if sessions > `AutoSwitchThreshold` (default 7).
- **aggregate**: `思考:1|工作:2` — groups by state, shows counts.
- **highlight**: `工作 S2 +3` — shows only the most recently active session with count of others.

## Session Lifecycle

1. `SessionStart` → creates `SessionState` in `ConcurrentDictionary`, assigns `SortIndex` (monotonically increasing)
2. Events update `Status`, `LastEvent`, `LastUpdateAt`, `ToolName`, `Cwd`, etc.
3. `SessionEnd` → immediate `RemoveSession()` (from `ConcurrentDictionary`)
4. Fallback cleanup: `DispatcherTimer` every 10s calls `CleanupDeadSessions()` — checks if `AgentPid`/`SourcePid` process is alive via `ProcessHelper.IsProcessAlive()`

## Key Design Decisions

- **Three taskbar implementations exist**: `NativeTaskbarText` is the production one. `TaskbarRenderer` (WPF) and `SimpleTaskbarText` (WinForms) are earlier iterations kept as reference.
- **`TaskbarInterop.cs`** contains P/Invoke helpers used by the legacy implementations. `NativeTaskbarText` has its own P/Invoke declarations inline.
- **`GlobalUsings.cs`** resolves WPF/WinForms type conflicts (`Color`, `Application`, `UserControl`, etc.) — WPF types win.
- **Hook install is safe**: `install.js` preserves existing hooks, only adds missing entries, marks with `__claude_monitor__` key for detection/uninstall.
- **Single instance**: Mutex `Global\ClaudeMonitor` prevents duplicate instances.
- **Port discovery**: Server tries 23333→23337, writes actual port to `~/.clawd-monitor/runtime.json`. If port 23333 is occupied (e.g., by "Clawd on Desk"), falls back to next.

## Reference Projects

These sibling directories contain proven implementations that ClaudeMonitor was modeled after:

- **`D:\code\taskbar-hello\src\main.cpp`** — Working C++ taskbar text embedding. The `NativeTaskbarText.cs` pattern (SetParent to Shell_TrayWnd, ScreenToClient, 500ms timer) was directly derived from this.
- **`D:\code\clawd-on-desk\`** — JavaScript/Node.js Claude Code session monitor. Reference for session lifecycle management, especially `SessionEnd → sessions.delete(sessionId)` pattern in `src/state.js:961-978` and stale session cleanup in `src/state-stale-cleanup.js`.

## Config & Runtime Files

- `~/.clawd-monitor/config.json` — User config (server ports, taskbar mode, font, state mappings)
- `~/.clawd-monitor/runtime.json` — Written by ClaudeMonitor at startup with `{ "port": N }`, read by hooks
- `~/.claude/settings.json` — Claude Code settings, hooks injected here by `install.js`

## Testing During Development

- Use the Debug tab → "模拟事件" button to inject synthetic SessionStart events
- Use the "发送 GET" button to test HTTP server health endpoint (`GET http://127.0.0.1:{port}/state`)
- Run `node hooks/install.js install` / `node hooks/install.js uninstall` manually
- Watch the debug log for `[NTT]` prefixed messages about taskbar window positioning
- The DebugLog panel shows all events in real-time with timestamps

## Known Issues / Deferred Items

- **"通知" vs "完成"**: `Notification` and `Elicitation` events map to "notification" state (pink, "通知"). The `Stop` event maps to "attention" (green, "完成"). If the user reports seeing "通知" when they expect "完成", it means `Notification` event fired instead of `Stop`. This could be changed by remapping `Notification` → `attention` in `ConfigModel.DefaultMappings()`, or by making notification a transient auto-return-to-idle state.
- **Dashboard auto-hide**: The MainWindow hides when deactivated (loses focus) unless pinned via the pin checkbox in SessionGrid.
- **`TaskbarInterop.cs` is only used by legacy implementations** — `NativeTaskbarText` has its own P/Invoke declarations. If cleaning up, `TaskbarInterop.cs`, `TaskbarRenderer.cs`, and `SimpleTaskbarText.cs` could be removed.

## Development Notes

- The project was built iteratively: first a WinForms approach (`SimpleTaskbarText`), then a WPF approach (`TaskbarRenderer`), then the production Win32 child window approach (`NativeTaskbarText`) based on the proven `taskbar-hello` C++ code.
- The `hooks/` directory must be accessible relative to the executable. `HookInstaller.FindHooksDir()` traverses up to 6 parent directories from `AppDomain.CurrentDomain.BaseDirectory` to find it. In development (bin/Debug/net8.0-windows/), this means the repo root's `hooks/` folder is found.
