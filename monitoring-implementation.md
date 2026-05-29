# ClaudeMonitor — 状态监控实现文档

> 本文档详尽描述 ClaudeMonitor 项目如何检测 Claude Code 的所有运行状态（思考、工作、完成、提问、通知、权限请求等），包含当前实现覆盖度分析和缺失功能清单。

---

## 1. 项目概述

ClaudeMonitor 是一个 Windows 任务栏常驻的 Claude Code 会话状态监控器。它通过 **Hook 注入 + HTTP Server + Win32 子窗口嵌入** 三大机制，在任务栏上以彩色文字实时展示 Claude Code 所有 session 的运行状态。

### 技术栈

| 层 | 技术 |
|---|------|
| 主程序 | C# 12 / .NET 8.0 (WPF + WinForms) |
| HTTP 服务 | `System.Net.HttpListener` |
| 任务栏渲染 | Win32 P/Invoke (`SetParent` 到 `Shell_TrayWnd` + GDI+) |
| Hook 脚本 | Node.js（与 Claude Code 原生 hook 机制一致） |
| 进程探活 | `System.Diagnostics.Process.GetProcessById()` |

---

## 2. 数据流架构

```
Claude Code 生命周期事件
    │
    ▼
~/.claude/settings.json 中的 command hook
    │  node "claude-status-hook.js" <EventName> < stdin_JSON
    ▼
claude-status-hook.js
    │  1. 从 stdin 读取 Claude Code 传入的 JSON payload
    │  2. EVENT_TO_STATE 映射表查表
    │  3. 读 ~/.clawd-monitor/runtime.json 获取端口
    │  4. POST http://127.0.0.1:{port}/state
    ▼
HttpStateServer.cs (HttpListener)
    │  1. 接收 POST /state 请求
    │  2. 解析 JSON body → JsonDocument
    │  3. 调用 StateEngine.ProcessEvent()
    ▼
StateEngine.cs
    │  1. event → state 映射（ConfigModel.StateMapping）
    │  2. SessionEnd → 直接 RemoveSession()
    │  3. 其他事件 → ConcurrentDictionary 更新/创建 SessionState
    │  4. 触发 OnSessionUpdated / OnAnyChange 事件
    ▼
NativeTaskbarText.cs (Win32 子窗口)
    │  1. WM_PAINT → GDI+ 绘制彩色文字
    │  2. 每 500ms Reposition() 对齐任务栏右侧
    │  3. Explorer 重启时自动重建 (WM_TASKBARCREATED)
    ▼
Windows 任务栏显示 "1:思考 | 2:工作 | 3:完成"
```

### 数据流关键文件一览

| 文件 | 职责 |
|------|------|
| `hooks/claude-status-hook.js` | Node.js hook 脚本，接收 Claude Code 事件，POST 到 C# 服务 |
| `hooks/install.js` | 注入/卸载 hook 到 `~/.claude/settings.json` |
| `ClaudeMonitor/Server/HttpStateServer.cs` | HttpListener HTTP 服务，监听 `127.0.0.1:23333-23337` |
| `ClaudeMonitor/Server/StateEngine.cs` | 事件→状态映射 + 会话追踪 (ConcurrentDictionary) |
| `ClaudeMonitor/Server/ProcessHelper.cs` | 进程探活 (IsProcessAlive) |
| `ClaudeMonitor/Server/HookInstaller.cs` | C# 端 hook 安装器（调用 Node.js install.js） |
| `ClaudeMonitor/Models/SessionState.cs` | 会话状态数据模型 |
| `ClaudeMonitor/Models/ConfigModel.cs` | 配置模型 + 默认状态映射表 |
| `ClaudeMonitor/Config/ConfigManager.cs` | JSON 配置读写 + runtime.json 端口发布 |
| `ClaudeMonitor/App.xaml.cs` | 应用入口，组装所有服务 + 10s 清理定时器 |
| `ClaudeMonitor/Taskbar/NativeTaskbarText.cs` | 核心任务栏渲染（Win32 子窗口 + GDI+） |

---

## 3. 状态检测机制详解

### 3.1 主通道：Claude Code Command Hook

这是 **唯一** 的状态检测通道。工作方式：

1. **启动时自动安装**：`App.xaml.cs:47` 调用 `HookInstaller.EnsureInstalledAsync()`，如果 `~/.claude/settings.json` 中不存在 `__claude_monitor__` 标记，则运行 `node hooks/install.js install`

2. **Hook 注入位置**：`~/.claude/settings.json` → `hooks` 字段，针对每个事件注入 command hook：
   ```json
   {
     "__claude_monitor__": { "installed": true, "installedAt": "..." },
     "hooks": {
       "SessionStart": [
         { "matcher": "", "hooks": [
           { "type": "command", "command": "node \".../claude-status-hook.js\" SessionStart", "timeout": 5 }
         ]}
       ],
       "UserPromptSubmit": [ ... ],
       "PreToolUse": [ ... ],
       // ... 共 13 个事件
     }
   }
   ```

3. **Hook 脚本执行流程** (`claude-status-hook.js`):
   - Claude Code 在生命周期事件触发时，将事件上下文 JSON 通过 stdin 传给 hook 脚本
   - Hook 脚本解析 stdin JSON → 提取 `session_id`、`cwd`、`tool_name` 等字段
   - 查 `EVENT_TO_STATE` 映射表（行 14-30）得到 state
   - 构造 JSON body，POST 到 `http://127.0.0.1:{port}/state`
   - 超时 500ms，连接失败静默丢弃（不影响 Claude Code 主流程）

### 3.2 事件→状态映射表

当前 `claude-status-hook.js:14-30` 和 `ConfigModel.cs:10-27` 各维护一份映射（**需保持同步**）：

| Claude Code 事件 | 映射状态 | 显示名称 | 颜色 | 说明 |
|---|---|---|---|---|
| `SessionStart` | `idle` | 空闲 | #888888 | 会话创建 |
| `SessionEnd` | `sleeping` → **立即删除** | — | — | 会话结束，直接从 Sessions 移除 |
| `UserPromptSubmit` | `thinking` | 思考 | #E8A000 | 用户提交 prompt，Claude 开始思考 |
| `PreToolUse` | `working` | 工作 | #0080E0 | 工具调用前 |
| `PostToolUse` | `working` | 工作 | #0080E0 | 工具调用完成 |
| `PostToolUseFailure` | `error` | 错误 | #E04040 | 工具执行失败 |
| `Stop` | `attention` | 完成 | #00C030 | 任务完成 |
| `StopFailure` | `error` | 错误 | #E04040 | 任务异常终止 |
| `SubagentStart` | `juggling` | 调度 | #9050C0 | 子代理启动 |
| `SubagentStop` | `working` | 工作 | #0080E0 | 子代理结束 |
| `PreCompact` | `sweeping` | 清理 | #A06030 | 上下文压缩开始 |
| `PostCompact` | `attention` | 完成 | #00C030 | 上下文压缩完成 |
| `Notification` | `notification` | 通知 | #E06090 | 系统通知事件 |
| `Elicitation` | `notification` | 通知 | #E06090 | 向用户提问 (AskUserQuestion) |
| `WorktreeCreate` | `carrying` | 执行 | #6090C0 | Worktree 操作 |

### 3.3 特殊事件处理

#### SessionEnd — 立即删除
`StateEngine.cs:34-38`：`SessionEnd` 事件**不走映射表**，直接调用 `RemoveSession(sessionId)` 从 `ConcurrentDictionary` 中移除。这确保会话关闭后立即从任务栏消失，而非显示为 "休眠" 状态。

#### Elicitation — 提问弹窗
Claude Code 的 `AskUserQuestion` 工具触发 `Elicitation` 事件，映射到 `notification` 状态。这是 Claude 在执行过程中**阻塞等待用户回答**的标志。注意：claude-monitor **不做** 权限/提问气泡 UI，只是展示状态文字。

### 3.4 HTTP 服务

- **端口范围**：23333 → 23337（依次尝试，与 clawd-on-desk 共存时自动错开）
- **路由**：
  - `GET /state` → 健康检查，返回 `{"ok":true,"app":"claude-monitor","port":N}`
  - `POST /state` → 接收状态事件，调用 `StateEngine.ProcessEvent()`
- **端口发现**：启动时将实际端口写入 `~/.clawd-monitor/runtime.json`，hook 脚本从此文件读取

### 3.5 进程探活 + 死 Session 清理

`App.xaml.cs:62-63` 启动一个 10 秒间隔的 `DispatcherTimer`，调用 `StateEngine.CleanupDeadSessions()`：

```csharp
// StateEngine.cs:89-99
public void CleanupDeadSessions()
{
    foreach (var (id, session) in Sessions)
    {
        var pid = session.AgentPid ?? session.SourcePid;
        if (pid != null && !ProcessHelper.IsProcessAlive(pid.Value))
        {
            RemoveSession(id);
        }
    }
}
```

`ProcessHelper.IsProcessAlive()` 使用 `Process.GetProcessById(pid)` 判断进程存活，异常则返回 false。

### 3.6 三种显示模式

| 模式 | 格式示例 | 说明 |
|------|----------|------|
| **compact** (默认) | `1:思考 \| 2:工作 \| 3:完成` | 按 SortIndex 逐个展示，超过 AutoSwitchThreshold(7) 自动切换到 aggregate |
| **aggregate** | `思考:1 工作:1 完成:3` | 按状态分组计数 |
| **highlight** | `工作 S2 +3` | 只显示最近活跃 session + 其余计数 |

---

## 4. 与 clawd-on-desk 实现的对比分析

clawd-on-desk 是同一功能域的 Electron 桌宠项目，其状态检测机制更加完善。以下是**claude-monitor 当前缺失**的功能：

### 4.1 缺失：多 Agent 支持

| 项目 | clawd-on-desk | claude-monitor |
|------|--------------|----------------|
| 支持 Agent | Claude Code、Codex CLI、Copilot CLI、Gemini CLI、Cursor Agent、CodeBuddy、Kiro CLI、Kimi CLI、opencode、Pi、OpenClaw (共 13 个) | **仅 Claude Code** |
| Agent 注册表 | `agents/registry.js:18-32` — 完整注册表 | 无 |
| Agent Gate | `src/agent-gate.js` — 可逐个启用/禁用 | 无 |

### 4.2 缺失：Codex CLI JSONL 日志轮询

clawd-on-desk 对 Codex CLI 有**双重监控**：
1. **官方 hook**（primary）：`hooks/codex-hook.js` + `src/server-route-state.js:122-133`
2. **JSONL 日志轮询**（fallback）：`agents/codex-log-monitor.js` 每 1.5s 读取 `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`

JSONL 轮询日志事件映射（`agents/codex.js:21-36`）：

```
session_meta                    → idle
event_msg:task_started          → thinking
event_msg:guardian_assessment   → working
event_msg:exec_command_end       → working
event_msg:patch_apply_end       → working
response_item:function_call      → working
response_item:custom_tool_call   → working
response_item:web_search_call    → working
event_msg:task_complete          → codex-turn-end (内部处理)
event_msg:context_compacted      → sweeping
event_msg:turn_aborted           → idle
```

> claude-monitor 仅计划支持 Claude Code，但架构未保留多 agent 扩展点

### 4.3 缺失：权限请求检测（PermissionRequest）

clawd-on-desk 通过**阻塞式 HTTP hook** 检测权限请求：

| Agent | 检测机制 | 关键代码 |
|-------|----------|----------|
| Claude Code | `POST /permission` HTTP hook（阻塞） | `src/server-route-permission.js:157-354` |
| Codex CLI | 官方 `PermissionRequest` command hook → `POST /permission` | `hooks/codex-hook.js:311+` |
| Codex JSONL | 2s 超时启发式推断 + 显式 `escalated` 检测 | `agents/codex-log-monitor.js:453-481` |
| Kimi CLI | 显式许可信号 / suspect 延迟模式 | `src/state.js:1284` |
| opencode | event hook + reverse bridge | `src/server-route-permission.js:275-277` |

> claude-monitor **不处理 `/permission` 端点**，因此无法感知 Claude Code 的权限请求（如执行 shell 命令前需要用户确认）。权限请求发生时，任务栏只会显示 `notification`（如果有 Elicitation 事件）或保持在当前状态不变。

### 4.4 缺失：提问弹窗细分（Elicitation vs Notification vs PermissionRequest）

clawd-on-desk 的 `notification` 状态实际上覆盖了 **三个不同子场景**：

| 子场景 | 事件来源 | HTTP 端点 | Bubble UI | 阻塞？ |
|--------|----------|-----------|-----------|--------|
| **Elicitation** (提问) | Claude Code `AskUserQuestion` 工具 | `POST /state` (command hook) | 问题卡片 (标题/选项/Other) | 是（阻塞 Claude Code） |
| **PermissionRequest** (权限) | 工具需审批（shell/write 等） | `POST /permission` (HTTP hook) | Allow/Deny 按钮 | 是（阻塞 Claude Code） |
| **Notification** (通知) | 系统通知事件 | `POST /state` (command hook) | 纯提示 + 关闭按钮 | 否 |

这三者在 claude-monitor 中都映射到 `notification` 状态，但缺乏区分能力。**尤其是 `AskUserQuestion` (Elicitation) 是阻塞式提问**，如果只显示 "通知" 两个字，用户无法知道 Claude 正在等待回答。

### 4.5 缺失：Kimi CLI 的延迟许可检测（Permission Suspect Timer）

clawd-on-desk 对 Kimi CLI 有两种许可模式：
- **explicit 模式**：payload 中有 `permission_required` / `requires_approval` / `waiting_for_approval` 字段 → 直接 `notification`
- **suspect 模式**：`PreToolUse` 时启动 800ms 延迟定时器 → 如果 `PostToolUse` 在 800ms 内完成（自动批准），则取消定时器，不闪 notification → 如果超时未取消，说明 Kimi 可能还在等待用户确认 → 提升为 notification 状态

关键代码：`src/state.js:144-150` (parseSuspectDelay) + `src/state.js:1284-1357` (startKimiPermissionPoll / schedulePermissionSuspect)

> claude-monitor 无此逻辑

### 4.6 缺失：Stale Session 多级超时清理

claude-monitor 的清理只有一种：**进程不存活 → 删除**。

clawd-on-desk 有 **6 种**清理决策（`src/state-stale-cleanup.js:16-101`）：

| 条件 | 动作 | 原因 | 超时 |
|------|------|------|------|
| agentPid 可达 & agent 进程不存活 | **delete** | agent-exit | 立即 |
| PID 可达 & sourcePid 进程不存活 | **delete** | source-exit | 600s (sessionStaleMs) |
| idle 状态超过 600s & PID 不可达 | **delete** | unreachable | 600s |
| idle 状态超过 600s & PID 可达但 process 存活 | **→ idle** | session-timeout | 600s |
| working/thinking/juggling 超过 300s | **→ idle** | working-timeout | 300s (workingStaleMs) |
| requiresCompletionAck 挂起超过 24h | **delete** | ack-expired | 86400s |

> claude-monitor 缺失 "working/thinking 超 5 分钟自动回 idle" 和 "idle 超 10 分钟自动清理" 逻辑，可能导致卡死状态的 session 永远停留在任务栏上

### 4.7 缺失：多 Session 优先级合并

当多个 session 同时活跃时，clawd-on-desk 使用优先级算法（`src/state-priority.js:42-58`）选择最重要的状态展示：

```
error(8) > notification(7) > sweeping(6) > attention(5)
  > carrying(4) = juggling(4) > working(3) > thinking(2)
    > idle(1) > sleeping(0)
```

claude-monitor 采用**每个 session 独立显示**（compact 模式）或**按状态分组计数**（aggregate 模式），不做优先级合并。这对于任务栏场景通常是正确的（用户想看所有 session），但 aggregate 模式丢失了优先级信息。

### 4.8 缺失：Session 持续时间追踪

`SessionState` 模型中有 `StartedAt` 字段但**未使用**，任务栏不显示每个 session 的运行时长。clawd-on-desk 在 SessionHUD 中显示 badge 和持续时间。

### 4.9 缺失：Session 名称提取

clawd-on-desk 会从 `transcript_path` 解析用户自定义标题和 prompt 摘要（`hooks/clawd-hook.js:205-212`）：
- 从 transcript 尾部扫描 `custom-title` / `agent-name` 事件
- 从 UserPromptSubmit payload 提取 prompt 摘要作为 session 名称

claude-monitor 只回传 `session_title` 字段（如果 payload 中有），不做主动提取。

### 4.10 缺失：交互式工具识别

claude-monitor 的 `StateEngine.ProcessEvent()` 会提取 `tool_name` 存入 `SessionState.ToolName`，但**未对特定工具做差异化展示**。clawd-on-desk 会在 `tool_name === "AskUserQuestion"` 时做特殊处理。

### 4.11 缺失：One-Shot 状态自动回退

clawd-on-desk 的 ONEHOT 状态 (`attention`, `error`, `sweeping`, `notification`, `carrying`) 展示完后会自动回退到 persistent 状态：
- `attention` → 6s 后回退到 working/idle
- `error` → 6s 后回退
- `notification` → 6s 后回退（如有 auto-dismiss 配置）

claude-monitor 不区分 ONEHOT 和 PERSISTENT 状态，所有状态都持续到下一个事件到达。

---

## 5. 实现建议

### 5.1 高优先级（影响理解）

#### 5.1.1 添加 `/permission` 端点 + Elicitation 细分

当前最影响体验的缺失：用户无法区分 "通知" 和 "提问等待"。

**建议映射：**

```
Elicitation         → "提问" (橙色 #E08000) — 阻塞等待回答
PermissionRequest   → "权限" (粉色 #E06090) — 阻塞等待审批
Notification        → "通知" (浅粉 #E0A0B0) — 非阻塞提示
```

实现要点：
1. `HttpStateServer.cs` 添加 `POST /permission` 路由
2. `StateEngine.cs` 新增 `ProcessPermissionRequest()` 方法
3. `SessionState` 新增 `IsBlocking` 字段，标识阻塞式交互

#### 5.1.2 Working/Thinking 超时自动回 idle

clawd-on-desk 的做法：working/thinking/juggling 超过 300s 自动回 idle。

```csharp
// 在 CleanupDeadSessions() 中增加超时判断
if (session.Status is "working" or "thinking" or "juggling")
{
    if ((DateTime.Now - session.LastUpdateAt).TotalSeconds > 300)
    {
        session.Status = "idle";
        OnSessionUpdated?.Invoke(session);
        OnAnyChange?.Invoke();
    }
}
```

#### 5.1.3 Idle Session 超时自动清理

idle 状态超过 600s 且进程已死 → 删除；进程存活 → 保留。

### 5.2 中优先级（提升体验）

#### 5.2.1 Session 持续时间显示

compact 模式中增加持续时间：

```
1:思考[2m] | 2:工作[15s] | 3:完成[5s]
```

在 `NativeTaskbarText.cs` 的渲染循环中使用 `DateTime.Now - session.StartedAt`。

#### 5.2.2 阻塞状态闪烁

`Elicitation` 和 `PermissionRequest` 是阻塞式的 — Claude Code 被卡住等待输入。建议任务栏文字闪烁（已有 `attention` 闪烁机制，可复用）。

#### 5.2.3 交互式工具名显示

当 `tool_name` 为特定值时的替换显示：

| tool_name | 显示 |
|-----------|------|
| `AskUserQuestion` | 提问 |
| `Bash` / `Shell` | 终端 |
| `Edit` / `Write` | 编辑 |
| `Read` | 读取 |
| `Task` (subagent) | 子代理 |
| `Grep` / `Glob` | 搜索 |

### 5.3 低优先级（扩展性）

#### 5.3.1 多 Agent 架构扩展点

如果未来支持多 Agent（Codex、Gemini 等），建议：
- `SessionState` 增加 `AgentId` 字段
- 替换 `ConcurrentDictionary<string, SessionState>` 的 key 为 `{agentId}|{sessionId}` 复合键
- 每个 Agent 可配置独立的端口或处理路径

#### 5.3.2 JSONL 日志轮询（Codex CLI fallback）

如需支持 Codex CLI，参考 clawd-on-desk 的 `agents/codex-log-monitor.js` 实现：
- `FileSystemWatcher` 监听 `~/.codex/sessions/` 目录
- 逐行解析 JSONL，映射到状态
- 2s 超时启发式推断审批请求

---

## 6. 完整状态机速查

### 6.1 状态优先级（clawd-on-desk 定义）

```
error(8) > notification(7) > sweeping(6) > attention(5)
  > carrying(4) = juggling(4) > working(3) > thinking(2)
    > idle(1) > sleeping(0)
```

### 6.2 所有状态的触发事件和持续时间

| 状态 | 优先级 | 类型 | 触发事件 | 默认持续时间 | 回退到 |
|------|--------|------|----------|-------------|--------|
| `error` | 8 | ONESHOT | PostToolUseFailure, StopFailure | 6s | 上一个 persistent |
| `notification` | 7 | ONESHOT | Elicitation, PermissionRequest, Notification | 6s (可配) | 上一个 persistent |
| `sweeping` | 6 | ONESHOT | PreCompact | 3s | 上一个 persistent |
| `attention` | 5 | ONESHOT | Stop, PostCompact | 6s | working/idle |
| `carrying` | 4 | ONESHOT | WorktreeCreate | 3s | 上一个 persistent |
| `juggling` | 4 | PERSISTENT | SubagentStart (2+) | — | — |
| `working` | 3 | PERSISTENT | PreToolUse, PostToolUse | — | — |
| `thinking` | 2 | PERSISTENT | UserPromptSubmit | — | — |
| `idle` | 1 | PERSISTENT | SessionStart | — | — |
| `sleeping` | 0 | PERSISTENT | 鼠标 60s 无活动 | — | — |

### 6.3 完整事件→状态映射（clawd-on-desk 全集）

```
SessionStart         → idle
SessionEnd           → sleeping (或 sweeping 当 source===clear)
                          claude-monitor: 直接删除 session
UserPromptSubmit     → thinking
PreToolUse           → working (或 juggling 当 tool_name===Task)
PostToolUse          → working
PostToolUseFailure   → error
Stop                 → attention
StopFailure          → error
SubagentStart        → juggling
SubagentStop         → working
PreCompact           → sweeping
PostCompact          → attention
Notification         → notification
Elicitation          → notification
WorktreeCreate       → carrying
PermissionRequest    → notification (通过 /permission 端点)
```

### 6.4 特殊重映射规则（clawd-on-desk）

| 条件 | 重映射 |
|------|--------|
| `SessionEnd` + `source === "clear"` | `sleeping` → `sweeping` |
| `PreToolUse` + `tool_name === "Task"` | `working` → `juggling` (合成 SubagentStart) |
| Codex Stop + turn 中有 toolUse | → `attention`（有工具执行 → 完成庆祝） |
| Codex Stop + turn 中无 toolUse | → `idle`（无工具执行 → 仅对话结束） |
| Kimi PreToolUse + permission suspect | 800ms 后 → `notification`（若 PostToolUse 未及时到达） |

---

## 7. 配置系统

### 7.1 运行时文件

| 文件 | 内容 | 写入者 | 读取者 |
|------|------|--------|--------|
| `~/.claud-monitor/config.json` | 用户配置（端口、显示模式、字体、映射表） | ConfigManager.Save() | ConfigManager.Load() |
| `~/.clawd-monitor/runtime.json` | `{"port": 23333}` | HttpStateServer.StartAsync() | claude-status-hook.js |

### 7.2 Hook 安装标记

`~/.claude/settings.json` 中通过 `__claude_monitor__` 字段标识已安装。`HookInstaller.cs:15-22` 检查此标记判断 hook 是否已安装。

---

## 8. 当前问题汇总

| # | 问题 | 严重程度 | 建议 |
|---|------|----------|------|
| 1 | Elicitation (AskUserQuestion) 和 Notification 无法区分，都显示 "通知" | **高** | 拆分为 "提问" + "通知" |
| 2 | PermissionRequest 完全不感知（无 /permission 端点） | **高** | 添加 /permission 端点 |
| 3 | working/thinking 卡死后不回 idle，永久停留在任务栏 | **中** | 300s 超时自动 idle |
| 4 | idle session 永不自动清理（仅靠进程死） | **中** | 600s 超时 + 进程探活双重判断 |
| 5 | 无 session 持续时间显示 | **低** | 任务栏文字中追加时长 |
| 6 | 多 Agent 不支持 | **低** | 保留 SessionState.AgentId 扩展点 |
| 7 | Session 名称未自动提取 | **低** | 从 prompt/transcript 解析 |
| 8 | ONEHOT 状态无自动回退 | **低** | attention/error/notification 定时回退 |
