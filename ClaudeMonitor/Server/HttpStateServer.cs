using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Config;

namespace ClaudeMonitor.Server;

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
            if (req.HttpMethod == "GET" && req.Url!.AbsolutePath == "/state")
            {
                var ok = Encoding.UTF8.GetBytes("{\"ok\":true,\"app\":\"claude-monitor\",\"port\":" + _actualPort + "}");
                resp.ContentType = "application/json";
                await resp.OutputStream.WriteAsync(ok);
                resp.StatusCode = 200;
                OnLog?.Invoke($"GET /state -> 200 OK (health check)");
            }
            else if (req.HttpMethod == "POST" && req.Url!.AbsolutePath == "/state")
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);
                _engine.ProcessEvent(doc.RootElement);
                resp.StatusCode = 200;
                var evt = doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : "?";
                var sid = doc.RootElement.TryGetProperty("session_id", out var s) ? s.GetString() : "?";
                OnLog?.Invoke($"POST /state -> 200 OK (event={evt}, session={sid})");
            }
            else
            {
                resp.StatusCode = 404;
                OnLog?.Invoke($"{req.HttpMethod} {req.Url!.AbsolutePath} -> 404");
            }
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            OnLog?.Invoke($"ERROR: {ex.Message}");
        }
        finally
        {
            resp.Close();
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
