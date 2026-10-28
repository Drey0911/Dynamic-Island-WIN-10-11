// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

// ---------------------------------------------------------------------
// Modelo (se guarda en settings.json, así se conserva al apagar el PC)
// ---------------------------------------------------------------------

/// <summary>Un dato que se muestra de la respuesta de la API.</summary>
public sealed class ApiField
{
    public string Label { get; set; } = "";
    /// <summary>Ruta dentro del JSON, por ejemplo "bitcoin.usd", "rates.COP" o "prices[-1][1]".</summary>
    public string Path { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    /// <summary>Decimales fijos; -1 = automático.</summary>
    public int Decimals { get; set; } = -1;
    /// <summary>Verde si es positivo y rojo si es negativo (cambios de precio).</summary>
    public bool ColorSign { get; set; }
    /// <summary>Notación corta: 1,2 K · 3,4 M.</summary>
    public bool Compact { get; set; }
}

/// <summary>Vista principal de la API (debajo de los datos).</summary>
public enum ApiChartKind { None, Line, Bars, Table, Image, Text }

/// <summary>
/// Vista principal de la API: gráfica (serie de la respuesta o historial de un dato), tabla, imagen o texto.
/// (Se llama "Chart" por compatibilidad con los ajustes guardados.)
/// </summary>
public sealed class ApiChart
{
    public ApiChartKind Kind { get; set; } = ApiChartKind.None;
    /// <summary>Tabla: ruta de la lista (por ejemplo "data.items" o "data.items[*]").</summary>
    public string TablePath { get; set; } = "";
    /// <summary>Tabla: columnas a mostrar; vacía = las primeras automáticamente.</summary>
    public List<string> TableColumns { get; set; } = new();
    /// <summary>Imagen: ruta del enlace de la imagen (con [*] muestra varias).</summary>
    public string ImagePath { get; set; } = "";
    /// <summary>Texto: ruta de un texto largo (frase, descripción, noticia).</summary>
    public string TextPath { get; set; } = "";
    /// <summary>Ruta con [*] para tomar una serie de la respuesta (por ejemplo "prices[*][1]"). Vacía = historial.</summary>
    public string SeriesPath { get; set; } = "";
    /// <summary>Etiquetas opcionales de la serie (por ejemplo "hourly.time[*]").</summary>
    public string LabelsPath { get; set; } = "";
    /// <summary>Si no hay serie: índice del dato cuyo historial se grafica.</summary>
    public int HistoryField { get; set; }
}

/// <summary>Una API configurada por el usuario en el modo Beta DEV.</summary>
public sealed class ApiWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "Nueva API";
    public string Url { get; set; } = "";
    /// <summary>Claves, tokens y encabezados (lista de ApiParam en JSON), cifrados con el usuario de Windows.</summary>
    public string HeadersProtected { get; set; } = "";
    public int RefreshSeconds { get; set; } = 60;
    public bool ShowInIsland { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public List<ApiField> Fields { get; set; } = new();
    public ApiChart Chart { get; set; } = new();

    /// <summary>Claves y tokens ya descifrados (solo en memoria).</summary>
    [JsonIgnore]
    public List<ApiParam> Params
    {
        get => ApiParam.Parse(Secret.Unprotect(HeadersProtected));
        set => HeadersProtected = value.Count == 0 ? "" : Secret.Protect(JsonSerializer.Serialize(value.Where(p => p.Name.Trim().Length > 0).ToList()));
    }

    /// <summary>Formato de texto "Nombre: valor" por línea (compatibilidad y pruebas).</summary>
    [JsonIgnore]
    public string Headers
    {
        get => string.Join("\n", Params.Where(p => !p.InUrl).Select(p => $"{p.Name}: {p.Value}"));
        set => Params = ApiParam.Parse(value);
    }

    public ApiWidget Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<ApiWidget>(json) ?? new ApiWidget();
    }
}

/// <summary>Una clave o token: se envía como encabezado o como parámetro de la URL.</summary>
public sealed class ApiParam
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>true = se agrega a la URL (?nombre=valor); false = encabezado HTTP.</summary>
    public bool InUrl { get; set; }

    /// <summary>Lee la lista guardada (JSON) o el formato anterior "Nombre: valor" por línea.</summary>
    public static List<ApiParam> Parse(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return new List<ApiParam>();
        if (text.StartsWith("["))
        {
            try { return JsonSerializer.Deserialize<List<ApiParam>>(text) ?? new List<ApiParam>(); }
            catch { }
        }
        var list = new List<ApiParam>();
        foreach (var line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            list.Add(new ApiParam { Name = line[..colon].Trim(), Value = line[(colon + 1)..].Trim() });
        }
        return list;
    }

    /// <summary>
    /// Valor que se envía. Si el encabezado es Authorization y el valor es un token suelto (por ejemplo un JWT),
    /// se le antepone "Bearer " automáticamente.
    /// </summary>
    public string Effective()
    {
        string v = Value.Trim();
        if (!InUrl && Name.Trim().Equals("Authorization", StringComparison.OrdinalIgnoreCase) && v.Length > 0 && !v.Contains(' '))
            return "Bearer " + v;
        return v;
    }
}

// ---------------------------------------------------------------------
// Rutas dentro del JSON
// ---------------------------------------------------------------------

/// <summary>
/// Rutas simples: "a.b.c", índices "a[0]", desde el final "a[-1]" y todos los elementos "a[*].precio".
/// Claves con puntos o espacios: ["mi clave"].
/// </summary>
public static class JsonPathLite
{
    abstract record Seg;
    sealed record Key(string Name) : Seg;
    sealed record Index(int I) : Seg;
    sealed record All : Seg;

    static List<Seg> Parse(string path)
    {
        var segs = new List<Seg>();
        int i = 0;
        path = path.Trim();
        if (path.StartsWith("$")) i = 1;
        while (i < path.Length)
        {
            char c = path[i];
            if (c == '.') { i++; continue; }
            if (c == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0) throw new FormatException("Falta ']' en la ruta");
                string inner = path[(i + 1)..end].Trim();
                if (inner == "*") segs.Add(new All());
                else if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'')) segs.Add(new Key(inner[1..^1]));
                else if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) segs.Add(new Index(n));
                else segs.Add(new Key(inner));
                i = end + 1;
                continue;
            }
            int start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            segs.Add(new Key(path[start..i]));
        }
        return segs;
    }

    /// <summary>Todos los valores que coinciden (varios si la ruta tiene [*]).</summary>
    public static List<JsonElement> Select(JsonElement root, string path)
    {
        var current = new List<JsonElement> { root };
        if (string.IsNullOrWhiteSpace(path)) return current;
        foreach (var seg in Parse(path))
        {
            var next = new List<JsonElement>();
            foreach (var e in current)
            {
                switch (seg)
                {
                    case Key k when e.ValueKind == JsonValueKind.Object:
                        if (e.TryGetProperty(k.Name, out var v)) next.Add(v);
                        else
                        {
                            // Sin distinguir mayúsculas como respaldo.
                            foreach (var p in e.EnumerateObject())
                                if (p.Name.Equals(k.Name, StringComparison.OrdinalIgnoreCase)) { next.Add(p.Value); break; }
                        }
                        break;
                    case Index ix when e.ValueKind == JsonValueKind.Array:
                        int len = e.GetArrayLength();
                        int idx = ix.I < 0 ? len + ix.I : ix.I;
                        if (idx >= 0 && idx < len) next.Add(e[idx]);
                        break;
                    case All when e.ValueKind == JsonValueKind.Array:
                        next.AddRange(e.EnumerateArray());
                        break;
                    case All when e.ValueKind == JsonValueKind.Object:
                        next.AddRange(e.EnumerateObject().Select(p => p.Value));
                        break;
                }
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current;
    }

    public static JsonElement? First(JsonElement root, string path)
    {
        var r = Select(root, path);
        return r.Count > 0 ? r[0] : null;
    }

    /// <summary>Número de un elemento (también si viene como texto, por ejemplo "64123.5").</summary>
    public static double? Number(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double d)) return d;
        if (e.ValueKind == JsonValueKind.String &&
            double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return s;
        return null;
    }

    public static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.Array => $"[{e.GetArrayLength()} elementos]",
        _ => "{...}",
    };

    public static string Join(string parent, string key)
    {
        bool simple = key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
        string part = simple ? key : $"[\"{key}\"]";
        if (parent.Length == 0) return part;
        return simple ? parent + "." + part : parent + part;
    }
}

/// <summary>Un dato encontrado al explorar la respuesta (para elegir qué mostrar).</summary>
public sealed record ApiLeaf(string Path, string Sample, bool IsNumber, bool IsSeries, int Count)
{
    /// <summary>Lista de objetos: se puede ver como tabla.</summary>
    public bool IsTable { get; init; }
    /// <summary>Enlace a una imagen.</summary>
    public bool IsImage { get; init; }
    /// <summary>Texto largo (frase, descripción).</summary>
    public bool IsLongText { get; init; }
}

/// <summary>Explora una respuesta JSON y propone datos y series para graficar.</summary>
public static class ApiExplorer
{
    public static List<ApiLeaf> Leaves(JsonElement root, int max = 400)
    {
        var list = new List<ApiLeaf>();
        Walk(root, "", list, max, 0);
        return list;
    }

    static void Walk(JsonElement e, string path, List<ApiLeaf> list, int max, int depth)
    {
        if (list.Count >= max || depth > 12) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    Walk(p.Value, JsonPathLite.Join(path, p.Name), list, max, depth + 1);
                    if (list.Count >= max) return;
                }
                break;
            case JsonValueKind.Array:
                int n = e.GetArrayLength();
                if (n == 0) return;
                var first = e[0];
                if (n >= 2 && JsonPathLite.Number(first) != null)
                {
                    // Lista de números: se puede graficar completa o tomar el último.
                    list.Add(new ApiLeaf(path + "[*]", Preview(e), true, true, n));
                    list.Add(new ApiLeaf(path + "[-1]", JsonPathLite.Text(e[n - 1]), true, false, 1));
                }
                else if (n >= 2 && first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0)
                {
                    // Pares como [fecha, valor]: cada columna numérica es una serie.
                    for (int c = 0; c < first.GetArrayLength(); c++)
                    {
                        if (JsonPathLite.Number(first[c]) == null) continue;
                        list.Add(new ApiLeaf($"{path}[*][{c}]", PreviewColumn(e, c), true, true, n));
                        list.Add(new ApiLeaf($"{path}[-1][{c}]", JsonPathLite.Text(e[n - 1][c]), true, false, 1));
                    }
                }
                else if (n >= 2 && first.ValueKind == JsonValueKind.Object)
                {
                    // Lista de objetos: se puede ver como tabla; cada propiedad numérica es una serie.
                    list.Add(new ApiLeaf(path + "[*]", $"{n} filas: {string.Join(", ", first.EnumerateObject().Take(4).Select(p => p.Name))}", false, false, n) { IsTable = true });
                    foreach (var p in first.EnumerateObject())
                        if (JsonPathLite.Number(p.Value) != null)
                            list.Add(new ApiLeaf(JsonPathLite.Join(path + "[*]", p.Name), $"{n} valores", true, true, n));
                    Walk(first, path + "[0]", list, max, depth + 1);
                }
                else
                {
                    if (n >= 2 && first.ValueKind == JsonValueKind.String)
                    {
                        bool images = IsImageUrl(first.GetString() ?? "", path);
                        list.Add(new ApiLeaf(path + "[*]", images ? $"{n} imágenes" : $"{n} textos", false, !images, n) { IsImage = images });
                    }
                    Walk(first, path + "[0]", list, max, depth + 1);
                }
                break;
            default:
                string text = JsonPathLite.Text(e);
                bool isText = e.ValueKind == JsonValueKind.String;
                list.Add(new ApiLeaf(path, Short(text), JsonPathLite.Number(e) != null && !isText || IsNumericString(e), false, 1)
                {
                    IsImage = isText && IsImageUrl(text, path),
                    IsLongText = isText && text.Length >= 50 && !text.StartsWith("http", StringComparison.OrdinalIgnoreCase),
                });
                break;
        }
    }

    static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    static readonly string[] ImageWords = { "image", "img", "photo", "picture", "thumbnail", "thumb", "avatar", "icon", "logo", "poster", "imagen", "foto" };

    /// <summary>¿Es un enlace a una imagen? Por la extensión o por el nombre del dato (url de "image", "thumbnail"...).</summary>
    public static bool IsImageUrl(string value, string path)
    {
        if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        string v = value.ToLowerInvariant();
        int q = v.IndexOf('?');
        string clean = q >= 0 ? v[..q] : v;
        if (ImageExt.Any(clean.EndsWith)) return true;
        string p = path.ToLowerInvariant();
        return ImageWords.Any(p.Contains) && !clean.EndsWith(".svg");
    }

    static bool IsNumericString(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && JsonPathLite.Number(e) != null;

    static string Short(string s) => s.Length > 60 ? s[..57] + "..." : s;

    static string Preview(JsonElement arr)
    {
        var items = arr.EnumerateArray().Take(3).Select(JsonPathLite.Text);
        return $"{arr.GetArrayLength()} valores: {string.Join(", ", items)}...";
    }

    static string PreviewColumn(JsonElement arr, int c)
    {
        var items = arr.EnumerateArray().Take(3).Select(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() > c ? JsonPathLite.Text(x[c]) : "?");
        return $"{arr.GetArrayLength()} valores: {string.Join(", ", items)}...";
    }

    /// <summary>Propuesta automática al probar una API nueva: datos principales y una serie para graficar.</summary>
    public static (List<ApiField> Fields, ApiChart Chart) Suggest(List<ApiLeaf> leaves)
    {
        var fields = new List<ApiField>();
        var chart = new ApiChart();

        // Serie: la de mejor puntaje (precios, temperaturas...) que no sea de fechas.
        var series = leaves.Where(l => l.IsSeries && l.IsNumber && l.Count >= 3 && !LooksLikeTime(l))
            .OrderByDescending(Score).FirstOrDefault();
        // En una lista de objetos sin un número claramente interesante (ids, edades...), se ve mejor como tabla o imagen.
        if (series != null && series.Path.Contains("[*].") && Score(series) <= 0 && leaves.Any(l => l.IsTable || l.IsImage))
            series = null;
        if (series != null)
        {
            chart.Kind = ApiChartKind.Line;
            chart.SeriesPath = series.Path;
            var labels = leaves.FirstOrDefault(l => l.IsSeries && LooksLikeTime(l) && SameParent(l.Path, series.Path));
            if (labels != null) chart.LabelsPath = labels.Path;
        }

        if (series == null)
        {
            // Sin series de números: imagen, tabla o texto, en ese orden.
            var image = leaves.FirstOrDefault(l => l.IsImage);
            var table = leaves.Where(l => l.IsTable).OrderByDescending(l => l.Count).FirstOrDefault();
            var longText = leaves.Where(l => l.IsLongText).OrderByDescending(l => l.Sample.Length).FirstOrDefault();
            if (image != null) chart = new ApiChart { Kind = ApiChartKind.Image, ImagePath = AllItems(image.Path) };
            else if (table != null) chart = new ApiChart { Kind = ApiChartKind.Table, TablePath = table.Path };
            else if (longText != null) chart = new ApiChart { Kind = ApiChartKind.Text, TextPath = longText.Path };
        }

        foreach (var l in leaves.Where(l => l.IsNumber && !l.IsSeries && !LooksLikeTime(l)).OrderByDescending(Score).Take(2))
            fields.Add(new ApiField { Label = PrettyLabel(l.Path), Path = l.Path });
        if (fields.Count == 0)
        {
            // Un texto corto como dato (por ejemplo un título); el largo ya va en la vista.
            var text = leaves.Where(l => !l.IsSeries && !l.IsTable && !l.IsImage && l.Path != chart.TextPath)
                .OrderBy(l => l.IsLongText).ThenByDescending(Score).FirstOrDefault();
            if (text != null) fields.Add(new ApiField { Label = PrettyLabel(text.Path), Path = text.Path });
        }
        if (series == null && chart.Kind == ApiChartKind.None && fields.Count > 0 && leaves.Any(l => l.Path == fields[0].Path && l.IsNumber))
            chart = new ApiChart { Kind = ApiChartKind.Line, HistoryField = 0 };
        return (fields, chart);
    }

    /// <summary>"photos[0].url" -> "photos[*].url": todas las imágenes de la lista, no solo la primera.</summary>
    static string AllItems(string path)
    {
        int i = path.LastIndexOf("[0]", StringComparison.Ordinal);
        return i < 0 ? path : path[..i] + "[*]" + path[(i + 3)..];
    }

    static readonly string[] Good = { "price", "precio", "rate", "value", "valor", "usd", "eur", "cop", "temp", "current", "close", "last", "amount", "total", "count", "score", "speed", "humidity" };
    static readonly string[] Poor = { "latitude", "longitude", "lat", "lon", "lng", "generationtime", "offset", "elevation", "interval", "id", "version", "status", "code", "page", "limit", "unix", "timestamp" };

    /// <summary>Qué tan probable es que un dato sea el interesante para mostrar.</summary>
    static int Score(ApiLeaf l)
    {
        string p = l.Path.ToLowerInvariant();
        int score = 0;
        foreach (var g in Good) if (p.Contains(g)) score += 3;
        var words = PrettyLabel(l.Path).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string joined = string.Concat(words);
        if (words.Any(w => Poor.Contains(w)) || Poor.Any(b => b.Length > 6 && joined.StartsWith(b))) score -= 6;
        if (p.EndsWith("[-1][1]") || p.EndsWith("[*][1]")) score += 4;
        if (p.EndsWith("[-1][0]") || p.EndsWith("[*][0]")) score -= 4;
        if (!l.IsSeries && l.Sample is "0" or "1" or "true" or "false") score -= 5; // valores de base o banderas
        score -= p.Count(c => c == '.'); // lo más superficial primero
        return score;
    }

    /// <summary>Fechas o marcas de tiempo (no se proponen como dato ni como serie).</summary>
    public static bool IsTime(ApiLeaf l) => LooksLikeTime(l);

    static bool LooksLikeTime(ApiLeaf l)
    {
        string p = l.Path.ToLowerInvariant();
        if (p.Contains("time") || p.Contains("date") || p.Contains("fecha") || p.EndsWith("[*][0]") || p.EndsWith("[-1][0]")) return true;
        return false;
    }

    /// <summary>¿Las dos rutas vienen de la misma lista? ("prices[*][0]" y "prices[*][1]", o "hourly.time[*]" y "hourly.temp[*]").</summary>
    static bool SameParent(string a, string b)
    {
        static string Base(string x)
        {
            int pair = x.IndexOf("[*][", StringComparison.Ordinal);
            if (pair >= 0) return x[..pair];
            int i = x.LastIndexOf('.');
            return i < 0 ? "" : x[..i];
        }
        return Base(a) == Base(b);
    }

    /// <summary>"current.temperature_2m" -> "Temperature 2m".</summary>
    public static string PrettyLabel(string path)
    {
        string last = path;
        int dot = path.LastIndexOf('.');
        if (dot >= 0) last = path[(dot + 1)..];
        last = new string(last.TakeWhile(c => c != '[').ToArray());
        if (last.Length == 0)
        {
            var parts = path.Split('.', '[');
            last = parts.FirstOrDefault(p => p.Length > 0 && char.IsLetter(p[0])) ?? path;
        }
        last = last.Replace('_', ' ').Replace('-', ' ').Trim();
        return last.Length == 0 ? "Valor" : char.ToUpper(last[0]) + last[1..];
    }
}

// ---------------------------------------------------------------------
// Formato de los valores
// ---------------------------------------------------------------------
public static class ApiFormat
{
    public static string Value(ApiField f, JsonElement? e)
    {
        if (e == null) return "--";
        double? n = JsonPathLite.Number(e.Value);
        if (n == null) return f.Prefix + JsonPathLite.Text(e.Value) + f.Suffix;
        return f.Prefix + Number(n.Value, f.Decimals, f.Compact, f.ColorSign) + f.Suffix;
    }

    public static string Number(double v, int decimals = -1, bool compact = false, bool sign = false)
    {
        var c = CultureInfo.CurrentCulture;
        string unit = "";
        double x = v;
        if (compact)
        {
            double a = Math.Abs(v);
            if (a >= 1e12) { x = v / 1e12; unit = " B"; }
            else if (a >= 1e9) { x = v / 1e9; unit = " MM"; }
            else if (a >= 1e6) { x = v / 1e6; unit = " M"; }
            else if (a >= 1e3) { x = v / 1e3; unit = " K"; }
        }
        int d = decimals >= 0 ? decimals : AutoDecimals(x);
        string s = x.ToString("N" + d, c);
        if (sign && v > 0) s = "+" + s;
        return s + unit;
    }

    static int AutoDecimals(double v)
    {
        double a = Math.Abs(v);
        if (a == 0 || a >= 1000) return a % 1 == 0 || a >= 100000 ? 0 : 2;
        if (a >= 1) return a % 1 == 0 ? 0 : 2;
        if (a >= 0.01) return 4;
        return 6;
    }
}

// ---------------------------------------------------------------------
// Estado de cada API y actualización periódica
// ---------------------------------------------------------------------
public sealed class ApiState
{
    public string? Error { get; set; }
    public DateTime? Updated { get; set; }
    public bool Loading { get; set; }
    public JsonDocument? Document { get; set; }
    public List<string> Values { get; } = new();
    public List<double?> Numbers { get; } = new();
    public List<double> Series { get; } = new();
    public List<string> SeriesLabels { get; } = new();
    public List<string> TableHeaders { get; } = new();
    public List<List<string>> TableRows { get; } = new();
    public List<string> ImageUrls { get; } = new();
    public string Text { get; set; } = "";
}

/// <summary>
/// Pide cada API en su intervalo, guarda el historial de los valores (para las gráficas de historial)
/// y avisa a la interfaz. Solo funciona con el modo Beta DEV activo.
/// </summary>
public sealed class ApiHub
{
    public static ApiHub Instance { get; } = new();

    public const int HistoryMax = 120;
    static readonly string HistoryFile = Path.Combine(Log.Folder, "apis-history.json");

    readonly HttpClient _http;
    readonly Dictionary<string, ApiState> _states = new();
    readonly Dictionary<string, DateTime> _due = new();
    Dictionary<string, List<double>> _history = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _saveHistory = new() { Interval = TimeSpan.FromSeconds(20) };
    bool _historyDirty;

    /// <summary>Se dispara (en el hilo de la interfaz) cuando una API cambia: id de la API.</summary>
    public event Action<string>? Updated;

    ApiHub()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/0.9 (Windows; modo DEV)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _tick.Tick += (_, _) => Tick();
        _saveHistory.Tick += (_, _) => SaveHistory();
        LoadHistory();
        Settings.Changed += Sync;
        Sync();
    }

    static Settings S => Settings.Current;
    public static IEnumerable<ApiWidget> Active => S.DevMode ? S.Apis.Where(a => a.Enabled) : Enumerable.Empty<ApiWidget>();

    public ApiState State(string id)
    {
        if (!_states.TryGetValue(id, out var s)) _states[id] = s = new ApiState();
        return s;
    }

    public IReadOnlyList<double> History(string id, int field) =>
        _history.TryGetValue($"{id}#{field}", out var h) ? h : Array.Empty<double>();

    /// <summary>Arranca o detiene la actualización según el modo DEV.</summary>
    void Sync()
    {
        if (S.DevMode && S.Apis.Count > 0) { if (!_tick.IsEnabled) { _tick.Start(); _saveHistory.Start(); } }
        else { _tick.Stop(); _saveHistory.Stop(); SaveHistory(); }

        // Quita el estado de las APIs borradas.
        var ids = S.Apis.Select(a => a.Id).ToHashSet();
        foreach (var gone in _states.Keys.Where(k => !ids.Contains(k)).ToList()) { _states.Remove(gone); _due.Remove(gone); }
        foreach (var key in _history.Keys.Where(k => !ids.Contains(k.Split('#')[0])).ToList()) { _history.Remove(key); _historyDirty = true; }
    }

    void Tick()
    {
        var now = DateTime.Now;
        foreach (var api in Active.ToList())
        {
            if (_due.TryGetValue(api.Id, out var due) && now < due) continue;
            _due[api.Id] = now.AddSeconds(Math.Max(15, api.RefreshSeconds));
            _ = RefreshAsync(api);
        }
    }

    /// <summary>Pide la API ya mismo (botón "Actualizar" o al guardar cambios).</summary>
    public void RefreshNow(ApiWidget api)
    {
        _due[api.Id] = DateTime.Now.AddSeconds(Math.Max(15, api.RefreshSeconds));
        _ = RefreshAsync(api);
    }

    async Task RefreshAsync(ApiWidget api)
    {
        var state = State(api.Id);
        if (state.Loading) return;
        state.Loading = true;
        try
        {
            var doc = await FetchAsync(_http, api.Url, api.Params);
            Apply(api, state, doc, recordHistory: true);
            state.Error = null;
            state.Updated = DateTime.Now;
        }
        catch (Exception ex)
        {
            state.Error = Describe(ex);
        }
        finally
        {
            state.Loading = false;
            Updated?.Invoke(api.Id);
        }
    }

    /// <summary>Pide una URL y devuelve el JSON. Lanza una excepción con un mensaje claro si falla.</summary>
    public static Task<JsonDocument> FetchAsync(HttpClient http, string url, string headers, CancellationToken ct = default) =>
        FetchAsync(http, url, ApiParam.Parse(headers), ct);

    /// <summary>Pide una URL con sus claves (encabezados o parámetros de la URL) y devuelve el JSON.</summary>
    public static async Task<JsonDocument> FetchAsync(HttpClient http, string url, IEnumerable<ApiParam> parameters, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new ApiException("La URL debe empezar con https://");
        var list = parameters.Where(p => p.Name.Trim().Length > 0).ToList();

        // Claves que van en la URL: ?nombre=valor (o &nombre=valor si ya hay parámetros).
        var query = list.Where(p => p.InUrl).ToList();
        if (query.Count > 0)
        {
            var sb = new StringBuilder(uri.GetLeftPart(UriPartial.Path));
            string existing = uri.Query.TrimStart('?');
            sb.Append('?').Append(existing);
            foreach (var p in query)
            {
                if (sb[^1] != '?') sb.Append('&');
                sb.Append(Uri.EscapeDataString(p.Name.Trim())).Append('=').Append(Uri.EscapeDataString(p.Value.Trim()));
            }
            uri = new Uri(sb.ToString());
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var p in list.Where(p => !p.InUrl))
        {
            string name = p.Name.Trim();
            if (name.Any(c => char.IsWhiteSpace(c) || c == ':')) throw new ApiException($"Nombre de encabezado no válido: {name}");
            if (!req.Headers.TryAddWithoutValidation(name, p.Effective())) throw new ApiException($"Encabezado no válido: {name}");
        }
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            int code = (int)res.StatusCode;
            throw new ApiException(code switch
            {
                401 or 403 => $"Acceso negado ({code}): la API pide una clave",
                404 => "No encontrada (404): revisa la URL",
                429 => "Demasiadas consultas (429): sube el intervalo",
                _ => $"La API respondió {code}",
            });
        }
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 4 * 1024 * 1024) throw new ApiException("La respuesta es demasiado grande (más de 4 MB)");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new ApiException("La respuesta no es JSON"); }
    }

    static string Describe(Exception ex) => ex switch
    {
        ApiException a => a.Message,
        TaskCanceledException => "Sin respuesta (tiempo agotado)",
        HttpRequestException => "Sin conexión con la API",
        _ => ex.Message,
    };

    /// <summary>Extrae los datos y la serie de la respuesta según la configuración.</summary>
    public void Apply(ApiWidget api, ApiState state, JsonDocument doc, bool recordHistory)
    {
        if (!ReferenceEquals(state.Document, doc)) state.Document?.Dispose();
        state.Document = doc;
        var root = doc.RootElement;
        state.Values.Clear();
        state.Numbers.Clear();
        for (int i = 0; i < api.Fields.Count; i++)
        {
            var f = api.Fields[i];
            JsonElement? e = null;
            try { e = JsonPathLite.First(root, f.Path); } catch { }
            state.Values.Add(ApiFormat.Value(f, e));
            double? n = e.HasValue ? JsonPathLite.Number(e.Value) : null;
            state.Numbers.Add(n);
            if (recordHistory && n.HasValue) AddHistory($"{api.Id}#{i}", n.Value);
        }

        ApplyView(api, state, root);

        state.Series.Clear();
        state.SeriesLabels.Clear();
        if (api.Chart.Kind is ApiChartKind.Line or ApiChartKind.Bars && api.Chart.SeriesPath.Length > 0)
        {
            try
            {
                foreach (var e in JsonPathLite.Select(root, api.Chart.SeriesPath))
                    if (JsonPathLite.Number(e) is double d && !double.IsNaN(d)) state.Series.Add(d);
                if (api.Chart.LabelsPath.Length > 0)
                    state.SeriesLabels.AddRange(JsonPathLite.Select(root, api.Chart.LabelsPath).Select(JsonPathLite.Text));
            }
            catch { }
            // Series muy largas se reducen para dibujar rápido.
            if (state.Series.Count > 400)
            {
                int step = (int)Math.Ceiling(state.Series.Count / 400.0);
                var reduced = state.Series.Where((_, i) => i % step == 0 || i == state.Series.Count - 1).ToList();
                state.Series.Clear();
                state.Series.AddRange(reduced);
                if (state.SeriesLabels.Count > 0)
                {
                    var lbl = state.SeriesLabels.Where((_, i) => i % step == 0 || i == state.SeriesLabels.Count - 1).ToList();
                    state.SeriesLabels.Clear();
                    state.SeriesLabels.AddRange(lbl);
                }
            }
        }
    }

    /// <summary>Tabla, imágenes y texto de la vista principal.</summary>
    static void ApplyView(ApiWidget api, ApiState state, JsonElement root)
    {
        state.TableHeaders.Clear();
        state.TableRows.Clear();
        state.ImageUrls.Clear();
        state.Text = "";
        var c = api.Chart;
        try
        {
            switch (c.Kind)
            {
                case ApiChartKind.Table when c.TablePath.Length > 0:
                    BuildTable(root, c, state);
                    break;
                case ApiChartKind.Image when c.ImagePath.Length > 0:
                    foreach (var e in JsonPathLite.Select(root, c.ImagePath).Take(8))
                        if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            state.ImageUrls.Add(url);
                    break;
                case ApiChartKind.Text when c.TextPath.Length > 0:
                    state.Text = string.Join("\n\n", JsonPathLite.Select(root, c.TextPath).Take(10).Select(JsonPathLite.Text));
                    break;
            }
        }
        catch { }
    }

    public const int TableMaxRows = 100;

    static void BuildTable(JsonElement root, ApiChart c, ApiState state)
    {
        string path = c.TablePath.Trim();
        if (!path.EndsWith("[*]")) path += "[*]";
        var rows = JsonPathLite.Select(root, path).Take(TableMaxRows).ToList();
        if (rows.Count == 0) return;

        var columns = c.TableColumns.Where(x => x.Trim().Length > 0).Select(x => x.Trim()).ToList();
        if (columns.Count == 0)
        {
            // Automáticas: las primeras propiedades simples (texto, número o sí/no) del primer elemento.
            if (rows[0].ValueKind == JsonValueKind.Object)
                columns = rows[0].EnumerateObject()
                    .Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    .Select(p => p.Name).Take(5).ToList();
            else if (rows[0].ValueKind == JsonValueKind.Array)
                columns = Enumerable.Range(0, Math.Min(5, rows[0].GetArrayLength())).Select(i => $"[{i}]").ToList();
            else columns = new List<string> { "" };
        }
        state.TableHeaders.AddRange(columns.Select(col => col.Length == 0 ? "Valor" : ApiExplorer.PrettyLabel(col)));
        foreach (var r in rows)
        {
            var cells = new List<string>();
            foreach (var col in columns)
            {
                JsonElement? v = col.Length == 0 ? r : JsonPathLite.First(r, col);
                if (v == null) { cells.Add(""); continue; }
                cells.Add(v.Value.ValueKind == JsonValueKind.Number && JsonPathLite.Number(v.Value) is double d
                    ? ApiFormat.Number(d)
                    : JsonPathLite.Text(v.Value));
            }
            state.TableRows.Add(cells);
        }
    }

    /// <summary>Valores para la gráfica: la serie de la respuesta o el historial guardado.</summary>
    public IReadOnlyList<double> ChartValues(ApiWidget api)
    {
        if (api.Chart.Kind is not (ApiChartKind.Line or ApiChartKind.Bars)) return Array.Empty<double>();
        if (api.Chart.SeriesPath.Length > 0) return State(api.Id).Series;
        return History(api.Id, Math.Clamp(api.Chart.HistoryField, 0, Math.Max(0, api.Fields.Count - 1)));
    }

    void AddHistory(string key, double v)
    {
        if (!_history.TryGetValue(key, out var list)) _history[key] = list = new List<double>();
        list.Add(v);
        if (list.Count > HistoryMax) list.RemoveRange(0, list.Count - HistoryMax);
        _historyDirty = true;
    }

    public void ClearHistory(string id)
    {
        foreach (var key in _history.Keys.Where(k => k.StartsWith(id + "#")).ToList()) _history.Remove(key);
        _historyDirty = true;
        SaveHistory();
    }

    void LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFile))
                _history = JsonSerializer.Deserialize<Dictionary<string, List<double>>>(File.ReadAllText(HistoryFile)) ?? new();
        }
        catch (Exception ex) { Log.Error("Historial de APIs", ex); }
    }

    public void SaveHistory()
    {
        if (!_historyDirty) return;
        _historyDirty = false;
        SafeFile.WriteAllText(HistoryFile, JsonSerializer.Serialize(_history));
    }
}

public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

// ---------------------------------------------------------------------
// Plantillas de APIs gratuitas (sin clave)
// ---------------------------------------------------------------------
public static class ApiTemplates
{
    public sealed record Template(string Name, string Description, Func<ApiWidget> Create);

    public static IReadOnlyList<Template> All { get; } = new List<Template>
    {
        new("Bitcoin", "Precio en dólares y gráfica de las últimas 24 h (CoinGecko)", () => Crypto("bitcoin", "Bitcoin")),
        new("Ethereum", "Precio en dólares y gráfica de las últimas 24 h (CoinGecko)", () => Crypto("ethereum", "Ethereum")),
        new("Dólar a peso", "Cuántos pesos colombianos vale un dólar (ExchangeRate-API)", () => new ApiWidget
        {
            Title = "Dólar a peso",
            Url = "https://open.er-api.com/v6/latest/USD",
            RefreshSeconds = 1800,
            Fields =
            {
                new ApiField { Label = "1 USD", Path = "rates.COP", Prefix = "$ ", Suffix = " COP", Decimals = 0 },
                new ApiField { Label = "1 EUR", Path = "rates.EUR", Suffix = " EUR", Decimals = 3 },
            },
            Chart = new ApiChart { Kind = ApiChartKind.Line, HistoryField = 0 },
        }),
        new("Temperatura por hora", "Pronóstico de hoy en tu ciudad (Open-Meteo)", () =>
        {
            var (lat, lon) = Location();
            string inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            return new ApiWidget
            {
                Title = "Temperatura hoy",
                Url = $"https://api.open-meteo.com/v1/forecast?latitude={inv(lat)}&longitude={inv(lon)}&current=temperature_2m,relative_humidity_2m&hourly=temperature_2m&forecast_days=1&timezone=auto",
                RefreshSeconds = 900,
                Fields =
                {
                    new ApiField { Label = "Ahora", Path = "current.temperature_2m", Suffix = " °C", Decimals = 1 },
                    new ApiField { Label = "Humedad", Path = "current.relative_humidity_2m", Suffix = " %", Decimals = 0 },
                },
                Chart = new ApiChart { Kind = ApiChartKind.Bars, SeriesPath = "hourly.temperature_2m[*]", LabelsPath = "hourly.time[*]" },
            };
        }),
    };

    static ApiWidget Crypto(string id, string name) => new()
    {
        Title = name,
        Url = $"https://api.coingecko.com/api/v3/coins/{id}/market_chart?vs_currency=usd&days=1",
        RefreshSeconds = 120,
        Fields =
        {
            new ApiField { Label = "Precio", Path = "prices[-1][1]", Prefix = "$ ", Decimals = 2 },
            new ApiField { Label = "Volumen 24 h", Path = "total_volumes[-1][1]", Prefix = "$ ", Compact = true },
        },
        Chart = new ApiChart { Kind = ApiChartKind.Line, SeriesPath = "prices[*][1]", LabelsPath = "prices[*][0]" },
    };

    /// <summary>Ubicación del clima elegida en Ajustes; si no hay, Bogotá.</summary>
    static (double, double) Location()
    {
        var s = Settings.Current;
        if (s.WeatherLat != 0 || s.WeatherLon != 0) return (s.WeatherLat, s.WeatherLon);
        return (4.711, -74.0721);
    }
}

// ---------------------------------------------------------------------
// Utilidades: escritura segura y cifrado de claves
// ---------------------------------------------------------------------

/// <summary>Escribe primero en un archivo temporal y luego lo reemplaza: un apagón no deja el archivo a medias.</summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(content);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch (Exception ex) { Log.Error("Guardar " + Path.GetFileName(path), ex); }
    }
}

/// <summary>Cifra textos con la cuenta de Windows del usuario (DPAPI): solo se pueden leer en este PC y usuario.</summary>
public static class Secret
{
    const string Prefix = "dpapi:";

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(plain);
            var input = new DATA_BLOB();
            var output = new DATA_BLOB();
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                input.cbData = data.Length;
                input.pbData = handle.AddrOfPinnedObject();
                if (!CryptProtectData(ref input, "DynamicIsland", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output))
                    return "b64:" + Convert.ToBase64String(data);
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return Prefix + Convert.ToBase64String(result);
            }
            finally
            {
                handle.Free();
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
        catch { return "b64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)); }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            if (stored.StartsWith("b64:")) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[4..]));
            if (!stored.StartsWith(Prefix)) return stored;
            byte[] data = Convert.FromBase64String(stored[Prefix.Length..]);
            var input = new DATA_BLOB();
            var output = new DATA_BLOB();
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                input.cbData = data.Length;
                input.pbData = handle.AddrOfPinnedObject();
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output)) return "";
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return Encoding.UTF8.GetString(result);
            }
            finally
            {
                handle.Free();
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
}
