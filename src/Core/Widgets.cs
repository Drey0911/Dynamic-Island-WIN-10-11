// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicIsland.Core;

/// <summary>
/// Puntitos de audio: uno por banda (graves, medios, agudos). Cada punto se estira
/// en vertical según la energía real de su banda; en silencio quedan quietos.
/// </summary>
public sealed class AudioDots : Canvas
{
    readonly List<Rectangle> _dots = new();
    readonly double _size, _gap, _maxStretch;

    public AudioDots(int count = 3, double size = 4, double gap = 3.5, double maxStretch = 10)
    {
        _size = size;
        _gap = gap;
        _maxStretch = maxStretch;
        Width = count * size + (count - 1) * gap;
        Height = size + maxStretch;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        for (int i = 0; i < count; i++)
        {
            var r = new Rectangle { Width = size, Height = size, RadiusX = size / 2, RadiusY = size / 2, Fill = UI.Accent };
            SetLeft(r, i * (size + gap));
            SetTop(r, maxStretch / 2);
            _dots.Add(r);
            Children.Add(r);
        }
    }

    /// <param name="bands">Energía 0–1 por punto (se reparte cíclicamente si hay más puntos que bandas).</param>
    public void Update(IReadOnlyList<double> bands)
    {
        for (int i = 0; i < _dots.Count; i++)
        {
            double v = bands.Count == 0 ? 0 : Math.Clamp(bands[i % bands.Count], 0, 1);
            double h = _size + v * _maxStretch;
            _dots[i].Height = h;
            SetTop(_dots[i], (Height - h) / 2);
            _dots[i].Opacity = 0.55 + 0.45 * v;
        }
    }
}

/// <summary>
/// Superficie de agua discreta: una ola suave con degradado que se desvanece hacia abajo
/// y una línea de brillo fina. La amplitud la marcan los graves y cada golpe genera una onda.
/// </summary>
public sealed class WaterLayer : Canvas
{
    readonly Path _back = new() { Opacity = 0.5 };
    readonly Path _front = new();
    readonly Path _line = new() { StrokeThickness = 1, Opacity = 0.75 };
    double _width, _height, _phase, _ripple;
    readonly double _level;

    public WaterLayer(double level = 0.78)
    {
        _level = level;
        IsHitTestVisible = false;
        ClipToBounds = true;

        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0x4D, 0xD9, 0xEC), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x4D, 0xD9, 0xEC), 1));
        fill.Freeze();
        _front.Fill = fill;
        _back.Fill = fill;

        // La línea de brillo se desvanece hacia los lados.
        var stroke = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        stroke.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x96, 0xE8, 0xF7), 0));
        stroke.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x96, 0xE8, 0xF7), 0.5));
        stroke.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x96, 0xE8, 0xF7), 1));
        stroke.Freeze();
        _line.Stroke = stroke;

        Children.Add(_back);
        Children.Add(_front);
        Children.Add(_line);
        SizeChanged += (_, e) => { _width = e.NewSize.Width; _height = e.NewSize.Height; };
    }

    /// <param name="bass">Graves 0–1.</param>
    /// <param name="energy">Energía total 0–1 (velocidad del flujo).</param>
    /// <param name="beat">true en el fotograma en que se detecta un golpe.</param>
    public void Update(double dt, double bass, double energy, bool beat)
    {
        if (_width < 2 || _height < 2) return;
        if (beat) _ripple = Math.Min(1, _ripple + 0.7);
        _ripple *= Math.Exp(-dt * 3.5);
        _phase += dt * (0.5 + energy * 2.2);

        double amp = 0.35 + bass * 1.6 + _ripple * 1.4;
        double surface = _height * (_level - bass * 0.08);
        const double k = 0.055;
        _back.Data = Geometry.Parse(IslandGeometry.WaterFill(_width, _height, surface + 1.2, amp * 0.7, -_phase * 0.8 + 1.7, k * 1.3));
        _front.Data = Geometry.Parse(IslandGeometry.WaterFill(_width, _height, surface, amp, _phase, k));
        _line.Data = Geometry.Parse(IslandGeometry.WaveLine(_width, surface, amp, _phase, k));
        Opacity = 0.35 + 0.65 * Math.Clamp(energy * 1.5 + _ripple, 0, 1);
    }

    /// <summary>Agua en calma (pausa).</summary>
    public void Calm()
    {
        _ripple = 0;
        if (_width < 2 || _height < 2) return;
        double surface = _height * _level;
        _back.Data = Geometry.Parse(IslandGeometry.WaterFill(_width, _height, surface + 1.2, 0, 0, 0.05));
        _front.Data = Geometry.Parse(IslandGeometry.WaterFill(_width, _height, surface, 0, 0, 0.05));
        _line.Data = Geometry.Parse(IslandGeometry.WaveLine(_width, surface, 0, 0, 0.05));
        Opacity = 0.25;
    }
}

public sealed class ProgressRing : Grid
{
    readonly Path _arc;
    readonly double _size, _thickness;

    public ProgressRing(double size, double thickness, Brush stroke, Brush track)
    {
        _size = size;
        _thickness = thickness;
        Width = Height = size;
        Children.Add(new Ellipse { Width = size, Height = size, Stroke = track, StrokeThickness = thickness });
        _arc = new Path
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Children.Add(_arc);
    }

    public void SetProgress(double value)
    {
        value = Math.Clamp(value, 0, 0.9999);
        double r = (_size - _thickness) / 2, c = _size / 2;
        double a = (-90 + 360 * value) * Math.PI / 180;
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsFilled = false, IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Cos(a), c + r * Math.Sin(a)), new Size(r, r), 0, value > 0.5, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        _arc.Data = geo;
    }
}

public sealed class ThinBar : Grid
{
    readonly Border _fill;
    readonly ScaleTransform _scale = new(0, 1);

    public ThinBar(double height, Brush fill, Brush track)
    {
        Height = height;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(new Border { Background = track, CornerRadius = new CornerRadius(height / 2) });
        _fill = new Border
        {
            Background = fill,
            CornerRadius = new CornerRadius(height / 2),
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = _scale,
        };
        Children.Add(_fill);
    }

    public void SetFill(Brush b) => _fill.Background = b;

    public void SetValue01(double value, bool animate)
    {
        value = Math.Clamp(value, 0, 1);
        if (!animate)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.ScaleX = value;
            return;
        }
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }
}

/// <summary>Deslizador 0–1 (arrastrar o rueda del mouse).</summary>
public sealed class LevelSlider : Grid
{
    readonly ThinBar _bar;
    readonly Ellipse _knob;
    readonly TranslateTransform _knobMove = new();
    bool _dragging;
    double _value;

    public event Action<double>? ValueChanged;

    public LevelSlider(double height = 22)
    {
        Height = height;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        _bar = new ThinBar(5, UI.Accent, UI.Faint);
        Children.Add(_bar);
        _knob = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = UI.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _knobMove,
            IsHitTestVisible = false,
        };
        Children.Add(_knob);

        MouseLeftButtonDown += (_, e) => { _dragging = true; CaptureMouse(); SetFromMouse(e); e.Handled = true; };
        MouseMove += (_, e) => { if (_dragging) SetFromMouse(e); };
        MouseLeftButtonUp += (_, e) => { _dragging = false; ReleaseMouseCapture(); e.Handled = true; };
        MouseWheel += (_, e) => { e.Handled = true; Set(_value + (e.Delta > 0 ? 0.05 : -0.05), true); };
        SizeChanged += (_, _) => Paint();
    }

    public double Value
    {
        get => _value;
        set { if (!_dragging) { _value = Math.Clamp(value, 0, 1); Paint(); } }
    }

    void SetFromMouse(MouseEventArgs e)
    {
        double w = Math.Max(1, ActualWidth);
        Set(e.GetPosition(this).X / w, true);
    }

    void Set(double v, bool notify)
    {
        _value = Math.Clamp(v, 0, 1);
        Paint();
        if (notify) ValueChanged?.Invoke(_value);
    }

    void Paint()
    {
        _bar.SetValue01(_value, animate: false);
        _knobMove.X = Math.Max(0, _value * ActualWidth - 6);
    }
}

public sealed class ToggleSwitch : Border
{
    readonly Ellipse _knob;
    readonly TranslateTransform _move = new();
    bool _on;
    public event Action<bool>? Toggled;

    public ToggleSwitch(bool on)
    {
        Width = 38;
        Height = 22;
        CornerRadius = new CornerRadius(11);
        Cursor = Cursors.Hand;
        _knob = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = UI.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = _move,
        };
        Child = _knob;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            IsOn = !IsOn;
            Toggled?.Invoke(IsOn);
        };
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        _on = !on;
        IsOn = on;
    }

    public bool IsOn
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Background = value ? UI.Accent : UI.Faint;
            _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(value ? 16 : 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut },
            });
        }
    }
}
