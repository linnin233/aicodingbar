namespace AiCodingBar.Models;

public class ConfigModel
{
    public ServerConfig Server { get; set; } = new();
    public TaskbarConfig Taskbar { get; set; } = new();
    public Dictionary<string, StateMapping> StateMapping { get; set; } = DefaultMappings();
    public List<string>? HookEvents { get; set; }

    /// <summary>Per-agent 配置（claude-code / opencode / ...）</summary>
    public Dictionary<string, AgentConfig> Agents { get; set; } = new();

    /// <summary>启动时自动安装 Claude Code hooks</summary>
    public bool AutoInstallHooks { get; set; } = true;

    /// <summary>启动时自动安装 opencode plugin</summary>
    public bool AutoInstallPlugin { get; set; } = true;

    /// <summary>
    /// clawd-on-desk 对齐的默认事件→状态映射表。
    /// 状态类型说明：
    ///   Persistent — 持续到下一个事件覆盖 (idle/thinking/working/juggling)
    ///   OneShot    — 展示后定时回退 (attention/error/sweeping/carrying/notification)
    ///   Blocking   — 等待用户输入，不自动回退 (permission/question)
    /// </summary>
    public static Dictionary<string, StateMapping> DefaultMappings() => new()
    {
        ["SessionStart"]       = new("idle",         "空闲", "空闲", "#888888", StateKind: "Persistent", Priority: 1),
        ["SessionEnd"]         = new("sleeping",     "休眠", "休眠", "#555555", StateKind: "Persistent", Priority: 0),
        ["UserPromptSubmit"]   = new("thinking",     "思考", "思考", "#E8A000", StateKind: "Persistent", Priority: 2),
        ["PreToolUse"]         = new("working",      "工作", "工作", "#0080E0", StateKind: "Persistent", Priority: 3),
        ["PostToolUse"]        = new("working",      "工作", "工作", "#0080E0", StateKind: "Persistent", Priority: 3),
        ["PostToolUseFailure"] = new("error",        "错误", "错误", "#E04040", StateKind: "OneShot",    Priority: 8, AutoReturnMs: 6000),
        ["Stop"]               = new("attention",    "完成", "完成", "#00C030", StateKind: "OneShot",    Priority: 5, AutoReturnMs: 6000),
        ["StopFailure"]        = new("error",        "错误", "错误", "#E04040", StateKind: "OneShot",    Priority: 8, AutoReturnMs: 6000),
        ["SubagentStart"]      = new("juggling",     "调度", "调度", "#9050C0", StateKind: "Persistent", Priority: 4),
        ["SubagentStop"]       = new("working",      "工作", "工作", "#0080E0", StateKind: "Persistent", Priority: 3),
        ["PreCompact"]         = new("sweeping",     "清理", "清理", "#A06030", StateKind: "OneShot",    Priority: 6, AutoReturnMs: 3000),
        ["PostCompact"]        = new("attention",    "完成", "完成", "#00C030", StateKind: "OneShot",    Priority: 5, AutoReturnMs: 6000),

        // ── notification 拆分为三种 ──
        // 普通通知 → 非阻塞，6s 自动回退
        ["Notification"]       = new("notification", "通知", "通知", "#E06090", StateKind: "OneShot",    Priority: 7, AutoReturnMs: 6000),
        // 提问 Elicitation → 阻塞等待回答，不自动回退
        ["Elicitation"]        = new("question",     "提问", "提问", "#FF8040", StateKind: "Blocking",   Priority: 7),
        // 权限请求 PermissionRequest → 阻塞等待审批，不自动回退（通过 /permission 端点触发）
        ["PermissionRequest"]  = new("permission",   "权限", "权限", "#FF6080", StateKind: "Blocking",   Priority: 7),

        ["WorktreeCreate"]     = new("carrying",     "执行", "执行", "#6090C0", StateKind: "OneShot",    Priority: 4, AutoReturnMs: 3000),
    };

    /// <summary>
    /// 获取指定状态的配置元信息（StateKind / Priority / AutoReturnMs）
    /// 优先从 StateMapping 查找，找不到则返回默认值
    /// </summary>
    public static StateInfo GetStateInfo(Dictionary<string, StateMapping> mappings, string state)
    {
        foreach (var kv in mappings)
        {
            if (kv.Value.State == state)
            {
                return new StateInfo
                {
                    StateKind = Enum.TryParse<StateType>(kv.Value.StateKind, out var sk) ? sk : StateType.Persistent,
                    Priority = kv.Value.Priority,
                    AutoReturnMs = kv.Value.AutoReturnMs,
                };
            }
        }
        return state switch
        {
            "idle"         => new StateInfo { StateKind = StateType.Persistent, Priority = 1 },
            "thinking"     => new StateInfo { StateKind = StateType.Persistent, Priority = 2 },
            "working"      => new StateInfo { StateKind = StateType.Persistent, Priority = 3 },
            "juggling"     => new StateInfo { StateKind = StateType.Persistent, Priority = 4 },
            "carrying"     => new StateInfo { StateKind = StateType.OneShot,    Priority = 4, AutoReturnMs = 3000 },
            "attention"    => new StateInfo { StateKind = StateType.OneShot,    Priority = 5, AutoReturnMs = 6000 },
            "sweeping"     => new StateInfo { StateKind = StateType.OneShot,    Priority = 6, AutoReturnMs = 3000 },
            "notification" => new StateInfo { StateKind = StateType.OneShot,    Priority = 7, AutoReturnMs = 6000 },
            "permission"   => new StateInfo { StateKind = StateType.Blocking,   Priority = 7 },
            "question"     => new StateInfo { StateKind = StateType.Blocking,   Priority = 7 },
            "error"        => new StateInfo { StateKind = StateType.OneShot,    Priority = 8, AutoReturnMs = 6000 },
            "sleeping"     => new StateInfo { StateKind = StateType.Persistent, Priority = 0 },
            _              => new StateInfo { StateKind = StateType.Persistent, Priority = 1 },
        };
    }
}

public class ServerConfig
{
    public int StartPort { get; set; } = 23333;
    public int EndPort { get; set; } = 23337;
}

public class TaskbarConfig
{
    public string Mode { get; set; } = "compact"; // compact | aggregate | highlight
    public int AutoSwitchThreshold { get; set; } = 7;
    public bool ShowZeroCounts { get; set; } = false;
    public string FontName { get; set; } = "Microsoft YaHei UI";
    public float FontSize { get; set; } = 9f;

    /// <summary>紧凑模式下是否显示 session 持续时间</summary>
    public bool ShowDuration { get; set; } = true;

    /// <summary>双行模式下是否显示详情行（Line 2）</summary>
    public bool ShowLine2 { get; set; } = true;

    /// <summary>双行模式 Line 2 最多显示几个 session</summary>
    public int Line2MaxSessions { get; set; } = 8;

    /// <summary>是否根据 taskbar 高度自动计算字体大小（双行模式推荐开启）</summary>
    public bool AutoFontSize { get; set; } = true;

    /// <summary>Dashboard 窗口是否固定置顶（不自动隐藏）</summary>
    public bool PinWindow { get; set; } = true;
}

public static class ConfigExtensions
{
    public static AgentConfig GetOrAdd(this Dictionary<string, AgentConfig> dict, string key, Func<AgentConfig> factory)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            value = factory();
            dict[key] = value;
        }
        return value;
    }
}

/// <summary>
/// 事件→状态映射配置项
/// </summary>
public class StateMapping
{
    /// <summary>内部状态名：idle/thinking/working/juggling/attention/error/sweeping/carrying/notification/permission/question</summary>
    public string State { get; set; }

    /// <summary>中文显示名</summary>
    public string Name { get; set; }

    /// <summary>缩写（compact 模式用）</summary>
    public string Abbr { get; set; }

    /// <summary>颜色 hex (#RRGGBB)</summary>
    public string Color { get; set; }

    /// <summary>状态类型：Persistent / OneShot / Blocking</summary>
    public string StateKind { get; set; } = "Persistent";

    /// <summary>优先级：8=error, 7=notification/permission/question, 6=sweeping, 5=attention, 4=juggling/carrying, 3=working, 2=thinking, 1=idle, 0=sleeping</summary>
    public int Priority { get; set; } = 1;

    /// <summary>OneShot 状态的自动回退时间 (ms)，0 表示不自动回退。Blocking 状态忽略此值</summary>
    public int AutoReturnMs { get; set; } = 0;

    public StateMapping()
    {
        State = "idle";
        Name = "";
        Abbr = "IDLE";
        Color = "#888888";
    }

    public StateMapping(string state, string name, string abbr, string color,
        string StateKind = "Persistent", int Priority = 1, int AutoReturnMs = 0)
    {
        this.State = state;
        this.Name = name;
        this.Abbr = abbr;
        this.Color = color;
        this.StateKind = StateKind;
        this.Priority = Priority;
        this.AutoReturnMs = AutoReturnMs;
    }
}

/// <summary>
/// 状态的运行时元信息（从 StateMapping 解析而来）
/// </summary>
public class StateInfo
{
    public StateType StateKind { get; set; } = StateType.Persistent;
    public int Priority { get; set; } = 1;
    public int AutoReturnMs { get; set; } = 0;
}
