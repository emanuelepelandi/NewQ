using System.Globalization;
using NAudio.CoreAudioApi;
using NewQ.Core.Model;
using NewQ.Core.Serialization;

// StressKit workspace <out.newq> <count> <video> [<video>...]
//     A workspace of <count> video cues (the videos in turn, 0 dB), shown in the preview window, never on a monitor.
// StressKit level <pid> <seconds> <windowMs>
//     Every window: "<utc ticks> <peak>", the loudest the process was heard (session peak × session volume, all
//     output devices). Runs alongside the test; the script matches the windows to its GOs.

var invariant = CultureInfo.InvariantCulture;
switch (args.FirstOrDefault())
{
    case "workspace" when args.Length >= 4:
    {
        var ws = new Workspace();
        ws.EnsureRoutes(1);
        ws.VideoRoutes[0].Outputs[0].ScreenIndex = VideoOutput.PreviewWindow;
        var count = int.Parse(args[2], invariant);
        var videos = args.Skip(3).ToArray();
        for (var i = 0; i < count; i++)
            ws.Cues.Add(new VideoCue
            {
                Number = (i + 1).ToString(invariant),
                Name = "V" + (i + 1),
                FilePath = videos[i % videos.Length],
                VideoRouteId = ws.VideoRoutes[0].Id,
                AudioRouteId = ws.AudioRoutes[0].Id,
            });
        ws.EnsureShowMarkers();
        WorkspaceSerializer.Save(ws, args[1]);
        return 0;
    }

    case "level" when args.Length >= 4:
    {
        var pid = int.Parse(args[1], invariant);
        var end = DateTime.UtcNow.AddSeconds(double.Parse(args[2], invariant));
        var window = TimeSpan.FromMilliseconds(double.Parse(args[3], invariant));
        var devices = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        while (DateTime.UtcNow < end)
        {
            var stop = DateTime.UtcNow + window;
            float peak = 0;
            while (DateTime.UtcNow < stop)
            {
                foreach (var device in devices)
                {
                    try
                    {
                        var sessions = device.AudioSessionManager;
                        sessions.RefreshSessions();
                        for (var i = 0; i < sessions.Sessions.Count; i++)
                        {
                            var s = sessions.Sessions[i];
                            if (s.GetProcessID == pid)
                                peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue * s.SimpleAudioVolume.Volume);
                        }
                    }
                    catch { /* device gone */ }
                }
                Thread.Sleep(15);
            }
            Console.WriteLine($"{stop.Ticks} {peak.ToString("F3", invariant)}");
            Console.Out.Flush();
        }
        return 0;
    }

    default:
        Console.Error.WriteLine("StressKit workspace <out.newq> <count> <video>... | level <pid> <seconds> <windowMs>");
        return 1;
}
