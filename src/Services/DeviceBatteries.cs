// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using DynamicIsland.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Services;

public enum DeviceLink { Bluetooth, BluetoothLE, UsbReceiver, Usb }

/// <param name="Percent">Porcentaje que informa el dispositivo, o null si no lo informa.</param>
/// <param name="Approx">El dispositivo solo informa niveles o voltaje, no un porcentaje exacto.</param>
/// <param name="Source">De dónde sale el dato (se muestra al pasar el mouse).</param>
public sealed record DeviceBattery(string Name, string Kind, DeviceLink Link, int? Percent, bool Charging, bool Approx, string Source)
{
    public string? LevelText { get; init; }
}

/// <summary>
/// Reúne la batería de los dispositivos inalámbricos conectados, sin importar si van por
/// Bluetooth o por un receptor USB. Cada fuente lee el valor que entrega el propio dispositivo;
/// nada se estima por tiempo de uso.
/// </summary>
public static class DeviceBatteries
{
    public static List<DeviceBattery> Last { get; private set; } = new();
    public static DateTime LastScan { get; private set; } = DateTime.MinValue;
    static readonly System.Threading.SemaphoreSlim Gate = new(1, 1);

    public static string HeadsetControlPath => Path.Combine(AppContext.BaseDirectory, "headsetcontrol.exe");
    public static bool HeadsetControlAvailable => File.Exists(HeadsetControlPath);

    public static async Task<List<DeviceBattery>> ScanAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var bt = Safe(BluetoothAsync());
            var logi = Safe(Task.Run(() => LogitechHidpp.ReadAsync()));
            var headsets = Safe(Task.Run(() => HeadsetControl()));
            var xbox = Safe(Task.Run(() => XInputPads()));
            var ps = Safe(Task.Run(() => PlayStationAsync()));
            await Task.WhenAll(bt, logi, headsets, xbox, ps);

            var result = Merge(bt.Result, headsets.Result.Concat(logi.Result).Concat(ps.Result).Concat(xbox.Result));
            Last = result;
            LastScan = DateTime.Now;
            return result;
        }
        finally { Gate.Release(); }
    }

    static async Task<List<DeviceBattery>> Safe(Task<List<DeviceBattery>> task)
    {
        try
        {
            var done = await Task.WhenAny(task, Task.Delay(12000));
            return done == task ? task.Result : new List<DeviceBattery>();
        }
        catch (Exception ex) { Log.Error("Devices", ex); return new List<DeviceBattery>(); }
    }

    /// <summary>
    /// Une resultados: si un dispositivo Bluetooth también se leyó por su protocolo propio,
    /// se queda una sola fila con el dato más preciso.
    /// </summary>
    static List<DeviceBattery> Merge(List<DeviceBattery> bluetooth, IEnumerable<DeviceBattery> others)
    {
        var result = new List<DeviceBattery>(bluetooth);
        foreach (var o in others)
        {
            // Solo se une con filas Bluetooth: dos controles iguales por USB siguen siendo dos filas.
            int i = result.FindIndex(0, bluetooth.Count, r => SameDevice(r, o));
            if (i < 0) { result.Add(o); continue; }
            var existing = result[i];
            bool better = existing.Percent == null && existing.LevelText == null
                          || (existing.Approx && o.Percent != null && !o.Approx);
            if (better) result[i] = o with { Link = existing.Link is DeviceLink.Bluetooth or DeviceLink.BluetoothLE ? existing.Link : o.Link, Name = existing.Name };
        }
        return result.OrderBy(r => r.Percent == null && r.LevelText == null).ThenBy(r => r.Name).ToList();
    }

    static bool SameDevice(DeviceBattery a, DeviceBattery b)
    {
        string x = Norm(a.Name), y = Norm(b.Name);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        if (a.Link is not (DeviceLink.Bluetooth or DeviceLink.BluetoothLE)) return false;
        // Un receptor USB no es el mismo dispositivo que uno Bluetooth (salvo los controles Xbox, que XInput no distingue).
        if (b.Link == DeviceLink.UsbReceiver && !y.Contains("xbox")) return false;
        if (x.Contains(y) || y.Contains(x)) return true;
        // Nombres distintos para el mismo control: "Xbox Wireless Controller" / "Control Xbox".
        if (x.Contains("xbox") && y.Contains("xbox")) return true;
        if ((x.Contains("wirelesscontroller") || x.Contains("dualsense")) && (y.Contains("dualsense") || y.Contains("dualshock"))) return true;
        return false;
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // ------------------------------------------------------------------
    // Bluetooth: el valor que Windows recibe del dispositivo (perfil HFP o servicio de batería BLE).
    // ------------------------------------------------------------------
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    static async Task<List<DeviceBattery>> BluetoothAsync()
    {
        var list = new List<DeviceBattery>();
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
                if (level is >= 0 and <= 100) batteries[d.Name.Trim()] = level;
            }
        }
        catch (Exception ex) { Log.Error("BT battery", ex); }

        int? Find(string name)
        {
            if (batteries.TryGetValue(name, out int exact)) return exact;
            var m = batteries.FirstOrDefault(b => b.Key.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(b.Key, StringComparison.OrdinalIgnoreCase));
            return m.Key != null ? m.Value : null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)))
            {
                string name = d.Name?.Trim() ?? "";
                if (name.Length == 0 || !seen.Add(name)) continue;
                string kind = KindFromName(name);
                try
                {
                    using var dev = await BluetoothDevice.FromIdAsync(d.Id);
                    if (dev != null) kind = KindFromClass(dev.ClassOfDevice, name);
                }
                catch { }
                int? pct = Find(name);
                list.Add(new DeviceBattery(name, kind, DeviceLink.Bluetooth, pct, false, false,
                    pct != null ? "Informado por el dispositivo a Windows (Bluetooth)" : "Este dispositivo no informa batería por Bluetooth"));
            }
        }
        catch (Exception ex) { Log.Error("BT classic", ex); }

        try
        {
            foreach (var d in await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)))
            {
                string name = d.Name?.Trim() ?? "";
                if (name.Length == 0 || !seen.Add(name)) continue;
                int? pct = Find(name);
                string source = "Informado por el dispositivo a Windows (Bluetooth LE)";
                if (pct == null)
                {
                    pct = await GattBatteryAsync(d.Id);
                    source = pct != null ? "Servicio de batería Bluetooth LE (leído del dispositivo)" : "Este dispositivo no informa batería";
                }
                list.Add(new DeviceBattery(name, KindFromName(name), DeviceLink.BluetoothLE, pct, false, false, source));
            }
        }
        catch (Exception ex) { Log.Error("BT LE", ex); }
        return list;
    }

    /// <summary>Lee la característica Battery Level (0x2A19) directamente del dispositivo.</summary>
    static async Task<int?> GattBatteryAsync(string id)
    {
        try
        {
            var work = Task.Run(async () =>
            {
                using var dev = await BluetoothLEDevice.FromIdAsync(id);
                if (dev == null) return (int?)null;
                var services = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
                if (services.Status != GattCommunicationStatus.Success) return null;
                foreach (var s in services.Services)
                {
                    using (s)
                    {
                        var chars = await s.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
                        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) continue;
                        var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
                        if (read.Status != GattCommunicationStatus.Success || read.Value.Length < 1) continue;
                        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
                        int v = reader.ReadByte();
                        if (v is >= 0 and <= 100) return v;
                    }
                }
                return null;
            });
            var done = await Task.WhenAny(work, Task.Delay(4000));
            return done == work ? work.Result : null;
        }
        catch { return null; }
    }

    static string KindFromClass(BluetoothClassOfDevice cod, string name)
    {
        try
        {
            switch (cod.MajorClass)
            {
                case BluetoothMajorClass.AudioVideo: return "Audífonos";
                case BluetoothMajorClass.Phone: return "Teléfono";
                case BluetoothMajorClass.Peripheral:
                    int minor = (int)cod.MinorClass;
                    if ((minor & 0x10) != 0 && (minor & 0x20) == 0) return "Teclado";
                    if ((minor & 0x20) != 0 && (minor & 0x10) == 0) return "Mouse";
                    return KindFromName(name);
            }
        }
        catch { }
        return KindFromName(name);
    }

    static string KindFromName(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("mx anywhere")) return "Mouse";
        if (n.Contains("keyboard") || n.Contains("teclado") || n.Contains("keys")) return "Teclado";
        if (n.Contains("controller") || n.Contains("dualsense") || n.Contains("gamepad") || n.Contains("xbox")) return "Control";
        if (n.Contains("bud") || n.Contains("pods") || n.Contains("head") || n.Contains("wh-") || n.Contains("wf-")
            || n.Contains("audif") || n.Contains("earphone") || n.Contains("jbl") || n.Contains("speaker")) return "Audífonos";
        return "Otro";
    }

    // ------------------------------------------------------------------
    // Audífonos inalámbricos USB (receptores de SteelSeries, HyperX, Corsair, Logitech, Sony, etc.)
    // con HeadsetControl, que habla el protocolo de cada fabricante.
    // ------------------------------------------------------------------
    static List<DeviceBattery> HeadsetControl()
    {
        var list = new List<DeviceBattery>();
        if (!HeadsetControlAvailable) return list;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(HeadsetControlPath, "-b -o json")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false, // sin leerlo, un stderr lleno bloquearía el proceso
            });
            if (p == null) return list;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return list; }
            string json = output.Result;
            int start = json.IndexOf('{');
            if (start < 0) return list;

            using var doc = JsonDocument.Parse(json[start..]);
            if (!doc.RootElement.TryGetProperty("devices", out var devices)) return list;
            foreach (var d in devices.EnumerateArray())
            {
                string name = Str(d, "device");
                if (name.Length == 0) name = Str(d, "product");
                if (name.Length == 0) name = "Audífonos USB";
                int? pct = null;
                bool charging = false;
                string? level = null;
                if (d.TryGetProperty("battery", out var b))
                {
                    string status = Str(b, "status");
                    int lv = b.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : -1;
                    if (lv is >= 0 and <= 100) pct = lv;
                    charging = status == "BATTERY_CHARGING";
                    if (status == "BATTERY_UNAVAILABLE") level = "Apagado";
                }
                list.Add(new DeviceBattery(name, "Audífonos", DeviceLink.UsbReceiver, pct, charging, false,
                    pct != null ? "Informado por los audífonos (HeadsetControl)" : "Los audífonos no respondieron")
                { LevelText = pct == null ? level : null });
            }
        }
        catch (Exception ex) { Log.Error("HeadsetControl", ex); }
        return list;
    }

    static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";

    // ------------------------------------------------------------------
    // Controles de Xbox (adaptador inalámbrico USB o Bluetooth). Windows solo da 4 niveles.
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_BATTERY_INFORMATION { public byte BatteryType; public byte BatteryLevel; }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint PacketNumber; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int user, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int user, byte type, out XINPUT_BATTERY_INFORMATION info);

    static List<DeviceBattery> XInputPads()
    {
        var list = new List<DeviceBattery>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out _) != 0) continue;
                if (XInputGetBatteryInformation(i, 0, out var b) != 0) continue;
                if (b.BatteryType is 0 or 1) continue; // desconectado o con cable
                string level = b.BatteryLevel switch { 0 => "Vacía", 1 => "Baja", 2 => "Media", _ => "Alta" };
                int approx = b.BatteryLevel switch { 0 => 5, 1 => 25, 2 => 60, _ => 100 };
                list.Add(new DeviceBattery($"Control Xbox {i + 1}", "Control", DeviceLink.UsbReceiver, approx, false, true,
                    "Windows (XInput) solo informa 4 niveles para controles Xbox") { LevelText = level });
            }
        }
        catch (DllNotFoundException) { }
        catch (Exception ex) { Log.Error("XInput", ex); }
        return list;
    }

    // ------------------------------------------------------------------
    // Controles PlayStation (DualSense y DualShock 4) por USB o Bluetooth.
    // La batería viene en cada reporte de entrada, en pasos de 10 %.
    // ------------------------------------------------------------------
    static async Task<List<DeviceBattery>> PlayStationAsync()
    {
        var list = new List<DeviceBattery>();
        foreach (var info in Hid.Enumerate(0x054C).Where(h => h.UsagePage == 0x01 && h.Usage == 0x05))
        {
            bool dualSense = info.Pid is 0x0CE6 or 0x0DF2;
            bool ds4 = info.Pid is 0x05C4 or 0x09CC or 0x0BA0;
            if (!dualSense && !ds4) continue;
            using var hid = Hid.Open(info);
            if (hid == null) continue;
            hid.Flush();
            byte[]? r = null;
            for (int tries = 0; tries < 6 && r == null; tries++)
            {
                var rep = await hid.ReadAsync(250);
                if (rep == null) break;
                if (rep[0] is 0x01 or 0x31 or 0x11 && rep.Length >= 33) r = rep;
            }
            string name = info.Pid switch { 0x0DF2 => "DualSense Edge", 0x0CE6 => "DualSense", _ => "DualShock 4" };
            bool bluetooth = r != null ? r[0] is 0x31 or 0x11 : info.InputLength > 64;
            int? pct = null;
            bool charging = false;
            if (r != null)
            {
                int offset = dualSense ? (r[0] == 0x31 ? 54 : 53) : (r[0] == 0x11 ? 32 : 30);
                if (offset < r.Length && !(dualSense && r[0] == 0x01 && r.Length < 64))
                {
                    byte b = r[offset];
                    int level = b & 0x0F;
                    if (dualSense)
                    {
                        int status = b >> 4;
                        charging = status == 1;
                        pct = status == 2 ? 100 : Math.Min(level * 10 + 5, 100);
                    }
                    else
                    {
                        bool cable = (b & 0x10) != 0;
                        charging = cable && level <= 10;
                        pct = cable && level >= 11 ? 100 : Math.Min(level * 10 + 5, 100);
                    }
                }
            }
            list.Add(new DeviceBattery(name, "Control", bluetooth ? DeviceLink.Bluetooth : DeviceLink.Usb, pct, charging, pct != null,
                pct != null ? "Informado por el control (pasos de 10 %)"
                    : bluetooth ? "Por Bluetooth el control envía la batería cuando un juego o Steam lo usa"
                    : "Mueve el control para leer la batería"));
        }
        return list;
    }
}
