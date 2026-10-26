// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using DynamicIsland.Core;
using NAudio.CoreAudioApi;

namespace DynamicIsland.Services;

public sealed record AppAudio(int Pid, string Name, double Volume, bool Muted, double Peak);
public sealed record DeviceAudio(string Name, double Volume, bool Muted, double Peak);

/// <summary>Volumen por aplicación, dispositivos de salida/entrada, micrófono y brillo.</summary>
public static class AudioControl
{
    static readonly Dictionary<int, string> NameCache = new();

    static MMDevice? Default(DataFlow flow, Role role)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return e.GetDefaultAudioEndpoint(flow, role);
        }
        catch { return null; }
    }

    static DeviceAudio? Read(DataFlow flow, Role role)
    {
        using var d = Default(flow, role);
        if (d == null) return null;
        try
        {
            var v = d.AudioEndpointVolume;
            return new DeviceAudio(d.FriendlyName, v.MasterVolumeLevelScalar, v.Mute, d.AudioMeterInformation.MasterPeakValue);
        }
        catch { return null; }
    }

    public static DeviceAudio? Output() => Read(DataFlow.Render, Role.Multimedia);
    public static DeviceAudio? Input() => Read(DataFlow.Capture, Role.Communications);

    public static void SetOutputVolume(double v) => With(DataFlow.Render, Role.Multimedia, d => d.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(v, 0, 1));
    public static void SetInputVolume(double v) => With(DataFlow.Capture, Role.Communications, d => d.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(v, 0, 1));
    public static void ToggleOutputMute() => With(DataFlow.Render, Role.Multimedia, d => d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute);

    /// <summary>Silencia o activa el micrófono predeterminado (afecta a todas las apps).</summary>
    public static bool ToggleMicMute()
    {
        bool muted = false;
        With(DataFlow.Capture, Role.Communications, d =>
        {
            d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute;
            muted = d.AudioEndpointVolume.Mute;
        });
        return muted;
    }

    public static bool IsMicMuted() => Input()?.Muted ?? false;

    static void With(DataFlow flow, Role role, Action<MMDevice> action)
    {
        using var d = Default(flow, role);
        if (d == null) return;
        try { action(d); } catch (Exception ex) { Log.Error("Audio", ex); }
    }

    /// <summary>Apps que tienen sesión de audio en la salida predeterminada.</summary>
    public static List<AppAudio> Apps()
    {
        var result = new Dictionary<int, AppAudio>();
        using var d = Default(DataFlow.Render, Role.Multimedia);
        if (d == null) return new List<AppAudio>();
        try
        {
            var manager = d.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession) continue;
                int pid = (int)s.GetProcessID;
                if (pid == 0) continue;
                double peak = s.AudioMeterInformation.MasterPeakValue;
                var app = new AppAudio(pid, ProcessName(pid, s.DisplayName), s.SimpleAudioVolume.Volume, s.SimpleAudioVolume.Mute, peak);
                if (!result.TryGetValue(pid, out var prev) || peak > prev.Peak) result[pid] = app;
            }
        }
        catch (Exception ex) { Log.Error("Sesiones de audio", ex); }
        return result.Values.OrderByDescending(a => a.Peak).ThenBy(a => a.Name).ToList();
    }

    public static void SetAppVolume(int pid, double v) => ForSessions(pid, s => s.SimpleAudioVolume.Volume = (float)Math.Clamp(v, 0, 1));
    public static void ToggleAppMute(int pid)
    {
        bool? target = null;
        ForSessions(pid, s =>
        {
            target ??= !s.SimpleAudioVolume.Mute;
            s.SimpleAudioVolume.Mute = target.Value;
        });
    }

    static void ForSessions(int pid, Action<AudioSessionControl> action)
    {
        using var d = Default(DataFlow.Render, Role.Multimedia);
        if (d == null) return;
        try
        {
            var manager = d.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
                if ((int)sessions[i].GetProcessID == pid) action(sessions[i]);
        }
        catch (Exception ex) { Log.Error("Volumen de app", ex); }
    }

    static string ProcessName(int pid, string fallback)
    {
        if (NameCache.TryGetValue(pid, out var cached)) return cached;
        string name = fallback;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) name = desc;
            }
            catch { }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(name)) name = "App";
        NameCache[pid] = name;
        return name;
    }

    // ---- Brillo (pantallas integradas de portátiles, vía WMI) ----

    public static int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            foreach (ManagementObject mo in searcher.Get())
                using (mo) return Convert.ToInt32(mo["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static void SetBrightness(int value)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
            foreach (ManagementObject mo in searcher.Get())
                using (mo) mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(value, 0, 100) });
        }
        catch (Exception ex) { Log.Error("Brillo", ex); }
    }
}
