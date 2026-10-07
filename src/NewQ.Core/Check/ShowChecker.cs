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
        var cues = workspace.Cues.ToList();
        var issues = new List<CheckIssue>();

        CheckWorkspace(workspace, cues, env, issues);
        CheckNumbering(cues, issues);
        CheckSequence(cues, issues);
        CheckTargets(cues, issues);
        CheckDevicesAndMessages(cues, env, issues);
        CheckRoutes(workspace, cues, env, issues);

        var media = cues.OfType<MediaCue>().ToList();
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
            issues.Add(new(CheckSeverity.Warning, null, "Workspace mai salvato: i percorsi dei file non possono essere relativi alla cartella dello show."));
        if (cues.OfType<AudioCue>().Any() && !env.AudioOutputReady)
            issues.Add(new(CheckSeverity.Error, null, "L'uscita audio non è disponibile: controlla Strumenti → Impostazioni."));
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
            issues.Add(new(CheckSeverity.Info, cue, "Cue senza numero: non può essere richiamata da Fade, Stop o OSC."));
    }

    private static void CheckSequence(List<Cue> cues, List<CheckIssue> issues)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (!cue.Armed)
                issues.Add(new(CheckSeverity.Info, cue, "Cue disarmata: verrà saltata."));

            if (cue.ContinueMode != ContinueMode.DoNotContinue && i == cues.Count - 1)
                issues.Add(new(CheckSeverity.Info, cue, "Ultima cue con auto-continue/follow: non c'è nessuna cue successiva."));

            if (cue.ContinueMode == ContinueMode.AutoFollow && NeverEnds(cue))
                issues.Add(new(CheckSeverity.Warning, cue, "Auto-follow su una cue che non finisce da sola (loop o immagine senza durata): la cue successiva non partirà mai."));
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
                        issues.Add(new(CheckSeverity.Error, fade, $"La cue target {fade.TargetNumber} non esiste."));
                        break;
                    }
                    if (target is not MediaCue)
                        issues.Add(new(CheckSeverity.Error, fade, $"La cue target {fade.TargetNumber} ({target.TypeName}) non ha né audio né immagine da sfumare."));
                    if (!fade.FadeVolume && !fade.FadeOpacity && !fade.StopWhenDone)
                        issues.Add(new(CheckSeverity.Warning, fade, "Il fade non cambia né volume né opacità e non ferma il target: non fa nulla."));
                    if (fade.FadeVolume && target is ImageCue)
                        issues.Add(new(CheckSeverity.Info, fade, $"Fade del volume su un'immagine (cue {fade.TargetNumber}): non ha effetto."));
                    if (fade.FadeOpacity && target is AudioCue)
                        issues.Add(new(CheckSeverity.Info, fade, $"Fade dell'opacità su una cue audio (cue {fade.TargetNumber}): non ha effetto."));
                    if (cues.IndexOf(target) > i)
                        issues.Add(new(CheckSeverity.Warning, fade, $"La cue target {fade.TargetNumber} viene dopo il fade nella lista: probabilmente non sarà in esecuzione."));
                    break;
                }
                case StopCue stop when !string.IsNullOrWhiteSpace(stop.TargetNumber):
                {
                    var target = FindTarget(cues, stop.TargetNumber);
                    if (target is null)
                        issues.Add(new(CheckSeverity.Error, stop, $"La cue target {stop.TargetNumber} non esiste."));
                    else if (cues.IndexOf(target) > i)
                        issues.Add(new(CheckSeverity.Warning, stop, $"La cue target {stop.TargetNumber} viene dopo lo stop nella lista: probabilmente non sarà in esecuzione."));
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
                issues.Add(new(CheckSeverity.Error, null, $"Route video \"{route.Name}\" senza uscite: le sue cue non saranno visibili."));
            foreach (var output in route.Outputs)
            {
                if (output.ScreenIndex >= env.ScreenCount)
                    issues.Add(new(CheckSeverity.Warning, null, $"Route video \"{route.Name}\", uscita \"{output.Name}\": lo schermo {output.ScreenIndex + 1} non è collegato, verrà usata la finestra di anteprima."));
                else if (output.ScreenIndex < 0)
                    issues.Add(new(CheckSeverity.Info, null, $"Route video \"{route.Name}\", uscita \"{output.Name}\": finestra di anteprima, non un proiettore."));
            }
        }

        foreach (var group in workspace.AudioRoutes.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new(CheckSeverity.Info, null, $"Più route audio si chiamano \"{group.Key}\": nelle cue sono difficili da distinguere."));
        foreach (var group in workspace.VideoRoutes.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new(CheckSeverity.Info, null, $"Più route video si chiamano \"{group.Key}\": nelle cue sono difficili da distinguere."));

        void CheckReference(Cue cue, Guid? id, bool video)
        {
            if (id is null) return;
            var exists = video ? workspace.VideoRoutes.Any(r => r.Id == id) : workspace.AudioRoutes.Any(r => r.Id == id);
            if (exists) return;
            var fallback = video ? workspace.VideoRoutes.FirstOrDefault()?.Name : workspace.AudioRoutes.FirstOrDefault()?.Name;
            issues.Add(new(CheckSeverity.Warning, cue, $"La route {(video ? "video" : "audio")} della cue non esiste più: verrà usata \"{fallback}\"."));
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
            }
        }
    }

    private static void CheckDevicesAndMessages(List<Cue> cues, IShowCheckEnvironment env, List<CheckIssue> issues)
    {
        foreach (var cue in cues)
        {
            switch (cue)
            {
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
                        issues.Add(new(CheckSeverity.Info, midi, $"Nessun dispositivo scelto: verrà usato \"{env.MidiDevices[0]}\"."));
                    break;
                }
                case NetworkCue net:
                {
                    if (string.IsNullOrWhiteSpace(net.Host))
                        issues.Add(new(CheckSeverity.Error, net, "Host di destinazione mancante."));
                    else if (!IPAddress.TryParse(net.Host, out _))
                        issues.Add(new(CheckSeverity.Info, net, $"\"{net.Host}\" è un nome host: verrà risolto al momento dell'invio. Un indirizzo IP è più affidabile in scena."));
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
            issues.Add(new(CheckSeverity.Warning, cue, $"Percorso assoluto fuori dalla cartella dello show ({cue.FilePath}): copiando lo show su un altro PC il file non verrà trovato."));

        if (env.RiskyDrive(path) is string drive)
            issues.Add(new(CheckSeverity.Warning, cue, $"Il file è su un'unità {drive}: se si scollega durante lo show la cue fallisce."));

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
                    issues.Add(new(CheckSeverity.Info, cue, $"Il punto di fine ({audio.EndTime:0.##} s) supera la durata del file ({duration:0.##} s): verrà usata la fine del file."));
            }

            var playable = cue is AudioCue a2 ? (a2.EndTime > 0 ? Math.Min(a2.EndTime, duration) : duration) - a2.StartTime : duration;
            if (cue.FadeIn + cue.FadeOut > playable && playable > 0 && !(cue is AudioCue { Loop: true } or VideoCue { Loop: true }))
                issues.Add(new(CheckSeverity.Warning, cue, $"Fade in + fade out ({cue.FadeIn + cue.FadeOut:0.##} s) più lunghi della durata ({playable:0.##} s)."));
        }

        if (cue is AudioCue level && probe.PeakDb is double peak)
        {
            var output = peak + level.VolumeDb;
            if (output > 0.05)
                issues.Add(new(CheckSeverity.Warning, cue, $"Andrà in clip: picco del file {peak:0.0} dBFS + volume {level.VolumeDb:+0.0;-0.0} dB = {output:+0.0} dBFS. Abbassa il volume di almeno {output:0.0} dB."));
            else if (peak > -0.1)
                issues.Add(new(CheckSeverity.Info, cue, $"Il file arriva già a {peak:0.0} dBFS: potrebbe essere distorto all'origine."));
        }

        return issues;
    }
}
