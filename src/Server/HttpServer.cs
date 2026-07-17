using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sas.Auth;
using Sas.Logging;

namespace Sas.Server;

sealed class HttpServer
{
    private readonly string _addr;
    private readonly int _port;
    private readonly int _maxBodyBytes;
    private readonly SemaphoreSlim _concurrencyLimit;
    private readonly HttpAuthHandler _handler;
    private readonly IResponseTemplate _template;
    private HttpListener? _listener;

    public HttpServer(string addr, int port, int maxBodyBytes, int maxConcurrent,
        HttpAuthHandler handler, IResponseTemplate template)
    {
        _addr = addr;
        _port = port;
        _maxBodyBytes = maxBodyBytes;
        _concurrencyLimit = new SemaphoreSlim(maxConcurrent);
        _handler = handler;
        _template = template;
    }

    public void Start()
    {
        var host = _addr;
        if (host == "0.0.0.0" || host == "*" || host == "+")
            host = "+";

        var prefix = $"http://{host}:{_port}/auth/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5 || ex.ErrorCode == 50)
        {
            Logger.Error($"HTTP auth: cannot bind to {prefix} (error {ex.ErrorCode})");
            Logger.Error($"  Run as admin or grant URL ACL:");
            Logger.Error($"  netsh http add urlacl url=http://+:{_port}/auth/ user=Everyone");
            throw;
        }
        Logger.Info("HTTP auth server ready");
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_listener == null)
            throw new InvalidOperationException("Not started");

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = HandleRequestAsync(ctx);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        if (!await _concurrencyLimit.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }
        try
        {
            await HandleRequestCoreAsync(ctx);
        }
        finally
        {
            _concurrencyLimit.Release();
        }
    }

    private async Task HandleRequestCoreAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod != "POST" ||
                !ctx.Request.Url!.AbsolutePath.TrimEnd('/').EndsWith("/auth", StringComparison.OrdinalIgnoreCase))
            {
                await WriteDenyResponse(ctx.Response);
                return;
            }

            var body = await ReadRequestBodyAsync(ctx.Request);
            if (body == null)
            {
                ctx.Response.StatusCode = 413;
                ctx.Response.Close();
                return;
            }

            JsonElement json;
            try
            {
                json = JsonDocument.Parse(body).RootElement;
            }
            catch
            {
                await WriteDenyResponse(ctx.Response);
                return;
            }

            var username = json.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "";
            var password = json.TryGetProperty("password", out var pw) ? pw.GetString() ?? "" : "";

            Logger.Debug($"HTTP auth: received request username={username} password.length={password.Length}");

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                Logger.Debug("HTTP auth: empty username or password, denying");
                await WriteDenyResponse(ctx.Response);
                return;
            }

            object responseBody;
            try
            {
                var result = _handler.Process(username, password);
                responseBody = _template.BuildAllow(result);
            }
            catch (AuthDeniedException)
            {
                responseBody = _template.BuildDeny();
            }
            catch (Exception ex)
            {
                Logger.Error($"HTTP auth: internal error: {ex}");
                responseBody = _template.BuildDeny();
            }

            var responseJson = JsonSerializer.Serialize(responseBody,
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var buffer = Encoding.UTF8.GetBytes(responseJson);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buffer.Length;
            await ctx.Response.OutputStream.WriteAsync(buffer);
            ctx.Response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"HTTP auth: request handling error: {ex}");
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch { }
        }
    }

    private async Task<string?> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        var length = request.ContentLength64;
        if (length > _maxBodyBytes)
            return null;

        int maxRead = length > 0 ? (int)length : _maxBodyBytes;

        var buffer = new byte[maxRead];
        var totalRead = 0;
        while (totalRead < maxRead)
        {
            var read = await request.InputStream.ReadAsync(
                buffer, totalRead, Math.Min(maxRead - totalRead, 4096));
            if (read == 0) break;
            totalRead += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, totalRead);
    }

    private static async Task WriteDenyResponse(HttpListenerResponse response)
    {
        var denyJson = Encoding.UTF8.GetBytes("{\"result\":\"deny\"}");
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = denyJson.Length;
        await response.OutputStream.WriteAsync(denyJson);
        response.OutputStream.Close();
    }

    public void Stop()
    {
        _listener?.Stop();
        _listener?.Close();
    }
}
