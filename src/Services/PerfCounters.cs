// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

/// <summary>
/// Contadores de rendimiento de Windows (PDH): las mismas fuentes que usa el
/// Administrador de tareas para CPU, GPU y memoria de video.
/// </summary>
public sealed class PerfCounters : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_FMT_NOCAP100 = 0x00008000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Explicit)]
    struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);

    IntPtr _query, _cpu, _gpuEngine, _gpuMemory;
    bool _primed;

    public PerfCounters()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        // "% Processor Utility" es lo que muestra el Administrador de tareas en Windows 10/11.
        if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu) != 0)
            PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpu);
        PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuEngine);
        PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _gpuMemory);
    }

    /// <summary>Toma una muestra. La primera solo sirve de referencia (devuelve false).</summary>
    public bool Collect()
    {
        if (_query == IntPtr.Zero) return false;
        if (PdhCollectQueryData(_query) != 0) return false;
        if (!_primed) { _primed = true; return false; }
        return true;
    }

    /// <summary>Uso total del CPU, 0–1.</summary>
    public double? Cpu()
    {
        if (_cpu == IntPtr.Zero) return null;
        if (PdhGetFormattedCounterValue(_cpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) != 0 || v.CStatus > 1) return null;
        return Math.Clamp(v.DoubleValue / 100.0, 0, 1);
    }

    /// <summary>
    /// Uso de la GPU como el Administrador de tareas: por cada motor (3D, copia, video...)
    /// se suma el uso de todos los procesos y se toma el motor más ocupado. 0–1.
    /// </summary>
    public double? Gpu()
    {
        var items = ReadArray(_gpuEngine);
        if (items == null || items.Count == 0) return null;
        var engines = new Dictionary<string, double>();
        foreach (var (name, value) in items)
        {
            // "pid_1234_luid_0x0_0xC6F4_phys_0_eng_0_engtype_3D" -> clave sin el pid
            int i = name.IndexOf("luid_", StringComparison.Ordinal);
            string key = i >= 0 ? name[i..] : name;
            engines[key] = engines.TryGetValue(key, out var sum) ? sum + value : value;
        }
        double max = 0;
        foreach (var v in engines.Values) max = Math.Max(max, v);
        return Math.Clamp(max / 100.0, 0, 1);
    }

    /// <summary>Memoria de video dedicada en uso (bytes) del adaptador más usado.</summary>
    public double? GpuMemoryUsed()
    {
        var items = ReadArray(_gpuMemory);
        if (items == null || items.Count == 0) return null;
        double max = 0;
        foreach (var (_, value) in items) max = Math.Max(max, value);
        return max;
    }

    List<(string name, double value)>? ReadArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero || !_primed) return null;
        uint size = 0;
        int r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (r != PDH_MORE_DATA || size == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer) != 0) return null;
            var result = new List<(string, double)>((int)count);
            const int itemSize = 24; // PDH_FMT_COUNTERVALUE_ITEM_W con relleno
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * itemSize;
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                var value = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE>(item + IntPtr.Size);
                if (value.CStatus <= 1) result.Add((name, value.DoubleValue));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
