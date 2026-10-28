// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Modules;

/// <summary>
/// Llamadas y chats de voz (Discord, Teams, Zoom, WhatsApp, Meet y cualquier app que use el micrófono
/// mientras reproduce audio). Se muestra como la música: icono real de la app y actividad de voz.
/// Al abrir la isla: silenciar el micrófono, ensordecer e ir a la app.
/// </summary>
public sealed class CallModule : IslandModule
{
    public override string Name => "Llamada";
    public override string Glyph => UI.GPhone;
    public override int Priority => 75;
    public override IslandModuleCategory Category => IslandModuleCategory.Calls;
    public override Size CompactSize => new(200, 28);
    public override bool WantsFrames => IsActive;

    // Segundos sin micrófono antes de quitar la llamada (evita parpadeos breves).
    const double Grace = 4;

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Grid _compact, _bubble = new(), _expanded;
    readonly Border _cIcon, _eIcon, _bIcon;
    readonly TextBlock _cName, _cMic, _eTitle, _eSub, _status;
    readonly AudioDots _cDots = new(3, 3.5, 3, 9), _eDots = new(5, 4, 3.5, 14);
    readonly RoundAction _mic, _deafen, _open;
    readonly CallAudio _audio = new();
    CallSignal? _call;
    DateTime _lastSeen;
    double _meterTime, _level;
    int _iconRequest;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement BubbleView => _bubble;
    public override FrameworkElement ExpandedView => _expanded;

    /// <summary>Clave del registro de la app en llamada (el aviso de privacidad no la repite).</summary>
    public static string? ActiveKey { get; private set; }

    /// <summary>App en llamada ahora (o null).</summary>
    public static CallApp? ActiveApp { get; private set; }

    public CallModule()
    {
        // --- Isla cerrada: icono, nombre y actividad de voz ---
        _compact = new Grid { Margin = new Thickness(6, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _cIcon = AppIconBox(20);
        _compact.Children.Add(_cIcon);
        _cName = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _cName.Margin = new Thickness(8, 0, 6, 0);
        Grid.SetColumn(_cName, 1);
        _compact.Children.Add(_cName);
        _cMic = UI.Icon("\uEC54", 11, UI.Red);
        _cMic.Margin = new Thickness(0, 0, 8, 0);
        _cMic.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_cMic, 2);
        _compact.Children.Add(_cMic);
        Paint(_cDots, UI.Accent);
        Grid.SetColumn(_cDots, 3);
        _compact.Children.Add(_cDots);

        // --- Gota secundaria ---
        _bIcon = AppIconBox(20);
        _bIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _bubble.Children.Add(_bIcon);

        // --- Isla abierta ---
        _expanded = new Grid { Margin = new Thickness(24, 18, 24, 12) };
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _expanded.RowDefinitions.Add(new RowDefinition());

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _eIcon = AppIconBox(52);
        head.Children.Add(_eIcon);
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _eTitle = UI.Label("", 17, UI.White, FontWeights.SemiBold, display: true);
        _eSub = UI.Label("En llamada", 12, UI.Green);
        titles.Children.Add(_eTitle);
        titles.Children.Add(_eSub);
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        Paint(_eDots, UI.Accent);
        Grid.SetColumn(_eDots, 2);
        head.Children.Add(_eDots);
        _expanded.Children.Add(head);

        var bottom = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _mic = new RoundAction(UI.GMic, "Silenciar", ToggleMic);
        _deafen = new RoundAction(UI.GVolume, "Ensordecer", ToggleDeafen);
        _open = new RoundAction("\uE8A7", "Ir a la app", OpenApp);
        foreach (var b in new[] { _mic, _deafen, _open }) buttons.Children.Add(b.Root);
        bottom.Children.Add(buttons);
        _status = UI.Label("", 11, UI.Dim);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.Margin = new Thickness(0, 4, 0, 0);
        bottom.Children.Add(_status);
        Grid.SetRow(bottom, 1);
        _expanded.Children.Add(bottom);

        _poll.Tick += (_, _) => Check();
    }

    static Border AppIconBox(double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size * 0.28),
        Background = UI.Hex("#3330D158"),
        VerticalAlignment = VerticalAlignment.Center,
        Child = UI.Icon(UI.GPhone, size * 0.5, UI.Green),
    };

    static void Paint(AudioDots dots, Brush brush)
    {
        foreach (var r in dots.Children.OfType<System.Windows.Shapes.Rectangle>()) r.Fill = brush;
    }

    public override void Start()
    {
        Check();
        _poll.Start();
    }

    public override void Stop()
    {
        _poll.Stop();
        _audio.Dispose();
    }

    void Check()
    {
        CallSignal? signal = null;
        if (Settings.Current.ShowCalls)
        {
            try { signal = CallDetector.Detect(Settings.Current.CallsInBrowser); }
            catch (Exception ex) { Log.Error("Llamadas", ex); }
        }

        if (signal != null) _lastSeen = DateTime.Now;
        else if (_call != null && Settings.Current.ShowCalls && (DateTime.Now - _lastSeen).TotalSeconds < Grace) return;

        if (signal == null)
        {
            if (IsActive) End();
            return;
        }

        bool isNew = !IsActive || _call?.App.Key != signal.App.Key;
        _call = signal;
        ActiveKey = signal.RegistryKey;
        ActiveApp = signal.App;
        _audio.Attach(signal.Pids);
        if (isNew) Begin(signal);
        UpdateButtons();
    }

    void Begin(CallSignal s)
    {
        _cName.Text = s.App.Name;
        _eTitle.Text = s.App.Name;
        _status.Text = "";
        SetIcon(null);
        LoadIcon(s.Pids);
        SetActive(true);
    }

    async void LoadIcon(int[] pids)
    {
        int request = ++_iconRequest;
        ImageSource? icon = null;
        try { icon = await AppIcons.GetAsync(pids); } catch { }
        if (request == _iconRequest && IsActive) SetIcon(icon);
    }

    void SetIcon(ImageSource? icon)
    {
        foreach (var box in new[] { _cIcon, _eIcon, _bIcon })
        {
            if (icon != null)
            {
                box.Background = Brushes.Transparent;
                box.Child = new Image { Source = icon, Stretch = Stretch.Uniform };
            }
            else
            {
                box.Background = UI.Hex("#3330D158");
                box.Child = UI.Icon(UI.GPhone, box.Width * 0.5, UI.Green);
            }
        }
    }

    void End()
    {
        // Si se ensordeció desde la isla, se devuelve el audio a la app.
        if (_audio.Muted) _audio.Muted = false;
        _audio.Dispose();
        _call = null;
        ActiveKey = null;
        ActiveApp = null;
        _iconRequest++;
        SetActive(false);
    }

    void UpdateButtons()
    {
        bool micMuted = AudioControl.IsMicMuted();
        _mic.Set(micMuted ? "\uEC54" : UI.GMic, micMuted ? "Activar mic" : "Silenciar", micMuted);
        _cMic.Visibility = micMuted ? Visibility.Visible : Visibility.Collapsed;

        bool deaf = _audio.Muted;
        _deafen.Set(deaf ? UI.GMute : UI.GVolume, deaf ? "Escuchar" : "Ensordecer", deaf);
        _deafen.Root.Opacity = _audio.HasSessions ? 1 : 0.4;

        _eSub.Text = micMuted && deaf ? "En llamada · micrófono y audio silenciados"
            : micMuted ? "En llamada · micrófono silenciado"
            : deaf ? "En llamada · audio silenciado"
            : "En llamada";
    }

    /// <summary>Actividad de voz: los puntos se mueven cuando alguien habla en la llamada.</summary>
    public override void OnFrame(double dt, double time)
    {
        _meterTime += dt;
        if (_meterTime < 0.05) return;
        _meterTime = 0;
        double peak = _audio.Muted ? 0 : _audio.Peak;
        _level = peak > _level ? peak : _level * 0.82;
        double v = Math.Clamp(Math.Sqrt(_level) * 1.2, 0, 1);
        var bands = new[] { v * 0.7, v, v * 0.8, v * 0.6, v * 0.9 };
        _cDots.Update(bands);
        if (_eDots.IsVisible) _eDots.Update(bands);
    }

    void ToggleMic()
    {
        AudioControl.ToggleMicMute();
        UpdateButtons();
    }

    void ToggleDeafen()
    {
        if (!_audio.HasSessions) { Say("La app no tiene audio abierto"); return; }
        _audio.Muted = !_audio.Muted;
        UpdateButtons();
    }

    void OpenApp()
    {
        if (_call == null) return;
        if (!CallWindow.Show(CallDetector.PidsOf(_call.App).Concat(_call.Pids).Distinct().ToArray()))
        {
            Say("No se encontró la ventana de " + _call.App.Name);
            return;
        }
        IslandHost.Collapse?.Invoke();
    }

    void Say(string text)
    {
        _status.Text = text;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) => { t.Stop(); if (_status.Text == text) _status.Text = ""; };
        t.Start();
    }

    /// <summary>Botón redondo con texto debajo, como en la pantalla de llamada del celular.</summary>
    sealed class RoundAction
    {
        public readonly StackPanel Root = new() { Margin = new Thickness(12, 0, 12, 0), Width = 80 };
        readonly TextBlock _icon, _label;

        public RoundAction(string glyph, string label, Action onClick)
        {
            _icon = UI.Icon(glyph, 16, UI.White);
            _icon.HorizontalAlignment = HorizontalAlignment.Center;
            _icon.VerticalAlignment = VerticalAlignment.Center;
            var circle = UI.Button(_icon, onClick, 48, 48, 24);
            circle.HorizontalAlignment = HorizontalAlignment.Center;
            Root.Children.Add(circle);
            _label = UI.CenterLabel(label, 11, UI.Dim);
            _label.Margin = new Thickness(0, 5, 0, 0);
            Root.Children.Add(_label);
        }

        /// <summary>El estado activo (silenciado) se marca en rojo.</summary>
        public void Set(string glyph, string label, bool on)
        {
            _icon.Text = glyph;
            _label.Text = label;
            _icon.Foreground = on ? UI.Red : UI.White;
            _label.Foreground = on ? UI.Red : UI.Dim;
        }
    }
}
