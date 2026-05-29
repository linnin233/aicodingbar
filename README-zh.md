# ClaudeMonitor

[English](README.md)

在 Windows 任务栏中实时显示 [Claude Code](https://docs.anthropic.com/en/docs/claude-code) 的会话状态。

## 效果

```
┌──────────────────────────────────────────────────────────┐
│                                                          │
│  [Claude: 1:思考 | 2:工作 | 3:完成]  [ 🔔 12:34 PM ]    │
│          └── 带颜色的状态文字 ──┘                        │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

彩色状态标签嵌入在系统托盘左侧，每个会话独立显示，颜色自动编码。

## 功能特性

- **实时任务栏指示器** — 在 Windows 任务栏中嵌入彩色状态文字，无需切换终端
- **多会话支持** — 同时追踪多个 Claude Code 会话
- **Dashboard 界面** — WPF 窗口，可浏览会话、编辑配置、查看调试日志
- **系统托盘集成** — 右键托盘图标切换显示模式、调整字号、快速退出
- **三种显示模式：**
  - `compact`（紧凑） — 逐个显示会话（`1:思考 | 2:工作 | 3:完成`）
  - `aggregate`（聚合） — 按状态分组统计（`思考:1 工作:1 完成:3`）
  - `highlight`（高亮） — 只显示最近活跃的会话
- **深色/浅色主题** — 自动检测 Windows 主题并适配背景色
- **交互工具识别** — 自动识别 `AskUserQuestion`，显示"提问"状态
- **自动安装 Hook** — 首次运行时自动将 Hook 安装到 `~/.claude/settings.json`
- **死会话清理** — 当 Claude Code 进程退出时自动移除对应会话
- **配置自动迁移** — 旧版本配置文件自动修复

## 系统要求

- **操作系统：** Windows 10 (1809+) / Windows 11
- **运行时：** [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Node.js：** 用于运行 Hook 脚本（[下载](https://nodejs.org/)）
- **Claude Code：** 已安装并配置好 [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code)

## 安装

### 方式一：下载发行版（推荐）

1. 前往 [Releases](https://github.com/linnin233/claude-monitor/releases)
2. 下载最新的 `ClaudeMonitor-vX.X.X.zip`
3. 解压到任意目录
4. 运行 `taskbar-monitor.exe`

### 方式二：从源码构建

```bash
# 前置条件：.NET 8.0 SDK + Node.js
git clone https://github.com/linnin233/claude-monitor.git
cd claude-monitor

# 构建
dotnet build taskbar-monitor -c Release

# 直接运行
dotnet run --project taskbar-monitor

# 或发布为单文件可执行程序
dotnet publish taskbar-monitor -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

发布的可执行文件位于 `taskbar-monitor/bin/Release/net8.0-windows/win-x64/publish/`。

## 使用说明

### 首次运行

1. 启动 `taskbar-monitor.exe`
2. 程序自动将 Claude Code Hook 安装到 `~/.claude/settings.json`
3. 任务栏（系统托盘左侧）出现彩色状态标签
4. 系统托盘区域出现程序图标

### 状态颜色表

| 状态   | 颜色   | 含义                         |
|--------|--------|------------------------------|
| 空闲   | 灰色   | 会话已启动，等待用户输入     |
| 思考   | 黄色   | 正在处理用户请求             |
| 工作   | 蓝色   | 正在执行工具调用             |
| 提问   | 橙色   | 等待用户响应（交互式工具）   |
| 完成   | 绿色   | 任务已完成                   |
| 错误   | 红色   | 执行出错                     |
| 调度   | 紫色   | 子代理并行执行中             |
| 清理   | 棕色   | 上下文压缩进行中             |
| 通知   | 粉色   | 通知事件                     |
| 执行   | 青色   | Worktree 操作                |
| 休眠   | 深色   | 会话已结束                   |

### 控制台快捷键

| 按键 | 功能             |
|------|------------------|
| `+`  | 增大字号         |
| `-`  | 减小字号         |
| `s`  | 显示当前状态信息 |

### 托盘菜单

右键托盘图标可进行以下操作：

- **Dashboard** — 打开会话列表/配置/调试日志界面
- **Font +/-** — 调整任务栏文字大小
- **Mode** — 切换 `compact` / `aggregate` / `highlight` 显示模式
- **Exit** — 退出程序

### Dashboard 界面

Dashboard 窗口包含三个标签页：

- **会话列表（Sessions）** — 查看所有活跃会话、状态、会话 ID、工具名和最后更新时间
- **配置（Config）** — 编辑显示模式、字体、语言和状态映射颜色
- **调试日志（Debug Log）** — 实时查看 Hook 事件和状态变化日志

## 配置

配置文件路径：`~/.aicoding-bar/config.json`

首次运行时自动创建。示例：

```jsonc
{
  "server": {
    "startPort": 23400,       // HTTP Hook 服务器起始端口
    "endPort": 23404          // HTTP Hook 服务器结束端口
  },
  "taskbar": {
    "mode": "compact",        // 显示模式：compact / aggregate / highlight
    "autoSwitchThreshold": 7, // 会话数超过此值时自动切换到 aggregate 模式
    "showZeroCounts": false,  // 聚合模式下是否显示数量为 0 的状态
    "fontName": "Microsoft YaHei UI",
    "fontSize": 11,           // 字号（6-24，单位：磅）
    "spacing": 4,             // 各段之间的像素间距
    "paddingX": 0,            // 水平内边距
    "paddingY": 0             // 垂直内边距
  },
  "language": "zh",           // "zh" 中文 / "en" 英文
  "stateMapping": {           // 事件 → 状态映射（旧版本配置自动迁移）
    "SessionStart":         { "state": "idle",         "name": "空闲", "abbr": "空闲", "color": "#888888" },
    "UserPromptSubmit":     { "state": "thinking",     "name": "思考", "abbr": "思考", "color": "#E8A000" },
    "PreToolUse":           { "state": "working",      "name": "工作", "abbr": "工作", "color": "#0080E0" },
    "Stop":                 { "state": "complete",     "name": "完成", "abbr": "完成", "color": "#00C030" }
    // ... 更多映射
  }
}
```

运行时端口写入 `~/.aicoding-bar/runtime.json`，供 Hook 脚本自动发现。

## 工作原理

```
Claude Code hook (Node.js)
    ↓ POST http://127.0.0.1:{port}/state
HTTP Hook Server (.NET HttpListener)
    ↓ 解析 JSON 数据
State Engine (ProcessEvent)
    ↓ 更新会话状态
Taskbar Window (Win32 原生子窗口)
    ↓ WM_PAINT → 彩色文字渲染
Windows 任务栏
```

1. **Hook 脚本**（`hooks/claude-status-hook.js`）在每个 Claude Code 事件触发时运行
2. 将事件数据 POST 到 `http://127.0.0.1:{port}/state`
3. **HTTP 服务器**接收并解析事件
4. **State Engine** 通过 `StateMapping` 将事件名映射为状态
5. **Taskbar Window**（一个 Win32 子窗口，父窗口为 `Shell_TrayWnd`）渲染彩色文字

## 卸载

### 移除 Hook

```bash
cd claude-monitor/hooks
node install.js uninstall
```

这会从 `~/.claude/settings.json` 中移除 ClaudeMonitor 的条目，不影响其他 Hook。

### 删除程序

1. 退出程序（右键托盘图标 → Exit）
2. 删除 `claude-monitor` 目录
3. 可选：删除配置目录 `~/.aicoding-bar/`

## 常见问题

**任务栏没有显示文字：**
- 确认程序正在运行（检查托盘图标）
- 检查是否有其他实例已在运行（单实例限制）
- 尝试重启 Explorer（程序会自动恢复）

**Hook 不工作：**
- 确认 Node.js 已安装：`node --version`
- 检查 `~/.claude/settings.json` 中是否有 `__aicoding_bar__` 键
- 手动重新安装 Hook：`node hooks/install.js install`

**端口冲突：**
- 默认尝试端口 23400-23404
- 查看 `~/.aicoding-bar/runtime.json` 获取实际使用的端口
- 在配置中修改 `startPort` / `endPort`

## 技术栈

- **C# / .NET 8** — 主程序
- **WPF** — Dashboard 界面
- **WinForms** — 系统托盘图标
- **Win32 P/Invoke** — 原生任务栏子窗口（无外部依赖）
- **Node.js** — Hook 脚本和安装器

## License

MIT
