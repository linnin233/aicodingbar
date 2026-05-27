# ClaudeMonitor

在 Windows 任务栏中实时显示 Claude Code 会话状态。

## 效果

将一个彩色文本标签嵌入到 Windows 任务栏托盘图标的左侧，显示当前 Claude Code 会话的工作状态（思考/工作中/完成等）。

## 安装

需要 .NET 8.0 SDK。

```bash
# 构建
cd ClaudeMonitor && dotnet build -c Release

# 发布为单文件
cd ClaudeMonitor && dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 使用

1. 运行 `ClaudeMonitor.exe`
2. 程序会自动安装 Claude Code Hook
3. 之后每次启动 Claude Code 会话，状态会自动显示在任务栏

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

右键托盘图标可切换：

- **紧凑模式**：显示每个会话的状态
- **聚合模式**：按状态分组统计
- **高亮模式**：只显示最近活跃的会话

## 配置

配置文件位于 `~/.clawd-monitor/config.json`，支持自定义：

- 事件到状态的映射关系
- 显示颜色
- 字体和字号
- 服务器端口范围 (23333-23337)
