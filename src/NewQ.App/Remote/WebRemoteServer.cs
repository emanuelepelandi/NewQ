using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NewQ.App.Remote;

/// <summary>What the web page needs from the app. Called on the server's threads: implementations must be thread-safe.</summary>
public interface IWebRemoteHost
{
    /// <summary>The control page.</summary>
    string Page { get; }

    /// <summary>The access page, when a password is set.</summary>
    string LoginPage { get; }

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
/// GET  /                 the page (or the access page)
/// POST /login            password=...: sets the session cookie
/// GET  /api/timeline     timeline JSON
/// GET  /api/events       text/event-stream of state JSON (every 100 ms when it changes, a heartbeat otherwise)
/// POST /api/cmd/{name}   go | pause | fade | stop | estop | up | down
/// </code>
/// With a password, everything but the access page needs the session cookie given by /login (401 otherwise).
/// Sessions live until the password changes or NewQ restarts (the server is recreated). Five wrong passwords from
/// one address within a minute block that address for 30 s.
/// </summary>
public sealed class WebRemoteServer : IDisposable
{
    private const int MaxClients = 32;
    private static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(100);

    private readonly TcpListener _listener;
    private readonly IWebRemoteHost _host;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _password;
    private readonly ConcurrentDictionary<string, byte> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime First, DateTime BlockedUntil)> _failures = new();
    private int _clients;

    private const string SessionCookie = "newq_session";
    private const int MaxFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BlockTime = TimeSpan.FromSeconds(30);

    /// <param name="password">Empty = the page is open to anyone on the network.</param>
    public WebRemoteServer(int port, IWebRemoteHost host, string password = "")
    {
        Port = port;
        _host = host;
        _password = password ?? "";
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    /// <summary>Connections open now (pages listening to the state, requests in progress).</summary>
    public int Clients => Volatile.Read(ref _clients);

    public bool HasPassword => _password.Length > 0;

    /// <summary>"http://192.168.1.10:8080" for each local IPv4 address of a working network.</summary>
    public string[] Addresses() => AddressesFor(Port);

    /// <inheritdoc cref="Addresses"/>
    public static string[] AddressesFor(int port)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0) // the LAN with a router first
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => $"http://{a.Address}:{port}")
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
        var request = await ReadRequest(stream).ConfigureAwait(false);
        if (request is null) return;
        var (method, path) = (request.Method, request.Path);
        var allowed = !HasPassword || IsSignedIn(request);

        if (method == "POST" && path == "/login")
            await Login(stream, request, from).ConfigureAwait(false);
        else if (method == "GET" && path is "/" or "/index.html")
            await Respond(stream, "200 OK", "text/html; charset=utf-8", allowed ? _host.Page : _host.LoginPage).ConfigureAwait(false);
        else if (!allowed)
            await Respond(stream, "401 Unauthorized", "text/plain", "").ConfigureAwait(false);
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

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body);

    /// <summary>An HTTP request: request line, headers, and a small body (forms only, up to 4 KB).</summary>
    private static async Task<Request?> ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[16384];
        var length = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0) return null;
            length += read;
            var text = Encoding.UTF8.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;

            var lines = text[..end].Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            var bodyLength = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n) ? Math.Clamp(n, 0, 4096) : 0;
            var bodyStart = Encoding.UTF8.GetByteCount(text[..(end + 4)]);
            // Clients that ask first (Expect: 100-continue) would otherwise wait ~350 ms before sending the body.
            if (bodyLength > 0 && length - bodyStart < bodyLength
                && headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), timeout.Token).ConfigureAwait(false);
            while (length - bodyStart < bodyLength && length < buffer.Length)
            {
                read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            var body = Encoding.UTF8.GetString(buffer, bodyStart, Math.Max(0, Math.Min(bodyLength, length - bodyStart)));
            return new Request(parts[0], parts[1].Split('?')[0], headers, body);
        }
        return null;
    }

    // ------------------------------------------------------------------ access

    private bool IsSignedIn(Request request)
    {
        if (!request.Headers.TryGetValue("Cookie", out var cookies)) return false;
        foreach (var cookie in cookies.Split(';'))
        {
            var pair = cookie.Trim();
            if (pair.StartsWith(SessionCookie + "=", StringComparison.Ordinal) && _sessions.ContainsKey(pair[(SessionCookie.Length + 1)..]))
                return true;
        }
        return false;
    }

    /// <summary>The access form: right password = a session cookie and the page; wrong = the form again, slowly.</summary>
    private async Task Login(NetworkStream stream, Request request, string from)
    {
        var now = DateTime.UtcNow;
        if (_failures.TryGetValue(from, out var f) && f.BlockedUntil > now)
        {
            await Redirect(stream, "/?e=blocked").ConfigureAwait(false);
            return;
        }

        var password = "";
        foreach (var field in request.Body.Split('&'))
            if (field.StartsWith("password=", StringComparison.Ordinal))
                password = Uri.UnescapeDataString(field["password=".Length..].Replace('+', ' '));

        if (!HasPassword || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(_password)))
        {
            _failures.TryRemove(from, out _);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _sessions[token] = 0;
            await Redirect(stream, "/", $"Set-Cookie: {SessionCookie}={token}; Path=/; Max-Age=2592000; HttpOnly; SameSite=Strict\r\n").ConfigureAwait(false);
            return;
        }

        var count = f.Count > 0 && now - f.First < FailureWindow ? f.Count + 1 : 1;
        var first = count == 1 ? now : f.First;
        _failures[from] = (count, first, count >= MaxFailures ? now + BlockTime : DateTime.MinValue);
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false); // slows down guessing
        await Redirect(stream, count >= MaxFailures ? "/?e=blocked" : "/?e=password").ConfigureAwait(false);
    }

    private static async Task Redirect(NetworkStream stream, string location, string extraHeaders = "")
    {
        var head = $"HTTP/1.1 303 See Other\r\nLocation: {location}\r\n{extraHeaders}Content-Length: 0\r\n" +
                   "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
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
