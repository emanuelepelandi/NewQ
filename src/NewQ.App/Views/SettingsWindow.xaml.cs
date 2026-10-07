using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NewQ.App.Audio;
using NewQ.App.Settings;
using NewQ.Core;
using NewQ.Core.Model;

namespace NewQ.App.Views;

public partial class SettingsWindow : Window
{
    private sealed record DeviceItem(string? Id, string Name);

    private readonly WorkspaceSettings _workspaceSettings;

    public SettingsWindow(AppSettings settings, WorkspaceSettings workspaceSettings, IReadOnlyList<string> midiDevices)
    {
        InitializeComponent();
        Result = settings;
        _workspaceSettings = workspaceSettings;

        DriverBox.SelectedIndex = settings.AudioDriver == AudioDriver.Asio ? 1 : 0;

        var devices = new List<DeviceItem> { new(null, "Predefinito di Windows") };
        try { devices.AddRange(AudioEngine.GetWasapiDevices().Select(d => new DeviceItem(d.Id, d.Name))); }
        catch { /* no audio subsystem */ }
        WasapiDeviceBox.ItemsSource = devices;
        WasapiDeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Id == settings.WasapiDeviceId) ?? devices[0];
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

        PanicFadeBox.Text = workspaceSettings.PanicFadeSeconds.ToString(CultureInfo.CurrentCulture);
        LockoutBox.Text = workspaceSettings.GoLockoutMs.ToString(CultureInfo.CurrentCulture);

        UpdatePanels();
    }

    public AppSettings Result { get; }

    private void OnDriverChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

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
            Result.WasapiDeviceId = (WasapiDeviceBox.SelectedItem as DeviceItem)?.Id;
            Result.LatencyMs = Math.Clamp(ParseInt(LatencyBox, "Latenza"), 3, 500);
            Result.WasapiExclusive = ExclusiveBox.IsChecked == true;
            Result.AsioDriverName = AsioDriverBox.SelectedItem as string;
            Result.AsioSampleRate = ParseInt(SampleRateBox, "Frequenza");
            Result.MasterVolumeDb = Math.Clamp(ParseDouble(MasterBox, "Volume master"), Decibels.Floor, 12);
            Result.DefaultMidiDevice = MidiBox.Text.Trim();
            Result.OscInputEnabled = OscEnabledBox.IsChecked == true;
            Result.OscInputPort = Math.Clamp(ParseInt(OscPortBox, "Porta OSC"), 1, 65535);

            // Parse both before touching the (live) workspace settings.
            var panicFade = ParseDouble(PanicFadeBox, "Fade del Panic");
            var lockout = ParseInt(LockoutBox, "Blocco doppio GO");
            _workspaceSettings.PanicFadeSeconds = panicFade;
            _workspaceSettings.GoLockoutMs = lockout;
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
