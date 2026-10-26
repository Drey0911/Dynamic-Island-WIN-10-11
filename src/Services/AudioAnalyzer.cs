// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using DynamicIsland.Core;
using NAudio.Dsp;
using NAudio.Wave;

namespace DynamicIsland.Services;

/// <summary>
/// Escucha lo que suena en el PC (captura loopback, sin micrófono) y calcula la energía
/// de graves, medios y agudos con una FFT, además de detectar golpes de ritmo.
/// </summary>
public sealed class AudioAnalyzer
{
    public static AudioAnalyzer Instance { get; } = new();

    const int Size = 1024;
    const int Log2Size = 10;

    readonly float[] _samples = new float[Size];
    readonly Complex[] _fft = new Complex[Size];
    readonly float[] _window = new float[Size];
    readonly double[] _max = { 1e-3, 1e-3, 1e-3 };
    int _pos;
    double _bassAvg;
    DateTime _lastBeat = DateTime.MinValue;

    WasapiLoopbackCapture? _capture;
    readonly object _gate = new();

    volatile float _bass, _mid, _treble, _energy;
    volatile int _beats;
    long _lastDataTicks;

    AudioAnalyzer()
    {
        for (int i = 0; i < Size; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (Size - 1))));
    }

    public double Bass => _bass;
    public double Mid => _mid;
    public double Treble => _treble;
    public double Energy => _energy;
    /// <summary>Contador de golpes detectados (compara con el valor anterior).</summary>
    public int Beats => _beats;
    public bool IsRunning => _capture != null;
    public TimeSpan SinceLastData => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref _lastDataTicks));

    public void Start()
    {
        lock (_gate)
        {
            if (_capture != null) return;
            try
            {
                var cap = new WasapiLoopbackCapture();
                cap.DataAvailable += OnData;
                cap.RecordingStopped += (_, _) =>
                {
                    lock (_gate) { if (_capture == cap) _capture = null; }
                    cap.Dispose();
                };
                _capture = cap;
                System.Threading.Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
                cap.StartRecording();
            }
            catch (Exception ex)
            {
                Log.Error("Loopback", ex);
                _capture = null;
            }
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? c;
        lock (_gate) { c = _capture; _capture = null; }
        try { c?.StopRecording(); } catch { }
        _bass = _mid = _treble = _energy = 0;
    }

    /// <summary>Reinicia la captura (por ejemplo, si cambió el dispositivo de salida).</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture || capture != _capture || e.BytesRecorded == 0) return;
        System.Threading.Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);

        var fmt = capture.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        int rate = fmt.SampleRate;

        if (fmt.BitsPerSample == 32)
        {
            int frame = 4 * channels;
            for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += BitConverter.ToSingle(e.Buffer, i + 4 * c);
                Push(sum / channels, rate);
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            int frame = 2 * channels;
            for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(e.Buffer, i + 2 * c) / 32768f;
                Push(sum / channels, rate);
            }
        }
    }

    void Push(float sample, int rate)
    {
        _samples[_pos++] = sample;
        if (_pos < Size) return;
        _pos = 0;
        Analyze(rate);
    }

    void Analyze(int rate)
    {
        double rms = 0;
        for (int i = 0; i < Size; i++)
        {
            _fft[i].X = _samples[i] * _window[i];
            _fft[i].Y = 0;
            rms += _samples[i] * _samples[i];
        }
        rms = Math.Sqrt(rms / Size);
        FastFourierTransform.FFT(true, Log2Size, _fft);

        double hz = (double)rate / Size;
        double bass = Band(30, 160, hz), mid = Band(160, 2000, hz), treble = Band(2000, 10000, hz);

        // Golpe: graves claramente por encima de su promedio reciente.
        var now = DateTime.UtcNow;
        if (bass > _bassAvg * 1.45 && bass > 1e-4 && (now - _lastBeat).TotalMilliseconds > 230)
        {
            _lastBeat = now;
            _beats++;
        }
        _bassAvg = _bassAvg * 0.92 + bass * 0.08;

        _bass = (float)Normalize(0, bass);
        _mid = (float)Normalize(1, mid);
        _treble = (float)Normalize(2, treble);
        _energy = (float)Math.Clamp(rms * 3.2, 0, 1);
    }

    double Band(double from, double to, double hz)
    {
        int a = Math.Max(1, (int)(from / hz)), b = Math.Min(Size / 2 - 1, (int)(to / hz));
        if (b < a) return 0;
        double sum = 0;
        for (int i = a; i <= b; i++) sum += Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
        return sum / (b - a + 1);
    }

    /// <summary>Ganancia automática: cada banda se compara con su máximo reciente.</summary>
    double Normalize(int band, double value)
    {
        _max[band] = Math.Max(_max[band] * 0.997, Math.Max(value, 1e-4));
        return Math.Pow(Math.Clamp(value / _max[band], 0, 1), 0.85);
    }
}
