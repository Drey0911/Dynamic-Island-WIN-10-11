// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Text;

namespace DynamicIsland.Core;

/// <summary>
/// Contorno de la isla como trazado SVG/XAML: pegada al borde superior con esquinas cóncavas.
/// Todas las figuras van en sentido horario para que se fusionen con relleno Nonzero.
/// </summary>
public static class IslandGeometry
{
    static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static void AppendNotch(StringBuilder sb, double left, double right, double h, double r, double fillet)
    {
        double w = right - left;
        if (w < 1 || h < 0.5) return;
        r = Math.Clamp(r, 0, Math.Min(h, w / 2));
        double f = Math.Clamp(fillet, 0, Math.Max(0, h - r));

        sb.Append($"M {F(left - f)},0 L {F(right + f)},0 ");
        if (f > 0.1) sb.Append($"Q {F(right)},0 {F(right)},{F(f)} ");
        sb.Append($"L {F(right)},{F(h - r)} ");
        Corner(sb, r, right - r, h);
        sb.Append($"L {F(left + r)},{F(h)} ");
        Corner(sb, r, left, h - r);
        sb.Append($"L {F(left)},{F(f)} ");
        if (f > 0.1) sb.Append($"Q {F(left)},0 {F(left - f)},0 ");
        sb.Append("Z ");
    }

    static void Corner(StringBuilder sb, double r, double x, double y)
    {
        if (r < 0.1) sb.Append($"L {F(x)},{F(y)} ");
        else sb.Append($"A {F(r)},{F(r)} 0 0 1 {F(x)},{F(y)} ");
    }

    public static void AppendCircle(StringBuilder sb, double cx, double cy, double r)
    {
        if (r < 0.5) return;
        sb.Append($"M {F(cx - r)},{F(cy)} A {F(r)},{F(r)} 0 1 1 {F(cx + r)},{F(cy)} A {F(r)},{F(r)} 0 1 1 {F(cx - r)},{F(cy)} Z ");
    }

    public static void AppendBridge(StringBuilder sb, MetaballBridge b)
    {
        static string P(Vec2 v) => $"{F(v.X)},{F(v.Y)}";
        sb.Append($"M {P(b.P2)} C {P(b.H2)} {P(b.H4)} {P(b.P4)} ");
        sb.Append($"A {F(b.R2)},{F(b.R2)} 0 {(b.LargeArc ? 1 : 0)} 1 {P(b.P3)} ");
        sb.Append($"C {P(b.H3)} {P(b.H1)} {P(b.P1)} Z ");
    }

    /// <summary>Altura de una ola suave (suma de dos senos).</summary>
    public static double WaveY(double x, double level, double amp, double phase, double k)
        => level + amp * (Math.Sin(x * k + phase) + 0.35 * Math.Sin(x * k * 2.1 - phase * 1.4)) / 1.35;

    /// <summary>Área cerrada desde la ola hasta el fondo.</summary>
    public static string WaterFill(double width, double height, double level, double amp, double phase, double k)
    {
        var sb = new StringBuilder();
        sb.Append($"M 0,{F(height)} ");
        int n = Math.Max(2, (int)(width / 4));
        for (int i = 0; i <= n; i++)
        {
            double x = width * i / n;
            sb.Append($"L {F(x)},{F(WaveY(x, level, amp, phase, k))} ");
        }
        sb.Append($"L {F(width)},{F(height)} Z");
        return sb.ToString();
    }

    /// <summary>Línea abierta sobre la superficie de la ola (brillo fino).</summary>
    public static string WaveLine(double width, double level, double amp, double phase, double k)
    {
        var sb = new StringBuilder();
        int n = Math.Max(2, (int)(width / 4));
        for (int i = 0; i <= n; i++)
        {
            double x = width * i / n;
            sb.Append(i == 0 ? "M " : "L ").Append($"{F(x)},{F(WaveY(x, level, amp, phase, k))} ");
        }
        return sb.ToString();
    }
}
