using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeMonitor.Config;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Server;

public class StateEngine
{
    private readonly ConfigManager _config;
    private int _nextSortIndex;

    public ConcurrentDictionary<string, SessionState> Sessions { get; } = new();

    public event Action<SessionState>? OnSessionUpdated;
    public event Action<SessionState>? OnSessionRemoved;
    public event Action? OnAnyChange;

    public StateEngine(ConfigManager config)
    {
        _config = config;
    }

    public SessionState? ProcessEvent(JsonElement payload)
    {
        try
        {
            var eventName = payload.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "";
            var sessionId = payload.TryGetProperty("session_id", out var sid)
                ? sid.GetString() ?? "default"
                : "default";

            // clawd-on-desk pattern: SessionEnd immediately removes the session
            if (eventName == "SessionEnd")
            {
                RemoveSession(sessionId);
                return null;
            }

            var mapping = _config.Current.StateMapping.GetValueOrDefault(eventName);
            if (mapping == null) return null;

        var state = Sessions.GetOrAdd(sessionId, _ =>
        {
            var s = new SessionState
            {
                SessionId = sessionId,
                SortIndex = Interlocked.Increment(ref _nextSortIndex)
            };
            s.StartedAt = DateTime.Now;
            return s;
        });

        state.Status = mapping.State;
        state.LastEvent = eventName;
        state.LastUpdateAt = DateTime.Now;

        if (payload.TryGetProperty("tool_name", out var tn))
            state.ToolName = tn.GetString();
        if (payload.TryGetProperty("cwd", out var cwd))
            state.Cwd = cwd.GetString();
        if (payload.TryGetProperty("session_title", out var title))
            state.SessionTitle = title.GetString();
        if (payload.TryGetProperty("source_pid", out var spid) && spid.TryGetInt32(out var sp))
            state.SourcePid = sp;
        if (payload.TryGetProperty("agent_pid", out var apid) && apid.TryGetInt32(out var ap))
            state.AgentPid = ap;

        OnSessionUpdated?.Invoke(state);
        OnAnyChange?.Invoke();
        return state;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StateEngine] ProcessEvent error: {ex.Message}");
            return null;
        }
    }

    public void RemoveSession(string sessionId)
    {
        if (Sessions.TryRemove(sessionId, out var session))
        {
            OnSessionRemoved?.Invoke(session);
            OnAnyChange?.Invoke();
        }
    }

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
}
