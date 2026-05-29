# ClaudeMonitor

[中文文档](README-zh.md)

A Windows taskbar status indicator for [Claude Code](https://docs.anthropic.com/en/docs/claude-code). Displays real-time session states directly in the system taskbar — no terminal switching needed.

## Demo

```
┌──────────────────────────────────────────────────────────┐
│                                                          │
│  [Claude: 1:思考 | 2:工作 | 3:完成]  [ 🔔 12:34 PM ]    │
│          └── colored text ──┘                            │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

The colored text label appears to the left of your system tray, showing per-session status with automatic color coding.

## Features

- **Real-time taskbar indicator** — Colored status text embedded in the Windows taskbar, left of the system tray
- **Multi-session support** — Tracks multiple Claude Code sessions simultaneously
- **Dashboard UI** — WPF window for browsing sessions, editing config, and viewing debug logs
- **System tray integration** — Right-click tray icon for mode switching, font adjustment, and quick exit
- **Three display modes:**
  - `compact` — Show each session individually (`1:思考 | 2:工作 | 3:完成`)
  - `aggregate` — Group by status (`思考:1 工作:1 完成:3`)
  - `highlight` — Show only the most recently active session
- **Dark/Light theme** — Automatically detects Windows theme and adapts background color
- **Interactive tool detection** — Recognizes `AskUserQuestion` and shows "提问" (attention) state
- **Auto hook installation** — Installs Claude Code hooks into `~/.claude/settings.json` on first run
- **Dead session cleanup** — Automatically removes sessions when the Claude Code process exits
- **Config migration** — Automatically fixes config from older versions

## Requirements

- **OS:** Windows 10 (1809+) / Windows 11
- **Runtime:** [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (Desktop Runtime)
- **Node.js:** Required for hook scripts ([Download](https://nodejs.org/))
- **Claude Code:** [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) installed and configured

## Installation

### Option 1: Download Release (Recommended)

1. Go to [Releases](https://github.com/linnin233/claude-monitor/releases)
2. Download the latest `ClaudeMonitor-vX.X.X.zip`
3. Extract to any folder
4. Run `taskbar-monitor.exe`

### Option 2: Build from Source

```bash
# Prerequisites: .NET 8.0 SDK + Node.js
git clone https://github.com/linnin233/claude-monitor.git
cd claude-monitor

# Build
dotnet build taskbar-monitor -c Release

# Run directly
dotnet run --project taskbar-monitor

# Or publish as a single-file executable
dotnet publish taskbar-monitor -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The published executable will be in `taskbar-monitor/bin/Release/net8.0-windows/win-x64/publish/`.

## Usage

### First Run

1. Launch `taskbar-monitor.exe`
2. The app automatically installs Claude Code hooks into `~/.claude/settings.json`
3. A colored status label appears in the taskbar (left of system tray)
4. A tray icon appears with a right-click context menu

### Status Display

| Status    | Color  | Meaning                              |
|-----------|--------|--------------------------------------|
| 空闲 idle | Gray   | Session started, waiting for input   |
| 思考      | Yellow | Processing user request              |
| 工作      | Blue   | Executing tool calls                 |
| 提问      | Orange | Waiting for user response (interactive tool) |
| 完成      | Green  | Task completed                       |
| 错误      | Red    | Execution error                      |
| 调度      | Purple | Subagent parallel execution          |
| 清理      | Brown  | Context compaction in progress       |
| 通知      | Pink   | Notification event                   |
| 执行      | Cyan   | Worktree operation                   |
| 休眠      | Dark   | Session ended                        |

### Keyboard Shortcuts (Console)

| Key   | Action           |
|-------|------------------|
| `+`   | Increase font size |
| `-`   | Decrease font size |
| `s`   | Show current status |

### Tray Menu

Right-click the tray icon for:

- **Dashboard** — Open the session/config/debug UI
- **Font +/-** — Adjust taskbar text size
- **Mode** — Switch between `compact` / `aggregate` / `highlight`
- **Exit** — Close the application

### Dashboard

The Dashboard window has three tabs:

- **Sessions** — View all active sessions, their status, session ID, tool name, and last update time
- **Config** — Edit display mode, font, language, and state mapping colors
- **Debug Log** — Real-time log of hook events and state changes

## Configuration

Config file: `~/.clawd-monitor/config.json`

Created automatically on first run. Example:

```jsonc
{
  "server": {
    "startPort": 23400,       // First port to try for HTTP hook server
    "endPort": 23404          // Last port to try
  },
  "taskbar": {
    "mode": "compact",        // Display mode: compact / aggregate / highlight
    "autoSwitchThreshold": 7, // Auto-switch to aggregate mode when sessions exceed this
    "showZeroCounts": false,  // Show zero-count statuses in aggregate mode
    "fontName": "Microsoft YaHei UI",
    "fontSize": 11,           // Font size in points (6-24)
    "spacing": 4,             // Pixel spacing between segments
    "paddingX": 0,            // Horizontal padding
    "paddingY": 0             // Vertical padding
  },
  "language": "zh",           // "zh" for Chinese, "en" for English
  "stateMapping": {           // Event → state mapping (auto-migrated from old versions)
    "SessionStart":         { "state": "idle",         "name": "空闲", "abbr": "空闲", "color": "#888888" },
    "UserPromptSubmit":     { "state": "thinking",     "name": "思考", "abbr": "思考", "color": "#E8A000" },
    "PreToolUse":           { "state": "working",      "name": "工作", "abbr": "工作", "color": "#0080E0" },
    "Stop":                 { "state": "complete",     "name": "完成", "abbr": "完成", "color": "#00C030" },
    // ... more mappings
  }
}
```

Runtime port is written to `~/.clawd-monitor/runtime.json` for the hook script to discover.

## How It Works

```
Claude Code hook (Node.js)
    ↓ POST http://127.0.0.1:{port}/state
HTTP Hook Server (.NET HttpListener)
    ↓ Parse JSON payload
State Engine (ProcessEvent)
    ↓ Update session state
Taskbar Window (Win32 native child window)
    ↓ WM_PAINT → colored text rendering
Windows Taskbar
```

1. **Hook script** (`hooks/claude-status-hook.js`) runs on every Claude Code event
2. It POSTs the event data to `http://127.0.0.1:{port}/state`
3. The **HTTP server** receives and parses the event
4. The **State Engine** maps event names to states via `StateMapping`
5. The **Taskbar Window** (a Win32 child window parented to `Shell_TrayWnd`) renders colored text

## Uninstall

### Remove Hooks

```bash
cd claude-monitor/hooks
node install.js uninstall
```

This removes ClaudeMonitor entries from `~/.claude/settings.json` without affecting other hooks.

### Remove Application

1. Exit the app (right-click tray icon → Exit)
2. Delete the `claude-monitor` folder
3. Optionally delete config: `~/.clawd-monitor/`

## Troubleshooting

**No text appears in the taskbar:**
- Ensure the app is running (check for tray icon)
- Check if another instance is already running (single-instance enforced)
- Try restarting Explorer (the app auto-recovers from Explorer restarts)

**Hooks not working:**
- Verify Node.js is installed: `node --version`
- Check `~/.claude/settings.json` for `__claude_monitor__` key
- Run `node hooks/install.js install` to reinstall hooks manually

**Port conflict:**
- The app tries ports 23400-23404 by default
- Check `~/.clawd-monitor/runtime.json` for the actual port
- Change `startPort` / `endPort` in config if needed

## Tech Stack

- **C# / .NET 8** — Main application
- **WPF** — Dashboard UI
- **WinForms** — System tray icon
- **Win32 P/Invoke** — Native taskbar child window (no external dependencies)
- **Node.js** — Hook script and installer

## License

MIT
