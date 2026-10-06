using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;
using NewQ.Core.Midi;

namespace NewQ.App.Midi;

/// <summary>WinMM MIDI outputs. Devices are opened on first use and kept open.</summary>
public sealed class NAudioMidiOutput : IMidiOutput, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, MidiOut> _open = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> DeviceNames
        => Enumerable.Range(0, MidiOut.NumberOfDevices).Select(i => MidiOut.DeviceInfo(i).ProductName).ToList();

    public void Send(string deviceName, byte[] message)
    {
        lock (_lock)
        {
            var output = GetOrOpen(deviceName);
            if (message.Length > 0 && message[0] == 0xF0)
            {
                output.SendBuffer(message);
            }
            else
            {
                var packed = 0;
                for (var i = 0; i < Math.Min(3, message.Length); i++) packed |= message[i] << (8 * i);
                output.Send(packed);
            }
        }
    }

    private MidiOut GetOrOpen(string deviceName)
    {
        if (_open.TryGetValue(deviceName, out var existing)) return existing;

        for (var i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            if (!string.Equals(MidiOut.DeviceInfo(i).ProductName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            var output = new MidiOut(i);
            _open[deviceName] = output;
            return output;
        }
        throw new InvalidOperationException($"Dispositivo MIDI \"{deviceName}\" non trovato.");
    }

    /// <summary>Closes all devices (call after the device list changes).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            foreach (var output in _open.Values) output.Dispose();
            _open.Clear();
        }
    }

    public void Dispose() => Reset();
}
