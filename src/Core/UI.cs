// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Core;

/// <summary>Paleta, iconos y controles básicos.</summary>
public static class UI
{
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    // Color de acento: celeste del logo por defecto, cambiable en Ajustes.
    // Los pinceles no se congelan para poder cambiar su color en vivo; no los congeles al usarlos.
    public const string DefaultAccent = "#74D4E2";
    public static readonly SolidColorBrush Accent = new(), AccentLight = new(), Aqua = new(), AccentSoft = new(), AccentFaint = new();
    public static event Action? AccentChanged;
    public static Color AccentColor => Accent.Color;
    /// <summary>"#RRGGBB" del acento; se puede leer desde cualquier hilo.</summary>
    public static string AccentHex { get; private set; } = DefaultAccent;

    static UI() => ApplyAccent(DefaultAccent);

    /// <summary>Lee "#RRGGBB" (o "RRGGBB").</summary>
    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        var t = (text ?? "").Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    static double Luminance(Color c)
    {
        static double L(byte b) { double x = b / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Aclara un color hasta que se lea bien sobre negro (iconos y textos).</summary>
    public static Color Readable(Color c)
    {
        for (int i = 0; i < 20 && Luminance(c) < 0.32; i++) c = Mix(c, Colors.White, 0.1);
        return c;
    }

    /// <summary>Cambia el acento de toda la app al momento. Devuelve false si el color no es válido.</summary>
    public static bool ApplyAccent(string? hex)
    {
        if (!TryParseColor(hex, out var c)) { if (Accent.Color.A != 0) return false; TryParseColor(DefaultAccent, out c); }
        c = Readable(c);
        if (Accent.Color == c) return true;
        Accent.Color = c;
        AccentLight.Color = Mix(c, Colors.White, 0.4);
        Aqua.Color = Mix(c, Colors.Black, 0.1);
        AccentSoft.Color = Color.FromArgb(0x33, c.R, c.G, c.B);
        AccentFaint.Color = Color.FromArgb(0x1A, c.R, c.G, c.B);
        AccentHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        AccentChanged?.Invoke();
        return true;
    }

    public static readonly SolidColorBrush White = Hex("#FFFFFFFF");
    public static readonly SolidColorBrush Black = Hex("#FF000000");
    public static readonly SolidColorBrush Dim = Hex("#99FFFFFF");
    public static readonly SolidColorBrush Faint = Hex("#2EFFFFFF");
    public static readonly SolidColorBrush ButtonBg = Hex("#1AFFFFFF");
    public static readonly SolidColorBrush ButtonHover = Hex("#33FFFFFF");
    public static readonly SolidColorBrush CardBg = Hex("#12FFFFFF");

    // Colores con significado propio
    public static readonly SolidColorBrush Green = Hex("#FF30D158");
    public static readonly SolidColorBrush Orange = Hex("#FFFF9F0A");
    public static readonly SolidColorBrush Red = Hex("#FFFF453A");
    public static readonly SolidColorBrush RedSoft = Hex("#40FF453A");

    // Glifos (Segoe MDL2 / Fluent Icons)
    public const string GPlay = "\uE768", GPause = "\uE769", GPrev = "\uE892", GNext = "\uE893";
    public const string GMusic = "\uEC4F", GTimer = "\uE916", GStop = "\uE71A", GAdd = "\uE710";
    public const string GMute = "\uE74F", GVolume = "\uE767", GKeyboard = "\uE765", GClock = "\uE121", GAlarm = "\uEA8F";
    public const string GReset = "\uE72C", GUp = "\uE70E", GDown = "\uE70D", GHome = "\uE80F";
    public const string GFolder = "\uE8B7", GClipboard = "\uE77F", GChart = "\uE9D9", GDownload = "\uE896";
    public const string GMic = "\uE720", GCamera = "\uE714", GWifi = "\uE701", GWifiOff = "\uE7BA";
    public const string GClose = "\uE711", GFile = "\uE8A5", GCopy = "\uE8C8", GDelete = "\uE74D";
    public const string GOpenFolder = "\uE838", GSnooze = "\uE708", GGlobe = "\uE774", GSettings = "\uE713";
    public const string GSpeaker = "\uE995", GSwap = "\uE8AB", GPhone = "\uE717", GHangUp = "\uE778";
    public const string GGame = "\uE7FC", GBrightness = "\uE706", GCpu = "\uE950", GDisk = "\uEDA2";
    public const string GShield = "\uEA18", GKey = "\uE8D7";

    public static SolidColorBrush Hex(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public static TextBlock Icon(string glyph, double size, Brush? fg = null) => new()
    {
        Text = glyph,
        FontFamily = Icons,
        FontSize = size,
        Foreground = fg ?? White,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

    public static TextBlock Label(string text, double size, Brush? fg = null, FontWeight? weight = null, bool display = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = fg ?? White,
        FontWeight = weight ?? FontWeights.Normal,
        FontFamily = display ? DisplayFont : TextFont,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock CenterLabel(string text, double size, Brush? fg = null, FontWeight? weight = null)
    {
        var t = Label(text, size, fg, weight);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        return t;
    }

    /// <summary>Botón redondeado con hover y rebote. El contenido siempre va centrado.</summary>
    public static Border Button(UIElement content, Action onClick, double width, double height, double radius, Brush? background = null)
    {
        if (content is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
            if (fe is TextBlock tb) tb.TextAlignment = TextAlignment.Center;
        }

        Brush normal = background ?? ButtonBg;
        bool plain = background == null || background == Brushes.Transparent || background == ButtonBg;
        var scale = new ScaleTransform(1, 1);
        var b = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = normal,
            Child = content,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };
        b.MouseEnter += (_, _) =>
        {
            if (plain) b.Background = ButtonHover;
            else b.Opacity = 0.82;
        };
        b.MouseLeave += (_, _) => { b.Background = normal; b.Opacity = 1; Bounce(scale, 1); };
        b.MouseLeftButtonDown += (_, e) => { Bounce(scale, 0.9); e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            Bounce(scale, 1);
            e.Handled = true;
            try { onClick(); } catch (Exception ex) { Log.Error("Button", ex); }
        };
        return b;
    }

    /// <summary>Botón con icono y texto.</summary>
    public static Border IconTextButton(string glyph, string text, Action onClick, double width, double height, Brush? background = null, Brush? fg = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Icon(glyph, 13, fg ?? White));
        var t = Label(text, 12, fg ?? White, FontWeights.SemiBold);
        t.Margin = new Thickness(6, 0, 0, 0);
        sp.Children.Add(t);
        return Button(sp, onClick, width, height, height / 2, background);
    }

    static void Bounce(ScaleTransform st, double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut },
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    public static Border ArtBox(double size, double radius) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(radius),
        Background = AccentFaint,
        VerticalAlignment = VerticalAlignment.Center,
        Child = Icon(GMusic, size * 0.45, Accent),
    };

    public static Border Card(UIElement child, double radius = 16) => new()
    {
        Background = CardBg,
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(12, 10, 12, 10),
        Child = child,
    };

    public static StackPanel Caption(string glyph, string text, Brush? iconBrush = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Icon(glyph, 12, iconBrush ?? Accent));
        var t = Label(text, 12, Dim, FontWeights.SemiBold);
        t.Margin = new Thickness(6, 0, 0, 0);
        sp.Children.Add(t);
        return sp;
    }

    /// <summary>Caja de texto oscura. Al hacer clic pide el teclado a la ventana.</summary>
    public static TextBox Input(double height = 34, bool multiline = false)
    {
        var t = new TextBox
        {
            Height = height,
            Background = ButtonBg,
            Foreground = White,
            CaretBrush = Accent,
            SelectionBrush = Accent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            FontFamily = TextFont,
            FontSize = 13,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        t.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (t.IsKeyboardFocusWithin) return;
            IslandHost.RequestKeyboard?.Invoke();
            t.Focus();
        };
        // Bordes redondeados sin plantilla personalizada.
        t.Loaded += (_, _) =>
        {
            if (t.Template?.FindName("border", t) is Border bd) bd.CornerRadius = new CornerRadius(10);
        };
        return t;
    }

    /// <summary>Selector de opciones en forma de pastillas (una activa).</summary>
    public static WrapPanel Segmented<T>(IEnumerable<(T value, string label)> options, T current, Action<T> onChange)
    {
        var panel = new WrapPanel();
        var items = new List<(Border border, TextBlock text, T value)>();
        void Paint(T selected)
        {
            foreach (var (border, text, value) in items)
            {
                bool on = EqualityComparer<T>.Default.Equals(value, selected);
                border.Background = on ? AccentSoft : ButtonBg;
                text.Foreground = on ? Accent : White;
            }
        }
        foreach (var (value, label) in options)
        {
            var text = Label(label, 12, White, FontWeights.SemiBold);
            var border = new Border
            {
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(14),
                Child = text,
                Cursor = Cursors.Hand,
            };
            var v = value;
            border.MouseLeftButtonDown += (_, e) => e.Handled = true;
            border.MouseLeftButtonUp += (_, e) => { e.Handled = true; Paint(v); onChange(v); };
            items.Add((border, text, value));
            panel.Children.Add(border);
        }
        Paint(current);
        return panel;
    }

    static BitmapImage? _logo;
    public static ImageSource? Logo
    {
        get
        {
            if (_logo != null) return _logo;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri("pack://application:,,,/Assets/logo.png", UriKind.Absolute);
                bmp.DecodePixelWidth = 64;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _logo = bmp;
            }
            catch (Exception ex) { Log.Error("Logo", ex); }
            return _logo;
        }
    }

    public static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string Bytes(double b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
}

/// <summary>Registro de errores en %LOCALAPPDATA%\DynamicIsland\log.txt</summary>
public static class Log
{
    public static readonly string Folder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");

    public static void Error(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(System.IO.Path.Combine(Folder, "log.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}\n\n");
        }
        catch { }
    }
}
