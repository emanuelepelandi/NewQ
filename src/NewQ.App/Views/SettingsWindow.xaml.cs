using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NewQ.App.Audio;
using NewQ.App.Infrastructure;
using NewQ.App.Settings;
using NewQ.App.Views.Setup;
using NewQ.Core;
using NewQ.Core.Model;

namespace NewQ.App.Views;

public partial class SettingsWindow : Window
{

    private readonly WorkspaceSettings _workspaceSettings;

    public SettingsWindow(AppSettings settings, WorkspaceSettings workspaceSettings, IReadOnlyList<string> midiDevices,
        SetupContext setup, int initialTab = 0)
    {
        InitializeComponent();
        // Fields confirm and lose focus on Enter or a click outside, like the inspector of the main window.
        FieldCommit.Attach(this);
        AudioRoutesPage.Initialize(setup);
        VideoRoutesPage.Initialize(setup);
        TestVideoPage.Initialize(setup);
        TestAudioPage.Initialize(setup);
        ShortcutsPage.Initialize(Core.Input.ShortcutMap.FromSettings(settings.Shortcuts));
        GraphicsPage.Initialize(settings.TimelineCursors);
        Tabs.SelectedIndex = initialTab;
        Result = settings;
        _workspaceSettings = workspaceSettings;

        DriverBox.SelectedIndex = settings.AudioDriver == AudioDriver.Asio ? 1 : 0;

        LatencyBox.Text = settings.LatencyMs.ToString(CultureInfo.CurrentCulture);
        ExclusiveBox.IsChecked = settings.WasapiExclusive;

        var asio = AudioEngine.GetAsioDrivers();
        AsioDriverBox.ItemsSource = asio;
        AsioDriverBox.SelectedItem = asio.FirstOrDefault(d => d == settings.AsioDriverName) ?? asio.FirstOrDefault();
        SampleRateBox.Text = settings.AsioSampleRate.ToString(CultureInfo.CurrentCulture);

        MasterBox.Text = settings.MasterVolumeDb.ToString(CultureInfo.CurrentCulture);
        MidiBox.ItemsSource = midiDevices;
        MidiBox.Text = settings.DefaultMidiDevice;
        OscEnabledBox.IsChecked = settings.OscInputEnabled;
        OscPortBox.Text = settings.OscInputPort.ToString(CultureInfo.CurrentCulture);
        WebEnabledBox.IsChecked = settings.WebRemoteEnabled;
        WebPortBox.Text = settings.WebRemotePort.ToString(CultureInfo.CurrentCulture);
        WebPasswordBox.Text = settings.WebRemotePassword;

        PanicFadeBox.Text = workspaceSettings.PanicFadeSeconds.ToString(CultureInfo.CurrentCulture);
        LockoutBox.Text = workspaceSettings.GoLockoutMs.ToString(CultureInfo.CurrentCulture);
        CueFadeBox.Text = workspaceSettings.CueFadeSeconds.ToString(CultureInfo.CurrentCulture);

        UpdatePanels();
    }

    public AppSettings Result { get; }

    private void OnDriverChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

    /// <summary>The addresses of this PC with the port being typed (where phones and tablets open the page).</summary>
    private void OnWebPortChanged(object sender, TextChangedEventArgs e)
    {
        if (WebAddressBox is null) return; // still loading
        var port = int.TryParse(WebPortBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var p) && p is > 0 and < 65536 ? p : 8080;
        var addresses = Remote.WebRemoteServer.AddressesFor(port);
        WebAddressBox.Text = addresses.Length > 0 ? string.Join("   ", addresses) : "Nessuna rete collegata";
    }

    private void UpdatePanels()
    {
        if (WasapiPanel is null || AsioPanel is null) return; // during InitializeComponent
        var asio = DriverBox.SelectedIndex == 1;
        WasapiPanel.Visibility = asio ? Visibility.Collapsed : Visibility.Visible;
        AsioPanel.Visibility = asio ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        try
        {
            Result.AudioDriver = DriverBox.SelectedIndex == 1 ? AudioDriver.Asio : AudioDriver.Wasapi;
            Result.LatencyMs = Math.Clamp(ParseInt(LatencyBox, "Latenza"), 3, 500);
            Result.WasapiExclusive = ExclusiveBox.IsChecked == true;
            Result.AsioDriverName = AsioDriverBox.SelectedItem as string;
            Result.AsioSampleRate = ParseInt(SampleRateBox, "Frequenza");
            Result.MasterVolumeDb = Math.Clamp(ParseDouble(MasterBox, "Volume master"), Decibels.Floor, 12);
            Result.DefaultMidiDevice = MidiBox.Text.Trim();
            Result.OscInputEnabled = OscEnabledBox.IsChecked == true;
            Result.OscInputPort = Math.Clamp(ParseInt(OscPortBox, "Porta OSC"), 1, 65535);
            Result.WebRemoteEnabled = WebEnabledBox.IsChecked == true;
            Result.WebRemotePort = Math.Clamp(ParseInt(WebPortBox, "Porta HTTP"), 1, 65535);
            Result.WebRemotePassword = WebPasswordBox.Text.Trim();

            // Parse both before touching the (live) workspace settings.
            var panicFade = ParseDouble(PanicFadeBox, "Fade dello Stop");
            var lockout = ParseInt(LockoutBox, "Blocco doppio GO");
            var cueFade = ParseDouble(CueFadeBox, "Fade cue");
            Result.Shortcuts = ShortcutsPage.Map.ToSettings();
            Result.CueColors = GraphicsPage.Result();
            Result.TimelineCursors = GraphicsPage.TimelineCursors;
            _workspaceSettings.PanicFadeSeconds = panicFade;
            _workspaceSettings.GoLockoutMs = lockout;
            _workspaceSettings.CueFadeSeconds = cueFade;
        }
        catch (FormatException ex)
        {
            MessageBox.Show(ex.Message, "Impostazioni", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private static int ParseInt(TextBox box, string field)
        => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v)
            ? v : throw new FormatException($"{field}: valore non valido.");

    private static double ParseDouble(TextBox box, string field)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)
            ? v : throw new FormatException($"{field}: valore non valido.");
}
