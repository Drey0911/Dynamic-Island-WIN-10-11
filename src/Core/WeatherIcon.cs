// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicIsland.Core;

/// <summary>Icono del clima dibujado con vectores (32×32), según el código WMO.</summary>
public sealed class WeatherIcon : Viewbox
{
    readonly Canvas _canvas = new() { Width = 32, Height = 32 };
    static readonly Brush Sun = UI.Hex("#FFFFD25A");
    static readonly Brush Cloud = UI.Hex("#F2FFFFFF");
    static readonly Brush CloudDark = UI.Hex("#B3C8D2DC");

    public WeatherIcon(double size)
    {
        Width = Height = size;
        Child = _canvas;
    }

    public void Set(int code, bool isDay)
    {
        _canvas.Children.Clear();
        bool rain = code is >= 51 and <= 67 or >= 80 and <= 82;
        bool snow = code is >= 71 and <= 77 or 85 or 86;
        bool storm = code >= 95;
        bool fog = code is 45 or 48;

        if (code <= 1) { Body(isDay); return; }
        if (code == 2) { Body(isDay, small: true); CloudShape(Cloud, 2); return; }
        if (fog)
        {
            CloudShape(CloudDark, -3);
            Stroke("M 7,24 H 25 M 9,28 H 23", CloudDark, 2);
            return;
        }

        CloudShape(rain || snow || storm ? CloudDark : Cloud, rain || snow || storm ? -3 : 0);
        if (rain) Stroke("M 11,24 L 9.5,28 M 16.5,24 L 15,28 M 22,24 L 20.5,28", UI.Accent, 2);
        if (snow)
            foreach (var x in new[] { 10.5, 16.0, 21.5 })
                Dot(x, 26.5, 1.6, Cloud);
        if (storm) Fill("M 17,21 L 12.5,27.5 H 16 L 14.5,31.5 L 20.5,24.5 H 17 L 19,21 Z", Sun);
    }

    void Body(bool day, bool small = false)
    {
        double cx = small ? 12 : 16, cy = small ? 11 : 16, r = small ? 5 : 6.5;
        if (day)
        {
            Dot(cx, cy, r, Sun);
            double a = r + 2.2, b = r + 5;
            for (int i = 0; i < 8; i++)
            {
                double ang = i * System.Math.PI / 4;
                Stroke(Fmt($"M {cx + a * System.Math.Cos(ang)},{cy + a * System.Math.Sin(ang)} L {cx + b * System.Math.Cos(ang)},{cy + b * System.Math.Sin(ang)}"), Sun, 1.8);
            }
        }
        else
        {
            // Luna creciente
            Fill(Fmt($"M {cx + 2},{cy - r - 1} A {r + 1},{r + 1} 0 1 0 {cx + r + 2},{cy + 4} A {r},{r} 0 0 1 {cx + 2},{cy - r - 1} Z"), UI.AccentLight);
        }
    }

    void CloudShape(Brush brush, double dy)
    {
        Fill(Fmt($"M 9,{22 + dy} H 24 A 5,5 0 0 0 24.5,{12 + dy} A 7,7 0 0 0 11.5,{11.5 + dy} A 5.3,5.3 0 0 0 9,{22 + dy} Z"), brush);
    }

    static string Fmt(System.FormattableString s) => System.FormattableString.Invariant(s);

    void Fill(string data, Brush brush) =>
        _canvas.Children.Add(new Path { Data = Geometry.Parse(data), Fill = brush });

    void Stroke(string data, Brush brush, double thickness) =>
        _canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(data),
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

    void Dot(double cx, double cy, double r, Brush brush)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = brush };
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        _canvas.Children.Add(e);
    }
}
