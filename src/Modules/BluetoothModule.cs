// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DynamicIsland.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Modules;

public sealed record BtDevice(string Name, int? Battery);

/// <summary>Dispositivos Bluetooth conectados y su batería (cuando el dispositivo la informa a Windows).</summary>
public static class BluetoothInfo
{
    // DEVPKEY de batería que Windows publica para audífonos, mouse y teclados Bluetooth.
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public static async Task<List<BtDevice>> ConnectedAsync()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selector in new[]
        {
            BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
        })
        {
            try
            {
                foreach (var d in await DeviceInformation.FindAllAsync(selector))
                    if (!string.IsNullOrWhiteSpace(d.Name)) names.Add(d.Name.Trim());
            }
            catch { }
        }
        if (names.Count == 0) return new List<BtDevice>();

        var batteries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var all = await DeviceInformation.FindAllAsync(
                "System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                new[] { BatteryKey }, DeviceInformationKind.Device);
            foreach (var d in all)
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                if (!d.Properties.TryGetValue(BatteryKey, out var v) || v == null) continue;
                int level = Convert.ToInt32(v);
                if (level is < 0 or > 100) continue;
                batteries[d.Name.Trim()] = level;
            }
        }
        catch { }

        return names.Select(n =>
        {
            var match = batteries.FirstOrDefault(b => b.Key.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(b.Key, StringComparison.OrdinalIgnoreCase));
            return new BtDevice(n, match.Key != null ? match.Value : null);
        }).OrderBy(d => d.Name).ToList();
    }

    /// <summary>Último resultado conocido (lo usa la pestaña Audio).</summary>
    public static List<BtDevice> Last { get; set; } = new();
}

/// <summary>Aviso al conectar un dispositivo Bluetooth, con su batería.</summary>
public sealed class BluetoothModule : TransientModule
{
    public override string Name => "Bluetooth";
    public override int Priority => 89;
    public override IslandModuleCategory Category => IslandModuleCategory.System;
    public override Size CompactSize => new(270, 32);
    public override FrameworkElement CompactView => _toast.Root;

    readonly ToastView _toast = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(12) };
    HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    bool _first = true, _busy, _logged;

    public BluetoothModule() => _poll.Tick += async (_, _) => await CheckAsync();

    public override void Start()
    {
        _poll.Start();
        _ = CheckAsync();
    }

    public override void Stop() => _poll.Stop();

    async Task CheckAsync()
    {
        if (_busy || !Settings.Current.BluetoothBattery) return;
        _busy = true;
        try
        {
            var devices = await BluetoothInfo.ConnectedAsync();
            BluetoothInfo.Last = devices;
            var now = new HashSet<string>(devices.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            if (!_first)
            {
                var added = devices.FirstOrDefault(d => !_known.Contains(d.Name));
                if (added != null)
                {
                    // La batería a veces tarda unos segundos en publicarse tras conectar.
                    if (added.Battery == null)
                    {
                        await Task.Delay(4000);
                        added = (await BluetoothInfo.ConnectedAsync()).FirstOrDefault(d => d.Name == added.Name) ?? added;
                    }
                    string glyph = added.Battery is int b ? Battery.Glyph(b / 100.0, false) : "\uE702";
                    var color = added.Battery is int lvl && lvl <= 20 ? UI.Red : UI.Accent;
                    _toast.Show(glyph, added.Name, added.Battery is int pct ? $"{pct} %" : "Conectado", null, color);
                    Flash(3);
                }
            }
            _known = now;
            _first = false;
        }
        catch (Exception ex)
        {
            // Sin Bluetooth en el equipo el error se repetiría cada 12 s: se anota una sola vez.
            if (!_logged) { _logged = true; Log.Error("Bluetooth", ex); }
        }
        finally { _busy = false; }
    }
}
