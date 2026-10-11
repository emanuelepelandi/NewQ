using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NewQ.App.Remote;

/// <summary>What the web page needs from the app. Called on the server's threads: implementations must be thread-safe.</summary>
public interface IWebRemoteHost
{
    /// <summary>The control page.</summary>
    string Page { get; }

    /// <summary>The timeline (lanes, blocks, cues) as JSON; changes rarely.</summary>
    string TimelineJson { get; }

    /// <summary>The live state (cursors, playhead, pause, fading cues...) as JSON; refreshed several times a second.</summary>
    string StateJson { get; }

    /// <summary>Runs a command ("go", "pause", "fade", "stop", "estop", "up", "down"); false if unknown.</summary>
    bool Execute(string command, string from);
}

/// <summary>
/// The web remote: a small HTTP server on the local network, without dependencies. Pages and data are GETs, the
/// live state is a Server-Sent Events stream, commands are POSTs. One task per connection.
/// <code>
/// GET  /                 the page
/// GET  /api/timeline     timeline JSON
/// GET  /api/events       text/event-stream of state JSON (every 100 ms when it changes, a heartbeat otherwise)
/// POST /api/cmd/{name}   go | pause | fade | stop | estop | up | down
/// </code>
/// </summary>
public sealed class WebRemoteServer : IDisposable
{
    private const int MaxClients = 32;
    private static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(100);

    private readonly TcpListener _listener;
    private readonly IWebRemoteHost _host;
    private readonly CancellationTokenSource _cts = new();
    private int _clients;

    public WebRemoteServer(int port, IWebRemoteHost host)
    {
        Port = port;
        _host = host;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    /// <summary>Connections open now (pages listening to the state, requests in progress).</summary>
    public int Clients => Volatile.Read(ref _clients);

    /// <summary>"http://192.168.1.10:8080" for each local IPv4 address of a working network.</summary>
    public string[] Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0) // the LAN with a router first
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => $"http://{a.Address}:{Port}")
                .Distinct()
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }
            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await Serve(client).ConfigureAwait(false); }
                catch { /* the page went away */ }
                finally
                {
                    client.Dispose();
                    Interlocked.Decrement(ref _clients);
                }
            });
        }
    }

    private async Task Serve(TcpClient client)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        var from = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        var (method, path) = await ReadRequest(stream).ConfigureAwait(false);
        if (method is null) return;
        path = path!.Split('?')[0];

        if (method == "GET" && path is "/" or "/index.html")
            await Respond(stream, "200 OK", "text/html; charset=utf-8", _host.Page).ConfigureAwait(false);
        else if (method == "GET" && path == "/api/timeline")
            await Respond(stream, "200 OK", "application/json; charset=utf-8", _host.TimelineJson).ConfigureAwait(false);
        else if (method == "GET" && path == "/api/events")
            await StreamState(stream).ConfigureAwait(false);
        else if (method == "POST" && path.StartsWith("/api/cmd/", StringComparison.Ordinal))
        {
            var ok = _host.Execute(path["/api/cmd/".Length..], from);
            await Respond(stream, ok ? "204 No Content" : "404 Not Found", "text/plain", "").ConfigureAwait(false);
        }
        else
            await Respond(stream, "404 Not Found", "text/plain", "").ConfigureAwait(false);
    }

    /// <summary>Request line of an HTTP request (the headers are read and ignored; bodies are not used).</summary>
    private static async Task<(string? Method, string? Path)> ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var length = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0) return (null, null);
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            var parts = text[..text.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
            return parts.Length >= 2 ? (parts[0], parts[1]) : (null, null);
        }
        return (null, null);
    }

    private static async Task Respond(NetworkStream stream, string status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                   "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        if (bytes.Length > 0) await stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>Server-Sent Events: the state when it changes (at most every 100 ms), a comment every 2 s otherwise.</summary>
    private async Task StreamState(NetworkStream stream)
    {
        const string head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        string? last = null;
        var quiet = TimeSpan.Zero;
        while (!_cts.IsCancellationRequested)
        {
            var state = _host.StateJson;
            if (!ReferenceEquals(state, last) && state != last)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + state + "\n\n")).ConfigureAwait(false);
                last = state;
                quiet = TimeSpan.Zero;
            }
            else if ((quiet += StateInterval) >= TimeSpan.FromSeconds(2))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(": ping\n\n")).ConfigureAwait(false); // notices a page that went away
                quiet = TimeSpan.Zero;
            }
            await Task.Delay(StateInterval, _cts.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
    }
}
