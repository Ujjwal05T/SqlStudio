using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlStudio.Services.Mcp;

/// <summary>
/// Serves MCP over HTTP on this computer only. Every request must carry the secret key, and requests that look like they
/// come from a web page (an Origin header, or a Host other than this computer) are refused so a website cannot drive it.
/// </summary>
public sealed class McpHttpServer(McpDispatcher dispatcher, int port, string token) : IDisposable
{
    const int MaxBodyBytes = 1 << 20;
    HttpListener? _listener;
    CancellationTokenSource? _cts;

    public int Port => port;
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>Starts listening. Throws <see cref="HttpListenerException"/> when the port is taken.</summary>
    public void Start()
    {
        if (IsRunning)
            return;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        var listener = _listener;
        _listener = null;
        if (listener is null)
            return;

        // HttpListener can throw while shutting down if a request is still finishing (it even reports "address in use").
        // Stopping must always succeed, so each step is tried on its own and failures are ignored: the listener is going away anyway.
        try
        {
            listener.Stop();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
        }
        try
        {
            listener.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void Dispose() => Stop();

    async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // stopped
            }
            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (!HostIsThisComputer(request) || request.Headers["Origin"] is not null)
            {
                response.StatusCode = 403;
                return;
            }
            if (!TokenMatches(request.Headers["Authorization"]))
            {
                response.StatusCode = 401;
                response.Headers["WWW-Authenticate"] = "Bearer";
                return;
            }
            if (request.Url?.AbsolutePath != "/mcp")
            {
                response.StatusCode = 404;
                return;
            }
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405; // this server does not stream, so there is no GET event stream to offer
                response.Headers["Allow"] = "POST";
                return;
            }
            if (request.ContentLength64 > MaxBodyBytes)
            {
                response.StatusCode = 413;
                return;
            }

            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(request.InputStream, cancellationToken: ct);
            }
            catch (JsonException)
            {
                await WriteJsonAsync(response, 400, McpDispatcher.Error(null, -32700, "Parse error"), ct);
                return;
            }

            JsonNode? reply;
            if (body is JsonArray batch)
            {
                var replies = new List<JsonNode>();
                foreach (var item in batch)
                    if (await dispatcher.HandleAsync(item, ct) is { } r)
                        replies.Add(r);
                reply = replies.Count == 0 ? null : new JsonArray(replies.ToArray());
            }
            else
            {
                reply = await dispatcher.HandleAsync(body, ct);
            }

            if (reply is null)
                response.StatusCode = 202; // only notifications: nothing to say back
            else
                await WriteJsonAsync(response, 200, reply, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            response.StatusCode = 500;
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
            }
        }
    }

    bool HostIsThisComputer(HttpListenerRequest request) =>
        request.Headers["Host"] == $"127.0.0.1:{port}";

    bool TokenMatches(string? header)
    {
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[prefix.Length..]), Encoding.UTF8.GetBytes(token));
    }

    static async Task WriteJsonAsync(HttpListenerResponse response, int status, JsonNode body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
    }
}
