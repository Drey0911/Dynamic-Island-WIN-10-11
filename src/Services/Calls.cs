// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DynamicIsland.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland.Services;

/// <param name="Key">Identificador de la app (también se busca en el registro de privacidad de Windows).</param>
/// <param name="Processes">Nombres de proceso (sin .exe).</param>
public sealed record CallApp(string Key, string Name, string[] Processes, bool Browser);

/// <summary>Una llamada detectada: la app y los procesos que la sostienen.</summary>
public sealed record CallSignal(CallApp App, string? RegistryKey, int[] Pids);

/// <summary>
/// Detecta si estás en una llamada o chat de voz. Una app está en llamada mientras usa el micrófono:
/// se revisan las sesiones de captura de audio de Windows y el registro de privacidad
/// (el mismo del icono de micrófono de la barra de tareas).
/// </summary>
public static class CallDetector
{
    /// <summary>Apps de llamadas conocidas (nombre limpio aunque el proceso tenga otro).</summary>
    public static readonly CallApp[] Apps =
    {
        new("discord", "Discord", new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" }, false),
        new("teams", "Teams", new[] { "ms-teams", "Teams" }, false),
        new("zoom", "Zoom", new[] { "Zoom" }, false),
        new("whatsapp", "WhatsApp", new[] { "WhatsApp", "WhatsApp.Root" }, false),
        new("slack", "Slack", new[] { "slack" }, false),
        new("telegram", "Telegram", new[] { "Telegram" }, false),
        new("skype", "Skype", new[] { "Skype" }, false),
        new("signal", "Signal", new[] { "Signal" }, false),
        new("webex", "Webex", new[] { "CiscoCollabHost", "webexmta", "atmgr" }, false),
        new("guilded", "Guilded", new[] { "Guilded" }, false),
        new("teamspeak", "TeamSpeak", new[] { "ts3client_win64", "TeamSpeak" }, false),
        new("mumble", "Mumble", new[] { "mumble" }, false),
        new("steam", "Steam", new[] { "steamwebhelper" }, false),
        new("chrome", "Chrome", new[] { "chrome" }, true),
        new("msedge", "Edge", new[] { "msedge" }, true),
        new("firefox", "Firefox", new[] { "firefox" }, true),
        new("brave", "Brave", new[] { "brave" }, true),
        new("opera", "Opera", new[] { "opera" }, true),
        new("vivaldi", "Vivaldi", new[] { "vivaldi" }, true),
    };

    // Programas que usan el micrófono sin ser llamadas (grabadoras, streaming, dictado).
    static readonly HashSet<string> NotCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "obs", "Streamlabs OBS", "streamlabs", "audacity", "SoundRecorder", "VoiceRecorder",
        "nvcontainer", "NVIDIA Share", "nvsphelper64", "ShareX", "CamtasiaStudio", "CamRecorder", "Bandicam", "bdcam",
        "XboxGameBarWidgets", "GameBar", "TextInputHost", "SearchHost", "SearchApp", "Cortana", "DynamicIsland",
        "audiodg", "svchost", "VoiceAccess", "msedgewebview2",
    };

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Busca una llamada en curso. Prefiere apps de llamadas sobre navegadores.</summary>
    public static CallSignal? Detect(bool includeBrowsers)
    {
        var found = new List<CallSignal>();
        var capturing = CapturingProcesses();

        // 1. Sesiones de captura activas en cualquier micrófono.
        foreach (var (pid, process) in capturing)
        {
            var app = ByProcess(process);
            if (app == null || (app.Browser && !includeBrowsers) || found.Any(f => f.App.Key == app.Key)) continue;
            found.Add(new CallSignal(app, null, PidsOf(app)));
        }

        // 2. Registro de privacidad (apps de la Tienda como el Teams nuevo o WhatsApp usan WebView2).
        foreach (var (key, _) in CapabilityUsage.InUse("microphone"))
        {
            // Apps de escritorio: la clave es la ruta del .exe con '#'; se compara solo el nombre del archivo.
            CallApp? app = key.Contains('#')
                ? ByProcess(Path.GetFileNameWithoutExtension(key.Split('#')[^1]))
                : Apps.FirstOrDefault(a => key.Contains(a.Key, StringComparison.OrdinalIgnoreCase));
            if (app == null || (app.Browser && !includeBrowsers)) continue;
            int i = found.FindIndex(f => f.App.Key == app.Key);
            if (i >= 0) found[i] = found[i] with { RegistryKey = key };
            else found.Add(new CallSignal(app, key, PidsOf(app)));
        }

        // 3. Cualquier otra app que use el micrófono y a la vez reproduzca audio (juegos con chat de voz,
        //    apps de llamadas nuevas). Así no hace falta que esté en la lista.
        if (found.Count == 0)
        {
            foreach (var (pid, process) in capturing)
            {
                if (NotCalls.Contains(process) || ByProcess(process) != null) continue;
                var pids = PidsOfName(process);
                if (!HasActiveOutput(pids)) continue;
                if (!Names.TryGetValue(process, out var friendly)) Names[process] = friendly = AppIcons.FriendlyName(pid, process);
                var app = new CallApp("app:" + process.ToLowerInvariant(), friendly, new[] { process }, false);
                found.Add(new CallSignal(app, null, pids));
                break;
            }
        }

        return found.OrderBy(f => f.App.Browser).FirstOrDefault();
    }

    public static CallApp? ByProcess(string process) =>
        Apps.FirstOrDefault(a => a.Processes.Any(p => p.Equals(process, StringComparison.OrdinalIgnoreCase)));

    public static int[] PidsOf(CallApp app) => app.Processes.SelectMany(PidsOfName).ToArray();

    static int[] PidsOfName(string name)
    {
        var pids = new List<int>();
        try
        {
            foreach (var p in Process.GetProcessesByName(name)) { pids.Add(p.Id); p.Dispose(); }
        }
        catch { }
        return pids.ToArray();
    }

    /// <summary>Procesos que están grabando de algún micrófono ahora mismo.</summary>
    static List<(int Pid, string Process)> CapturingProcesses()
    {
        var list = new List<(int, string)>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var dev in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (dev)
                {
                    SessionCollection sessions;
                    try
                    {
                        var manager = dev.AudioSessionManager;
                        manager.RefreshSessions();
                        sessions = manager.Sessions;
                    }
                    catch { continue; }
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            using var s = sessions[i];
                            if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                            int pid = (int)s.GetProcessID;
                            if (pid == 0) continue;
                            string name = ProcessName(pid);
                            if (name.Length > 0) list.Add((pid, name));
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error("Sesiones de captura", ex); }
        return list;
    }

    static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    /// <summary>¿Alguno de estos procesos tiene audio de salida activo?</summary>
    static bool HasActiveOutput(int[] pids)
    {
        if (pids.Length == 0) return false;
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var dev in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                {
                    var manager = dev.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var s = sessions[i];
                        if (s.State == AudioSessionState.AudioSessionStateActive && pids.Contains((int)s.GetProcessID)) return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }
}

/// <summary>Audio de la app de la llamada: silencio (ensordecer) y nivel (para animar la isla).</summary>
public sealed class CallAudio : IDisposable
{
    // Se revisan todas las salidas: Discord o Teams suelen usar el dispositivo de comunicaciones (audífonos).
    readonly List<MMDevice> _devices = new();
    readonly List<AudioSessionControl> _sessions = new();

    public void Attach(int[] pids)
    {
        Dispose();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var dev in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                bool used = false;
                try
                {
                    var manager = dev.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        if (pids.Contains((int)s.GetProcessID)) { _sessions.Add(s); used = true; }
                        else s.Dispose();
                    }
                }
                catch { }
                if (used) _devices.Add(dev); else dev.Dispose();
            }
        }
        catch (Exception ex) { Log.Error("Audio de llamada", ex); }
    }

    public bool HasSessions => _sessions.Count > 0;

    public double Peak
    {
        get
        {
            double max = 0;
            foreach (var s in _sessions)
            {
                try { max = Math.Max(max, s.AudioMeterInformation.MasterPeakValue); } catch { }
            }
            return max;
        }
    }

    public bool Muted
    {
        get
        {
            foreach (var s in _sessions)
            {
                try { return s.SimpleAudioVolume.Mute; } catch { }
            }
            return false;
        }
        set
        {
            foreach (var s in _sessions)
            {
                try { s.SimpleAudioVolume.Mute = value; } catch { }
            }
        }
    }

    public void Dispose()
    {
        foreach (var s in _sessions) { try { s.Dispose(); } catch { } }
        _sessions.Clear();
        foreach (var d in _devices) { try { d.Dispose(); } catch { } }
        _devices.Clear();
    }
}

/// <summary>Ventana principal de la app de la llamada.</summary>
public static class CallWindow
{
    /// <summary>Ventana principal de la app, aunque esté oculta en la bandeja (Discord, Teams).</summary>
    public static IntPtr Find(int[] processIds)
    {
        var pids = new HashSet<int>(processIds);
        if (pids.Count == 0) return IntPtr.Zero;
        IntPtr visible = IntPtr.Zero, hidden = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains((int)pid)) return true;
            if (GetWindow(hwnd, 4) != IntPtr.Zero) return true; // ventanas con dueño (menús, avisos)
            if (GetWindowTextLength(hwnd) == 0) return true;
            if (IsWindowVisible(hwnd)) { if (visible == IntPtr.Zero) visible = hwnd; }
            else if (hidden == IntPtr.Zero) hidden = hwnd;
            return true;
        }, IntPtr.Zero);
        return visible != IntPtr.Zero ? visible : hidden;
    }

    /// <summary>Muestra la ventana (también si estaba en la bandeja) y la trae al frente.</summary>
    public static bool Show(int[] pids)
    {
        IntPtr hwnd = Find(pids);
        if (hwnd == IntPtr.Zero) return false;
        if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, 5);
        Native.BringToFront(hwnd);
        return true;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
}

/// <summary>
/// Icono y nombre reales de cualquier app en ejecución. Las apps de la Tienda (WhatsApp, Teams nuevo,
/// Spotify de la Tienda) no guardan el icono en su .exe, así que se lee el logo de su paquete.
/// </summary>
public static class AppIcons
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ImageSource?> GetAsync(int[] pids)
    {
        foreach (int pid in pids)
        {
            string? package = PackageFullName(pid);
            string? exe = ExePath(pid);
            string key = package ?? exe ?? "";
            if (key.Length == 0) continue;
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;

            ImageSource? img = null;
            if (package != null)
            {
                try { img = await PackageLogoAsync(package); } catch { }
                img ??= ManifestLogo(package);
            }
            if (img == null && exe != null) img = ExeIcon(exe);
            Cache[key] = img;
            if (img != null) return img;
        }
        return null;
    }

    /// <summary>Nombre visible de la app: el del paquete o la descripción del .exe.</summary>
    public static string FriendlyName(int pid, string fallback)
    {
        try
        {
            string? package = PackageFullName(pid);
            if (package != null)
            {
                var pkg = new Windows.Management.Deployment.PackageManager().FindPackageForUser(string.Empty, package);
                if (pkg != null && !string.IsNullOrWhiteSpace(pkg.DisplayName)) return pkg.DisplayName;
            }
            string? exe = ExePath(pid);
            if (exe != null)
            {
                string? desc = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
            }
        }
        catch { }
        return fallback;
    }

    static async Task<ImageSource?> PackageLogoAsync(string fullName)
    {
        var pkg = new Windows.Management.Deployment.PackageManager().FindPackageForUser(string.Empty, fullName);
        if (pkg == null) return null;
        var entries = await pkg.GetAppListEntriesAsync();
        var entry = entries.FirstOrDefault();
        if (entry != null)
        {
            var reference = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await reference.OpenReadAsync();
            var ms = new MemoryStream();
            using (var input = stream.AsStreamForRead()) await input.CopyToAsync(ms);
            ms.Position = 0;
            return Decode(ms);
        }
        if (pkg.Logo != null && pkg.Logo.IsFile && File.Exists(pkg.Logo.LocalPath))
            return Decode(new MemoryStream(File.ReadAllBytes(pkg.Logo.LocalPath)));
        return null;
    }

    /// <summary>Respaldo: logo declarado en AppxManifest.xml (con su variante de escala más grande).</summary>
    static ImageSource? ManifestLogo(string fullName)
    {
        try
        {
            int len = 0;
            GetPackagePathByFullName(fullName, ref len, null);
            if (len <= 0) return null;
            var sb = new StringBuilder(len);
            if (GetPackagePathByFullName(fullName, ref len, sb) != 0) return null;
            string root = sb.ToString();
            var doc = XDocument.Load(Path.Combine(root, "AppxManifest.xml"));
            var visual = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            string? rel = visual?.Attribute("Square44x44Logo")?.Value
                          ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;
            if (string.IsNullOrEmpty(rel)) return null;
            string path = Path.Combine(root, rel);
            string dir = Path.GetDirectoryName(path) ?? root;
            string stem = Path.GetFileNameWithoutExtension(path);
            var best = Directory.Exists(dir)
                ? Directory.GetFiles(dir, stem + "*" + Path.GetExtension(path))
                    .Where(f => !f.Contains("contrast", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                : null;
            best ??= File.Exists(path) ? path : null;
            return best == null ? null : Decode(new MemoryStream(File.ReadAllBytes(best)));
        }
        catch { return null; }
    }

    static ImageSource? ExeIcon(string exe)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return null;
            var bmp = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    static ImageSource? Decode(Stream stream)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Ruta del .exe (funciona también con procesos de otro nivel de permisos).</summary>
    static string? ExePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Nombre completo del paquete si la app es de la Tienda; null si es una app clásica.</summary>
    static string? PackageFullName(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int len = 0;
            if (GetPackageFullName(h, ref len, null) != 122 || len <= 0) return null; // 122: búfer insuficiente
            var sb = new StringBuilder(len);
            return GetPackageFullName(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFullName(IntPtr h, ref int length, StringBuilder? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackagePathByFullName(string fullName, ref int length, StringBuilder? path);
}
