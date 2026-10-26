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
using System.Threading.Tasks;
using DynamicIsland.Core;
using Windows.Media.SpeechSynthesis;

namespace DynamicIsland.Services;

/// <summary>
/// Traducción con MyMemory (gratis, sin clave, ~5000 caracteres/día) o DeepL (clave propia).
/// </summary>
public static class Translator
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static readonly (string Code, string Name)[] Languages =
    {
        ("auto", "Detectar"), ("es", "Español"), ("en", "Inglés"), ("pt", "Portugués"),
        ("fr", "Francés"), ("de", "Alemán"), ("it", "Italiano"), ("ja", "Japonés"),
        ("zh", "Chino"), ("ko", "Coreano"), ("ru", "Ruso"), ("ar", "Árabe"),
    };

    public static async Task<string> TranslateAsync(string text, string from, string to)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        var s = Settings.Current;
        if (s.TranslatorService == "DeepL" && !string.IsNullOrWhiteSpace(s.DeepLKey))
            return await DeepLAsync(text, from, to, s.DeepLKey.Trim());
        return await MyMemoryAsync(text, from, to);
    }

    static async Task<string> MyMemoryAsync(string text, string from, string to)
    {
        // MyMemory acepta hasta 500 bytes por consulta: se traduce por fragmentos.
        var sb = new StringBuilder();
        string source = from == "auto" ? "Autodetect" : from;
        foreach (var chunk in Chunks(text, 450))
        {
            string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(chunk)}&langpair={source}|{to}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            string status = root.TryGetProperty("responseStatus", out var st) ? st.ToString() : "200";
            string translated = root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
            if (status != "200") throw new InvalidOperationException(translated.Length > 0 ? translated : "Servicio no disponible");
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(WebUtility.HtmlDecode(translated));
        }
        return sb.ToString();
    }

    static async Task<string> DeepLAsync(string text, string from, string to, string key)
    {
        string endpoint = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
        var body = new Dictionary<string, object> { ["text"] = new[] { text }, ["target_lang"] = DeepLTarget(to) };
        if (from != "auto") body["source_lang"] = from.ToUpperInvariant();

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
        using var res = await Http.SendAsync(req);
        string json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"DeepL: {(int)res.StatusCode}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }

    static string DeepLTarget(string code) => code switch
    {
        "en" => "EN-US",
        "pt" => "PT-BR",
        _ => code.ToUpperInvariant(),
    };

    static IEnumerable<string> Chunks(string text, int max)
    {
        var current = new StringBuilder();
        foreach (var sentence in SplitSentences(text))
        {
            if (current.Length + sentence.Length > max && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            if (sentence.Length > max)
            {
                for (int i = 0; i < sentence.Length; i += max)
                    yield return sentence.Substring(i, Math.Min(max, sentence.Length - i));
                continue;
            }
            current.Append(sentence);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    static IEnumerable<string> SplitSentences(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (".!?\n".IndexOf(text[i]) < 0) continue;
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

    /// <returns>false si no hay voz instalada para ese idioma.</returns>
    public static async Task<bool> SpeakAsync(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
            v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase));
        if (voice == null) return false;

        using var synth = new SpeechSynthesizer { Voice = voice };
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
