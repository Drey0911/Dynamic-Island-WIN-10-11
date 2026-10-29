// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamicIsland.Core;
using Windows.Media.SpeechSynthesis;

namespace DynamicIsland.Services;

/// <summary>
/// Traducción gratis con Google Translate (sin clave, el mismo motor del traductor web; MyMemory queda de respaldo)
/// o con DeepL (clave propia).
/// </summary>
public static class Translator
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DynamicIsland/1.0");
        return client;
    }

    public static readonly (string Code, string Name)[] Languages =
    {
        ("auto", "Detectar"), ("es", "Español"), ("en", "Inglés"), ("pt", "Portugués"),
        ("fr", "Francés"), ("de", "Alemán"), ("it", "Italiano"), ("ja", "Japonés"),
        ("zh", "Chino"), ("ko", "Coreano"), ("ru", "Ruso"), ("ar", "Árabe"),
    };

    // Resultados recientes: al escribir se repiten consultas (borrar y volver a escribir, Enter, cambiar idioma).
    // Así no se gasta la cuota diaria de MyMemory ni se espera de nuevo por la red.
    static readonly Dictionary<string, string> Cache = new();
    static readonly Queue<string> CacheOrder = new();
    const int CacheSize = 200;

    /// <summary>Motor que respondió la última traducción ("Google", "MyMemory" o "DeepL").</summary>
    public static string LastEngine { get; private set; } = "Google";

    public static async Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        var s = Settings.Current;
        bool deepL = s.TranslatorService == "DeepL" && !string.IsNullOrWhiteSpace(s.DeepLKey);
        if (from == to) return text; // Ya está en el idioma de destino.

        string key = $"{(deepL ? "D" : "G")}|{from}|{to}|{text}";
        lock (Cache) if (Cache.TryGetValue(key, out var cached)) { LastEngine = CachedEngine.GetValueOrDefault(key, LastEngine); return cached; }

        string result, engine;
        if (deepL)
        {
            // DeepL detecta el idioma mejor por su cuenta: solo recibe el origen si el usuario lo eligió.
            result = await DeepLAsync(text, from, to, s.DeepLKey.Trim(), ct);
            engine = "DeepL";
        }
        else
        {
            try
            {
                result = await GoogleAsync(text, from, to, ct);
                engine = "Google";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Google no respondió (sin red, bloqueo temporal...): se usa MyMemory como respaldo.
                Log.Error("Translate Google", ex);
                string source = from == "auto" ? DetectLocal(text) ?? "auto" : from;
                if (source == to) return text;
                result = await MyMemoryAsync(text, source, to, ct);
                engine = "MyMemory";
            }
        }

        lock (Cache)
        {
            if (!Cache.ContainsKey(key))
            {
                Cache[key] = result;
                CachedEngine[key] = engine;
                CacheOrder.Enqueue(key);
                while (CacheOrder.Count > CacheSize)
                {
                    string old = CacheOrder.Dequeue();
                    Cache.Remove(old);
                    CachedEngine.Remove(old);
                }
            }
        }
        LastEngine = engine;
        return result;
    }

    static readonly Dictionary<string, string> CachedEngine = new();

    // ---------- Google Translate (endpoint público gratuito del traductor web) ----------

    static string GoogleCode(string code) => code switch
    {
        "zh" => "zh-CN",
        _ => code,
    };

    static async Task<string> GoogleAsync(string text, string from, string to, CancellationToken ct)
    {
        // Las URL largas fallan: el texto se envía por fragmentos y se conservan los saltos de línea.
        var sb = new StringBuilder();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            if (l > 0) sb.Append(Environment.NewLine);
            string line = lines[l].Trim();
            if (line.Length == 0) continue;
            var parts = new List<string>();
            foreach (var chunk in Chunks(line, 1500))
                parts.Add(await GoogleChunkAsync(chunk, from, to, ct));
            sb.Append(string.Join(" ", parts.Where(p => p.Length > 0)));
        }
        return sb.ToString().Trim();
    }

    static async Task<string> GoogleChunkAsync(string chunk, string from, string to, CancellationToken ct)
    {
        string url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&ie=UTF-8&oe=UTF-8"
                     + $"&sl={GoogleCode(from)}&tl={GoogleCode(to)}&q={Uri.EscapeDataString(chunk)}";
        string json = await GetWithRetryAsync(url, ct, requireSuccess: true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidOperationException("Respuesta inesperada del traductor");
        var sb = new StringBuilder();
        if (root[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in root[0].EnumerateArray())
            {
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                    sb.Append(seg[0].GetString());
            }
        }
        string result = sb.ToString().Trim();
        // Sin segmentos (por ejemplo solo símbolos): se deja el texto tal cual.
        return result.Length > 0 ? result : chunk.Trim();
    }

    /// <summary>Idioma del texto sin internet: por alfabeto (japonés, chino, coreano, ruso, árabe) o por palabras frecuentes.</summary>
    static string? DetectLocal(string text)
    {
        int kana = 0, han = 0, hangul = 0, cyrillic = 0, arabic = 0, letters = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c is >= '぀' and <= 'ヿ') kana++;
            else if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿') han++;
            else if (c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏') hangul++;
            else if (c is >= 'Ѐ' and <= 'ӿ') cyrillic++;
            else if (c is >= '؀' and <= 'ۿ') arabic++;
        }
        if (letters == 0) return null;
        double half = letters * 0.5;
        if (kana > 0 && kana + han >= half) return "ja";
        if (han >= half) return "zh";
        if (hangul >= half) return "ko";
        if (cyrillic >= half) return "ru";
        if (arabic >= half) return "ar";

        string? lang = SmartText.DetectLanguage(text);
        return lang != null && Languages.Any(l => l.Code == lang) ? lang : null;
    }

    static async Task<string> MyMemoryAsync(string text, string from, string to, CancellationToken ct)
    {
        // MyMemory acepta hasta 500 bytes por consulta: se traduce por fragmentos y se conservan los saltos de línea.
        string source = from == "auto" ? "Autodetect" : from;
        var sb = new StringBuilder();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            if (l > 0) sb.Append(Environment.NewLine);
            string line = lines[l].Trim();
            if (line.Length == 0) continue;
            var parts = new List<string>();
            foreach (var chunk in Chunks(line, 450))
                parts.Add(await MyMemoryChunkAsync(chunk, source, to, ct));
            sb.Append(string.Join(" ", parts.Where(p => p.Length > 0)));
        }
        return sb.ToString().Trim();
    }

    static async Task<string> MyMemoryChunkAsync(string chunk, string source, string to, CancellationToken ct)
    {
        string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(chunk)}&langpair={source}|{to}&mt=1";
        string json = await GetWithRetryAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string status = root.TryGetProperty("responseStatus", out var st) ? st.ToString() : "200";
        string translated = "";
        if (root.TryGetProperty("responseData", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("translatedText", out var tt) && tt.ValueKind == JsonValueKind.String)
            translated = tt.GetString() ?? "";

        // Con "Detectar", si el texto ya está en el idioma de destino MyMemory responde con error: se deja igual.
        if (translated.Contains("DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase))
            return chunk.Trim();
        if (status != "200" || translated.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
        {
            string reason = translated.Contains("USED ALL AVAILABLE FREE TRANSLATIONS", StringComparison.OrdinalIgnoreCase)
                ? "Se agotó la cuota diaria de MyMemory"
                : translated.Length > 0 ? translated : "Servicio no disponible";
            throw new InvalidOperationException(reason);
        }
        translated = WebUtility.HtmlDecode(translated).Trim();

        // MyMemory es una memoria colaborativa: con palabras a medio escribir a veces devuelve entradas basura
        // ("testvalue", textos de prueba...). Se busca una coincidencia válida o se deja el texto original.
        if (IsJunk(translated, chunk) && root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        {
            string? best = null;
            double bestScore = -1;
            foreach (var m in matches.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("translation", out var t) || t.ValueKind != JsonValueKind.String) continue;
                string candidate = WebUtility.HtmlDecode(t.GetString() ?? "").Trim();
                if (IsJunk(candidate, chunk)) continue;
                double score = m.TryGetProperty("match", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() : 0;
                if (score > bestScore) { bestScore = score; best = candidate; }
            }
            translated = best ?? chunk.Trim();
        }
        return IsJunk(translated, chunk) ? chunk.Trim() : translated;
    }

    static readonly string[] JunkMarkers = { "testvalue", "test value", "nan", "null", "undefined", "translated text" };

    /// <summary>Respuestas que no son una traducción real del fragmento.</summary>
    static bool IsJunk(string translated, string original)
    {
        string t = translated.Trim();
        if (t.Length == 0) return true;
        string lower = t.ToLowerInvariant();
        if (JunkMarkers.Any(j => lower == j) || lower.Contains("testvalue")) return true;
        // Una palabra suelta que se "traduce" a un párrafo entero tampoco es fiable.
        return original.Trim().Length <= 12 && t.Length > Math.Max(40, original.Length * 6);
    }

    /// <summary>GET con un reintento ante fallos pasajeros de red o del servidor.</summary>
    static async Task<string> GetWithRetryAsync(string url, CancellationToken ct, bool requireSuccess = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var res = await Http.GetAsync(url, ct);
                int code = (int)res.StatusCode;
                if ((code >= 500 || code == 429) && attempt == 0) { await Task.Delay(400, ct); continue; }
                if (requireSuccess && !res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {code}");
                return await res.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex) when (attempt == 0 && !ct.IsCancellationRequested && !ex.Message.StartsWith("HTTP "))
            {
                await Task.Delay(400, ct);
            }
            catch (TaskCanceledException) when (attempt == 0 && !ct.IsCancellationRequested)
            {
                // Tiempo de espera agotado (no cancelado por el usuario): se reintenta una vez.
            }
        }
    }

    static async Task<string> DeepLAsync(string text, string from, string to, string key, CancellationToken ct)
    {
        string endpoint = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
        var body = new Dictionary<string, object> { ["text"] = new[] { text }, ["target_lang"] = DeepLTarget(to) };
        if (from != "auto") body["source_lang"] = from.ToUpperInvariant();

        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
            using var res = await Http.SendAsync(req, ct);
            int code = (int)res.StatusCode;
            if ((code == 429 || code >= 500) && attempt == 0) { await Task.Delay(600, ct); continue; }
            string json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(code switch
                {
                    403 => "DeepL: clave no válida",
                    456 => "DeepL: se agotó la cuota",
                    429 => "DeepL: demasiadas solicitudes",
                    _ => $"DeepL: {code}",
                });
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        }
    }

    static string DeepLTarget(string code) => code switch
    {
        "en" => "EN-US",
        "pt" => "PT-BR",
        _ => code.ToUpperInvariant(),
    };

    /// <summary>Fragmentos de como máximo <paramref name="max"/> bytes UTF-8, cortando por frases y luego por palabras.</summary>
    static IEnumerable<string> Chunks(string text, int max)
    {
        var current = new StringBuilder();
        foreach (var piece in SplitSentences(text).SelectMany(s => Encoding.UTF8.GetByteCount(s) > max ? SplitWords(s, max) : new[] { s }))
        {
            if (current.Length > 0 && Encoding.UTF8.GetByteCount(current.ToString() + piece) > max)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            current.Append(piece);
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString().Trim();
    }

    static IEnumerable<string> SplitWords(string sentence, int max)
    {
        var current = new StringBuilder();
        foreach (var word in sentence.Split(' '))
        {
            string w = word + " ";
            if (Encoding.UTF8.GetByteCount(w) > max)
            {
                // Palabra enorme (por ejemplo un enlace): corte duro sin partir caracteres.
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                var piece = new StringBuilder();
                var e = System.Globalization.StringInfo.GetTextElementEnumerator(w);
                while (e.MoveNext())
                {
                    string el = e.GetTextElement();
                    if (Encoding.UTF8.GetByteCount(piece.ToString() + el) > max) { yield return piece.ToString(); piece.Clear(); }
                    piece.Append(el);
                }
                if (piece.Length > 0) yield return piece.ToString();
                continue;
            }
            if (current.Length > 0 && Encoding.UTF8.GetByteCount(current.ToString() + w) > max)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(w);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    static IEnumerable<string> SplitSentences(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (".!?。！？".IndexOf(text[i]) < 0) continue;
            yield return text.Substring(start, i - start + 1);
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }
}

/// <summary>Lectura en voz alta con las voces instaladas en Windows.</summary>
public static class Speech
{
    static SoundPlayer? _player;

    /// <summary>Voces instaladas en Windows (Configuración > Hora e idioma > Voz).</summary>
    public static IReadOnlyList<VoiceInformation> Voices
    {
        get
        {
            // Se copia la lista dentro del try: algunas instalaciones fallan al recorrerla, no al pedirla.
            try { return SpeechSynthesizer.AllVoices.ToList(); }
            catch { return Array.Empty<VoiceInformation>(); }
        }
    }

    /// <summary>Voz para un idioma ("en", "es"...). Prefiere la de la región del PC (por ejemplo es-CO o es-MX).</summary>
    public static VoiceInformation? VoiceFor(string language)
    {
        var matches = Voices.Where(v => v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return null;
        string region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
        string regional = $"{language}-{region}";
        return matches.FirstOrDefault(v => v.Language.Equals(regional, StringComparison.OrdinalIgnoreCase))
               ?? matches.FirstOrDefault(v => v.Gender == VoiceGender.Female)
               ?? matches[0];
    }

    /// <summary>Nombre del idioma en español: "en" -> "Inglés".</summary>
    public static string LanguageName(string code)
    {
        var known = Translator.Languages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (known.Name != null) return known.Name;
        try { return new System.Globalization.CultureInfo(code).NativeName; }
        catch { return code; }
    }

    /// <summary>Abre la página de voces de Windows para descargar más idiomas.</summary>
    public static void OpenVoiceSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:speech") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Speech settings", ex); }
    }

    /// <returns>false si no hay voz instalada para ese idioma.</returns>
    public static async Task<bool> SpeakAsync(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var voice = VoiceFor(language);
        if (voice == null) return false;

        using var synth = new SpeechSynthesizer { Voice = voice };
        synth.Options.SpeakingRate = Math.Clamp(Settings.Current.SpeechRate, 0.5, 2.0);
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        var ms = new MemoryStream();
        using (var input = stream.AsStreamForRead()) await input.CopyToAsync(ms);
        ms.Position = 0;

        Stop();
        _player = new SoundPlayer(ms);
        _player.Play();
        return true;
    }

    public static void Stop()
    {
        try { _player?.Stop(); _player?.Dispose(); } catch { }
        _player = null;
    }
}
