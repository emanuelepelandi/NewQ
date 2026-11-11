using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using Vortice.DXGI;

namespace NewQ.App.Video.Gpu;

/// <summary>A hardware graphics card. <see cref="Key"/> stays the same across reboots (the LUID does not).</summary>
public sealed record GpuAdapterInfo(string Key, string Name, long MemoryMB, bool IsDefault);

/// <summary>
/// The graphics card chosen in Impostazioni → Generale (empty key = the Windows default). The renderers create
/// their device on it; libVLC (whose hardware decoders always use the process's default card) is steered with the
/// Windows per-app GPU preference, the only choice Windows offers it: power saving or high performance.
/// </summary>
public static class GpuAdapters
{
    private const string PreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private static volatile string _selected = "";
    private static int _version;

    /// <summary>Key of the chosen card; "" = Windows default.</summary>
    public static string Selected => _selected;

    /// <summary>Changes each time the choice changes: renderers rebuild their device on the new card.</summary>
    public static int Version => System.Threading.Volatile.Read(ref _version);

    public static void Select(string? key)
    {
        key ??= "";
        if (key == _selected) return;
        _selected = key;
        System.Threading.Interlocked.Increment(ref _version);
    }

    /// <summary>Hardware cards in DXGI order (the first is the default one); never the software renderer.</summary>
    public static IReadOnlyList<GpuAdapterInfo> List()
    {
        var result = new List<GpuAdapterInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            foreach (var (adapter, key) in Enumerate(factory))
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    result.Add(new GpuAdapterInfo(key, d.Description.Trim(), (long)(d.DedicatedVideoMemory / (1024 * 1024)), result.Count == 0));
                }
            }
        }
        catch { /* no DXGI: no list, the default card is used */ }
        return result;
    }

    /// <summary>The chosen card, or null for the default one (also when it is no longer installed).</summary>
    internal static IDXGIAdapter1? Open(IDXGIFactory1 factory, string key)
    {
        if (key.Length == 0) return null;
        IDXGIAdapter1? found = null;
        foreach (var (adapter, k) in Enumerate(factory))
        {
            if (found is null && k == key) found = adapter;
            else adapter.Dispose();
        }
        return found;
    }

    /// <summary>Name of the card a renderer is drawing on (log).</summary>
    internal static string NameOf(IDXGIAdapter adapter)
    {
        try { return adapter.Description.Description.Trim(); }
        catch { return "?"; }
    }

    private static IEnumerable<(IDXGIAdapter1 Adapter, string Key)> Enumerate(IDXGIFactory1 factory)
    {
        var seen = new Dictionary<string, int>();
        for (var i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var d = adapter.Description1;
            if ((d.Flags & AdapterFlags.Software) != 0) { adapter.Dispose(); continue; }
            var model = $"{d.VendorId:X4}:{d.DeviceId:X4}:{d.SubsystemId:X8}";
            seen[model] = seen.TryGetValue(model, out var n) ? n + 1 : 0; // two identical cards: 0, 1…
            yield return (adapter, $"{model}:{seen[model]}");
        }
    }

    // ------------------------------------------------------------------ libVLC decoding

    /// <summary>
    /// Points the Windows GPU preference of NewQ.exe at the chosen card, so libVLC decodes on it too. Returns what to
    /// log, or null when nothing changed. Only the GpuPreference entry is touched, and back on the default card only
    /// if NewQ wrote it (<paramref name="owned"/>), never one set by the user in Windows. Applied at the next start.
    /// </summary>
    public static string? ApplyDecoderPreference(string key, ref bool owned)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;
        var cards = List();
        var chosen = cards.FirstOrDefault(c => c.Key == key);
        int wanted;
        if (chosen is null)
        {
            if (!owned) return null; // the user's own Windows setting, if any, stays
            wanted = 0;
        }
        else
        {
            var power = KeyByPreference(GpuPreference.MinimumPower);
            var speed = KeyByPreference(GpuPreference.HighPerformance);
            if (power == speed) wanted = 0; // one kind of card only: Windows offers no choice
            else if (chosen.Key == speed) wanted = 2;
            else if (chosen.Key == power) wanted = 1;
            else wanted = -1;
            if (wanted == 0 && !chosen.IsDefault || wanted == -1)
                return $"Decodifica video su {cards[0].Name}: Windows non consente di sceglierne un'altra.";
            if (wanted == 0 && !owned) return null;
        }

        try
        {
            using var reg = Registry.CurrentUser.CreateSubKey(PreferencesKey);
            var entries = ((reg.GetValue(exe) as string) ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var current = entries.FirstOrDefault(e => e.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase));
            var next = wanted == 0 ? null : $"GpuPreference={wanted}";
            owned = next is not null;
            if (string.Equals(current, next, StringComparison.OrdinalIgnoreCase)) return null;
            entries.RemoveAll(e => e.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase));
            if (next is not null) entries.Add(next);
            if (entries.Count == 0) reg.DeleteValue(exe, false);
            else reg.SetValue(exe, string.Join(";", entries) + ";");
            return chosen is null ? null : $"Decodifica video su {chosen.Name} dal prossimo avvio.";
        }
        catch (Exception ex) { return $"Preferenza GPU di Windows non salvata: {ex.Message}"; }
    }

    private static string? KeyByPreference(GpuPreference preference)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            using var adapter = factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, preference);
            var luid = adapter.Description1.Luid;
            using var all = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            string? key = null;
            foreach (var (a, k) in Enumerate(all))
            {
                if (key is null && a.Description1.Luid.Equals(luid)) key = k;
                a.Dispose();
            }
            return key;
        }
        catch { return null; }
    }
}
