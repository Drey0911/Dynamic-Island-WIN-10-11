// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Modules;

/// <summary>
/// Modo juego: con una app a pantalla completa muestra FPS, CPU y GPU en la isla.
/// La música pasa a la gota secundaria y se controla desde allí.
/// </summary>
public sealed class GameModule : IslandModule
{
    public override string Name => "Rendimiento";
    public override string Glyph => UI.GGame;
    public override int Priority => 65;
    public override Size CompactSize => new(300, 28);

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Grid _compact;
    readonly TextBlock _fps, _cpu, _gpu;
    readonly PerfCards _cards = new();
    bool _fullscreen, _acquired;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement ExpandedView => _cards.View;

    public GameModule()
    {
        _compact = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _fps = Stat(row, "FPS");
        _cpu = Stat(row, "CPU");
        _gpu = Stat(row, "GPU");
        _compact.Children.Add(row);
        _tick.Tick += (_, _) => Refresh();
    }

    static TextBlock Stat(StackPanel host, string label)
    {
        var l = UI.Label(label, 11, UI.Dim, FontWeights.SemiBold);
        l.Margin = new Thickness(host.Children.Count == 0 ? 0 : 14, 0, 5, 0);
        host.Children.Add(l);
        var v = UI.Label("--", 12, UI.Accent, FontWeights.SemiBold);
        host.Children.Add(v);
        return v;
    }

    public override void Start() => Settings.Changed += Update;

    public override void Stop()
    {
        _tick.Stop();
        if (_acquired) HardwareMonitor.Instance.Release();
        _acquired = false;
    }

    // Apps a pantalla completa que no son juegos (videos, navegadores, presentaciones).
    static readonly string[] NotGames =
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore",
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayermini", "potplayermini64", "wmplayer",
        "video.ui", "microsoft.media.player", "applicationframehost", "spotify", "powerpnt", "explorer",
        "mspaint", "photos", "microsoft.photos", "acrobat", "acrord32", "winword", "excel", "devenv", "code",
    };

    /// <summary>Recibe el estado de pantalla completa y el proceso en primer plano.</summary>
    public void SetFullscreen(bool fullscreen, int pid)
    {
        bool game = false;
        if (fullscreen && pid > 0)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                game = Array.IndexOf(NotGames, p.ProcessName.ToLowerInvariant()) < 0;
            }
            catch { game = false; }
        }
        _fullscreen = game;
        Update();
    }

    void Update()
    {
        bool on = _fullscreen && Settings.Current.ShowInFullscreen && Settings.Current.GameHud;
        if (on == IsActive) return;
        if (on)
        {
            if (!_acquired) { HardwareMonitor.Instance.Acquire(); _acquired = true; }
            _tick.Start();
            Refresh();
        }
        else
        {
            _tick.Stop();
            if (_acquired) { HardwareMonitor.Instance.Release(); _acquired = false; }
        }
        SetActive(on);
    }

    void Refresh()
    {
        var s = HardwareMonitor.Instance.Latest;
        _fps.Text = s.Fps.HasValue ? $"{s.Fps:0}" : "--";
        _cpu.Text = $"{s.CpuLoad * 100:0}%" + (s.CpuTemp.HasValue ? $"  {s.CpuTemp:0}°" : "");
        _gpu.Text = s.GpuLoad.HasValue ? $"{s.GpuLoad * 100:0}%" + (s.GpuTemp.HasValue ? $"  {s.GpuTemp:0}°" : "") : "--";
        _cpu.Foreground = PerfCards.TempBrush(s.CpuTemp);
        _gpu.Foreground = PerfCards.TempBrush(s.GpuTemp);
        if (_cards.View.IsVisible) _cards.Update(s);
    }
}

/// <summary>Tarjetas de rendimiento (pestaña Sistema y modo juego).</summary>
public sealed class PerfCards
{
    public FrameworkElement View => _root;
    readonly UniformGrid _root;
    readonly Card _fps, _cpu, _gpu, _vram, _ram, _net;

    sealed class Card
    {
        public readonly Border Root;
        public readonly TextBlock Value, Detail, Title;
        public readonly ThinBar Bar = new(4, UI.Accent, UI.Faint);
        readonly string _title;

        public Card(string glyph, string title)
        {
            _title = title;
            var sp = new StackPanel();
            // Encabezado con recorte: icono + título (y el modelo del componente si se conoce).
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.Children.Add(UI.Icon(glyph, 12, UI.Accent));
            Title = UI.Label(title, 12, UI.Dim, FontWeights.SemiBold);
            Title.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(Title, 1);
            head.Children.Add(Title);
            sp.Children.Add(head);
            Value = UI.Label("--", 18, UI.White, FontWeights.SemiBold, display: true);
            Value.Margin = new Thickness(0, 2, 0, 0);
            Detail = UI.Label("", 11, UI.Dim);
            sp.Children.Add(Value);
            sp.Children.Add(Detail);
            Bar.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(Bar);
            Root = UI.Card(sp, 14);
            Root.Padding = new Thickness(12, 6, 12, 6);
            Root.Margin = new Thickness(4);
        }

        /// <summary>Muestra el modelo junto al título, por ejemplo "GPU · GeForce RTX 3060".</summary>
        public void SetModel(string model)
        {
            Title.Text = model.Length > 0 ? $"{_title} · {model}" : _title;
            Root.ToolTip = model.Length > 0 ? model : null;
        }

        public void Set(string value, string detail, double? bar, Brush? color = null)
        {
            Value.Text = value;
            Value.Foreground = color ?? UI.White;
            Detail.Text = detail;
            Bar.Visibility = bar.HasValue ? Visibility.Visible : Visibility.Hidden;
            if (bar.HasValue)
            {
                Bar.SetValue01(bar.Value, animate: true);
                Bar.SetFill(bar.Value > 0.9 ? UI.Red : UI.Accent);
            }
        }
    }

    public PerfCards()
    {
        _root = new UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(14, 0, 14, 6) };
        _fps = Add(UI.GGame, "FPS");
        _cpu = Add(UI.GCpu, "CPU");
        _gpu = Add(UI.GChart, "GPU");
        _vram = Add(UI.GChart, "Memoria de video");
        _ram = Add(UI.GChart, "RAM");
        _net = Add(UI.GWifi, "Red");
    }

    Card Add(string glyph, string title)
    {
        var c = new Card(glyph, title);
        _root.Children.Add(c.Root);
        return c;
    }

    public static Brush TempBrush(double? t) =>
        t is null ? UI.Accent : t >= 85 ? UI.Red : t >= 75 ? UI.Orange : UI.Accent;

    public void Update(HardwareSnapshot s)
    {
        if (s.Fps.HasValue)
            _fps.Set($"{s.Fps:0}", $"1% low {s.FpsLow1:0} · {s.FrameTimeMs:0.0} ms", Math.Min(1, s.Fps.Value / 144));
        else
            _fps.Set("--", s.FpsCapturing ? "Sin fotogramas en esta app" : "Sin permiso: ver Ajustes", null, UI.Dim);
        _fps.Root.ToolTip = s.Fps.HasValue ? $"{s.FpsApp} · medido con {s.FpsSource}" : null;

        string cpuDetail = Join(s.CpuTemp.HasValue ? $"{s.CpuTemp:0} °C" : "Temp.: ver Ajustes",
                                s.CpuClock.HasValue ? $"{s.CpuClock / 1000:0.0} GHz" : "",
                                s.CpuPower.HasValue ? $"{s.CpuPower:0} W" : "");
        _cpu.Set($"{s.CpuLoad * 100:0}%", cpuDetail, s.CpuLoad, TempBrush(s.CpuTemp) == UI.Accent ? UI.White : TempBrush(s.CpuTemp));
        _cpu.SetModel(ShortName(s.CpuName));

        if (s.GpuLoad.HasValue)
        {
            string gpuDetail = Join(s.GpuTemp.HasValue ? $"{s.GpuTemp:0} °C" : "",
                                    s.GpuPower.HasValue ? $"{s.GpuPower:0} W" : "",
                                    s.GpuFan.HasValue ? $"{s.GpuFan:0} RPM" : "");
            _gpu.Set($"{s.GpuLoad * 100:0}%", gpuDetail, s.GpuLoad, TempBrush(s.GpuTemp) == UI.Accent ? UI.White : TempBrush(s.GpuTemp));
        }
        else _gpu.Set("--", "No disponible", null, UI.Dim);
        _gpu.SetModel(ShortName(s.GpuName));

        if (s.GpuMemUsed.HasValue && s.GpuMemTotal is > 0.0)
            _vram.Set($"{s.GpuMemUsed / 1024:0.0} GB", $"de {s.GpuMemTotal / 1024:0.0} GB", s.GpuMemUsed / s.GpuMemTotal);
        else if (s.GpuMemUsed.HasValue)
            _vram.Set($"{s.GpuMemUsed / 1024:0.0} GB", "en uso", null);
        else
            _vram.Set("--", "No disponible", null, UI.Dim);

        _ram.Set($"{s.RamLoad * 100:0}%", $"{UI.Bytes(s.RamUsed)} de {UI.Bytes(s.RamTotal)}", s.RamLoad);
        _net.Set($"{UI.Bytes(s.Down)}/s", $"de bajada · {UI.Bytes(s.Up)}/s de subida", null);
    }

    /// <summary>Nombre corto del componente: "NVIDIA GeForce RTX 3060" -> "GeForce RTX 3060".</summary>
    public static string ShortName(string name)
    {
        string n = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        foreach (var prefix in new[] { "NVIDIA ", "AMD ", "Intel ", "Intel® " })
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) n = n[prefix.Length..];
        return n.Replace("  ", " ").Trim();
    }

    static string Join(params string[] parts) => string.Join(" · ", Array.FindAll(parts, p => p.Length > 0));
}
