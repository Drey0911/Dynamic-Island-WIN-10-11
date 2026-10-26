// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DynamicIsland.Services;

/// <summary>Interfaz HID encontrada en el sistema (una colección de un dispositivo USB o Bluetooth).</summary>
public sealed record HidInfo(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage,
    int InputLength, int OutputLength, string Product);

/// <summary>Acceso mínimo a dispositivos HID con las APIs de Windows (hid.dll y setupapi.dll).</summary>
public static class Hid
{
    public static List<HidInfo> Enumerate(ushort? vendor = null)
    {
        var result = new List<HidInfo>();
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                string? path = DetailPath(set, ref data);
                if (path == null) continue;
                var info = Describe(path);
                if (info == null) continue;
                if (vendor.HasValue && info.Vid != vendor.Value) continue;
                result.Add(info);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    static string? DetailPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Lee VID/PID, uso y nombre sin abrir el dispositivo para lectura (funciona con mouse y teclados).</summary>
    static HidInfo? Describe(string path)
    {
        using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr)) return null;
        ushort page = 0, usage = 0;
        int inLen = 0, outLen = 0;
        if (HidD_GetPreparsedData(h, out IntPtr pre))
        {
            try
            {
                if (HidP_GetCaps(pre, out HIDP_CAPS caps) == HIDP_STATUS_SUCCESS)
                {
                    page = caps.UsagePage;
                    usage = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                }
            }
            finally { HidD_FreePreparsedData(pre); }
        }
        var name = new byte[256];
        string product = HidD_GetProductString(h, name, name.Length)
            ? System.Text.Encoding.Unicode.GetString(name).TrimEnd('\0').Trim()
            : "";
        return new HidInfo(path, attr.VendorID, attr.ProductID, page, usage, inLen, outLen, product);
    }

    /// <summary>Abre la colección para leer y escribir reportes. Null si otro programa la tiene en exclusiva.</summary>
    public static HidStream? Open(HidInfo info)
    {
        var h = CreateFile(info.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        try { return new HidStream(h, info); }
        catch { h.Dispose(); return null; }
    }

    // --- P/Invoke ---
    const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    // Las funciones HidD_* devuelven BOOLEAN (1 byte), no BOOL.
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FlushQueue(SafeFileHandle h);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}

/// <summary>Colección HID abierta: escribe reportes y lee con tiempo límite.</summary>
public sealed class HidStream : IDisposable
{
    readonly SafeFileHandle _handle;
    readonly FileStream _stream;
    public HidInfo Info { get; }

    internal HidStream(SafeFileHandle handle, HidInfo info)
    {
        _handle = handle;
        Info = info;
        _stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
    }

    /// <summary>Descarta los reportes viejos que Windows tenga en cola.</summary>
    public void Flush() => Hid.HidD_FlushQueue(_handle);

    public bool Write(byte[] report)
    {
        try
        {
            var buf = new byte[Math.Max(report.Length, Info.OutputLength)];
            Array.Copy(report, buf, report.Length);
            var t = _stream.WriteAsync(buf, 0, buf.Length);
            return t.Wait(1000);
        }
        catch { return false; }
    }

    /// <returns>El reporte leído, o null si no llegó nada a tiempo.</returns>
    public async Task<byte[]?> ReadAsync(int timeoutMs)
    {
        var buf = new byte[Math.Max(Info.InputLength, 64)];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            int n = await _stream.ReadAsync(buf, 0, buf.Length, cts.Token);
            if (n <= 0) return null;
            if (n < buf.Length) Array.Resize(ref buf, n);
            return buf;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
        _handle.Dispose();
    }
}
