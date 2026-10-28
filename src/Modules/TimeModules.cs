// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Modules;

// ---------------------------------------------------------------------
// Temporizador
// ---------------------------------------------------------------------
public sealed class TimerModule : IslandModule
{
    public override string Name => "Temporizador";
    public override string Glyph => UI.GTimer;
    public override int Priority => _finished ? 95 : 50;
    public override IslandModuleCategory Category => IslandModuleCategory.Timers;
    public override bool IsTransient => _finished;
    public override Size CompactSize => _finished ? new Size(230, 32) : new Size(150, 28);

    TimeSpan _duration, _remaining;
    DateTime _endsAt;
    bool _running, _finished;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _finishHide = new() { Interval = TimeSpan.FromSeconds(6) };

    readonly Grid _compact, _done, _expanded;
    readonly TextBlock _compactTime, _bigTime, _pauseGlyph;
    readonly ProgressRing _ring = new(18, 2.5, UI.Accent, UI.AccentSoft);
    readonly ProgressRing _bigRing = new(56, 4, UI.Accent, UI.AccentSoft);
    readonly Grid _bubble = new();

    public override FrameworkElement CompactView => _finished ? _done : _compact;
    public override FrameworkElement BubbleView => _bubble;
    public override FrameworkElement ExpandedView => _expanded;

    public bool IsRunning => IsActive && !_finished;
    public bool IsPaused => IsRunning && !_running;
    public string RemainingText => UI.Fmt(TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, _remaining.TotalSeconds))));

    /// <summary>Se dispara en cada actualización (lo usa el panel "Tiempo").</summary>
    public event Action? Changed;

    public TimerModule()
    {
        // Compacta
        _compact = new Grid { Margin = new Thickness(12, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.Children.Add(_ring);
        _compactTime = UI.Label("0:00", 13, UI.Accent, FontWeights.SemiBold, display: true);
        _compactTime.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_compactTime, 1);
        _compact.Children.Add(_compactTime);

        // Gota: icono (el anillo ya está en la vista compacta)
        _bubble.Children.Add(UI.Icon(UI.GTimer, 12, UI.Accent));

        // Aviso al terminar
        _done = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        _done.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _done.ColumnDefinitions.Add(new ColumnDefinition());
        _done.Children.Add(UI.Icon(UI.GTimer, 15, UI.Accent));
        var doneText = UI.Label("¡Tiempo terminado!", 13, UI.White, FontWeights.SemiBold);
        doneText.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(doneText, 1);
        _done.Children.Add(doneText);

        // Expandida
        _expanded = new Grid { Margin = new Thickness(26, 14, 24, 14) };
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _expanded.ColumnDefinitions.Add(new ColumnDefinition());
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _bigRing.VerticalAlignment = VerticalAlignment.Center;
        _expanded.Children.Add(_bigRing);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        left.Children.Add(UI.Caption(UI.GTimer, "Temporizador"));
        _bigTime = UI.Label("0:00", 44, UI.Accent, FontWeights.Light, display: true);
        left.Children.Add(_bigTime);
        Grid.SetColumn(left, 1);
        _expanded.Children.Add(left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _pauseGlyph = UI.Icon(UI.GPause, 18, UI.Accent);
        buttons.Children.Add(UI.Button(UI.CenterLabel("+1", 15, UI.White, FontWeights.SemiBold), AddMinute, 48, 48, 24));
        buttons.Children.Add(new Border { Width = 10 });
        buttons.Children.Add(UI.Button(_pauseGlyph, TogglePause, 48, 48, 24, UI.AccentSoft));
        buttons.Children.Add(new Border { Width = 10 });
        buttons.Children.Add(UI.Button(UI.Icon(UI.GStop, 16), StopTimer, 48, 48, 24));
        Grid.SetColumn(buttons, 2);
        _expanded.Children.Add(buttons);

        _tick.Tick += (_, _) => OnTick();
        _finishHide.Tick += (_, _) =>
        {
            _finishHide.Stop();
            _finished = false;
            SetActive(false);
            Changed?.Invoke();
        };
    }

    public void StartTimer(TimeSpan duration)
    {
        _finishHide.Stop();
        _duration = duration;
        _remaining = duration;
        _endsAt = DateTime.Now + duration;
        _running = true;
        _finished = false;
        _tick.Start();
        UpdateViews();
        SetActive(true);
    }

    public void TogglePause()
    {
        if (!IsActive || _finished) return;
        if (_running)
        {
            _remaining = _endsAt - DateTime.Now;
            _running = false;
        }
        else
        {
            _endsAt = DateTime.Now + _remaining;
            _running = true;
        }
        UpdateViews();
    }

    public void AddMinute()
    {
        if (!IsActive || _finished) return;
        var one = TimeSpan.FromMinutes(1);
        _duration += one;
        if (_running) _endsAt += one; else _remaining += one;
        _remaining = _running ? _endsAt - DateTime.Now : _remaining;
        UpdateViews();
    }

    public void StopTimer()
    {
        _tick.Stop();
        _finishHide.Stop();
        _running = false;
        _finished = false;
        SetActive(false);
        Changed?.Invoke();
    }

    void OnTick()
    {
        if (!_running) return;
        _remaining = _endsAt - DateTime.Now;
        if (_remaining <= TimeSpan.Zero) Finish();
        else UpdateViews();
    }

    void Finish()
    {
        _tick.Stop();
        _running = false;
        _finished = true;
        _remaining = TimeSpan.Zero;
        UpdateViews();
        try { SystemSounds.Exclamation.Play(); } catch { }
        _finishHide.Start();
        Raise(); // cambia de prioridad y de vista
    }

    void UpdateViews()
    {
        string text = RemainingText;
        _compactTime.Text = text;
        _bigTime.Text = text;
        _compactTime.Opacity = _running || _finished ? 1 : 0.55;
        _pauseGlyph.Text = _running ? UI.GPause : UI.GPlay;
        double total = Math.Max(1, _duration.TotalSeconds);
        double progress = 1 - _remaining.TotalSeconds / total;
        _ring.SetProgress(progress);
        _bigRing.SetProgress(progress);
        Changed?.Invoke();
    }
}

// ---------------------------------------------------------------------
// Cronómetro
// ---------------------------------------------------------------------
public sealed class StopwatchModule : IslandModule
{
    public override string Name => "Cronómetro";
    public override string Glyph => UI.GClock;
    public override int Priority => 45;
    public override IslandModuleCategory Category => IslandModuleCategory.Timers;
    public override Size CompactSize => new(150, 28);

    readonly System.Diagnostics.Stopwatch _sw = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Grid _compact, _expanded, _bubble = new();
    readonly TextBlock _compactTime, _bigTime, _playGlyph;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement BubbleView => _bubble;
    public override FrameworkElement ExpandedView => _expanded;

    public bool IsRunning => _sw.IsRunning;
    public string ElapsedText => Format(_sw.Elapsed);
    public event Action? Changed;

    public StopwatchModule()
    {
        _compact = new Grid { Margin = new Thickness(12, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.Children.Add(UI.Icon(UI.GClock, 13, UI.Accent));
        _compactTime = UI.Label("0:00.0", 13, UI.Accent, FontWeights.SemiBold, display: true);
        _compactTime.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_compactTime, 1);
        _compact.Children.Add(_compactTime);

        _bubble.Children.Add(UI.Icon(UI.GClock, 12, UI.Accent));

        _expanded = new Grid { Margin = new Thickness(26, 14, 24, 14) };
        _expanded.ColumnDefinitions.Add(new ColumnDefinition());
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(UI.Caption(UI.GClock, "Cronómetro"));
        _bigTime = UI.Label("0:00.0", 44, UI.Accent, FontWeights.Light, display: true);
        left.Children.Add(_bigTime);
        _expanded.Children.Add(left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _playGlyph = UI.Icon(UI.GPlay, 18, UI.Accent);
        buttons.Children.Add(UI.Button(_playGlyph, Toggle, 48, 48, 24, UI.AccentSoft));
        buttons.Children.Add(new Border { Width = 10 });
        buttons.Children.Add(UI.Button(UI.Icon(UI.GReset, 16), Reset, 48, 48, 24));
        Grid.SetColumn(buttons, 1);
        _expanded.Children.Add(buttons);

        _tick.Tick += (_, _) => UpdateViews();
    }

    public void Toggle()
    {
        if (_sw.IsRunning) { _sw.Stop(); _tick.Stop(); }
        else { _sw.Start(); _tick.Start(); }
        UpdateViews();
        if (!IsActive) SetActive(true); else Raise();
    }

    public void Reset()
    {
        _sw.Reset();
        _tick.Stop();
        UpdateViews();
        SetActive(false);
    }

    void UpdateViews()
    {
        string t = ElapsedText;
        _compactTime.Text = t;
        _bigTime.Text = t;
        _compactTime.Opacity = _sw.IsRunning ? 1 : 0.55;
        _playGlyph.Text = _sw.IsRunning ? UI.GPause : UI.GPlay;
        Changed?.Invoke();
    }

    static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
}

// ---------------------------------------------------------------------
// Alarma
// ---------------------------------------------------------------------
public sealed class AlarmModule : IslandModule
{
    public override string Name => "Alarma";
    public override string Glyph => UI.GAlarm;
    public override int Priority => 130; // lo más importante cuando suena
    public override IslandModuleCategory Category => IslandModuleCategory.Timers;
    public override Size CompactSize => new(220, 32);

    readonly Settings _settings = Settings.Current;
    readonly DispatcherTimer _check = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _beep = new() { Interval = TimeSpan.FromSeconds(2.2) };
    readonly Grid _compact, _expanded;
    readonly TextBlock _compactText, _bigTime, _bell, _bigBell;
    DateTime _lastFired = DateTime.MinValue;
    DateTime? _snoozeUntil;
    DateTime _ringStarted;
    SoundPlayer? _player;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement ExpandedView => _expanded;

    public int Hour => _settings.AlarmHour;
    public int Minute => _settings.AlarmMinute;
    public bool Enabled => _settings.AlarmEnabled;
    public bool IsSnoozed => _snoozeUntil != null;
    public string TimeText => $"{Hour:00}:{Minute:00}";
    public event Action? Changed;

    public AlarmModule()
    {
        _compact = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _bell = UI.Icon(UI.GAlarm, 15, UI.Accent);
        _compact.Children.Add(_bell);
        _compactText = UI.Label("Alarma", 13, UI.White, FontWeights.SemiBold);
        _compactText.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_compactText, 1);
        _compact.Children.Add(_compactText);

        _expanded = new Grid { Margin = new Thickness(26, 14, 24, 14) };
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _expanded.ColumnDefinitions.Add(new ColumnDefinition());
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bigBell = UI.Icon(UI.GAlarm, 40, UI.Accent);
        _expanded.Children.Add(_bigBell);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        mid.Children.Add(UI.Caption(UI.GAlarm, "Alarma"));
        _bigTime = UI.Label("07:00", 44, UI.White, FontWeights.Light, display: true);
        mid.Children.Add(_bigTime);
        Grid.SetColumn(mid, 1);
        _expanded.Children.Add(mid);

        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var snooze = new StackPanel { Orientation = Orientation.Horizontal };
        snooze.Children.Add(UI.Icon(UI.GSnooze, 13));
        var st = UI.Label("Posponer 5 min", 12, UI.White, FontWeights.SemiBold);
        st.Margin = new Thickness(6, 0, 0, 0);
        snooze.Children.Add(st);
        buttons.Children.Add(UI.Button(snooze, Snooze, 140, 38, 19));
        buttons.Children.Add(new Border { Height = 8 });
        buttons.Children.Add(UI.Button(UI.CenterLabel("Detener", 13, UI.Black, FontWeights.SemiBold), Dismiss, 140, 38, 19, UI.Accent));
        Grid.SetColumn(buttons, 2);
        _expanded.Children.Add(buttons);

        _check.Tick += (_, _) => Check();
        _beep.Tick += (_, _) =>
        {
            if ((DateTime.Now - _ringStarted).TotalMinutes >= 2) { Dismiss(); return; } // se calla sola a los 2 min
            if (_player == null) { try { SystemSounds.Exclamation.Play(); } catch { } }
        };
    }

    public override void Start() => _check.Start();

    public override void Stop()
    {
        _check.Stop();
        StopSound();
    }

    public void SetTime(int hour, int minute)
    {
        _settings.AlarmHour = ((hour % 24) + 24) % 24;
        _settings.AlarmMinute = ((minute % 60) + 60) % 60;
        _settings.Save();
        Changed?.Invoke();
    }

    public void SetEnabled(bool on)
    {
        _settings.AlarmEnabled = on;
        _snoozeUntil = null;
        _settings.Save();
        Changed?.Invoke();
    }

    void Check()
    {
        if (IsActive) return;
        var now = DateTime.Now;
        if (_snoozeUntil is DateTime s && now >= s)
        {
            _snoozeUntil = null;
            Ring();
            return;
        }
        if (!Enabled) return;
        if (now.Hour == Hour && now.Minute == Minute &&
            !(_lastFired.Date == now.Date && _lastFired.Hour == Hour && _lastFired.Minute == Minute))
        {
            _lastFired = now;
            Ring();
        }
    }

    void Ring()
    {
        _ringStarted = DateTime.Now;
        _compactText.Text = $"Alarma · {TimeText}";
        _bigTime.Text = TimeText;
        Pulse(_bell, true);
        Pulse(_bigBell, true);
        PlaySound();
        _beep.Start();
        SetActive(true);
    }

    void Snooze()
    {
        _snoozeUntil = DateTime.Now.AddMinutes(5);
        Silence();
    }

    void Dismiss()
    {
        _snoozeUntil = null;
        Silence();
    }

    void Silence()
    {
        _beep.Stop();
        StopSound();
        Pulse(_bell, false);
        Pulse(_bigBell, false);
        SetActive(false);
        Changed?.Invoke();
    }

    void PlaySound()
    {
        try
        {
            // Sonidos de alarma incluidos en Windows 10/11
            string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            foreach (var name in new[] { "Alarm01.wav", "Alarm02.wav", "Ring01.wav" })
            {
                string p = Path.Combine(media, name);
                if (!File.Exists(p)) continue;
                _player = new SoundPlayer(p);
                _player.PlayLooping();
                return;
            }
        }
        catch (Exception ex) { Log.Error("Sonido alarma", ex); }
        _player = null; // sin archivo: se usa el sonido del sistema cada 2 s
    }

    void StopSound()
    {
        try { _player?.Stop(); _player?.Dispose(); } catch { }
        _player = null;
    }

    static void Pulse(UIElement e, bool on)
    {
        if (!on)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
            return;
        }
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }
}
