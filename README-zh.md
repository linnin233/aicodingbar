# ClaudeMonitor

在 Windows 任务栏中实时显示 Claude Code 会话状态。

## 效果

将一个彩色文本标签嵌入到 Windows 任务栏托盘图标的左侧，显示当前 Claude Code 会话的工作状态（思考/工作中/完成等）。

## 安装

需要 .NET 8.0 SDK 和 Windows 系统。

```bash
# 克隆仓库
git clone https://github.com/linnin233/claude-monitor.git
cd claude-monitor

# 构建
cd ClaudeMonitor && dotnet build -c Release

# 运行
cd ClaudeMonitor && dotnet run

# 发布为单文件可执行程序
cd ClaudeMonitor && dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 使用

1. 运行 `ClaudeMonitor.exe`
2. 程序自动安装 Claude Code Hook
3. 之后每次启动 Claude Code 会话，状态会显示在任务栏左侧
4. 左键点击托盘图标打开 Dashboard
5. 右键点击托盘图标打开菜单（切换模式/安装 Hook/退出）

## 状态说明

| 状态 | 颜色 | 含义 |
|------|------|------|
| 空闲 | 灰色 | 会话已启动，等待输入 |
| 思考 | 黄色 | 正在处理用户请求 |
| 工作 | 蓝色 | 正在执行工具调用 |
| 完成 | 绿色 | 任务完成 |
| 错误 | 红色 | 执行出错 |
| 调度 | 紫色 | 子任务并行执行中 |

## 显示模式

- **紧凑模式**：显示每个会话的状态（如 `1:工作|2:思考`）
- **聚合模式**：按状态分组统计（如 `工作:2|思考:1`）
- **高亮模式**：只显示最近活跃的会话

## 工作原理

```
Claude Code Hook → HTTP POST → ClaudeMonitor → 任务栏显示
```

1. Claude Code 触发事件 → 运行 hook 脚本，通过 stdin 传递 JSON
2. Hook 脚本读取 `~/.clawd-monitor/runtime.json` 中的端口，POST 到本地 HTTP 服务
3. `HttpStateServer` 接收事件，传递给 `StateEngine` 更新会话状态
4. `NativeTaskbarText` 通过 Win32 API（SetParent 到 Shell_TrayWnd）嵌入任务栏并渲染文本

## 配置

配置文件位于 `~/.clawd-monitor/config.json`：

```json
{
  "server": { "startPort": 23333, "endPort": 23337 },
  "taskbar": {
    "mode": "compact",
    "fontName": "Microsoft YaHei UI",
    "fontSize": 9
  }
}
```
