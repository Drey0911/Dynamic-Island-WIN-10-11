// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DynamicIsland.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace DynamicIsland.Services;

public readonly record struct FpsReading(double Fps, double FrameTimeMs, double Low1, string Source);

/// <summary>
/// FPS de la app en primer plano. Usa Intel PresentMon (DirectX, Vulkan, OpenGL y cualquier
/// app que presente fotogramas en Windows) si está junto al ejecutable; si no, un contador
/// ETW propio para DirectX 9–12. Ambos necesitan permiso para eventos ETW
/// (administrador o el grupo "Usuarios del registro de rendimiento").
/// </summary>
public sealed class FpsService : IDisposable
{
    /// <summary>Tiempos entre fotogramas (ms) por nombre de proceso.</summary>
    readonly Dictionary<string, List<(long tick, double ms)>> _frames = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    Process? _presentMon;
    EtwPresentCounter? _etw;
    string _source = "";
    DateTime _nextRetry = DateTime.MinValue, _pmStarted;
    volatile bool _disposed;

    public static string PresentMonPath =>
        Path.Combine(AppContext.BaseDirectory, "PresentMon.exe");

    public static bool PresentMonAvailable => File.Exists(PresentMonPath);

    /// <summary>true si hay una captura de FPS funcionando.</summary>
    public bool IsCapturing => (_presentMon != null && !_presentMon.HasExited) || _etw != null;

    /// <summary>Arranca la captura si no está activa (se reintenta cada 15 s si falla).</summary>
    public void EnsureRunning()
    {
        if (_disposed || _etw != null) return;
        if (_presentMon != null)
        {
            if (!_presentMon.HasExited) return;
            // PresentMon terminó: si fue casi al instante, no tiene permiso; se prueba el contador propio.
            bool quickExit = (DateTime.Now - _pmStarted).TotalSeconds < 3;
            _presentMon.Dispose();
            _presentMon = null;
            if (quickExit)
            {
                _etw = EtwPresentCounter.TryStart(Record);
                _source = _etw != null ? "DirectX" : "";
                return;
            }
        }
        if (DateTime.Now < _nextRetry) return;
        _nextRetry = DateTime.Now.AddSeconds(15);

        if (PresentMonAvailable && StartPresentMon()) return;
        _etw = EtwPresentCounter.TryStart(Record);
        _source = _etw != null ? "DirectX" : "";
    }

    bool StartPresentMon()
    {
        try
        {
            var psi = new ProcessStartInfo(PresentMonPath,
                "--output_stdout --no_console_stats --stop_existing_session --session_name DynamicIslandFPS")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var p = Process.Start(psi);
            if (p == null) return false;
            _presentMon = p;
            _pmStarted = DateTime.Now;
            _source = "PresentMon";
            new Thread(() => ReadPresentMon(p)) { IsBackground = true, Name = "PresentMon" }.Start();
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("PresentMon", ex);
            return false;
        }
    }

    void ReadPresentMon(Process p)
    {
        try
        {
            int appCol = -1, msCol = -1, swapCol = -1;
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                var cols = line.Split(',');
                if (appCol < 0)
                {
                    appCol = Array.IndexOf(cols, "Application");
                    msCol = Array.IndexOf(cols, "MsBetweenPresents");
                    swapCol = Array.IndexOf(cols, "SwapChainAddress");
                    if (msCol < 0) msCol = Array.IndexOf(cols, "FrameTime");
                    if (appCol < 0 || msCol < 0) appCol = -1; // aún no es la cabecera
                    continue;
                }
                if (cols.Length <= Math.Max(appCol, msCol)) continue;
                if (!double.TryParse(cols[msCol], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) || ms <= 0) continue;
                string swap = swapCol >= 0 && swapCol < cols.Length ? cols[swapCol] : "";
                Record(Path.GetFileNameWithoutExtension(cols[appCol]) + "|" + swap, ms);
            }
        }
        catch (Exception ex) { Log.Error("PresentMon read", ex); }
    }

    void Record(string app, double ms)
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (!_frames.TryGetValue(app, out var list)) _frames[app] = list = new List<(long, double)>();
            list.Add((now, ms));
            // Se guardan ~10 s para calcular el 1% low.
            int drop = 0;
            while (drop < list.Count && now - list[drop].tick > 10_000) drop++;
            if (drop > 0) list.RemoveRange(0, drop);
        }
    }

    /// <summary>
    /// FPS del proceso (por nombre, sin extensión). Si la app tiene varias cadenas de
    /// presentación se usa la más activa. null si no presenta fotogramas.
    /// </summary>
    public FpsReading? Read(string processName)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            List<(long tick, double ms)>? list = null;
            string prefix = processName + "|";
            foreach (var (key, frames) in _frames)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || frames.Count == 0) continue;
                if (now - frames[^1].tick > 2000) continue;
                if (list == null || frames.Count > list.Count) list = frames;
            }
            if (list == null) return null;

            // FPS = fotogramas que suman el último segundo de tiempo de juego.
            double sum = 0;
            int n = 0;
            for (int i = list.Count - 1; i >= 0 && sum < 1000; i--)
            {
                sum += list[i].ms;
                n++;
            }
            if (n == 0 || sum <= 0) return null;
            double fps = n * 1000.0 / sum;

            // 1% low: promedio del 1% de fotogramas más lentos de los últimos 10 s.
            var slow = list.Select(f => f.ms).OrderByDescending(x => x).ToList();
            int k = Math.Max(1, slow.Count / 100);
            double low = 1000.0 / slow.Take(k).Average();
            return new FpsReading(fps, sum / n, low, _source);
        }
    }

    /// <summary>Detiene la captura (se vuelve a iniciar con EnsureRunning).</summary>
    public void Stop()
    {
        try { if (_presentMon != null && !_presentMon.HasExited) _presentMon.Kill(); } catch { }
        _presentMon?.Dispose();
        _presentMon = null;
        // Matar el proceso no cierra su sesión ETW: se cierra aquí.
        try { TraceEventSession.GetActiveSession("DynamicIslandFPS")?.Stop(); } catch { }
        _etw?.Dispose();
        _etw = null;
        _nextRetry = DateTime.MinValue;
        lock (_gate) _frames.Clear();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    /// <summary>Contador ETW propio: eventos Present de DXGI (D3D10–12) y D3D9.</summary>
    sealed class EtwPresentCounter : IDisposable
    {
        static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        readonly TraceEventSession _session;
        readonly Action<string, double> _record;
        readonly Dictionary<int, (string name, double lastMs)> _last = new();

        EtwPresentCounter(TraceEventSession session, Action<string, double> record)
        {
            _session = session;
            _record = record;
        }

        public static EtwPresentCounter? TryStart(Action<string, double> record)
        {
            try
            {
                var session = new TraceEventSession("DynamicIsland-FPS") { StopOnDispose = true };
                session.EnableProvider(DxgiProvider);
                session.EnableProvider(D3D9Provider);
                var c = new EtwPresentCounter(session, record);
                session.Source.AllEvents += c.OnEvent;
                new Thread(() =>
                {
                    try { session.Source.Process(); } catch (Exception ex) { Log.Error("ETW", ex); }
                }) { IsBackground = true, Name = "EtwFps" }.Start();
                return c;
            }
            catch (Exception ex)
            {
                Log.Error("ETW FPS", ex);
                return null;
            }
        }

        void OnEvent(TraceEvent e)
        {
            int id = (int)e.ID;
            bool present = (id == 42 && e.ProviderGuid == DxgiProvider) || (id == 1 && e.ProviderGuid == D3D9Provider);
            if (!present) return;
            double t = e.TimeStampRelativeMSec;
            if (_last.TryGetValue(e.ProcessID, out var prev))
            {
                double ms = t - prev.lastMs;
                if (ms > 0 && ms < 1000) _record(prev.name + "|" + e.ProcessID, ms);
                _last[e.ProcessID] = (prev.name, t);
            }
            else
            {
                string name = "";
                try { using var p = Process.GetProcessById(e.ProcessID); name = p.ProcessName; } catch { }
                _last[e.ProcessID] = (name, t);
            }
        }

        public void Dispose()
        {
            try { _session.Dispose(); } catch { }
        }
    }
}
