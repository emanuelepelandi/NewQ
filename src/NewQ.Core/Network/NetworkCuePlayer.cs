using System.Net.Sockets;
using NewQ.Core.Engine;
using NewQ.Core.Model;

namespace NewQ.Core.Network;

/// <summary>Sends OSC (UDP / TCP+SLIP) and raw UDP/TCP messages. Fire-and-forget: the cue completes immediately.</summary>
public sealed class NetworkCuePlayer : ICuePlayer
{
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(2);

    public bool CanPlay(Cue cue) => cue is NetworkCue;

    public IActiveCue Start(Cue cue, CueContext context)
    {
        var net = (NetworkCue)cue;
        if (string.IsNullOrWhiteSpace(net.Host)) throw new InvalidOperationException("Host di destinazione mancante.");

        // Build synchronously so that format errors are reported as a failed start.
        var payload = BuildPayload(net);
        var (protocol, host, port) = (net.Protocol, net.Host, net.Port);

        _ = Task.Run(async () =>
        {
            try
            {
                if (protocol is NetworkProtocol.OscUdp or NetworkProtocol.Udp)
                    await SendUdpAsync(host, port, payload).ConfigureAwait(false);
                else
                    await SendTcpAsync(host, port, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Error($"invio a {host}:{port} fallito: {ex.Message}");
            }
        });

        context.Completed();
        return InstantActiveCue.Instance;
    }

    public static byte[] BuildPayload(NetworkCue cue) => cue.Protocol switch
    {
        NetworkProtocol.OscUdp => new OscMessage(cue.OscAddress, OscArgumentParser.Parse(cue.OscArguments)).ToBytes(),
        NetworkProtocol.OscTcp => Slip.Encode(new OscMessage(cue.OscAddress, OscArgumentParser.Parse(cue.OscArguments)).ToBytes()),
        _ => TextEscapes.ToBytes(cue.Payload),
    };

    private static async Task SendUdpAsync(string host, int port, byte[] payload)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true; // allows x.x.x.255 destinations
        await udp.SendAsync(payload, payload.Length, host, port).ConfigureAwait(false);
    }

    private static async Task SendTcpAsync(string host, int port, byte[] payload)
    {
        using var cts = new CancellationTokenSource(TcpTimeout);
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();
        await stream.WriteAsync(payload, cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(cts.Token).ConfigureAwait(false);
    }
}
