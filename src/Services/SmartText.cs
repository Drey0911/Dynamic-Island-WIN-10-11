// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DynamicIsland.Services;

public enum SmartKind { None, Color, Math, Link, Language }

public sealed record SmartResult(SmartKind Kind, string Title, string Value, Color? Color = null, string? Language = null);

/// <summary>Reconoce qué tipo de texto se copió: color, operación, enlace o texto en otro idioma.</summary>
public static class SmartText
{
    static readonly Regex HexColor = new(@"^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    static readonly Regex RgbColor = new(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(,\s*[\d.]+\s*)?\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex HslColor = new(@"^hsla?\(\s*(\d{1,3})\s*,\s*(\d{1,3})%\s*,\s*(\d{1,3})%\s*(,\s*[\d.]+\s*)?\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Url = new(@"^(https?://|www\.)[^\s]+\.[^\s]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex MathChars = new(@"^[\d\s+\-*/^().,%x×÷]+$", RegexOptions.Compiled);

    static readonly Dictionary<string, (string name, HashSet<string> words)> Languages = new()
    {
        ["es"] = ("español", Set("de la que el en y los se del las un por con no una su para es al lo como más pero sus le ya este porque esta cuando muy sin sobre también me hasta hay donde desde todo nos todos")),
        ["en"] = ("inglés", Set("the be to of and a in that have it for not on with he as you do at this but his by from they we say her she or an will my one all would there their what so up out if about who get which go me is are was")),
        ["pt"] = ("portugués", Set("de que não uma os no se na por mais as dos como mas foi ao ele das tem seu sua ou ser quando muito nos já está eu também só pelo pela até isso ela entre era depois sem mesmo você")),
        ["fr"] = ("francés", Set("le la les de des du un une et est que qui dans pour pas sur au avec ce il elle ne se plus par je vous nous ils sont mais ou")),
        ["de"] = ("alemán", Set("der die das und ist nicht ein eine zu den mit von sich des auf für im dem auch es an werden aus er hat dass sie nach wird bei")),
        ["it"] = ("italiano", Set("il di che e un una per non sono con del della gli si è da al nel ma come anche più questo")),
    };

    static HashSet<string> Set(string words) => new(words.Split(' '), StringComparer.OrdinalIgnoreCase);

    public static SmartResult Analyze(string raw)
    {
        string text = raw.Trim();
        if (text.Length == 0 || text.Length > 2000) return None;

        if (TryColor(text, out var color))
            return new SmartResult(SmartKind.Color, "Color copiado", ToHex(color), color);

        if (text.Length < 200 && TryMath(text, out double result))
            return new SmartResult(SmartKind.Math, text, FormatNumber(result));

        if (!text.Contains(' ') && Url.IsMatch(text))
        {
            string url = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + text : text;
            string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : text;
            return new SmartResult(SmartKind.Link, host, url);
        }

        var lang = DetectLanguage(text);
        string mine = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (lang != null && lang != mine)
            return new SmartResult(SmartKind.Language, $"Texto en {Languages[lang].name}", text, null, lang);

        return None;
    }

    static readonly SmartResult None = new(SmartKind.None, "", "");

    // ---------- Colores ----------

    static bool TryColor(string text, out Color color)
    {
        color = default;
        var m = HexColor.Match(text);
        if (m.Success && (text.StartsWith('#') || (text.Any(char.IsLetter) && text.Any(char.IsDigit))))
        {
            string h = m.Groups[1].Value;
            if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
            if (h.Length == 6) h = "FF" + h;
            else if (h.Length == 8) h = h[6..] + h[..6]; // CSS #RRGGBBAA -> AARRGGBB
            uint v = uint.Parse(h, NumberStyles.HexNumber);
            color = Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }
        m = RgbColor.Match(text);
        if (m.Success)
        {
            int r = int.Parse(m.Groups[1].Value), g = int.Parse(m.Groups[2].Value), b = int.Parse(m.Groups[3].Value);
            if (r > 255 || g > 255 || b > 255) return false;
            color = Color.FromRgb((byte)r, (byte)g, (byte)b);
            return true;
        }
        m = HslColor.Match(text);
        if (m.Success)
        {
            color = FromHsl(int.Parse(m.Groups[1].Value) % 360, int.Parse(m.Groups[2].Value) / 100.0, int.Parse(m.Groups[3].Value) / 100.0);
            return true;
        }
        return false;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    public static string ToRgb(Color c) => $"rgb({c.R}, {c.G}, {c.B})";

    public static string ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return $"hsl({h:0}, {s * 100:0}%, {l * 100:0}%)";
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // ---------- Operaciones ----------

    static bool TryMath(string text, out double result)
    {
        result = 0;
        if (!MathChars.IsMatch(text) || !text.Any(char.IsDigit)) return false;
        if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$") || Regex.IsMatch(text, @"^\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}$")
            || Regex.IsMatch(text, @"^\d[\d\s-]{6,}\d$")) return false; // fechas y teléfonos
        bool hasOperator = text.IndexOfAny(new[] { '+', '*', '/', '^', 'x', '×', '÷', '%' }) >= 0 || text.Contains(" - ") || text.Contains('(');
        if (!hasOperator) return false;

        string expr = text.Replace('×', '*').Replace('÷', '/').Replace(" ", "");
        expr = Regex.Replace(expr, @"(?<=\d)x(?=\d)", "*");
        if (expr.Contains('x')) return false;
        // Coma decimal (1,5) si no hay punto; si hay ambos, la coma separa miles.
        expr = expr.Contains('.') ? expr.Replace(",", "") : expr.Replace(',', '.');

        try
        {
            var p = new Parser(expr);
            result = p.ParseExpression();
            return p.AtEnd && !double.IsNaN(result) && !double.IsInfinity(result);
        }
        catch { return false; }
    }

    public static string FormatNumber(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15) return Math.Round(v).ToString("N0", CultureInfo.CurrentCulture);
        return v.ToString("#,##0.##########", CultureInfo.CurrentCulture);
    }

    /// <summary>Analizador recursivo: + - * / % ^ y paréntesis.</summary>
    sealed class Parser
    {
        readonly string _s;
        int _i;
        public Parser(string s) => _s = s;
        public bool AtEnd => _i >= _s.Length;
        char Peek => _i < _s.Length ? _s[_i] : '\0';

        public double ParseExpression()
        {
            double v = ParseTerm();
            while (Peek is '+' or '-')
            {
                char op = _s[_i++];
                double r = ParseTerm();
                v = op == '+' ? v + r : v - r;
            }
            return v;
        }

        double ParseTerm()
        {
            double v = ParsePower();
            while (Peek is '*' or '/' or '%')
            {
                char op = _s[_i++];
                double r = ParsePower();
                v = op switch { '*' => v * r, '/' => v / r, _ => v % r };
            }
            return v;
        }

        double ParsePower()
        {
            double b = ParseUnary();
            if (Peek == '^')
            {
                _i++;
                return Math.Pow(b, ParsePower());
            }
            return b;
        }

        double ParseUnary()
        {
            if (Peek == '-') { _i++; return -ParseUnary(); }
            if (Peek == '+') { _i++; return ParseUnary(); }
            if (Peek == '(')
            {
                _i++;
                double v = ParseExpression();
                if (Peek != ')') throw new FormatException();
                _i++;
                return v;
            }
            int start = _i;
            while (char.IsDigit(Peek) || Peek == '.') _i++;
            if (start == _i) throw new FormatException();
            return double.Parse(_s[start.._i], CultureInfo.InvariantCulture);
        }
    }

    // ---------- Idioma ----------

    /// <summary>Idioma probable por palabras frecuentes (sin internet). null si no hay suficiente texto.</summary>
    public static string? DetectLanguage(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToList();
        if (words.Count < 4) return null;
        string? best = null;
        int bestHits = 0, second = 0;
        foreach (var (code, (_, set)) in Languages)
        {
            int hits = words.Count(set.Contains);
            if (hits > bestHits) { second = bestHits; bestHits = hits; best = code; }
            else if (hits > second) second = hits;
        }
        return bestHits >= 2 && bestHits > second * 1.5 ? best : null;
    }
}
