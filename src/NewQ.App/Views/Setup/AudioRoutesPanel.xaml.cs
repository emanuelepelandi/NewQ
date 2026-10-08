using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NewQ.App.Audio;
using NewQ.Core.Model;

namespace NewQ.App.Views.Setup;

public partial class AudioRoutesPanel : UserControl
{
    private sealed record DeviceChoice(string Id, string Label, int Channels);
    private sealed record PairChoice(int First, string Label);

    private SetupContext? _context;
    private List<DeviceChoice> _devices = new();
    private readonly DispatcherTimer _meterTimer;
    private double _meterDb = NewQ.Core.Decibels.Floor;

    public AudioRoutesPanel()
    {
        InitializeComponent();
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => UpdateMeter(), Dispatcher);
        _meterTimer.Stop();
        Unloaded += (_, _) => _meterTimer.Stop();
        Loaded += (_, _) => _meterTimer.Start();
    }

    public void Initialize(SetupContext context)
    {
        _context = context;
        _devices = new List<DeviceChoice> { new("", "Predefinita (scheda Generale)", 2) };
        try { _devices.AddRange(AudioEngine.GetWasapiDevices().Select(d => new DeviceChoice(d.Id, $"{d.Name} · {d.Channels} canali", d.Channels))); }
        catch { /* no audio subsystem */ }
        DeviceBox.ItemsSource = _devices;
        DeviceBox.IsEnabled = !context.IsAsio;
        AsioNote.Visibility = context.IsAsio ? Visibility.Visible : Visibility.Collapsed;

        RouteList.ItemsSource = context.Workspace.AudioRoutes;
        RouteList.SelectedIndex = 0;
    }

    private AudioRoute? Selected => RouteList.SelectedItem as AudioRoute;

    private void OnRouteSelected(object sender, SelectionChangedEventArgs e)
    {
        Editor.DataContext = Selected;
        Editor.IsEnabled = Selected is not null;
        RefreshPairs();
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e) => RefreshPairs();

    /// <summary>Channel pairs the selected sound card (or the ASIO driver) really has.</summary>
    private void RefreshPairs()
    {
        if (_context is null || Selected is not AudioRoute route) return;
        int channels;
        if (_context.IsAsio)
        {
            channels = 64;
            if (_context.Audio.CheckRoute(new AudioRoute { FirstChannel = 0 }) is null)
                for (var c = 64; c >= 2; c -= 2)
                    if (_context.Audio.CheckRoute(new AudioRoute { FirstChannel = c - 2 }) is null) { channels = c; break; }
        }
        else
        {
            var id = string.IsNullOrEmpty(route.DeviceId) ? _context.Settings.WasapiDeviceId ?? "" : route.DeviceId;
            channels = _devices.FirstOrDefault(d => d.Id == id)?.Channels ?? 2;
        }

        var pairs = new List<PairChoice>();
        for (var first = 0; first + 1 < Math.Max(2, channels); first += 2)
            pairs.Add(new PairChoice(first, $"{first + 1}-{first + 2}"));
        if (pairs.All(p => p.First != route.FirstChannel))
            pairs.Add(new PairChoice(route.FirstChannel, $"{route.FirstChannel + 1}-{route.FirstChannel + 2} (non disponibili)"));

        PairBox.ItemsSource = pairs;
        PairBox.SelectedValue = route.FirstChannel;
    }

    private void UpdateMeter()
    {
        if (_context is null || Selected is not AudioRoute route) return;
        var reading = _context.Audio.TakeRoutePeaks(route.Id);
        _meterDb = MeterMath.Fall(_meterDb, MeterMath.ToDb(reading.Max), 0.05);
        RouteMeter.Level = _meterDb;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        var route = new AudioRoute { Name = UniqueName("Audio") };
        _context.Workspace.AudioRoutes.Add(route);
        RouteList.SelectedItem = route;
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_context is null || Selected is not AudioRoute source) return;
        var copy = new AudioRoute
        {
            Name = UniqueName(source.Name), DeviceId = source.DeviceId, FirstChannel = source.FirstChannel,
            GainDb = source.GainDb, Muted = source.Muted,
        };
        _context.Workspace.AudioRoutes.Add(copy);
        RouteList.SelectedItem = copy;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_context is null || Selected is not AudioRoute route) return;
        var ws = _context.Workspace;
        if (ws.AudioRoutes.Count == 1)
        {
            MessageBox.Show("Il workspace deve avere almeno una route audio.", "Route audio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var users = ws.Cues.Where(c => c is AudioCue a && a.AudioRouteId == route.Id || c is VideoCue v && v.AudioRouteId == route.Id).ToList();
        var replacement = ws.AudioRoutes.First(r => r != route);
        if (users.Count > 0 && MessageBox.Show(
                $"{users.Count} cue usano \"{route.Name}\": passeranno a \"{replacement.Name}\". Continuare?",
                "Route audio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var cue in users)
        {
            if (cue is AudioCue a) a.AudioRouteId = replacement.Id;
            if (cue is VideoCue v) v.AudioRouteId = replacement.Id;
        }
        ws.AudioRoutes.Remove(route);
        RouteList.SelectedItem = replacement;
    }

    private string UniqueName(string baseName) => SetupHelpers.UniqueName(baseName, _context!.Workspace.AudioRoutes.Select(r => r.Name));
}
