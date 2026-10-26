// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

public readonly record struct LyricLine(double Time, string Text);

/// <summary>Letras sincronizadas desde LRCLIB (gratis, sin clave).</summary>
public static class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    static readonly Dictionary<string, List<LyricLine>?> Cache = new();
    static readonly Regex Stamp = new(@"\[(\d+):(\d+(?:[.:]\d+)?)\]", RegexOptions.Compiled);

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/0.6 (Windows)");
        return c;
    }

    /// <returns>Líneas ordenadas por tiempo, o null si no hay letra sincronizada.</returns>
    public static async Task<List<LyricLine>?> GetAsync(string title, string artist, string album, double durationSec)
    {
        title = title.Trim();
        artist = artist.Trim();
        if (title.Length == 0) return null;
        string key = $"{artist}|{title}".ToLowerInvariant();
        if (Cache.TryGetValue(key, out var cached)) return cached;

        List<LyricLine>? lines = null;
        try
        {
            string url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}" +
                         (album.Length > 0 ? $"&album_name={Uri.EscapeDataString(album)}" : "") +
                         (durationSec > 0 ? $"&duration={(int)Math.Round(durationSec)}" : "");
            using var res = await Http.GetAsync(url);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                lines = Parse(Synced(doc.RootElement));
            }
            if (lines == null)
            {
                // Búsqueda más flexible (sin álbum ni duración).
                string q = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(q));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    lines = Parse(Synced(item));
                    if (lines != null) break;
                }
            }
        }
        catch (Exception)
        {
            return null; // sin conexión o respuesta inesperada: se reintenta con la próxima canción
        }
        Cache[key] = lines;
        return lines;
    }

    static string? Synced(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;

    static List<LyricLine>? Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return null;
        var result = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var matches = Stamp.Matches(raw);
            if (matches.Count == 0) continue;
            string text = WebUtility.HtmlDecode(Stamp.Replace(raw, "").Trim());
            foreach (Match m in matches)
            {
                double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                result.Add(new LyricLine(min * 60 + sec, text));
            }
        }
        return result.Count == 0 ? null : result.OrderBy(l => l.Time).ToList();
    }

    /// <summary>Índice de la línea que corresponde a una posición (segundos). -1 antes de la primera.</summary>
    public static int IndexAt(List<LyricLine> lines, double position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}
