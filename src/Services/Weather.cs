// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DynamicIsland.Core;
using Windows.Devices.Geolocation;

namespace DynamicIsland.Services;

public sealed record WeatherNow(double Temp, double Feels, int Code, bool IsDay, double Max, double Min,
    int Humidity, double Wind, string Place, string Unit, string WindUnit);

/// <summary>
/// Clima con Open-Meteo (gratis, sin clave). La ubicación sale, en este orden, de la ciudad
/// elegida en Ajustes, de Windows (Configuración > Privacidad > Ubicación) o aproximada por IP.
/// </summary>
public static class WeatherService
{
    static readonly HttpClient Http = CreateClient();
    static (double lat, double lon, string place)? _location;
    static DateTime _locatedAt = DateTime.MinValue;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/0.9");
        return c;
    }

    /// <summary>Cambia cuando el usuario elige otra ciudad; la pestaña Inicio recarga el clima.</summary>
    public static int Version { get; private set; }

    public static void ResetLocation()
    {
        _location = null;
        Version++;
    }

    /// <summary>Busca una ciudad por nombre (geocodificación de Open-Meteo).</summary>
    /// <returns>Nombre para mostrar y coordenadas, o null si no se encontró.</returns>
    public static async Task<(string Name, double Lat, double Lon)?> FindCityAsync(string query)
    {
        query = query.Trim();
        if (query.Length < 2) return null;
        string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=1&language={lang}&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        string name = r.GetProperty("name").GetString() ?? query;
        string region = r.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "";
        string country = r.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
        string full = string.Join(", ", new[] { name, region, country }.Where(x => x.Length > 0).Distinct());
        return (full, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
    }

    static string Inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public static async Task<WeatherNow?> GetAsync()
    {
        var loc = await LocateAsync();
        if (loc == null) return null;
        var (lat, lon, place) = loc.Value;

        bool imperial = !RegionInfo.CurrentRegion.IsMetric;
        string url = $"https://api.open-meteo.com/v1/forecast?latitude={Inv(lat)}&longitude={Inv(lon)}" +
                     "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m" +
                     "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1" +
                     (imperial ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var cur = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        return new WeatherNow(
            cur.GetProperty("temperature_2m").GetDouble(),
            cur.GetProperty("apparent_temperature").GetDouble(),
            cur.GetProperty("weather_code").GetInt32(),
            cur.GetProperty("is_day").GetInt32() == 1,
            daily.GetProperty("temperature_2m_max")[0].GetDouble(),
            daily.GetProperty("temperature_2m_min")[0].GetDouble(),
            (int)Math.Round(cur.GetProperty("relative_humidity_2m").GetDouble()),
            cur.GetProperty("wind_speed_10m").GetDouble(),
            place,
            imperial ? "°F" : "°C",
            imperial ? "mph" : "km/h");
    }

    static async Task<(double, double, string)?> LocateAsync()
    {
        if (_location != null && (DateTime.Now - _locatedAt).TotalMinutes < 60) return _location;

        var s = Settings.Current;
        if (s.WeatherCity.Length > 0 && (s.WeatherLat != 0 || s.WeatherLon != 0))
            return Remember(s.WeatherLat, s.WeatherLon, s.WeatherCity.Split(',')[0]);

        if (Settings.Current.UseWindowsLocation)
        {
            try
            {
                if (await Geolocator.RequestAccessAsync() == GeolocationAccessStatus.Allowed)
                {
                    var geo = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
                    var pos = await geo.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
                    double lat = pos.Coordinate.Point.Position.Latitude, lon = pos.Coordinate.Point.Position.Longitude;
                    string place = await ReverseAsync(lat, lon);
                    return Remember(lat, lon, place);
                }
            }
            catch (Exception ex) { Log.Error("Windows location", ex); }
        }

        // Respaldo: ubicación aproximada según la conexión a internet.
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipapi.co/json/"));
            var r = doc.RootElement;
            return Remember(r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                r.TryGetProperty("city", out var city) ? city.GetString() ?? "" : "");
        }
        catch (Exception ex) { Log.Error("IP location", ex); }

        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/"));
            var r = doc.RootElement;
            return Remember(r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                r.TryGetProperty("city", out var city) ? city.GetString() ?? "" : "");
        }
        catch (Exception ex) { Log.Error("IP location 2", ex); }
        return null;
    }

    static (double, double, string) Remember(double lat, double lon, string place)
    {
        _location = (lat, lon, place);
        _locatedAt = DateTime.Now;
        return (lat, lon, place);
    }

    static async Task<string> ReverseAsync(double lat, double lon)
    {
        try
        {
            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            string url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={Inv(lat)}&longitude={Inv(lon)}&localityLanguage={lang}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var r = doc.RootElement;
            foreach (var key in new[] { "city", "locality", "principalSubdivision" })
                if (r.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } s) return s;
        }
        catch { }
        return "";
    }

    /// <summary>Descripción del código meteorológico WMO.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Despejado",
        1 => "Mayormente despejado",
        2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Niebla",
        51 or 53 or 55 => "Llovizna",
        56 or 57 => "Llovizna helada",
        61 or 63 => "Lluvia",
        65 => "Lluvia fuerte",
        66 or 67 => "Lluvia helada",
        71 or 73 or 75 or 77 => "Nieve",
        80 or 81 => "Chubascos",
        82 => "Chubascos fuertes",
        85 or 86 => "Chubascos de nieve",
        95 => "Tormenta",
        96 or 99 => "Tormenta con granizo",
        _ => "",
    };
}
