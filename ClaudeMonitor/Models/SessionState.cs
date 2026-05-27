namespace ClaudeMonitor.Models;

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
}
