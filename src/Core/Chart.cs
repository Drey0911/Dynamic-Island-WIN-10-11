// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Core;

/// <summary>
/// Gráfica ligera de línea o barras dibujada directamente (sin controles por punto).
/// En modo mini (isla cerrada) solo dibuja la línea.
/// </summary>
public sealed class ChartView : FrameworkElement
{
    IReadOnlyList<double> _values = Array.Empty<double>();
    IReadOnlyList<string> _labels = Array.Empty<string>();
    bool _bars, _mini;
    Brush _stroke = UI.Accent;
    double? _hover;

    public ChartView(bool mini = false)
    {
        _mini = mini;
        SnapsToDevicePixels = true;
        // Solo mientras está en pantalla, para no retener gráficas viejas.
        Loaded += (_, _) => { UI.AccentChanged -= InvalidateVisual; UI.AccentChanged += InvalidateVisual; };
        Unloaded += (_, _) => UI.AccentChanged -= InvalidateVisual;
        if (!mini)
        {
            MouseMove += (_, e) => { _hover = e.GetPosition(this).X; InvalidateVisual(); };
            MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        }
    }

    public void SetData(IReadOnlyList<double> values, IReadOnlyList<string>? labels, bool bars, Brush? stroke = null)
    {
        _values = values.ToList();
        _labels = labels?.ToList() ?? new List<string>();
        _bars = bars;
        _stroke = stroke ?? UI.Accent;
        InvalidateVisual();
    }

    public int Count => _values.Count;

    /// <summary>Cambio porcentual entre el primer y el último valor (para el encabezado de la tarjeta).</summary>
    public static double? Change(IReadOnlyList<double> v)
    {
        if (v.Count < 2 || v[0] == 0) return null;
        return (v[^1] - v[0]) / Math.Abs(v[0]) * 100;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // para recibir el mouse
        if (w < 4 || h < 4) return;
        if (_values.Count == 0)
        {
            if (!_mini)
            {
                var t = Text("Esperando datos...", 11, UI.Dim);
                dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
            }
            return;
        }

        double padTop = _mini ? 2 : 14, padBottom = _mini ? 2 : (_labels.Count > 0 ? 16 : 4), padLeft = 0, padRight = _mini ? 0 : 2;
        double min = _values.Min(), max = _values.Max();
        if (_bars && min > 0) min = Math.Min(min, max - (max - min) * 1.6); // las barras necesitan una base visible
        if (Math.Abs(max - min) < 1e-12) { max += 1; min -= 1; }
        double plotW = w - padLeft - padRight, plotH = h - padTop - padBottom;
        double X(int i) => padLeft + (_values.Count == 1 ? plotW / 2 : plotW * i / (_values.Count - 1));
        double Y(double v) => padTop + plotH * (1 - (v - min) / (max - min));

        if (!_mini)
        {
            // Líneas guía suaves arriba, al medio y abajo.
            var grid = new Pen(UI.Hex("#14FFFFFF"), 1);
            grid.Freeze();
            for (int g = 0; g <= 2; g++)
            {
                double y = padTop + plotH * g / 2.0;
                dc.DrawLine(grid, new Point(padLeft, Math.Round(y) + 0.5), new Point(w - padRight, Math.Round(y) + 0.5));
            }
        }

        var color = (_stroke as SolidColorBrush)?.Color ?? Colors.Cyan;
        if (_bars)
        {
            int n = _values.Count;
            double slot = plotW / n, bw = Math.Max(1, slot * 0.68);
            var fill = new SolidColorBrush(Color.FromArgb(_mini ? (byte)200 : (byte)170, color.R, color.G, color.B));
            fill.Freeze();
            var maxFill = new SolidColorBrush(color);
            maxFill.Freeze();
            int maxIndex = IndexOfMax();
            for (int i = 0; i < n; i++)
            {
                double x = padLeft + slot * i + (slot - bw) / 2, y = Y(_values[i]);
                double bottom = padTop + plotH;
                var r = new Rect(x, Math.Min(y, bottom - 1), bw, Math.Max(1, bottom - y));
                dc.DrawRoundedRectangle(i == maxIndex ? maxFill : fill, null, r, Math.Min(2, bw / 2), Math.Min(2, bw / 2));
            }
        }
        else
        {
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var c = line.Open())
            using (var a = area.Open())
            {
                c.BeginFigure(new Point(X(0), Y(_values[0])), false, false);
                a.BeginFigure(new Point(X(0), padTop + plotH), true, true);
                a.LineTo(new Point(X(0), Y(_values[0])), false, false);
                for (int i = 1; i < _values.Count; i++)
                {
                    var p = new Point(X(i), Y(_values[i]));
                    c.LineTo(p, true, true);
                    a.LineTo(p, false, false);
                }
                a.LineTo(new Point(X(_values.Count - 1), padTop + plotH), false, false);
            }
            line.Freeze();
            area.Freeze();
            var gradient = new LinearGradientBrush(
                Color.FromArgb(_mini ? (byte)70 : (byte)95, color.R, color.G, color.B),
                Color.FromArgb(0, color.R, color.G, color.B), 90);
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
            // Copia congelada: congelar el pincel compartido del acento impediría cambiarlo luego.
            var strokeBrush = new SolidColorBrush(color);
            strokeBrush.Freeze();
            var pen = new Pen(strokeBrush, _mini ? 1.4 : 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
            var last = new Point(X(_values.Count - 1), Y(_values[^1]));
            dc.DrawEllipse(strokeBrush, null, last, _mini ? 1.8 : 3, _mini ? 1.8 : 3);
        }

        if (_mini) return;

        // Máximo y mínimo arriba a la izquierda; primera y última etiqueta abajo.
        var top = Text($"máx {Short(_values.Max())} · mín {Short(_values.Min())}", 10, UI.Dim);
        dc.DrawText(top, new Point(padLeft, 0));
        if (_labels.Count > 0)
        {
            var l0 = Text(Label(_labels[0]), 10, UI.Dim);
            var l1 = Text(Label(_labels[^1]), 10, UI.Dim);
            dc.DrawText(l0, new Point(padLeft, h - l0.Height));
            dc.DrawText(l1, new Point(w - padRight - l1.Width, h - l1.Height));
        }

        // Valor bajo el cursor.
        if (_hover is double hx && _values.Count > 0)
        {
            int i = (int)Math.Round((hx - padLeft) / Math.Max(1, plotW) * (_values.Count - 1));
            i = Math.Clamp(i, 0, _values.Count - 1);
            double x = _bars ? padLeft + plotW / _values.Count * (i + 0.5) : X(i);
            var guide = new Pen(UI.Hex("#55FFFFFF"), 1);
            guide.Freeze();
            dc.DrawLine(guide, new Point(x, padTop), new Point(x, padTop + plotH));
            string label = i < _labels.Count ? Label(_labels[i]) + "  " : "";
            var tip = Text(label + Short(_values[i]), 11, UI.White);
            double tx = Math.Clamp(x - tip.Width / 2, 0, Math.Max(0, w - tip.Width - 8));
            var box = new Rect(tx, padTop, tip.Width + 8, tip.Height + 4);
            dc.DrawRoundedRectangle(UI.Hex("#E6181B20"), null, box, 6, 6);
            dc.DrawText(tip, new Point(tx + 4, padTop + 2));
        }
    }

    int IndexOfMax()
    {
        int idx = 0;
        for (int i = 1; i < _values.Count; i++) if (_values[i] > _values[idx]) idx = i;
        return idx;
    }

    /// <summary>"2026-10-27T14:00" -> "14:00"; textos largos se recortan.</summary>
    static string Label(string s)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
            return s.Contains('T') || s.Contains(':') ? d.ToString("HH:mm") : d.ToString("d MMM");
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && ms > 1e12)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("HH:mm");
        return s.Length > 14 ? s[..13] + "…" : s;
    }

    static string Short(double v) => Services_Format(v);

    // Formato compacto local (evita depender del servicio de APIs).
    static string Services_Format(double v)
    {
        double a = Math.Abs(v);
        var c = CultureInfo.CurrentCulture;
        if (a >= 1e9) return (v / 1e9).ToString("0.##", c) + " MM";
        if (a >= 1e6) return (v / 1e6).ToString("0.##", c) + " M";
        if (a >= 1e4) return v.ToString("N0", c);
        if (a >= 100) return v.ToString("N1", c);
        if (a >= 1) return v.ToString("0.##", c);
        return v.ToString("0.####", c);
    }

    FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(UI.TextFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
