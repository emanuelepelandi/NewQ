using System.Net;
using NewQ.Core.Midi;
using NewQ.Core.Model;
using NewQ.Core.Network;

namespace NewQ.Core.Check;

public enum CheckSeverity { Info, Warning, Error }

public sealed record CheckIssue(CheckSeverity Severity, Cue? Cue, string Message);

/// <summary>What the media probe found out about a file, without playing it.</summary>
/// <param name="Error">Why the file can't be used, or null if it can.</param>
/// <param name="DurationSeconds">Length, when known.</param>
/// <param name="PeakDb">Highest sample level in dBFS, when measured (audio).</param>
/// <param name="HasVideo">For video cues: whether a video track exists.</param>
/// <param name="Description">Short technical summary (codec, size...).</param>
public sealed record MediaProbe(string? Error, double? DurationSeconds = null, double? PeakDb = null,
                                bool HasVideo = true, string? Description = null);

/// <summary>The machine the show will run on: files, outputs, devices. Implemented by the app.</summary>
public interface IShowCheckEnvironment
{
    bool FileExists(string path);

    /// <summary>"removable" / "network" when the file sits on a risky drive, otherwise null.</summary>
    string? RiskyDrive(string path);

    int ScreenCount { get; }
    bool AudioOutputReady { get; }
    IReadOnlyList<string> MidiDevices { get; }
    string DefaultMidiDevice { get; }

    /// <summary>Why this audio route can't play here (device missing, channels out of range), or null if it can.</summary>
    string? CheckAudioRoute(AudioRoute route);

    /// <summary>Video capture devices connected now (cameras, HDMI capture cards), by name.</summary>
    IReadOnlyList<string> VideoCaptureDevices { get; }

    /// <summary>Audio capture devices connected now, by name.</summary>
    IReadOnlyList<string> AudioCaptureDevices { get; }

    /// <summary>Opens the file silently and reports what it found. Thread-safe.</summary>
    Task<MediaProbe> ProbeAsync(MediaCue cue, string path, CancellationToken token);
}

/// <summary>
/// Virtual rundown of the whole show: checks files, formats, levels, devices and cue logic without
/// playing or showing anything. Media are probed in parallel.
/// </summary>
public static class ShowChecker
{
    private const int MaxParallelProbes = 4;

    public static async Task<IReadOnlyList<CheckIssue>> RunAsync(Workspace workspace, IShowCheckEnvironment env,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        // Snapshot: the check runs in background while the UI may keep going.
        var cues = workspace.Cues.Where(c => !c.IsShowMarker).ToList(); // --SHOW START/END-- do nothing to check
        var issues = new List<CheckIssue>();

        CheckWorkspace(workspace, cues, env, issues);
        CheckNumbering(cues, issues);
        CheckSequence(cues, issues);
        CheckTargets(cues, issues);
        CheckDevicesAndMessages(cues, env, issues);
        CheckRoutes(workspace, cues, env, issues);

        var media = cues.OfType<MediaCue>().Where(c => c is not LiveCue).ToList(); // live sources have no file
        var done = 0;
        var probeIssues = new System.Collections.Concurrent.ConcurrentBag<CheckIssue>();
        await Parallel.ForEachAsync(media, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelProbes, CancellationToken = token },
            async (cue, ct) =>
            {
                foreach (var issue in await CheckMediaAsync(workspace, cue, env, ct).ConfigureAwait(false))
                    probeIssues.Add(issue);
                progress?.Report((double)Interlocked.Increment(ref done) / Math.Max(1, media.Count));
            }).ConfigureAwait(false);
        issues.AddRange(probeIssues);
        progress?.Report(1);

        // Most severe first, then in cue-list order.
        return issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Cue is null ? -1 : cues.IndexOf(i.Cue))
            .ToList();
    }

    // ------------------------------------------------------------------ workspace

    private static void CheckWorkspace(Workspace workspace, List<Cue> cues, IShowCheckEnvironment env, List<CheckIssue> issues)
    {
        if (cues.Count == 0)
            issues.Add(new(CheckSeverity.Warning, null, "Il workspace non contiene cue."));
        if (workspace.FilePath is null)
            issues.Add(new(CheckSeverity.Warning, null, "Workspace mai salvato: percorsi non relativi."));
        if (cues.OfType<AudioCue>().Any() && !env.AudioOutputReady)
            issues.Add(new(CheckSeverity.Error, null, "Uscita audio non disponibile."));
    }

    private static void CheckNumbering(List<Cue> cues, List<CheckIssue> issues)
    {
        var targeted = cues.OfType<FadeCue>().Select(f => f.TargetNumber)
            .Concat(cues.OfType<StopCue>().Select(s => s.TargetNumber))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in cues.Where(c => !string.IsNullOrWhiteSpace(c.Number))
                                  .GroupBy(c => c.Number.Trim(), StringComparer.OrdinalIgnoreCase)
                                  .Where(g => g.Count() > 1))
        {
            var severity = targeted.Contains(group.Key) ? CheckSeverity.Error : CheckSeverity.Warning;
            foreach (var cue in group)
                issues.Add(new(severity, cue, $"Numero \"{group.Key}\" usato da {group.Count()} cue: Fade, Stop e comandi OSC possono colpire quella sbagliata."));
        }

        foreach (var cue in cues.Where(c => string.IsNullOrWhiteSpace(c.Number)))
            issues.Add(new(CheckSeverity.Info, cue, "Cue senza numero."));
    }

    private static void CheckSequence(List<Cue> cues, List<CheckIssue> issues)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (!cue.Armed)
                issues.Add(new(CheckSeverity.Info, cue, "Cue disarmata: verrà saltata."));

            if (Engine.ShowSequence.Follows(cue) && i == cues.Count - 1)
                issues.Add(new(CheckSeverity.Info, cue, "Follow sull'ultima cue."));

            if (Engine.ShowSequence.Follows(cue) && NeverEnds(cue))
                issues.Add(new(CheckSeverity.Warning, cue, "Follow su una cue senza fine: la successiva non partirà mai."));

            if (cue.LinkAction != LinkAction.None)
            {
                if (cue.LinkTarget.Length == 0)
                    issues.Add(new(CheckSeverity.Warning, cue, "Link senza cue: non fa nulla."));
                else if (string.Equals(cue.Number.Trim(), cue.LinkTarget, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(CheckSeverity.Warning, cue, "Link verso sé stessa: non fa nulla."));
                else if (FindTarget(cues, cue.LinkTarget) is null)
                    issues.Add(new(CheckSeverity.Error, cue, $"Link: cue {cue.LinkTarget} inesistente."));
                else if (Engine.ShowSequence.StartLinkLoop(cues, cue) is { } loop)
                    issues.Add(new(CheckSeverity.Error, cue, $"Link non consentito: loop {Engine.ShowSequence.Describe(loop)} infinito"));
            }
        }
    }

    private static bool NeverEnds(Cue cue) => cue switch
    {
        AudioCue a => a.Loop,
        VideoCue v => v.Loop,
        ImageCue img => img.HoldDuration <= 0,
        _ => false,
    };

    private static void CheckTargets(List<Cue> cues, List<CheckIssue> issues)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            switch (cues[i])
            {
                case FadeCue fade:
                {
                    if (string.IsNullOrWhiteSpace(fade.TargetNumber))
                    {
                        issues.Add(new(CheckSeverity.Error, fade, "Fade senza cue target."));
                        break;
                    }
                    var target = FindTarget(cues, fade.TargetNumber);
                    if (target is null)
                    {
                        issues.Add(new(CheckSeverity.Error, fade, $"Cue {fade.TargetNumber} inesistente."));
                        break;
                    }
                    if (target is not MediaCue)
                        issues.Add(new(CheckSeverity.Error, fade, $"Cue {fade.TargetNumber} ({target.TypeName}): niente da sfumare."));
                    if (!fade.FadeVolume && !fade.FadeOpacity && !fade.StopWhenDone)
                        issues.Add(new(CheckSeverity.Warning, fade, "Fade senza effetto."));
                    if (fade.FadeVolume && target is ImageCue)
                        issues.Add(new(CheckSeverity.Info, fade, $"Fade del volume su un'immagine (cue {fade.TargetNumber})."));
                    if (fade.FadeOpacity && target is AudioCue)
                        issues.Add(new(CheckSeverity.Info, fade, $"Fade dell'opacità su una cue audio (cue {fade.TargetNumber})."));
                    if (cues.IndexOf(target) > i)
                        issues.Add(new(CheckSeverity.Warning, fade, $"Cue {fade.TargetNumber} viene dopo il fade: non sarà in esecuzione."));
                    break;
                }
                case StopCue stop when !string.IsNullOrWhiteSpace(stop.TargetNumber):
                {
                    var target = FindTarget(cues, stop.TargetNumber);
                    if (target is null)
                        issues.Add(new(CheckSeverity.Error, stop, $"Cue {stop.TargetNumber} inesistente."));
                    else if (cues.IndexOf(target) > i)
                        issues.Add(new(CheckSeverity.Warning, stop, $"Cue {stop.TargetNumber} viene dopo lo stop: non sarà in esecuzione."));
                    break;
                }
            }
        }
    }

    private static Cue? FindTarget(List<Cue> cues, string number)
        => cues.FirstOrDefault(c => string.Equals(c.Number.Trim(), number.Trim(), StringComparison.OrdinalIgnoreCase));

    private static void CheckRoutes(Workspace workspace, List<Cue> cues, IShowCheckEnvironment env, List<CheckIssue> issues)
    {
        foreach (var route in workspace.AudioRoutes)
        {
            if (env.CheckAudioRoute(route) is string problem)
                issues.Add(new(CheckSeverity.Error, null, $"Route audio \"{route.Name}\": {problem}"));
            if (route.Muted)
                issues.Add(new(CheckSeverity.Warning, null, $"Route audio \"{route.Name}\" è in muto."));
        }

        foreach (var route in workspace.VideoRoutes)
        {
            if (route.Outputs.Count == 0)
                issues.Add(new(CheckSeverity.Error, null, $"Route video \"{route.Name}\" senza uscite."));
            foreach (var output in route.Outputs)
            {
                if (output.ScreenIndex >= env.ScreenCount)
                    issues.Add(new(CheckSeverity.Warning, null, $"Route video \"{route.Name}\", uscita \"{output.Name}\": schermo {output.ScreenIndex + 1} non collegato, uso l'anteprima."));
                else if (output.ScreenIndex < 0)
                    issues.Add(new(CheckSeverity.Info, null, $"Route video \"{route.Name}\", uscita \"{output.Name}\": finestra di anteprima."));
            }
        }

        foreach (var group in workspace.AudioRoutes.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new(CheckSeverity.Info, null, $"Più route audio si chiamano \"{group.Key}\"."));
        foreach (var group in workspace.VideoRoutes.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new(CheckSeverity.Info, null, $"Più route video si chiamano \"{group.Key}\"."));

        void CheckReference(Cue cue, Guid? id, bool video)
        {
            if (id is null) return;
            var exists = video ? workspace.VideoRoutes.Any(r => r.Id == id) : workspace.AudioRoutes.Any(r => r.Id == id);
            if (exists) return;
            var fallback = video ? workspace.VideoRoutes.FirstOrDefault()?.Name : workspace.AudioRoutes.FirstOrDefault()?.Name;
            issues.Add(new(CheckSeverity.Warning, cue, $"Route {(video ? "video" : "audio")} inesistente: uso \"{fallback}\"."));
        }

        foreach (var cue in cues)
        {
            switch (cue)
            {
                case AudioCue audio: CheckReference(audio, audio.AudioRouteId, video: false); break;
                case VideoCue videoCue:
                    CheckReference(videoCue, videoCue.VideoRouteId, video: true);
                    CheckReference(videoCue, videoCue.AudioRouteId, video: false);
                    break;
                case ImageCue image: CheckReference(image, image.VideoRouteId, video: true); break;
                case LiveCue live:
                    CheckReference(live, live.VideoRouteId, video: true);
                    CheckReference(live, live.AudioRouteId, video: false);
                    break;
            }
        }
    }

    private static void CheckLiveSource(LiveCue live, IShowCheckEnvironment env, List<CheckIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(live.DeviceName))
            issues.Add(new(CheckSeverity.Error, live, "Nessuna sorgente live selezionata."));
        else if (!env.VideoCaptureDevices.Contains(live.DeviceName, StringComparer.OrdinalIgnoreCase))
            issues.Add(new(CheckSeverity.Error, live, $"La sorgente \"{live.DeviceName}\" non è collegata."));

        if (!string.IsNullOrWhiteSpace(live.AudioDeviceName)
            && !env.AudioCaptureDevices.Contains(live.AudioDeviceName, StringComparer.OrdinalIgnoreCase))
            issues.Add(new(CheckSeverity.Warning, live, $"Ingresso audio \"{live.AudioDeviceName}\" non collegato: niente suono."));
    }

    private static void CheckDevicesAndMessages(List<Cue> cues, IShowCheckEnvironment env, List<CheckIssue> issues)
    {
        foreach (var cue in cues)
        {
            switch (cue)
            {
                case LiveCue live:
                    CheckLiveSource(live, env, issues);
                    break;

                case MidiCue midi:
                {
                    try { MidiMessageBuilder.Build(midi); }
                    catch (FormatException ex) { issues.Add(new(CheckSeverity.Error, midi, ex.Message)); }

                    var device = string.IsNullOrWhiteSpace(midi.DeviceName) ? env.DefaultMidiDevice : midi.DeviceName;
                    if (env.MidiDevices.Count == 0)
                        issues.Add(new(CheckSeverity.Error, midi, "Nessuna uscita MIDI collegata."));
                    else if (!string.IsNullOrWhiteSpace(device)
                             && !env.MidiDevices.Contains(device, StringComparer.OrdinalIgnoreCase))
                        issues.Add(new(CheckSeverity.Error, midi, $"Il dispositivo MIDI \"{device}\" non è collegato."));
                    else if (string.IsNullOrWhiteSpace(device))
                        issues.Add(new(CheckSeverity.Info, midi, $"Nessun dispositivo: uso \"{env.MidiDevices[0]}\"."));
                    break;
                }
                case NetworkCue net:
                {
                    if (string.IsNullOrWhiteSpace(net.Host))
                        issues.Add(new(CheckSeverity.Error, net, "Host di destinazione mancante."));
                    else if (!IPAddress.TryParse(net.Host, out _))
                        issues.Add(new(CheckSeverity.Info, net, $"\"{net.Host}\" è un nome host: meglio un indirizzo IP."));
                    try { NetworkCuePlayer.BuildPayload(net); }
                    catch (FormatException ex) { issues.Add(new(CheckSeverity.Error, net, ex.Message)); }
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ media

    private static async Task<List<CheckIssue>> CheckMediaAsync(Workspace workspace, MediaCue cue, IShowCheckEnvironment env, CancellationToken token)
    {
        var issues = new List<CheckIssue>();
        if (string.IsNullOrWhiteSpace(cue.FilePath))
        {
            issues.Add(new(CheckSeverity.Error, cue, "Nessun file selezionato."));
            return issues;
        }

        var path = workspace.ResolvePath(cue.FilePath);
        if (!env.FileExists(path))
        {
            issues.Add(new(CheckSeverity.Error, cue, $"File non trovato: {path}"));
            return issues;
        }

        if (workspace.FilePath is not null && Path.IsPathRooted(cue.FilePath))
            issues.Add(new(CheckSeverity.Warning, cue, $"Percorso fuori dalla cartella dello show: {cue.FilePath}"));

        if (env.RiskyDrive(path) is string drive)
            issues.Add(new(CheckSeverity.Warning, cue, $"File su unità {drive}."));

        MediaProbe probe;
        try { probe = await env.ProbeAsync(cue, path, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { probe = new MediaProbe(ex.Message); }

        if (probe.Error is not null)
        {
            issues.Add(new(CheckSeverity.Error, cue, $"File non leggibile: {probe.Error}"));
            return issues;
        }

        if (cue is VideoCue && !probe.HasVideo)
            issues.Add(new(CheckSeverity.Error, cue, "Il file non contiene una traccia video."));

        if (probe.DurationSeconds is double duration)
        {
            if (cue is AudioCue audio)
            {
                if (audio.StartTime >= duration)
                    issues.Add(new(CheckSeverity.Error, cue, $"Il punto di inizio ({audio.StartTime:0.##} s) è oltre la fine del file ({duration:0.##} s)."));
                else if (audio.EndTime > 0 && audio.EndTime <= audio.StartTime)
                    issues.Add(new(CheckSeverity.Error, cue, "Il punto di fine è prima del punto di inizio."));
                else if (audio.EndTime > duration)
                    issues.Add(new(CheckSeverity.Info, cue, $"Fine ({audio.EndTime:0.##} s) oltre la durata del file ({duration:0.##} s)."));
            }

            var playable = cue is AudioCue a2 ? (a2.EndTime > 0 ? Math.Min(a2.EndTime, duration) : duration) - a2.StartTime : duration;
            if (cue.FadeIn + cue.FadeOut > playable && playable > 0 && !(cue is AudioCue { Loop: true } or VideoCue { Loop: true }))
                issues.Add(new(CheckSeverity.Warning, cue, $"Fade in + fade out ({cue.FadeIn + cue.FadeOut:0.##} s) più lunghi della durata ({playable:0.##} s)."));
        }

        if (cue is AudioCue level && probe.PeakDb is double peak)
        {
            var output = peak + level.VolumeDb;
            if (output > 0.05)
                issues.Add(new(CheckSeverity.Warning, cue, $"In clip: picco {peak:0.0} dBFS + volume {level.VolumeDb:+0.0;-0.0} dB = {output:+0.0} dBFS."));
            else if (peak > -0.1)
                issues.Add(new(CheckSeverity.Info, cue, $"File già a {peak:0.0} dBFS: forse distorto."));
        }

        return issues;
    }
}
