using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using NewQ.App.ViewModels;
using NewQ.Core.Model;

namespace NewQ.App.Remote;

/// <summary>
/// Bridge between the web remote and the app. A timer on the UI thread (10 Hz) writes the live state as JSON; the
/// server's threads only read that string. Commands are queued to the UI thread, exactly like the buttons of the
/// main window.
/// </summary>
public sealed class WebRemoteHost : IWebRemoteHost, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, Func<ICommand>> _commands;
    private readonly Dictionary<string, Func<ICommand>> _cueCommands;
    private readonly Action<string> _log;
    private Func<int> _clients = () => 0;

    private string _stateJson = "{}";

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
        // The buttons of a row of "In esecuzione".
        _cueCommands = new Dictionary<string, Func<ICommand>>(StringComparer.OrdinalIgnoreCase)
        {
            ["pause"] = () => vm.TogglePauseCueCommand,
            ["fade"] = () => vm.FadeCueCommand,
            ["stop"] = () => vm.StopCueCommand,
        };
        Page = LoadPage("remote.html");
        LoginPage = LoadPage("remote-login.html");
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
    public string LoginPage { get; }
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

    public bool ExecuteCue(string command, Guid cueId, string from)
    {
        if (!_cueCommands.TryGetValue(command, out var get)) return false;
        _dispatcher.BeginInvoke(() =>
        {
            // Only a cue still running: a late tap never restarts or touches anything else.
            if (_vm.ActiveCues.FirstOrDefault(c => c.Id == cueId) is not Cue cue) return;
            var cmd = get();
            if (!cmd.CanExecute(cue)) return;
            cmd.Execute(cue);
            _log($"Web da {from}: {CueLabel(command)} {cue.Number}".TrimEnd());
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

    private static string CueLabel(string command) => command.ToLowerInvariant() switch
    {
        "pause" => "Pausa / Riprendi cue",
        "fade" => "Fade out cue",
        _ => "Stop cue",
    };

    private static string LoadPage(string name)
    {
        using var stream = typeof(WebRemoteHost).Assembly.GetManifestResourceStream("NewQ.App.Remote." + name);
        if (stream is null) return "<!doctype html><title>NewQ</title><p>Pagina non trovata.</p>";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------ JSON (UI thread)

    private void Refresh() => Volatile.Write(ref _stateJson, BuildState());

    private string BuildState()
    {
        var playhead = _vm.SelectedCue;
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("paused", _vm.IsPaused);
            w.WriteBoolean("safe", _vm.IsSafeMode);
            if (playhead is null) w.WriteNull("next");
            else
            {
                w.WriteStartObject("next");
                w.WriteString("number", playhead.Number);
                w.WriteString("name", playhead.Name);
                w.WriteString("type", playhead.TypeName);
                w.WriteEndObject();
            }
            // In esecuzione, as in the app: one row per running cue, its fill, length and time left.
            w.WriteStartArray("running");
            foreach (var cue in _vm.ActiveCues)
            {
                w.WriteStartObject();
                w.WriteString("id", cue.Id.ToString("N"));
                w.WriteString("number", cue.Number);
                w.WriteString("name", cue.Name);
                w.WriteString("state", StateOf(cue));
                w.WriteNumber("progress", Math.Round(Math.Clamp(cue.Progress, 0, 1), 4));
                w.WriteBoolean("endless", cue.RuntimeDuration is null);
                w.WriteString("duration", cue.RuntimeDurationText);
                w.WriteString("remaining", cue.RemainingText);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The row's colour: the same states as the app's "In esecuzione".</summary>
    private static string StateOf(Cue cue) => cue.RunState switch
    {
        CueRunState.Paused => "paused",
        CueRunState.Error => "error",
        _ when cue.IsFadingOut => "fading",
        CueRunState.PreWait => "prewait",
        _ => "running",
    };

    public void Dispose() => _timer.Stop();
}
