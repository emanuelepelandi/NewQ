using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NewQ.Core.Engine;

namespace NewQ.Core.Network;

/// <summary>Receives OSC over UDP. Callbacks run on a background thread: marshal before touching the engine.</summary>
public sealed class OscRemoteListener : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<OscMessage, IPEndPoint> _onMessage;
    private readonly Action<string>? _onError;

    public OscRemoteListener(int port, Action<OscMessage, IPEndPoint> onMessage, Action<string>? onError = null)
    {
        Port = port;
        _onMessage = onMessage;
        _onError = onError;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _ = Task.Run(ReceiveLoop);
    }

    public int Port { get; }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var packet = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                foreach (var message in OscMessage.ParsePacket(packet.Buffer))
                    _onMessage(message, packet.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { /* ICMP noise on Windows */ }
            catch (Exception ex) { _onError?.Invoke($"OSC in ingresso: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Maps incoming OSC to engine commands. Addresses follow QLab's dictionary so existing remotes work;
/// an optional "/newq" prefix is accepted.
/// <code>
/// /go  /stop  /panic  /pause  /resume  /playhead/next  /playhead/previous
/// /cue/{number}/start  /cue/{number}/stop  /cue/{number}/select
/// </code>
/// </summary>
public static class RemoteCommandRouter
{
    private static readonly Regex CueCommand = new(@"^/cue/([^/]+)/(start|stop|select)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Executes the command. Returns a description, or null when the address is not recognised.</summary>
    public static string? Handle(CueEngine engine, OscMessage message)
    {
        var address = message.Address;
        if (address.StartsWith("/newq/", StringComparison.OrdinalIgnoreCase)) address = address[5..];

        switch (address.ToLowerInvariant())
        {
            case "/go": return engine.Go() ? "GO" : "GO ignorato";
            case "/stop": engine.StopAll(TimeSpan.Zero); return "Stop tutto";
            case "/panic": engine.Panic(); return "Panic";
            case "/pause": engine.PauseAll(); return "Pausa";
            case "/resume": engine.ResumeAll(); return "Riprendi";
            case "/playhead/next": engine.MovePlayhead(1); return "Playhead avanti";
            case "/playhead/previous": engine.MovePlayhead(-1); return "Playhead indietro";
        }

        var match = CueCommand.Match(address);
        if (!match.Success) return null;

        var number = Uri.UnescapeDataString(match.Groups[1].Value);
        var cue = engine.Workspace.FindByNumber(number);
        if (cue is null) return $"Cue {number} inesistente";

        switch (match.Groups[2].Value.ToLowerInvariant())
        {
            case "start": engine.Fire(cue); return $"Start cue {number}";
            case "stop": engine.Stop(cue, TimeSpan.Zero); return $"Stop cue {number}";
            default: engine.SetPlayhead(cue); return $"Playhead su cue {number}";
        }
    }
}
