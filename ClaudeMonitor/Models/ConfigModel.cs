namespace ClaudeMonitor.Models;

public class ConfigModel
{
    public ServerConfig Server { get; set; } = new();
    public TaskbarConfig Taskbar { get; set; } = new();
    public Dictionary<string, StateMapping> StateMapping { get; set; } = DefaultMappings();
    public List<string>? HookEvents { get; set; }

    public static Dictionary<string, StateMapping> DefaultMappings() => new()
    {
        ["SessionStart"] = new("idle", "空闲", "空闲", "#888888"),
        ["SessionEnd"] = new("sleeping", "休眠", "休眠", "#555555"),
        ["UserPromptSubmit"] = new("thinking", "思考", "思考", "#E8A000"),
        ["PreToolUse"] = new("working", "工作", "工作", "#0080E0"),
        ["PostToolUse"] = new("working", "工作", "工作", "#0080E0"),
        ["PostToolUseFailure"] = new("error", "错误", "错误", "#E04040"),
        ["Stop"] = new("attention", "完成", "完成", "#00C030"),
        ["StopFailure"] = new("error", "错误", "错误", "#E04040"),
        ["SubagentStart"] = new("juggling", "调度", "调度", "#9050C0"),
        ["SubagentStop"] = new("working", "工作", "工作", "#0080E0"),
        ["PreCompact"] = new("sweeping", "清理", "清理", "#A06030"),
        ["PostCompact"] = new("attention", "完成", "完成", "#00C030"),
        ["Notification"] = new("notification", "通知", "通知", "#E06090"),
        ["Elicitation"] = new("notification", "通知", "通知", "#E06090"),
        ["WorktreeCreate"] = new("carrying", "执行", "执行", "#6090C0"),
    };
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
}

public class StateMapping
{
    public string State { get; set; }
    public string Name { get; set; }
    public string Abbr { get; set; }
    public string Color { get; set; }

    public StateMapping() { State = "idle"; Name = ""; Abbr = "IDLE"; Color = "#888888"; }
    public StateMapping(string state, string name, string abbr, string color)
    {
        State = state;
        Name = name;
        Abbr = abbr;
        Color = color;
    }
}
