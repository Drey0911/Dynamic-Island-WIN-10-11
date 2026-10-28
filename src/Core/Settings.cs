// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Core;

/// <summary>Ajustes del usuario en %LOCALAPPDATA%\DynamicIsland\settings.json (persisten al reiniciar).</summary>
public sealed class Settings
{
    // General
    public bool StartWithWindows { get; set; }
    public bool RunAsAdmin { get; set; } = true;
    public double IslandScale { get; set; } = 1.0;
    /// <summary>Forma: "attached" (pegada al borde), "floating" (flotante) o "discrete" (oculta sin actividad).</summary>
    public string IslandStyle { get; set; } = "attached";
    /// <summary>"translucent" (negro translúcido) o "solid".</summary>
    public string IslandBackground { get; set; } = "translucent";
    /// <summary>Color de acento "#RRGGBB".</summary>
    public string AccentColor { get; set; } = DynamicIsland.Core.UI.DefaultAccent;
    public int HoverDelayMs { get; set; } = 120;
    public bool HotkeysEnabled { get; set; } = true;
    public bool AlwaysHiddenUntilHover { get; set; }
    public bool ShowDynamicActivities { get; set; } = true;
    public bool ShowMediaActivity { get; set; } = true;
    public bool ShowTimerActivities { get; set; } = true;
    public bool ShowSystemActivities { get; set; } = true;
    public bool ShowOtherActivities { get; set; } = true;

    // Pantalla completa / juegos
    public bool ShowInFullscreen { get; set; } = true;
    public bool GameHud { get; set; } = true;
    /// <summary>Datos del modo juego en la isla: fps, low, ft, cpu, cput, gpu, gput, vram, ram, time.</summary>
    public System.Collections.Generic.List<string> HudItems { get; set; } = new() { "fps", "cpu", "gpu" };

    // Música
    public bool MusicVisualizer { get; set; } = true;

    // Avisos
    public bool NotifyVolume { get; set; } = true;
    public bool NotifyMicrophone { get; set; } = true;
    public bool NotifyBrightness { get; set; } = true;
    public bool NotifyCapsLock { get; set; } = true;
    public bool NotifyNetwork { get; set; } = true;
    public bool NotifyPower { get; set; } = true;
    public bool NotifyDownloads { get; set; } = true;
    public bool ShowPrivacy { get; set; } = true;
    public bool ShowCalls { get; set; } = true;
    public bool CallsInBrowser { get; set; } = true;

    // Privacidad
    public bool ClipboardHistory { get; set; } = true;

    // Traductor
    public string TranslateFrom { get; set; } = "auto";
    public string TranslateTo { get; set; } = "en";
    public string TranslatorService { get; set; } = "MyMemory";
    public string DeepLKey { get; set; } = "";
    public double SpeechRate { get; set; } = 1.0;

    // Inicio
    public bool WeatherEnabled { get; set; } = true;
    public bool UseWindowsLocation { get; set; } = true;
    /// <summary>Ciudad elegida a mano (vacía = automática).</summary>
    public string WeatherCity { get; set; } = "";
    public double WeatherLat { get; set; }
    public double WeatherLon { get; set; }

    // Música: letras
    public bool LyricsEnabled { get; set; } = true;
    public bool LyricsInIsland { get; set; } = true;

    // Portapapeles inteligente y capturas
    public bool SmartClipboard { get; set; } = true;
    public bool ScreenshotTools { get; set; } = true;

    // Concentración
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public bool FocusDoNotDisturb { get; set; } = true;

    // Bluetooth
    public bool BluetoothBattery { get; set; } = true;

    // Modo Beta DEV: APIs propias como módulos de la isla
    public bool DevMode { get; set; }
    public System.Collections.Generic.List<DynamicIsland.Services.ApiWidget> Apis { get; set; } = new();
    /// <summary>Segundos que se muestra cada API en la isla cuando hay varias.</summary>
    public int ApiRotateSeconds { get; set; } = 8;

    // Alarma
    public int AlarmHour { get; set; } = 7;
    public int AlarmMinute { get; set; }
    public bool AlarmEnabled { get; set; }

    [JsonIgnore] public static string FilePath => Path.Combine(Log.Folder, "settings.json");
    [JsonIgnore] public static Settings Current { get; } = Load();

    /// <summary>Se dispara después de Save().</summary>
    public static event Action? Changed;

    static Settings Load()
    {
        // Si el archivo quedó dañado (por ejemplo, un apagón al guardar), se usa la copia anterior.
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                if (s == null) continue;
                if (s.HudItems == null || s.HudItems.Count == 0) s.HudItems = new() { "fps", "cpu", "gpu" };
                s.Apis ??= new();
                s.Apis.RemoveAll(a => a == null);
                foreach (var api in s.Apis)
                {
                    api.Fields ??= new();
                    api.Chart ??= new();
                    api.Chart.TableColumns ??= new();
                }
                s.WeatherCity ??= "";
                if (s.IslandBackground != "solid") s.IslandBackground = "translucent"; // "blur" de la 0.9 ya no existe
                s.AccentColor ??= DynamicIsland.Core.UI.DefaultAccent;
                return s;
            }
            catch (Exception ex) { Log.Error("Settings load " + Path.GetFileName(path), ex); }
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            DynamicIsland.Services.SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Error("Settings save", ex); }
        Changed?.Invoke();
    }
}
