// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicIsland.Services;

/// <summary>
/// Batería de mouse, teclados y audífonos Logitech por el protocolo HID++ 2.0
/// (receptores Unifying, Lightspeed y Bolt, cable o Bluetooth). El porcentaje lo informa el propio dispositivo.
/// Protocolo documentado por Logitech y por el proyecto Solaar.
/// </summary>
public static class LogitechHidpp
{
    const ushort Vendor = 0x046D;
    const byte LongReport = 0x11, ShortReport = 0x10;
    const byte SwId = 0x0E;
    const int Timeout = 260;

    const ushort FeatName = 0x0005, FeatBattery = 0x1000, FeatVoltage = 0x1001, FeatUnified = 0x1004;

    public static async Task<List<DeviceBattery>> ReadAsync()
    {
        var list = new List<DeviceBattery>();
        List<HidInfo> collections;
        try { collections = Hid.Enumerate(Vendor); }
        catch { return list; }

        // Colección de reportes largos de HID++ (página de uso del fabricante 0xFF00, uso 2).
        foreach (var info in collections.Where(c => c.UsagePage == 0xFF00 && c.Usage == 0x0002 && c.OutputLength >= 20))
        {
            using var hid = Hid.Open(info);
            if (hid == null) continue;
            bool receiver = info.Product.Contains("Receiver", StringComparison.OrdinalIgnoreCase)
                            || info.Product.Contains("Receptor", StringComparison.OrdinalIgnoreCase);
            var indexes = receiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF };
            foreach (byte index in indexes)
            {
                try
                {
                    var d = await ReadDeviceAsync(hid, index, receiver);
                    if (d != null) list.Add(d);
                }
                catch (Exception ex) { Core.Log.Error("HID++", ex); }
            }
        }
        return list;
    }

    static async Task<DeviceBattery?> ReadDeviceAsync(HidStream hid, byte index, bool receiver)
    {
        // Ping: si no responde, no hay dispositivo emparejado o está apagado.
        var ping = await RequestAsync(hid, index, 0x00, 0x1, new byte[] { 0, 0, 0x5A });
        if (ping == null) return null;

        string name = await NameAsync(hid, index);
        string kind = await KindAsync(hid, index);

        int? percent = null;
        bool charging = false, approx = false;
        string source = "";

        byte unified = await FeatureIndexAsync(hid, index, FeatUnified);
        if (unified != 0)
        {
            var r = await RequestAsync(hid, index, unified, 0x1, Array.Empty<byte>());
            if (r != null)
            {
                int soc = r[4], levels = r[5], status = r[6];
                if (soc is > 0 and <= 100) percent = soc;
                else if (levels != 0)
                {
                    percent = (levels & 8) != 0 ? 100 : (levels & 4) != 0 ? 60 : (levels & 2) != 0 ? 20 : 5;
                    approx = true;
                }
                charging = status is 1 or 2;
                source = "HID++ 2.0 (Unified Battery)";
            }
        }

        if (percent == null)
        {
            byte level = await FeatureIndexAsync(hid, index, FeatBattery);
            if (level != 0)
            {
                var r = await RequestAsync(hid, index, level, 0x0, Array.Empty<byte>());
                if (r != null && r[4] is > 0 and <= 100)
                {
                    percent = r[4];
                    charging = r[6] is 1 or 2 or 4;
                    if (r[6] == 3) percent = 100;
                    source = "HID++ 2.0 (Battery Status)";
                }
            }
        }

        if (percent == null)
        {
            byte voltage = await FeatureIndexAsync(hid, index, FeatVoltage);
            if (voltage != 0)
            {
                var r = await RequestAsync(hid, index, voltage, 0x0, Array.Empty<byte>());
                if (r != null)
                {
                    int mv = (r[4] << 8) | r[5];
                    if (mv is > 2500 and < 5000)
                    {
                        percent = FromVoltage(mv);
                        approx = true;
                        charging = (r[6] & 0x80) != 0;
                        source = $"HID++ 2.0 (voltaje {mv} mV)";
                    }
                }
            }
        }

        if (name.Length == 0) name = kind == "Mouse" ? "Mouse Logitech" : kind == "Teclado" ? "Teclado Logitech" : "Dispositivo Logitech";
        return new DeviceBattery(name, kind, receiver ? DeviceLink.UsbReceiver : DeviceLink.Usb, percent, charging, approx,
            percent == null ? "Este modelo no informa batería" : source);
    }

    /// <summary>Curva de descarga de una celda Li-ion típica (la misma idea que usa Solaar).</summary>
    static int FromVoltage(int mv)
    {
        (int mv, int pct)[] curve =
        {
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
            (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
        };
        if (mv >= curve[0].mv) return 100;
        for (int i = 1; i < curve.Length; i++)
        {
            if (mv >= curve[i].mv)
            {
                var (hiV, hiP) = curve[i - 1];
                var (loV, loP) = curve[i];
                return (int)Math.Round(loP + (double)(mv - loV) / (hiV - loV) * (hiP - loP));
            }
        }
        return 0;
    }

    static async Task<byte> FeatureIndexAsync(HidStream hid, byte index, ushort feature)
    {
        var r = await RequestAsync(hid, index, 0x00, 0x0, new[] { (byte)(feature >> 8), (byte)feature });
        return r == null ? (byte)0 : r[4];
    }

    static async Task<string> NameAsync(HidStream hid, byte index)
    {
        byte feat = await FeatureIndexAsync(hid, index, FeatName);
        if (feat == 0) return "";
        var len = await RequestAsync(hid, index, feat, 0x0, Array.Empty<byte>());
        if (len == null || len[4] == 0) return "";
        int total = len[4];
        var sb = new StringBuilder();
        while (sb.Length < total)
        {
            var part = await RequestAsync(hid, index, feat, 0x1, new[] { (byte)sb.Length });
            if (part == null) break;
            int take = Math.Min(16, total - sb.Length);
            int before = sb.Length;
            for (int i = 0; i < take && 4 + i < part.Length; i++)
                if (part[4 + i] != 0) sb.Append((char)part[4 + i]);
            if (sb.Length == before) break;
        }
        return sb.ToString().Trim();
    }

    static async Task<string> KindAsync(HidStream hid, byte index)
    {
        byte feat = await FeatureIndexAsync(hid, index, FeatName);
        if (feat == 0) return "Otro";
        var r = await RequestAsync(hid, index, feat, 0x2, Array.Empty<byte>());
        return r?[4] switch
        {
            0 or 2 => "Teclado",
            3 or 4 or 5 => "Mouse",
            8 => "Audífonos",
            12 or 11 or 10 => "Control",
            _ => "Otro",
        };
    }

    /// <summary>Envía un pedido HID++ 2.0 y espera su respuesta. Null si hay error o no responde.</summary>
    static async Task<byte[]?> RequestAsync(HidStream hid, byte index, byte feature, byte function, byte[] args)
    {
        var msg = new byte[20];
        msg[0] = LongReport;
        msg[1] = index;
        msg[2] = feature;
        msg[3] = (byte)((function << 4) | SwId);
        Array.Copy(args, 0, msg, 4, Math.Min(args.Length, 16));
        hid.Flush();
        if (!hid.Write(msg)) return null;

        var deadline = DateTime.UtcNow.AddMilliseconds(Timeout);
        while (DateTime.UtcNow < deadline)
        {
            int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            var r = await hid.ReadAsync(Math.Max(20, left));
            if (r == null) return null;
            if (r.Length < 7 || r[1] != index) continue;
            if (r[0] == ShortReport && r[2] == 0x8F) return null;                // error HID++ 1.0 (receptor)
            if (r[0] == LongReport && r[2] == 0xFF && r[3] == feature) return null; // error HID++ 2.0
            if (r[0] == LongReport && r[2] == feature && r[3] == msg[3]) return r;
        }
        return null;
    }
}
