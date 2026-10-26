// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DynamicIsland.Core;
using LibreHardwareMonitor.Hardware;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace DynamicIsland.Services;

/// <summary>Lectura de rendimiento en un momento dado. null = no disponible.</summary>
public sealed class HardwareSnapshot
{
    public string CpuName = "", GpuName = "";
    public double CpuLoad;
    public double? CpuTemp, CpuClock, CpuPower;
    public double? GpuLoad, GpuTemp, GpuMemUsed, GpuMemTotal, GpuPower, GpuClock, GpuFan;
    public double RamLoad, RamUsed, RamTotal;
    public double Down, Up, DiskLoad, DiskFree;
    public double? Fps, FrameTimeMs, FpsLow1;
    public string FpsApp = "", FpsSource = "";
    public bool FpsCapturing;
    public bool Elevated;
}

/// <summary>
/// Monitor de hardware en un hilo de fondo. Solo trabaja mientras alguien lo necesite
/// (Acquire/Release). CPU, GPU y temperaturas vía LibreHardwareMonitor; FPS vía ETW.
/// </summary>
public sealed class HardwareMonitor
{
    public static HardwareMonitor Instance { get; } = new();

    int _users;
    Thread? _thread;
    Computer? _computer;
    bool _computerFailed;
    readonly FpsService _fps = new();
    PerfCounters? _pdh;
    readonly SystemMonitor _basic = new();
    readonly bool _elevated = Native.IsElevated();
    volatile bool _shutdown;
    string? _wmiGpu, _wmiCpu;

    public HardwareSnapshot Latest { get; private set; } = new();

    public void Acquire()
    {
        Interlocked.Increment(ref _users);
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HardwareMonitor", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _users) < 0) Interlocked.Exchange(ref _users, 0);
    }

    public void Shutdown()
    {
        _shutdown = true;
        try { _computer?.Close(); } catch { }
        _fps.Dispose();
        _pdh?.Dispose();
    }

    void Loop()
    {
        int idle = 0;
        while (!_shutdown)
        {
            if (Volatile.Read(ref _users) <= 0)
            {
                // Sin nadie mirando: tras 10 s se detiene la captura de FPS.
                if (++idle == 20) _fps.Stop();
                Thread.Sleep(500);
                continue;
            }
            idle = 0;
            try { Latest = Sample(); }
            catch (Exception ex) { Log.Error("HardwareMonitor", ex); }
            Thread.Sleep(1000);
        }
    }

    HardwareSnapshot Sample()
    {
        var s = new HardwareSnapshot { Elevated = _elevated };
        _basic.Sample();
        s.CpuLoad = _basic.Cpu;
        s.RamLoad = _basic.Ram;
        s.RamUsed = _basic.RamUsed;
        s.RamTotal = _basic.RamTotal;
        s.Down = _basic.Down;
        s.Up = _basic.Up;
        s.DiskLoad = _basic.Disk;
        s.DiskFree = _basic.DiskFree;

        ReadSensors(s);
        if (s.GpuName.Length == 0) s.GpuName = _wmiGpu ??= QueryName("Win32_VideoController");
        if (s.CpuName.Length == 0) s.CpuName = _wmiCpu ??= QueryName("Win32_Processor");

        // Uso de CPU, GPU y VRAM con los contadores del Administrador de tareas.
        try
        {
            _pdh ??= new PerfCounters();
            if (_pdh.Collect())
            {
                if (_pdh.Cpu() is double cpu) s.CpuLoad = cpu;
                if (_pdh.Gpu() is double gpu) s.GpuLoad = gpu;
                if (_pdh.GpuMemoryUsed() is double vram && vram > 0) s.GpuMemUsed = vram / (1024 * 1024);
            }
        }
        catch (Exception ex) { Log.Error("PDH", ex); }

        // FPS de la app en primer plano.
        _fps.EnsureRunning();
        int pid = Native.ForegroundProcessId();
        try
        {
            using var proc = Process.GetProcessById(pid);
            s.FpsApp = proc.ProcessName;
            if (_fps.Read(proc.ProcessName) is FpsReading r)
            {
                s.Fps = r.Fps;
                s.FrameTimeMs = r.FrameTimeMs;
                s.FpsLow1 = r.Low1;
                s.FpsSource = r.Source;
            }
        }
        catch { }
        s.FpsCapturing = _fps.IsCapturing;
        return s;
    }

    void ReadSensors(HardwareSnapshot s)
    {
        if (_computerFailed) return;
        try
        {
            if (_computer == null)
            {
                _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
                _computer.Open();
            }
        }
        catch (Exception ex)
        {
            Log.Error("LibreHardwareMonitor", ex);
            _computerFailed = true;
            return;
        }

        IHardware? cpu = null, gpu = null;
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
            if (hw.HardwareType == HardwareType.Cpu) cpu ??= hw;
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd) gpu = hw;
            else if (hw.HardwareType == HardwareType.GpuIntel) gpu ??= hw;
        }

        if (cpu != null)
        {
            s.CpuName = Clean(cpu.Name);
            var temps = Sensors(cpu, SensorType.Temperature);
            s.CpuTemp = Pick(temps, "Package", "Tctl", "Tdie", "Core Average", "CPU") ?? Max(temps);
            var clocks = Sensors(cpu, SensorType.Clock).Where(x => x.Name.Contains("Core")).Select(x => x.Value).Where(v => v > 0).ToList();
            if (clocks.Count > 0) s.CpuClock = clocks.Average(v => (double)v!.Value);
            s.CpuPower = Pick(Sensors(cpu, SensorType.Power), "Package", "CPU");
            var load = Pick(Sensors(cpu, SensorType.Load), "Total");
            if (load != null) s.CpuLoad = load.Value / 100.0;
        }

        if (gpu != null)
        {
            s.GpuName = Clean(gpu.Name);
            var loads = Sensors(gpu, SensorType.Load);
            s.GpuLoad = (Pick(loads, "Core", "D3D 3D") ?? Max(loads)) / 100.0;
            var temps = Sensors(gpu, SensorType.Temperature);
            s.GpuTemp = Pick(temps, "Core") ?? Max(temps);
            var data = Sensors(gpu, SensorType.SmallData);
            s.GpuMemUsed = Pick(data, "Memory Used", "D3D Dedicated Memory Used");
            s.GpuMemTotal = Pick(data, "Memory Total", "D3D Dedicated Memory Total");
            s.GpuPower = Pick(Sensors(gpu, SensorType.Power), "Package", "Power", "Core");
            s.GpuClock = Pick(Sensors(gpu, SensorType.Clock), "Core");
            s.GpuFan = Max(Sensors(gpu, SensorType.Fan));
        }
    }

    /// <summary>
    /// Nombre del componente según Windows. Para la GPU se prefiere la dedicada
    /// (la de más memoria) y se ignoran adaptadores virtuales.
    /// </summary>
    static string QueryName(string wmiClass)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher($"SELECT * FROM {wmiClass}");
            string best = "";
            long bestRam = -1;
            foreach (System.Management.ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    string name = mo["Name"]?.ToString()?.Trim() ?? "";
                    if (name.Length == 0 || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                    long ram = 0;
                    if (wmiClass == "Win32_VideoController")
                        try { ram = Convert.ToInt64(mo["AdapterRAM"] ?? 0L); } catch { }
                    if (ram > bestRam) { best = name; bestRam = ram; }
                }
            }
            return Clean(best);
        }
        catch (Exception ex)
        {
            Log.Error("WMI name", ex);
            return "";
        }
    }

    static List<ISensor> Sensors(IHardware hw, SensorType type)
    {
        var list = hw.Sensors.Where(x => x.SensorType == type && x.Value.HasValue).ToList();
        foreach (var sub in hw.SubHardware)
            list.AddRange(sub.Sensors.Where(x => x.SensorType == type && x.Value.HasValue));
        return list;
    }

    static double? Pick(List<ISensor> sensors, params string[] names)
    {
        foreach (var n in names)
        {
            var hit = sensors.FirstOrDefault(x => x.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit?.Value != null) return hit.Value.Value;
        }
        return null;
    }

    static double? Max(List<ISensor> sensors) =>
        sensors.Count == 0 ? null : sensors.Max(x => (double)x.Value!.Value);

    static string Clean(string name) =>
        name.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", "").Replace("  ", " ").Trim();
}
