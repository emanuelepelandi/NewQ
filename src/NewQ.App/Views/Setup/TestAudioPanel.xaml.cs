using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NewQ.App.Audio;
using NewQ.Core;
using NewQ.Core.Engine;
using NewQ.Core.Model;
using NewQ.Core.Signals;

namespace NewQ.App.Views.Setup;

public partial class TestAudioPanel : UserControl
{
    /// <summary>A route with its checkbox, its live level and whether a test is playing on it.</summary>
    public sealed class RouteChoice : ObservableObject
    {
        private bool _selected = true;
        private string _status = "";
        private double _levelDb = Decibels.Floor;

        public RouteChoice(AudioRoute route) => Route = route;

        public AudioRoute Route { get; }
        public bool Selected { get => _selected; set => SetField(ref _selected, value); }
        public string Status { get => _status; set => SetField(ref _status, value); }
        public double LevelDb { get => _levelDb; set => SetField(ref _levelDb, value); }
    }

    private const double MeterInterval = 0.05;

    private SetupContext? _context;
    private List<RouteChoice> _routes = new();
    private readonly DispatcherTimer _timer;
    private bool _ready;
    private bool _syncing;

    public TestAudioPanel()
    {
        InitializeComponent();
        SetupHelpers.CommitOnEnter(this);
        // Enter in a plain text field (not bound): apply it like a focus loss.
        AddHandler(PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && e.OriginalSource is TextBox box)
                box.RaiseEvent(new RoutedEventArgs(LostFocusEvent, box));
        }), handledEventsToo: true);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(MeterInterval), DispatcherPriority.Render, (_, _) => Tick(), Dispatcher);
        _timer.Stop();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        _ready = true;
        UpdatePanels();
    }

    public void Initialize(SetupContext context)
    {
        _context = context;
        Refresh();
    }

    /// <summary>Re-reads the routes (they can change in the "Route audio" tab).</summary>
    private void Refresh()
    {
        if (_context is null) return;
        var previous = _routes.ToDictionary(r => r.Route.Id, r => r.Selected);
        _routes = _context.Workspace.AudioRoutes
            .Select(r => new RouteChoice(r) { Selected = previous.TryGetValue(r.Id, out var s) ? s : true })
            .ToList();
        RouteChecks.ItemsSource = _routes;
    }

    // ------------------------------------------------------------------ settings

    private TestSignalSettings ReadSettings()
    {
        var kind = KindPink.IsChecked == true ? TestSignalKind.PinkNoise
            : KindWhite.IsChecked == true ? TestSignalKind.WhiteNoise
            : KindSweep.IsChecked == true ? TestSignalKind.Sweep
            : TestSignalKind.Sine;
        var channels = ChLeft.IsChecked == true ? TestSignalChannels.Left
            : ChRight.IsChecked == true ? TestSignalChannels.Right
            : ChAlternate.IsChecked == true ? TestSignalChannels.Alternate
            : TestSignalChannels.Both;
        var from = Math.Clamp(Parse(SweepFromBox, 20), 10, 24000);
        var to = Math.Clamp(Parse(SweepToBox, 20000), 10, 24000);
        return new TestSignalSettings(kind, Math.Pow(10, FrequencySlider.Value), LevelSlider.Value, channels,
            from, to, Math.Clamp(Parse(SweepSecondsBox, 10), 0.5, 600));
    }

    private static double Parse(TextBox box, double fallback)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && double.IsFinite(v) ? v : fallback;

    private void UpdatePanels()
    {
        TonePanel.Visibility = KindSine.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SweepPanel.Visibility = KindSweep.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LevelWarning.Visibility = LevelSlider.Value > -10 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Any control changed: show the right fields and update the tests that are playing.</summary>
    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdatePanels();
        if (_context is null) return;
        var settings = ReadSettings();
        foreach (var signal in _context.TestSignals) signal.Settings = settings;
    }

    private void OnFrequencySlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        _syncing = true;
        FrequencyBox.Text = Math.Round(Math.Pow(10, e.NewValue)).ToString(CultureInfo.CurrentCulture);
        _syncing = false;
        OnSettingChanged(sender, e);
    }

    private void OnFrequencyBox(object sender, RoutedEventArgs e) => SetFrequency(Parse(FrequencyBox, Math.Pow(10, FrequencySlider.Value)));

    private void OnFrequencyPreset(object sender, RoutedEventArgs e)
        => SetFrequency(double.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture));

    private void SetFrequency(double hz)
    {
        hz = Math.Clamp(hz, 20, 20000);
        _syncing = true;
        FrequencySlider.Value = Math.Log10(hz);
        FrequencyBox.Text = Math.Round(hz).ToString(CultureInfo.CurrentCulture);
        _syncing = false;
        OnSettingChanged(this, new RoutedEventArgs());
    }

    private void OnLevelSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        _syncing = true;
        LevelBox.Text = Math.Round(e.NewValue, 1).ToString(CultureInfo.CurrentCulture);
        _syncing = false;
        OnSettingChanged(sender, e);
    }

    private void OnLevelBox(object sender, RoutedEventArgs e)
    {
        var db = Math.Clamp(Parse(LevelBox, LevelSlider.Value), -60, 0);
        LevelSlider.Value = db; // updates the box and the tests
        LevelBox.Text = Math.Round(db, 1).ToString(CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------------ start / stop

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        var targets = _routes.Where(r => r.Selected).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("Seleziona almeno una route.", "Test audio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = ReadSettings();
        foreach (var choice in targets)
        {
            // One test per route: restarting replaces it.
            foreach (var old in _context.TestSignals.Where(s => s.RouteId == choice.Route.Id).ToList())
            {
                old.Stop();
                _context.TestSignals.Remove(old);
            }
            try
            {
                _context.TestSignals.Add(_context.Audio.StartTestSignal(choice.Route.Id, settings));
            }
            catch (Exception ex)
            {
                _context.Log(EngineLogLevel.Error, $"Test audio su \"{choice.Route.Name}\": {ex.Message}");
                MessageBox.Show($"Impossibile avviare il test su \"{choice.Route.Name}\": {ex.Message}", "Test audio",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        _context.Log(EngineLogLevel.Info, $"Test audio ({Describe(settings)}) su {string.Join(", ", targets.Select(t => t.Route.Name))}");
    }

    private void OnStopAll(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        foreach (var signal in _context.TestSignals) signal.Stop();
        _context.TestSignals.Clear();
    }

    private static string Describe(TestSignalSettings s) => s.Kind switch
    {
        TestSignalKind.Sine => $"{s.FrequencyHz:0} Hz, {s.LevelDb:0} dBFS",
        TestSignalKind.PinkNoise => $"rumore rosa, {s.LevelDb:0} dBFS",
        TestSignalKind.WhiteNoise => $"rumore bianco, {s.LevelDb:0} dBFS",
        _ => $"sweep {s.SweepFromHz:0}–{s.SweepToHz:0} Hz in {s.SweepSeconds:0.#} s, {s.LevelDb:0} dBFS",
    };

    private void Tick()
    {
        if (_context is null) return;
        _context.TestSignals.RemoveAll(s => !s.IsRunning);
        foreach (var choice in _routes)
        {
            var reading = _context.Audio.TakeRoutePeaks(choice.Route.Id);
            choice.LevelDb = MeterMath.Fall(choice.LevelDb, MeterMath.ToDb(reading.Max), MeterInterval);
            var signal = _context.TestSignals.FirstOrDefault(s => s.RouteId == choice.Route.Id);
            choice.Status = signal is null ? "" : signal.Settings.Kind switch
            {
                TestSignalKind.Sine => $"{signal.Settings.FrequencyHz:0} Hz",
                TestSignalKind.PinkNoise => "rosa",
                TestSignalKind.WhiteNoise => "bianco",
                _ => "sweep",
            };
        }
        StartButton.Content = _context.TestSignals.Count > 0 ? "Avvia / riavvia sulle selezionate" : "Avvia sulle route selezionate";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var r in _routes) r.Selected = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var r in _routes) r.Selected = false; }
}
