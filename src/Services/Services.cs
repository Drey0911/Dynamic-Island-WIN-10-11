// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Historial de textos copiados (solo en memoria). Además avisa cuando se copia texto
/// o una imagen, para el portapapeles inteligente y las capturas.
/// </summary>
public sealed class ClipboardHistory
{
    public const int Max = 12;
    public ObservableCollection<string> Items { get; } = new();
    public event Action<string>? TextCopied;
    public event Action<System.Windows.Media.Imaging.BitmapSource>? ImageCopied;
    string? _ignoreNext;

    /// <summary>La próxima copia la hace la propia app: no debe generar avisos.</summary>
    public static bool SuppressNext;

    public void OnClipboardChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Read(3)));
    }

    void Read(int attempts)
    {
        try
        {
            if (SuppressNext) { SuppressNext = false; return; }

            // Solo imágenes "puras" (capturas): Office y navegadores copian texto con imagen adjunta.
            if (Clipboard.ContainsImage() && !Clipboard.ContainsText() && !Clipboard.ContainsFileDropList())
            {
                var img = Clipboard.GetImage();
                if (img != null) ImageCopied?.Invoke(img);
                return;
            }
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 5000) text = text[..5000];
            if (_ignoreNext == text) { _ignoreNext = null; return; }
            if (Settings.Current.ClipboardHistory) MoveToTop(text);
            TextCopied?.Invoke(text);
        }
        catch
        {
            // El portapapeles puede estar ocupado por otra app un instante.
            if (attempts <= 0) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            t.Tick += (_, _) => { t.Stop(); Read(attempts - 1); };
            t.Start();
        }
    }

    void MoveToTop(string text)
    {
        int i = Items.IndexOf(text);
        if (i == 0) return;
        if (i > 0) Items.RemoveAt(i);
        Items.Insert(0, text);
        while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
    }

    public void Copy(string text)
    {
        try
        {
            _ignoreNext = text;
            Clipboard.SetText(text);
        }
        catch (Exception ex) { Log.Error("Clipboard", ex); }
    }

    /// <summary>Copia sin generar avisos (para acciones de la propia isla).</summary>
    public static void CopySilently(string text)
    {
        try
        {
            SuppressNext = true;
            Clipboard.SetText(text);
        }
        catch (Exception ex) { SuppressNext = false; Log.Error("Clipboard", ex); }
    }

    public static string? CurrentText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }

    public void Clear() => Items.Clear();
}

/// <summary>
/// Bandeja de archivos. Guarda la ruta de cada archivo (no una copia) y la lista se conserva
/// entre reinicios en %LOCALAPPDATA%\DynamicIsland\shelf.json.
/// </summary>
public sealed class FileShelf
{
    public const int Max = 40;
    public ObservableCollection<string> Files { get; } = new();
    static readonly string FilePath = Path.Combine(Log.Folder, "shelf.json");

    public FileShelf()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
                if (saved != null)
                    foreach (var p in saved)
                        if (File.Exists(p) || Directory.Exists(p)) Files.Add(p);
            }
        }
        catch (Exception ex) { Log.Error("Shelf load", ex); }
        Files.CollectionChanged += (_, _) => Save();
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!(File.Exists(p) || Directory.Exists(p))) continue;
            if (Files.Contains(p)) Files.Remove(p);
            Files.Insert(0, p);
        }
        while (Files.Count > Max) Files.RemoveAt(Files.Count - 1);
    }

    public void Remove(string path) => Files.Remove(path);
    public void Clear() => Files.Clear();

    /// <summary>Quita de la lista los archivos que ya no existen (movidos o borrados).</summary>
    public void Prune()
    {
        foreach (var p in Files.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList()) Files.Remove(p);
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(Files.ToList()));
        }
        catch (Exception ex) { Log.Error("Shelf save", ex); }
    }
}

/// <summary>CPU, RAM, red y disco con APIs de Windows (sin controladores).</summary>
public sealed class SystemMonitor
{
    long _prevIdle, _prevTotal, _prevRx, _prevTx;
    DateTime _prevTime = DateTime.MinValue;

    public double Cpu { get; private set; }
    public double Ram { get; private set; }
    public double RamUsed { get; private set; }
    public double RamTotal { get; private set; }
    public double Down { get; private set; }
    public double Up { get; private set; }
    public double Disk { get; private set; }
    public double DiskFree { get; private set; }

    public void Sample()
    {
        if (Native.TryGetCpuTimes(out long idle, out long total))
        {
            long di = idle - _prevIdle, dt = total - _prevTotal;
            if (_prevTotal > 0 && dt > 0) Cpu = Math.Clamp(1 - (double)di / dt, 0, 1);
            _prevIdle = idle;
            _prevTotal = total;
        }

        (Ram, RamUsed, RamTotal) = Native.GetMemory();

        long rx = 0, tx = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var s = n.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch { }
        var now = DateTime.Now;
        double secs = (now - _prevTime).TotalSeconds;
        if (_prevTime != DateTime.MinValue && secs > 0.2)
        {
            Down = Math.Max(0, rx - _prevRx) / secs;
            Up = Math.Max(0, tx - _prevTx) / secs;
        }
        _prevRx = rx;
        _prevTx = tx;
        _prevTime = now;

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            if (drive.IsReady && drive.TotalSize > 0)
            {
                DiskFree = drive.AvailableFreeSpace;
                Disk = 1 - (double)drive.AvailableFreeSpace / drive.TotalSize;
            }
        }
        catch { }
    }
}

/// <summary>Apps que usan micrófono o cámara ahora mismo (registro de privacidad de Windows).</summary>
public static class CapabilityUsage
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    /// <param name="capability">"microphone" o "webcam".</param>
    /// <returns>Pares (clave del registro, nombre legible).</returns>
    public static List<(string Key, string Name)> InUse(string capability)
    {
        var result = new List<(string, string)>();
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{Root}\{capability}");
            if (root == null) return result;
            Collect(root, result, packaged: true);
            using var np = root.OpenSubKey("NonPackaged");
            if (np != null) Collect(np, result, packaged: false);
        }
        catch { }
        return result;
    }

    static void Collect(Microsoft.Win32.RegistryKey parent, List<(string, string)> result, bool packaged)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (packaged && name == "NonPackaged") continue;
            try
            {
                using var k = parent.OpenSubKey(name);
                if (k == null) continue;
                long start = Convert.ToInt64(k.GetValue("LastUsedTimeStart") ?? 0L);
                long stop = Convert.ToInt64(k.GetValue("LastUsedTimeStop") ?? 0L);
                if (start > 0 && stop == 0) result.Add((name, Friendly(name, packaged)));
            }
            catch { }
        }
    }

    static string Friendly(string key, bool packaged)
    {
        if (!packaged)
        {
            string file = key.Split('#')[^1];
            return Path.GetFileNameWithoutExtension(file);
        }
        string id = key.Split('_')[0];
        return id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;
    }
}
