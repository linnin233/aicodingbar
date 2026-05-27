# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ClaudeMonitor is a Windows taskbar-resident Claude Code session monitor. It embeds a colored text overlay into the Windows taskbar (via raw Win32 child window) showing real-time status of active Claude Code sessions. It also provides a WPF dashboard with session grid, config editing, and debug logging.

## Build & Run

```bash
# Build (debug)
cd ClaudeMonitor && dotnet build

# Build (release)
cd ClaudeMonitor && dotnet build -c Release

# Run
cd ClaudeMonitor && dotnet run

# Publish single-file executable
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
│   └── HookInstaller.cs     # Runs hooks/install.js via node, reads ~/.claude/settings.json
├── Models/
│   ├── SessionState.cs      # SessionId, Status, ToolName, Cwd, SourcePid, SortIndex, etc.
│   └── ConfigModel.cs       # Config structure: ServerConfig, TaskbarConfig, StateMapping
├── Config/
│   └── ConfigManager.cs     # JSON read/write to ~/.clawd-monitor/config.json + runtime.json
├── Taskbar/
│   ├── NativeTaskbarText.cs # PRODUCTION: raw Win32 child window (RegisterClassEx/CreateWindowEx),
│   │                          SetParent into Shell_TrayWnd/ReBarWindow32, GDI+ text rendering
│   │                          with colored segments. Listens for WM_TASKBARCREATED to survive
│   │                          explorer restarts. Flash animation on attention/stop events.
│   ├── TaskbarRenderer.cs   # WPF Window version (alternative, simpler but less integrated)
│   ├── SimpleTaskbarText.cs # WinForms version (initial approach, diagnostic fallback)
│   └── TrayIcon.cs          # System tray NotifyIcon with context menu + mode switching
├── Dashboard/
│   ├── SessionGrid.xaml     # DataGrid showing all sessions with filtering (all/active)
│   ├── ConfigPanel.xaml     # Event→state mapping editor, mode selector, threshold config
│   └── DebugLog.xaml        # RichTextBox log viewer + simulated event injection for testing
└── Native/
    └── TaskbarInterop.cs    # P/Invoke helpers: FindWindow("Shell_TrayWnd"), dark mode detection
hooks/                  # Node.js scripts injected into Claude Code
├── install.js          # Reads ~/.claude/settings.json, adds command hooks for all events
│                         Marks with __claude_monitor__ key. install | uninstall commands.
└── claude-status-hook.js  # Reads hook JSON from stdin, POSTs to ClaudeMonitor HTTP server
                              Port discovered from ~/.clawd-monitor/runtime.json
```

## Data Flow

1. Claude Code fires a hook event → runs `claude-status-hook.js <event>` with JSON on stdin
2. Hook script reads port from `~/.clawd-monitor/runtime.json`, POSTs to `http://127.0.0.1:{port}/state`
3. `HttpStateServer` deserializes, passes to `StateEngine.ProcessEvent()`
4. `StateEngine` maps event name → state via `ConfigModel.StateMapping`, updates/creates `SessionState` in `ConcurrentDictionary`
5. `OnSessionUpdated` / `OnAnyChange` fires → `NativeTaskbarText.Refresh()` redraws taskbar text, `SessionGrid` updates DataGrid

## Key Design Decisions

- **Three taskbar implementations exist**: `NativeTaskbarText` is the production one (raw Win32 child window, SetParent into taskbar). The WPF `TaskbarRenderer` and WinForms `SimpleTaskbarText` are earlier iterations kept for reference but not wired in `App.xaml.cs`.
- **Session cleanup**: `DispatcherTimer` every 10s calls `CleanupDeadSessions()` which checks `ProcessHelper.IsProcessAlive()` for each session's pid.
- **Port discovery**: Server tries 23333→23337, writes actual port to `~/.clawd-monitor/runtime.json` for hooks to discover.
- **Explorer restart resilience**: `NativeTaskbarText` registers `WM_TASKBARCREATED` message, recreates the child window on explorer restart.
- **Single instance**: Mutex `Global\ClaudeMonitor` prevents duplicate instances.
- **Config hot-reload**: Changes in the ConfigPanel take effect on save via `_onConfigChanged` callback → `NativeTaskbarText.Refresh()`.
- **Hook install is safe**: `install.js` preserves existing hooks, only adds missing entries, marks with `__claude_monitor__` key for detection/uninstall.

## Testing During Development

- Use the Debug tab → "模拟事件" button to inject synthetic SessionStart events
- Use the "发送 GET" button to test HTTP server health endpoint
- Run `node hooks/install.js` manually to install/uninstall hooks
- Watch the debug log for `[NTT]` prefixed messages about taskbar window positioning
