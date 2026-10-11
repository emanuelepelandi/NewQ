using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using NewQ.App.Infrastructure;
using NewQ.App.ViewModels;
using NewQ.Core.Model;
using NewQ.Core.Timeline;

namespace NewQ.App.Remote;

/// <summary>
/// Bridge between the web remote and the app. A timer on the UI thread (10 Hz) writes the timeline and the live state
/// as JSON; the server's threads only read those strings. Commands are queued to the UI thread, exactly like the
/// buttons of the main window.
/// </summary>
public sealed class WebRemoteHost : IWebRemoteHost, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, Func<ICommand>> _commands;
    private readonly Action<string> _log;
    private Func<int> _clients = () => 0;

    private string _timelineJson = "{}";
    private string _stateJson = "{}";
    private object? _timelineKey;
    private int _revision;
    private TimelineLayout? _layout;

    public WebRemoteHost(MainViewModel vm, Dispatcher dispatcher, Action<string> log)
    {
        _vm = vm;
        _dispatcher = dispatcher;
        _log = log;
        _commands = new Dictionary<string, Func<ICommand>>(StringComparer.OrdinalIgnoreCase)
        {
            ["go"] = () => vm.GoCommand,
            ["pause"] = () => vm.TogglePauseCommand,
            ["fade"] = () => vm.FadeSelectedCommand,
            ["stop"] = () => vm.PanicCommand,      // Stop: fade out of everything, twice = immediate
            ["estop"] = () => vm.StopAllCommand,   // E-STOP: everything at once
            ["up"] = () => vm.PlayheadUpCommand,
            ["down"] = () => vm.PlayheadDownCommand,
        };
        Page = LoadPage();
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            if (_clients() > 0) Refresh(); // nobody connected: no work at all
        }, dispatcher);
        _timer.Start();
    }

    /// <summary>The server's open connections (the timer works only while pages are connected).</summary>
    public void AttachClients(Func<int> clients) => _clients = clients;

    public string Page { get; }
    public string TimelineJson => Volatile.Read(ref _timelineJson);
    public string StateJson => Volatile.Read(ref _stateJson);

    public bool Execute(string command, string from)
    {
        if (!_commands.TryGetValue(command, out var get)) return false;
        _dispatcher.BeginInvoke(() =>
        {
            var cmd = get();
            if (!cmd.CanExecute(null)) return;
            cmd.Execute(null);
            _log($"Web da {from}: {Label(command)}");
        });
        return true;
    }

    private static string Label(string command) => command.ToLowerInvariant() switch
    {
        "go" => "GO",
        "pause" => "Pausa / Riprendi",
        "fade" => "Fade cue",
        "stop" => "Stop",
        "estop" => "E-STOP",
        "up" => "Playhead su",
        _ => "Playhead giù",
    };

    private static string LoadPage()
    {
        using var stream = typeof(WebRemoteHost).Assembly.GetManifestResourceStream("NewQ.App.Remote.remote.html");
        if (stream is null) return "<!doctype html><title>NewQ</title><p>Pagina non trovata.</p>";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------ JSON (UI thread)

    private void Refresh()
    {
        var key = (_vm.Timeline, _vm.Settings.TimelineLanesByRoute, _vm.RoutesVersion, _vm.Workspace);
        if (!Equals(key, _timelineKey))
        {
            _timelineKey = key;
            _revision++;
            _layout = TimelineLayout.Build(_vm.Timeline, _vm.Workspace, _vm.Settings.TimelineLanesByRoute);
            Volatile.Write(ref _timelineJson, BuildTimeline(_vm.Timeline, _layout, _revision));
        }
        Volatile.Write(ref _stateJson, BuildState());
    }

    private static string Hex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string BuildTimeline(ShowTimeline timeline, TimelineLayout layout, int revision)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("rev", revision);
            w.WriteBoolean("startHasGo", timeline.StartHasGo);
            w.WriteStartArray("blocks");
            foreach (var block in timeline.Blocks) w.WriteNumberValue(Math.Round(block.Length, 3));
            w.WriteEndArray();
            w.WriteStartArray("lanes");
            foreach (var lane in layout.Lanes)
            {
                w.WriteStartObject();
                w.WriteString("label", lane.Label);
                w.WriteNumber("tracks", lane.Tracks);
                w.WriteBoolean("visual", lane.Visual);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            var laneIndex = layout.Lanes.Select((l, i) => (l, i)).ToDictionary(x => x.l, x => x.i);
            w.WriteStartArray("items");
            foreach (var p in layout.Placements)
            {
                var item = p.Item;
                var instant = item.End is TimelinePoint e && Equals(e, item.Start);
                w.WriteStartObject();
                w.WriteNumber("lane", laneIndex[p.Lane]);
                w.WriteNumber("track", p.Track);
                w.WriteString("label", $"{item.Cue.Number}  {item.Cue.Name}".Trim());
                w.WriteString("kind", instant ? "instant" : item.Kind == TimelineItemKind.Media ? "media" : "control");
                w.WriteString("color", Hex(CueTypeBrushConverter.ColorOf(item.Kind == TimelineItemKind.Media || instant ? item.Cue.TypeName : "Fade")));
                w.WriteBoolean("disarmed", item.Disarmed);
                WritePoint(w, "start", item.Start);
                if (item.End is TimelinePoint end) WritePoint(w, "end", end); else w.WriteNull("end");
                if (item.Kind == TimelineItemKind.Media && !instant)
                {
                    // The level line: start, every change, end (as the app draws it).
                    w.WriteStartArray("level");
                    WriteLevel(w, item.Start, TimelineBuilder.LevelAt(item, item.Start));
                    foreach (var point in item.Envelope) WriteLevel(w, point.At, point.Level);
                    if (item.End is TimelinePoint last) WriteLevel(w, last, TimelineBuilder.LevelAt(item, last));
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WritePoint(Utf8JsonWriter w, string name, TimelinePoint p)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(p.Block);
        w.WriteNumberValue(Math.Round(p.Time, 3));
        w.WriteEndArray();
    }

    private static void WriteLevel(Utf8JsonWriter w, TimelinePoint p, double level)
    {
        w.WriteStartArray();
        w.WriteNumberValue(p.Block);
        w.WriteNumberValue(Math.Round(p.Time, 3));
        w.WriteNumberValue(Math.Round(level, 3));
        w.WriteEndArray();
    }

    private string BuildState()
    {
        var timeline = _vm.Timeline;
        var playhead = _vm.SelectedCue;
        var onStart = playhead is ShowMarkerCue { IsStart: true };
        var playheadBlock = playhead switch
        {
            null => -1,
            ShowMarkerCue { IsEnd: true } => timeline.Blocks.Count,
            _ when onStart && timeline.StartHasGo => -1,
            _ => timeline.BlockOf(playhead),
        };

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("rev", _revision);
            w.WriteBoolean("paused", _vm.IsPaused);
            w.WriteBoolean("safe", _vm.IsSafeMode);
            w.WriteNumber("playheadBlock", playheadBlock);
            w.WriteBoolean("onStart", onStart);
            if (playhead is null) w.WriteNull("next");
            else
            {
                w.WriteStartObject("next");
                w.WriteString("number", playhead.Number);
                w.WriteString("name", playhead.Name);
                w.WriteString("type", playhead.TypeName);
                w.WriteEndObject();
            }
            w.WriteStartArray("cursors");
            foreach (var now in _vm.TimelineCursors())
            {
                w.WriteStartArray();
                w.WriteNumberValue(now.At.Block);
                w.WriteNumberValue(Math.Round(now.At.Time, 3));
                w.WriteBooleanValue(now.WaitingForGo);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            // Cues fading out to a stop: their bars turn amber (indexes in the timeline's items).
            w.WriteStartArray("fading");
            if (_layout is not null)
                for (var i = 0; i < _layout.Placements.Count; i++)
                    if (_layout.Placements[i].Item.Cue.IsFadingOut) w.WriteNumberValue(i);
            w.WriteEndArray();
            w.WriteNumber("running", _vm.ActiveCues.Count);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose() => _timer.Stop();
}
