namespace ClaudeMonitor.Models;

/// <summary>
/// 状态类型：Persistent 持久状态，OneShot 一次性状态，Blocking 阻塞状态
/// 参考 clawd-on-desk src/state-priority.js STATE_PRIORITY + ONESHOT_STATE_NAMES
/// </summary>
public enum StateType
{
    /// <summary>持续直到被覆盖 (idle/thinking/working/juggling)</summary>
    Persistent,

    /// <summary>展示后定时回退到 persistent 状态 (attention/error/sweeping/carrying/notification)</summary>
    OneShot,

    /// <summary>阻塞等待用户输入，不自动回退 (permission/question)</summary>
    Blocking,
}

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

    // ── 新增字段 ──

    /// <summary>状态类型 (Persistent / OneShot / Blocking)</summary>
    public StateType StateKind { get; set; } = StateType.Persistent;

    /// <summary>是否为阻塞状态（等待用户输入：Elicitation/PermissionRequest）</summary>
    public bool IsBlocking => StateKind == StateType.Blocking;

    /// <summary>状态优先级 (8=error, 7=notification/permission/question, 6=sweeping, 5=attention, 4=juggling/carrying, 3=working, 2=thinking, 1=idle, 0=sleeping)</summary>
    public int StatePriority { get; set; } = 1;

    /// <summary>OneShot/Blocking 状态开始时间，用于自动回退计时</summary>
    public DateTime? OneShotStartedAt { get; set; }

    /// <summary>OneShot/Blocking 状态的自动回退目标状态</summary>
    public string? OneShotReturnTo { get; set; }

    /// <summary>会话已运行时长</summary>
    public TimeSpan Duration => DateTime.Now - StartedAt;

    /// <summary>状态已持续时长</summary>
    public TimeSpan StatusDuration => DateTime.Now - LastUpdateAt;

    /// <summary>工具名→交互类别（用于任务栏差异化展示）</summary>
    public static string GetToolCategory(string? toolName)
    {
        return toolName switch
        {
            "AskUserQuestion" => "提问",
            "Bash" or "Shell" or "Run" => "终端",
            "Edit" or "Write" => "编辑",
            "Read" => "读取",
            "Task" => "子代理",
            "Grep" or "Glob" => "搜索",
            "WebFetch" or "WebSearch" => "网页",
            _ => toolName ?? "",
        };
    }
}
