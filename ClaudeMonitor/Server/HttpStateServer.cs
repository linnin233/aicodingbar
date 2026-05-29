using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Config;

namespace ClaudeMonitor.Server;

/// <summary>
/// HTTP 状态服务 — 对齐 clawd-on-desk src/server.js
///
/// 路由：
///   GET  /state      — 健康检查
///   POST /state      — 状态事件（command hook → 非阻塞）
///   POST /permission — 权限请求事件（HTTP hook → 阻塞等待 Claude Code）
/// </summary>
public class HttpStateServer
{
    private readonly StateEngine _engine;
    private readonly ConfigManager _config;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _actualPort;

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
        resp.Headers.Add("X-Clawd-Monitor", "claude-monitor");

        try
        {
            var path = req.Url!.AbsolutePath;

            // ── GET /state — 健康检查 ──
            if (req.HttpMethod == "GET" && path == "/state")
            {
                var ok = Encoding.UTF8.GetBytes(
                    $"{{\"ok\":true,\"app\":\"claude-monitor\",\"port\":{_actualPort}}}");
                resp.ContentType = "application/json";
                await resp.OutputStream.WriteAsync(ok);
                resp.StatusCode = 200;
                OnLog?.Invoke($"GET /state -> 200 OK");
            }
            // ── POST /state — 状态事件（command hook）──
            else if (req.HttpMethod == "POST" && path == "/state")
            {
                await HandleStatePost(req, resp);
            }
            // ── POST /permission — 权限请求事件（HTTP hook，阻塞式）──
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
    /// 处理 POST /state — 非阻塞状态上报警告
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
    /// 处理 POST /permission — 权限请求事件（阻塞式 HTTP hook from Claude Code）
    /// 参考 clawd-on-desk src/server-route-permission.js
    ///
    /// Claude Code 的 PermissionRequest hook 会阻塞等待 HTTP 响应。
    /// 这里不阻塞 Claude Code：立即返回 allow，由 Claude Code 自行处理审批。
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

        var sessionId = doc.RootElement.TryGetProperty("session_id", out var sid)
            ? sid.GetString() ?? "default"
            : "default";
        var toolName = doc.RootElement.TryGetProperty("tool_name", out var tn)
            ? tn.GetString()
            : null;

        // 更新状态机 → "权限" 状态
        _engine.ProcessPermissionRequest(doc.RootElement);

        // 返回 allow 给 Claude Code（不阻塞）
        resp.StatusCode = 200;
        resp.ContentType = "application/json";
        var response = Encoding.UTF8.GetBytes("{\"ok\":true,\"decision\":\"allow\"}");
        await resp.OutputStream.WriteAsync(response);

        OnLog?.Invoke($"POST /permission -> 200 OK (session={sessionId}, tool={toolName})");
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
