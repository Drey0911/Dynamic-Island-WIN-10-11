// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;

namespace DynamicIsland.Core;

public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public static Vec2 FromAngle(Vec2 c, double angle, double r) => new(c.X + r * Math.Cos(angle), c.Y + r * Math.Sin(angle));
}

/// <summary>Puente metaball entre dos círculos (el cuello líquido de la gota).</summary>
public readonly record struct MetaballBridge(
    Vec2 P1, Vec2 P2, Vec2 P3, Vec2 P4,
    Vec2 H1, Vec2 H2, Vec2 H3, Vec2 H4,
    double R2, bool LargeArc);

public static class LiquidMath
{
    /// <summary>Devuelve null cuando la gota ya se separó.</summary>
    public static MetaballBridge? Bridge(Vec2 c1, double r1, Vec2 c2, double r2,
        double maxDistFactor = 1.22, double handleSize = 2.4, double v = 0.5)
    {
        double d = (c2 - c1).Length;
        double maxDist = (r1 + r2) * maxDistFactor;
        if (r1 < 0.5 || r2 < 0.5 || d > maxDist || d <= Math.Abs(r1 - r2)) return null;

        // El cuello se adelgaza al alejarse la gota para que el corte sea suave.
        double stretch = Math.Clamp((d - (r1 + r2)) / (maxDist - (r1 + r2)), 0, 1);
        v *= 1 - 0.75 * stretch;

        double u1 = 0, u2 = 0;
        if (d < r1 + r2)
        {
            u1 = Math.Acos(Clamp1((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d)));
            u2 = Math.Acos(Clamp1((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d)));
        }

        double between = Math.Atan2(c2.Y - c1.Y, c2.X - c1.X);
        double maxSpread = Math.Acos(Clamp1((r1 - r2) / d));

        double a1 = between + u1 + (maxSpread - u1) * v;
        double a2 = between - u1 - (maxSpread - u1) * v;
        double a3 = between + Math.PI - u2 - (Math.PI - u2 - maxSpread) * v;
        double a4 = between - Math.PI + u2 + (Math.PI - u2 - maxSpread) * v;

        var p1 = Vec2.FromAngle(c1, a1, r1);
        var p2 = Vec2.FromAngle(c1, a2, r1);
        var p3 = Vec2.FromAngle(c2, a3, r2);
        var p4 = Vec2.FromAngle(c2, a4, r2);

        double total = r1 + r2;
        double d2Base = Math.Min(v * handleSize, (p1 - p3).Length / total);
        double d2 = d2Base * Math.Min(1, d * 2 / total);
        double hr1 = r1 * d2, hr2 = r2 * d2;

        var h1 = Vec2.FromAngle(p1, a1 - Math.PI / 2, hr1);
        var h2 = Vec2.FromAngle(p2, a2 + Math.PI / 2, hr1);
        var h3 = Vec2.FromAngle(p3, a3 + Math.PI / 2, hr2);
        var h4 = Vec2.FromAngle(p4, a4 - Math.PI / 2, hr2);

        return new MetaballBridge(p1, p2, p3, p4, h1, h2, h3, h4, r2, d > r1);
    }

    static double Clamp1(double x) => Math.Max(-1, Math.Min(1, x));
}
