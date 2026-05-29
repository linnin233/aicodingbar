// Agent 配置数据模型 — clawd-on-desk 风格 per-agent settings

namespace AiCodingBar.Models;

public record AgentConfig
{
    /// <summary>内部标识 e.g. "claude-code", "opencode"</summary>
    public string AgentId { get; init; } = "";

    /// <summary>显示名 e.g. "Claude Code", "opencode"</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>事件来源类型：command-hook (Claude) / plugin-event (opencode)</summary>
    public string EventSource { get; init; } = "command-hook";

    /// <summary>Agent 缩写 (C / O / ...)</summary>
    public string Abbr { get; init; } = "?";

    /// <summary>是否存在 hook/plugin 安装检测方法</summary>
    public bool CanDetect { get; init; } = true;

    /// <summary>是否支持权限处理 (PermissionRequest / permission.asked)</summary>
    public bool SupportsPermission { get; init; } = false;

    // ── 用户可配置项 ──

    /// <summary>Master toggle — 是否启用此 agent 的监控</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否显示权限状态 (仅 Claude Code / opencode)</summary>
    public bool PermissionsEnabled { get; set; } = true;

    /// <summary>是否显示通知/提问状态 (仅 Claude Code)</summary>
    public bool NotificationHookEnabled { get; set; } = true;
}

public static class AgentConfigFactory
{
    public static List<AgentConfig> GetDefaults() => new()
    {
        new AgentConfig
        {
            AgentId = "claude-code",
            DisplayName = "Claude Code",
            EventSource = "command-hook",
            Abbr = "C",
            SupportsPermission = true,
            NotificationHookEnabled = true,
        },
        new AgentConfig
        {
            AgentId = "opencode",
            DisplayName = "opencode",
            EventSource = "plugin-event",
            Abbr = "O",
            SupportsPermission = true,
            NotificationHookEnabled = false, // opencode 没有独立的 Notification 事件
        },
    };
}
