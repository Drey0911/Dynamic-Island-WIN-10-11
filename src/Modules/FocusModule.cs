// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Modules;

/// <summary>
/// Modo concentración (Pomodoro): bloques de enfoque y descanso. Durante el enfoque
/// puede activar "No molestar" de Windows; se restaura al terminar.
/// </summary>
public sealed class FocusModule : IslandModule
{
    public override string Name => "Concentración";
    public override string Glyph => UI.GTimer;
    public override int Priority => 52;
    public override Size CompactSize => new(180, 28);

    enum Phase { Focus, Break }

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Grid _compact, _bubble = new(), _expanded;
    readonly TextBlock _cLabel, _cTime, _ePhase, _eTime, _eInfo, _pauseGlyph;
    readonly ProgressRing _ring = new(18, 2.5, UI.Accent, UI.AccentSoft);
    readonly ProgressRing _bubbleRing = new(18, 2.5, UI.Accent, UI.AccentSoft);
    readonly ProgressRing _bigRing = new(64, 5, UI.Accent, UI.AccentSoft);
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

    Phase _phase;
    DateTime _endsAt;
    TimeSpan _remaining, _length;
    bool _running;
    int _completed;
    bool _dndOn;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement BubbleView => _bubble;
    public override FrameworkElement ExpandedView => _expanded;

    public bool IsRunning => IsActive;
    public event Action? Changed;

    public FocusModule()
    {
        _compact = new Grid { Margin = new Thickness(12, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.Children.Add(_ring);
        _cLabel = UI.Label("Enfoque", 12, UI.White, FontWeights.SemiBold);
        _cLabel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_cLabel, 1);
        _compact.Children.Add(_cLabel);
        _cTime = UI.Label("25:00", 13, UI.Accent, FontWeights.SemiBold, display: true);
        Grid.SetColumn(_cTime, 2);
        _compact.Children.Add(_cTime);

        _bubbleRing.HorizontalAlignment = HorizontalAlignment.Center;
        _bubbleRing.VerticalAlignment = VerticalAlignment.Center;
        _bubble.Children.Add(_bubbleRing);

        _expanded = new Grid { Margin = new Thickness(26, 14, 24, 16) };
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _expanded.ColumnDefinitions.Add(new ColumnDefinition());
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bigRing.VerticalAlignment = VerticalAlignment.Center;
        _expanded.Children.Add(_bigRing);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        _ePhase = UI.Label("Enfoque", 13, UI.Dim, FontWeights.SemiBold);
        _eTime = UI.Label("25:00", 40, UI.Accent, FontWeights.Light, display: true);
        _eInfo = UI.Label("", 11, UI.Dim);
        mid.Children.Add(_ePhase);
        mid.Children.Add(_eTime);
        mid.Children.Add(_eInfo);
        mid.Children.Add(_dots);
        Grid.SetColumn(mid, 1);
        _expanded.Children.Add(mid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _pauseGlyph = UI.Icon(UI.GPause, 16, UI.Accent);
        buttons.Children.Add(UI.Button(_pauseGlyph, TogglePause, 46, 46, 23, UI.AccentSoft));
        buttons.Children.Add(new Border { Width = 8 });
        var skip = UI.Button(UI.Icon(UI.GNext, 14), Skip, 46, 46, 23);
        skip.ToolTip = "Saltar a la siguiente fase";
        buttons.Children.Add(skip);
        buttons.Children.Add(new Border { Width = 8 });
        buttons.Children.Add(UI.Button(UI.Icon(UI.GStop, 14), StopFocus, 46, 46, 23));
        Grid.SetColumn(buttons, 2);
        _expanded.Children.Add(buttons);

        _tick.Tick += (_, _) => OnTick();
    }

    public override void Stop() => SetDnd(false);

    public void StartFocus()
    {
        _completed = 0;
        Begin(Phase.Focus);
        SetActive(true);
    }

    void Begin(Phase phase)
    {
        var s = Settings.Current;
        _phase = phase;
        bool longBreak = phase == Phase.Break && _completed > 0 && _completed % 4 == 0;
        int minutes = phase == Phase.Focus ? s.FocusMinutes : longBreak ? s.LongBreakMinutes : s.BreakMinutes;
        _length = _remaining = TimeSpan.FromMinutes(Math.Max(1, minutes));
        _endsAt = DateTime.Now + _length;
        _running = true;
        _tick.Start();
        SetDnd(phase == Phase.Focus && s.FocusDoNotDisturb);

        var color = phase == Phase.Focus ? UI.Accent : UI.Green;
        _cLabel.Text = _ePhase.Text = phase == Phase.Focus ? "Enfoque" : longBreak ? "Descanso largo" : "Descanso";
        _cTime.Foreground = _eTime.Foreground = color;
        _eInfo.Text = phase == Phase.Focus
            ? (_dndOn ? "No molestar activado" : "Concéntrate")
            : "Levántate, estira y descansa la vista";
        UpdateViews();
    }

    void OnTick()
    {
        if (!_running) return;
        _remaining = _endsAt - DateTime.Now;
        if (_remaining > TimeSpan.Zero) { UpdateViews(); return; }

        try { SystemSounds.Asterisk.Play(); } catch { }
        if (_phase == Phase.Focus)
        {
            _completed++;
            Begin(Phase.Break);
        }
        else Begin(Phase.Focus);
        Raise();
    }

    void TogglePause()
    {
        if (!IsActive) return;
        if (_running) { _remaining = _endsAt - DateTime.Now; _running = false; }
        else { _endsAt = DateTime.Now + _remaining; _running = true; }
        UpdateViews();
    }

    void Skip()
    {
        if (!IsActive) return;
        if (_phase == Phase.Focus) _completed++;
        Begin(_phase == Phase.Focus ? Phase.Break : Phase.Focus);
        Raise();
    }

    public void StopFocus()
    {
        _tick.Stop();
        _running = false;
        SetDnd(false);
        SetActive(false);
        Changed?.Invoke();
    }

    void SetDnd(bool on)
    {
        if (on == _dndOn) return;
        if (Native.SetFocusAssist(on ? 1 : 0)) _dndOn = on;
        else if (!on) _dndOn = false;
    }

    void UpdateViews()
    {
        var shown = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, _remaining.TotalSeconds)));
        _cTime.Text = _eTime.Text = UI.Fmt(shown);
        double progress = 1 - _remaining.TotalSeconds / Math.Max(1, _length.TotalSeconds);
        _ring.SetProgress(progress);
        _bubbleRing.SetProgress(progress);
        _bigRing.SetProgress(progress);
        _cTime.Opacity = _running ? 1 : 0.55;
        _pauseGlyph.Text = _running ? UI.GPause : UI.GPlay;

        _dots.Children.Clear();
        for (int i = 0; i < 4; i++)
        {
            bool done = i < _completed % 4 || (_completed > 0 && _completed % 4 == 0 && _phase == Phase.Break);
            _dots.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 6, 0),
                Fill = done ? UI.Accent : UI.Faint,
            });
        }
        Changed?.Invoke();
    }
}
