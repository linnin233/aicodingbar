using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AiCodingBar.Config;

namespace AiCodingBar.Server;

/// <summary>
/// HTTP 状态服务
///
/// 路由：
///   GET  /state      — 健康检查
///   POST /state      — 状态事件（Claude Code command hook + opencode plugin）
///   POST /permission — 权限请求事件（CC 阻塞 HTTP hook + opencode bridge forward）
/// </summary>
public class HttpStateServer
{
    private readonly StateEngine _engine;
    private readonly ConfigManager _config;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _actualPort;

    // HTTP client 用于回写 opencode permission bridge
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    public int Port => _actualPort;

    public event Action<string>? OnLog;

    public HttpStateServer(StateEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
    }

    public async Task StartAsync()
    {
        _actualPort = await FindAvailablePort();
        _config.WriteRuntimePort(_actualPort);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_actualPort}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch { if (!ct.IsCancellationRequested) continue; }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        resp.Headers.Add("X-AiCoding-Bar", "aicoding-bar");

        try
        {
            var path = req.Url!.AbsolutePath;

            // ── GET /state — 健康检查 ──
            if (req.HttpMethod == "GET" && path == "/state")
            {
                var ok = Encoding.UTF8.GetBytes(
                    $"{{\"ok\":true,\"app\":\"aicoding-bar\",\"port\":{_actualPort}}}");
                resp.ContentType = "application/json";
                await resp.OutputStream.WriteAsync(ok);
                resp.StatusCode = 200;
                OnLog?.Invoke($"GET /state -> 200 OK");
            }
            // ── POST /state — 状态事件 ──
            else if (req.HttpMethod == "POST" && path == "/state")
            {
                await HandleStatePost(req, resp);
            }
            // ── POST /permission — 权限请求事件 ──
            else if (req.HttpMethod == "POST" && path == "/permission")
            {
                await HandlePermissionPost(req, resp);
            }
            else
            {
                resp.StatusCode = 404;
                OnLog?.Invoke($"{req.HttpMethod} {path} -> 404");
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"ERROR: {ex.Message}");
            try { resp.StatusCode = 500; } catch { }
        }
        finally
        {
            try { resp.Close(); } catch { }
        }
    }

    /// <summary>
    /// 处理 POST /state — 非阻塞状态上报
    /// </summary>
    private async Task HandleStatePost(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(body);
        _engine.ProcessEvent(doc.RootElement);

        resp.StatusCode = 200;
        resp.ContentType = "application/json";
        var ok = Encoding.UTF8.GetBytes("{\"ok\":true}");
        await resp.OutputStream.WriteAsync(ok);

        var evt = doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : "?";
        var sid = doc.RootElement.TryGetProperty("session_id", out var s) ? s.GetString() : "?";
        OnLog?.Invoke($"POST /state -> 200 OK (event={evt}, session={sid})");
    }

    /// <summary>
    /// 处理 POST /permission — 权限请求事件
    ///
    /// 两种来源：
    /// 1. Claude Code — 阻塞 HTTP hook，立即返回 allow
    /// 2. opencode plugin — fire-and-forget POST，带 bridge_url/bridge_token，
    ///    回复后 fire-and-forget POST 到 bridge 回写决策
    /// </summary>
    private async Task HandlePermissionPost(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch
        {
            resp.StatusCode = 400;
            var err = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"bad json\"}");
            await resp.OutputStream.WriteAsync(err);
            return;
        }

        var root = doc.RootElement;
        var sessionId = root.TryGetProperty("session_id", out var sid)
            ? sid.GetString() ?? "default"
            : "default";
        var toolName = root.TryGetProperty("tool_name", out var tn)
            ? tn.GetString()
            : null;
        var agentId = root.TryGetProperty("agent_id", out var aid)
            ? aid.GetString()
            : null;

        // 更新状态机 → "权限" 状态
        _engine.ProcessPermissionRequest(root);

        // 立即返回（不阻塞）
        resp.StatusCode = 200;
        resp.ContentType = "application/json";
        var response = Encoding.UTF8.GetBytes("{\"ok\":true,\"decision\":\"allow\"}");
        await resp.OutputStream.WriteAsync(response);

        OnLog?.Invoke($"POST /permission -> 200 OK (agent={agentId}, session={sessionId}, tool={toolName})");

        // opencode: 有 bridge URL → 异步回写决策到 plugin 的 reverse bridge
        var bridgeUrl = root.TryGetProperty("bridge_url", out var bu) ? bu.GetString() : null;
        var bridgeToken = root.TryGetProperty("bridge_token", out var bt) ? bt.GetString() : null;
        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;

        if (!string.IsNullOrEmpty(bridgeUrl) && !string.IsNullOrEmpty(bridgeToken) && !string.IsNullOrEmpty(requestId))
        {
            _ = PostOpencodeReplyAsync(bridgeUrl, bridgeToken, requestId);
        }
    }

    /// <summary>
    /// 向 opencode plugin 的 reverse bridge POST 权限决策。
    /// Fire-and-forget — 不等待结果。
    /// </summary>
    private async Task PostOpencodeReplyAsync(string bridgeUrl, string bridgeToken, string requestId)
    {
        try
        {
            var payload = new { request_id = requestId, reply = "once" };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var msg = new HttpRequestMessage(HttpMethod.Post, $"{bridgeUrl.TrimEnd('/')}/reply")
            {
                Content = content
            };
            msg.Headers.Add("Authorization", $"Bearer {bridgeToken}");

            var result = await _httpClient.SendAsync(msg);
            OnLog?.Invoke($"[opencode] Bridge reply -> {(int)result.StatusCode} (req={requestId})");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[opencode] Bridge reply failed: {ex.Message}");
        }
    }

    private Task<int> FindAvailablePort()
    {
        var cfg = _config.Current.Server;
        for (int port = cfg.StartPort; port <= cfg.EndPort; port++)
        {
            if (IsPortAvailable(port))
                return Task.FromResult(port);
        }
        return Task.FromResult(cfg.StartPort);
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
