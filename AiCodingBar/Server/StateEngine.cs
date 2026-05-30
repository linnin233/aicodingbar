using System.Collections.Concurrent;
using System.Text.Json;
using AiCodingBar.Config;
using AiCodingBar.Models;

namespace AiCodingBar.Server;

/// <summary>
/// 状态引擎 — 对齐 clawd-on-desk src/state.js + src/state-stale-cleanup.js
///
/// 核心功能：
/// 1. 事件→状态映射（含 StateKind / Priority / AutoReturnMs）
/// 2. SessionEnd 立即删除
/// 3. OneShot 状态自动回退到上一个 Persistent 状态
/// 4. Blocking 状态（permission/question）不自动回退，等待事件覆盖
/// 5. 多级 stale session 清理（agent-exit / source-exit / working-timeout / session-timeout）
/// 6. 交互式工具识别（AskUserQuestion → "提问" 等）
/// </summary>
public class StateEngine
{
    private readonly ConfigManager _config;
    private int _nextSortIndex;

    // ── Session 存储 ──
    public ConcurrentDictionary<string, SessionState> Sessions { get; } = new();

    // ── 事件 ──
    public event Action<SessionState>? OnSessionUpdated;
    public event Action<SessionState>? OnSessionRemoved;
    public event Action? OnAnyChange;

    // ── OneShot 自动回退定时器 ──
    private readonly Dictionary<string, System.Threading.Timer> _oneShotTimers = new();

    // ── Stale cleanup 常量（与 clawd-on-desk 对齐）──
    private const int SESSION_STALE_MS = 600_000;    // idle session 超时 10 分钟
    private const int WORKING_STALE_MS = 300_000;    // working/thinking/juggling 超时 5 分钟

    public StateEngine(ConfigManager config)
    {
        _config = config;
    }

    // ═══════════════════════════════════════════
    // 状态事件处理（POST /state）
    // ═══════════════════════════════════════════

    public SessionState? ProcessEvent(JsonElement payload)
    {
        try
        {
            var eventName = payload.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "";
            var sessionId = payload.TryGetProperty("session_id", out var sid)
                ? sid.GetString() ?? "default"
                : "default";

            // SessionEnd → 如果 agent 进程还活着（子 session 删除/旧版 plugin），
            // 标记为 sleeping 而非直接删除，避免用户看到断连假象。
            // 只有 agent 进程已死或 session 未设置 AgentPid 时才真正删除。
            if (eventName == "SessionEnd")
            {
                if (Sessions.TryGetValue(sessionId, out var existing) &&
                    existing.AgentPid.HasValue &&
                    ProcessHelper.IsProcessAlive(existing.AgentPid.Value))
                {
                    // 全局 _lastState 去重：避免重复的 sleeping 状态触发无意义刷新
                    existing.Status = "sleeping";
                    existing.StateKind = StateType.Persistent;
                    existing.StatePriority = 0;
                    existing.LastEvent = eventName;
                    existing.LastUpdateAt = DateTime.Now;
                    existing.OneShotStartedAt = null;
                    existing.OneShotReturnTo = null;
                    CancelOneShotTimer(sessionId);
                    OnSessionUpdated?.Invoke(existing);
                    OnAnyChange?.Invoke();
                    return existing;
                }
                RemoveSession(sessionId);
                return null;
            }

            // 事件→状态映射
            var mapping = _config.Current.StateMapping.GetValueOrDefault(eventName);
            if (mapping == null) return null;

            return ApplyStateUpdate(sessionId, mapping.State, eventName, payload, mapping);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StateEngine] ProcessEvent error: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════
    // 权限请求处理（POST /permission）
    // ═══════════════════════════════════════════

    public SessionState? ProcessPermissionRequest(JsonElement payload)
    {
        try
        {
            var sessionId = payload.TryGetProperty("session_id", out var sid)
                ? sid.GetString() ?? "default"
                : "default";

            return ApplyStateUpdate(sessionId, "permission", "PermissionRequest", payload,
                new StateMapping("permission", "权限", "权限", "#FF6080", StateKind: "Blocking", Priority: 7));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StateEngine] ProcessPermissionRequest error: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════
    // 核心：应用状态更新
    // ═══════════════════════════════════════════

    private SessionState ApplyStateUpdate(string sessionId, string resolvedState, string eventName,
        JsonElement payload, StateMapping mapping)
    {
        var info = ConfigModel.GetStateInfo(_config.Current.StateMapping, resolvedState);

        // 获取上一个状态和类型
        var previousState = Sessions.TryGetValue(sessionId, out var existing) ? existing.Status : null;
        var prevInfo = previousState != null
            ? ConfigModel.GetStateInfo(_config.Current.StateMapping, previousState)
            : null;

        // 获取或创建 session
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

        // OneShot/Blocking 状态：记录回退目标
        string? returnToStatus = null;
        if (info.StateKind == StateType.OneShot || info.StateKind == StateType.Blocking)
        {
            // 优先使用 mapping 明确指定的回退目标
            if (!string.IsNullOrEmpty(mapping.OneShotReturnState))
            {
                returnToStatus = mapping.OneShotReturnState;
            }
            // 其次使用上一个 Persistent 状态
            else if (prevInfo != null && prevInfo.StateKind == StateType.Persistent)
            {
                returnToStatus = previousState;
            }
            else if (state.OneShotReturnTo != null)
            {
                returnToStatus = state.OneShotReturnTo;
            }
            else
            {
                returnToStatus = "idle";
            }
        }

        // 更新 session
        state.Status = resolvedState;
        state.LastEvent = eventName;
        state.LastUpdateAt = DateTime.Now;
        state.StateKind = info.StateKind;
        state.StatePriority = info.Priority;
        state.OneShotStartedAt = (info.StateKind == StateType.OneShot || info.StateKind == StateType.Blocking)
            ? DateTime.Now : null;
        state.OneShotReturnTo = (info.StateKind != StateType.Persistent) ? returnToStatus : null;

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
        if (payload.TryGetProperty("agent_id", out var aid))
            state.AgentId = aid.GetString() ?? "unknown";

        // 取消旧 OneShot 定时器
        CancelOneShotTimer(sessionId);

        // 启动 OneShot 自动回退定时器
        StartOneShotTimer(sessionId, state, info);

        OnSessionUpdated?.Invoke(state);
        OnAnyChange?.Invoke();
        return state;
    }

    // ═══════════════════════════════════════════
    // Session 生命周期
    // ═══════════════════════════════════════════

    public void RemoveSession(string sessionId)
    {
        CancelOneShotTimer(sessionId);
        if (Sessions.TryRemove(sessionId, out var session))
        {
            OnSessionRemoved?.Invoke(session);
            OnAnyChange?.Invoke();
        }
    }

    // ═══════════════════════════════════════════
    // OneShot 自动回退定时器
    // ═══════════════════════════════════════════

    private void StartOneShotTimer(string sessionId, SessionState state, StateInfo info)
    {
        // 只有 OneShot 状态才启动回退定时器；Blocking 状态不自动回退
        if (info.StateKind != StateType.OneShot) return;
        if (info.AutoReturnMs <= 0) return;

        var autoReturnMs = info.AutoReturnMs;
        var expectedStatus = state.Status;

        var timer = new System.Threading.Timer(_ =>
        {
            if (!Sessions.TryGetValue(sessionId, out var s)) return;
            if (s.Status != expectedStatus) return; // 状态已被覆盖
            if (s.StateKind != StateType.OneShot) return;

            var returnTo = s.OneShotReturnTo ?? "idle";
            s.Status = returnTo;
            s.StateKind = ConfigModel.GetStateInfo(_config.Current.StateMapping, returnTo).StateKind;
            s.StatePriority = ConfigModel.GetStateInfo(_config.Current.StateMapping, returnTo).Priority;
            s.OneShotStartedAt = null;
            s.OneShotReturnTo = null;
            s.LastUpdateAt = DateTime.Now;

            OnSessionUpdated?.Invoke(s);
            OnAnyChange?.Invoke();
        }, null, TimeSpan.FromMilliseconds(autoReturnMs), Timeout.InfiniteTimeSpan);

        lock (_oneShotTimers) { _oneShotTimers[sessionId] = timer; }
    }

    private void CancelOneShotTimer(string sessionId)
    {
        lock (_oneShotTimers)
        {
            if (_oneShotTimers.TryGetValue(sessionId, out var timer))
            {
                timer.Dispose();
                _oneShotTimers.Remove(sessionId);
            }
        }
    }

    // ═══════════════════════════════════════════
    // 多级 Stale Session 清理（10s 定时器）
    // 参考 clawd-on-desk src/state-stale-cleanup.js:16-101
    // ═══════════════════════════════════════════

    public (int cleaned, int transitioned) CleanupDeadSessions()
    {
        int cleaned = 0, transitioned = 0;
        var now = DateTime.Now;

        // 快照迭代避免并发修改异常
        var entries = Sessions.ToArray();

        foreach (var (id, session) in entries)
        {
            // 1. agent-exit：agentPid 进程不存活 → 立即删除
            if (session.AgentPid.HasValue && !ProcessHelper.IsProcessAlive(session.AgentPid.Value))
            {
                RemoveSession(id);
                cleaned++;
                continue;
            }

            var age = (now - session.LastUpdateAt).TotalMilliseconds;

            // 2. working-timeout：working/thinking/juggling > 5 分钟且 sourcePid 进程已死 → 删除
            if (age > WORKING_STALE_MS)
            {
                if (session.Status is "working" or "thinking" or "juggling")
                {
                    var deadSource = session.SourcePid.HasValue
                        && !ProcessHelper.IsProcessAlive(session.SourcePid.Value);
                    if (deadSource)
                    {
                        RemoveSession(id);
                        cleaned++;
                        continue;
                    }
                    // 进程存活但卡在 working/thinking 超时 → 回退到 idle
                    session.Status = "idle";
                    session.StateKind = StateType.Persistent;
                    session.StatePriority = 1;
                    session.LastUpdateAt = now;
                    session.OneShotStartedAt = null;
                    session.OneShotReturnTo = null;
                    CancelOneShotTimer(id);
                    OnSessionUpdated?.Invoke(session);
                    transitioned++;
                    continue;
                }
            }

            // 3-6. sessionStaleMs 超时判断（idle 超过 10 分钟）
            if (age <= SESSION_STALE_MS) continue;

            if (session.AgentPid.HasValue || session.SourcePid.HasValue)
            {
                var pid = session.AgentPid ?? session.SourcePid!.Value;
                if (!ProcessHelper.IsProcessAlive(pid))
                {
                    // 3. source-exit：PID 可达但进程已死 → 删除
                    RemoveSession(id);
                    cleaned++;
                    continue;
                }
                // 4. session-timeout：进程存活 → idle 不回退，保留 session
                if (session.Status != "idle")
                {
                    session.Status = "idle";
                    session.StateKind = StateType.Persistent;
                    session.StatePriority = 1;
                    session.OneShotStartedAt = null;
                    session.OneShotReturnTo = null;
                    CancelOneShotTimer(id);
                    OnSessionUpdated?.Invoke(session);
                    transitioned++;
                }
            }
            else
            {
                // 5. unreachable / no-source：无 PID 且超时 → 删除
                RemoveSession(id);
                cleaned++;
            }
        }

        if (cleaned > 0 || transitioned > 0) OnAnyChange?.Invoke();
        return (cleaned, transitioned);
    }

    // ═══════════════════════════════════════════
    // 辅助：多 Session 优先级（aggregate 模式参考）
    // ═══════════════════════════════════════════

    public string GetDominantStatus()
    {
        string best = "idle";
        int bestPrio = 1;
        foreach (var (_, session) in Sessions)
        {
            if (session.StatePriority > bestPrio)
            {
                bestPrio = session.StatePriority;
                best = session.Status;
            }
        }
        return best;
    }

    public void Dispose()
    {
        lock (_oneShotTimers)
        {
            foreach (var timer in _oneShotTimers.Values)
                timer.Dispose();
            _oneShotTimers.Clear();
        }
    }
}
