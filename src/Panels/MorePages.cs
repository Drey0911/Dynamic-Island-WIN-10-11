// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Panels;

// ---------------------------------------------------------------------
// Traductor
// ---------------------------------------------------------------------
public sealed class TranslatePage
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly TextBox _input;
    readonly TextBlock _output, _status, _fromLabel, _toLabel;
    readonly Border _picker;
    readonly WrapPanel _pickerList = new();
    bool _pickingFrom;
    int _request;

    static Settings S => Settings.Current;

    public TranslatePage()
    {
        _root = new Grid { Margin = new Thickness(16, 2, 16, 12) };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Idiomas
        var langs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        langs.Children.Add(LangButton(out _fromLabel, () => OpenPicker(true)));
        var swap = UI.Button(UI.Icon(UI.GSwap, 12, UI.Dim), Swap, 32, 28, 14, Brushes.Transparent);
        swap.Margin = new Thickness(4, 0, 4, 0);
        langs.Children.Add(swap);
        langs.Children.Add(LangButton(out _toLabel, () => OpenPicker(false)));
        _status = UI.Label("", 11, UI.Dim);
        _status.Margin = new Thickness(12, 0, 0, 0);
        langs.Children.Add(_status);
        _root.Children.Add(langs);

        // Texto de entrada y resultado
        var boxes = new Grid();
        boxes.ColumnDefinitions.Add(new ColumnDefinition());
        boxes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        boxes.ColumnDefinitions.Add(new ColumnDefinition());
        _input = UI.Input(double.NaN, multiline: true);
        _input.VerticalAlignment = VerticalAlignment.Stretch;
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                _ = TranslateAsync();
            }
        };
        boxes.Children.Add(_input);

        _output = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = UI.White,
            FontFamily = UI.TextFont,
            FontSize = 13,
        };
        var outBox = new Border
        {
            Background = UI.AccentFaint,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _output },
        };
        Grid.SetColumn(outBox, 2);
        boxes.Children.Add(outBox);
        Grid.SetRow(boxes, 1);
        _root.Children.Add(boxes);

        // Acciones
        var actions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(UI.IconTextButton(UI.GClipboard, "Pegar", PasteAndTranslate, 86, 32));
        left.Children.Add(new Border { Width = 6 });
        left.Children.Add(UI.IconTextButton(UI.GGlobe, "Traducir", () => _ = TranslateAsync(), 100, 32, UI.Accent, UI.Black));
        actions.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(UI.IconTextButton(UI.GSpeaker, "Escuchar", () => _ = SpeakAsync(), 100, 32));
        right.Children.Add(new Border { Width = 6 });
        right.Children.Add(UI.IconTextButton(UI.GCopy, "Copiar", CopyResult, 86, 32));
        actions.Children.Add(right);
        Grid.SetRow(actions, 2);
        _root.Children.Add(actions);

        // Selector de idioma (se superpone a la página)
        _picker = new Border
        {
            Background = UI.Hex("#F0101214"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12),
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _pickerList },
        };
        Grid.SetRowSpan(_picker, 3);
        _root.Children.Add(_picker);

        UpdateLabels();
    }

    static Border LangButton(out TextBlock label, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        label = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        sp.Children.Add(label);
        var chevron = UI.Icon(UI.GDown, 8, UI.Dim);
        chevron.Margin = new Thickness(6, 1, 0, 0);
        sp.Children.Add(chevron);
        return UI.Button(sp, onClick, 110, 28, 14);
    }

    static string LangName(string code) => Translator.Languages.FirstOrDefault(l => l.Code == code).Name ?? code;

    void UpdateLabels()
    {
        _fromLabel.Text = LangName(S.TranslateFrom);
        _toLabel.Text = LangName(S.TranslateTo);
    }

    void OpenPicker(bool from)
    {
        _pickingFrom = from;
        _pickerList.Children.Clear();
        foreach (var (code, name) in Translator.Languages)
        {
            if (!from && code == "auto") continue;
            string c = code;
            bool selected = (from ? S.TranslateFrom : S.TranslateTo) == code;
            var b = UI.Button(UI.CenterLabel(name, 12, selected ? UI.Accent : UI.White, FontWeights.SemiBold),
                () => PickLanguage(c), 100, 30, 15, selected ? UI.AccentSoft : null);
            b.Margin = new Thickness(0, 0, 6, 6);
            _pickerList.Children.Add(b);
        }
        _picker.Visibility = Visibility.Visible;
    }

    void PickLanguage(string code)
    {
        if (_pickingFrom) S.TranslateFrom = code; else S.TranslateTo = code;
        S.Save();
        _picker.Visibility = Visibility.Collapsed;
        UpdateLabels();
        if (_input.Text.Trim().Length > 0) _ = TranslateAsync();
    }

    void Swap()
    {
        if (S.TranslateFrom == "auto") return;
        (S.TranslateFrom, S.TranslateTo) = (S.TranslateTo, S.TranslateFrom);
        S.Save();
        UpdateLabels();
        if (_output.Text.Length > 0) _input.Text = _output.Text;
        if (_input.Text.Trim().Length > 0) _ = TranslateAsync();
    }

    void PasteAndTranslate()
    {
        var text = ClipboardHistory.CurrentText();
        if (string.IsNullOrWhiteSpace(text)) { _status.Text = "El portapapeles no tiene texto"; return; }
        _input.Text = text;
        _ = TranslateAsync();
    }

    /// <summary>Traduce un texto recibido desde otra parte de la isla.</summary>
    public void TranslateText(string text)
    {
        _picker.Visibility = Visibility.Collapsed;
        _input.Text = text;
        _ = TranslateAsync();
    }

    /// <summary>Atajo de teclado: traduce lo que haya en el portapapeles.</summary>
    public void TranslateClipboard()
    {
        _picker.Visibility = Visibility.Collapsed;
        PasteAndTranslate();
    }

    async Task TranslateAsync()
    {
        string text = _input.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        int id = ++_request;
        _status.Text = "Traduciendo...";
        try
        {
            string result = await Translator.TranslateAsync(text, S.TranslateFrom, S.TranslateTo);
            if (id != _request) return;
            _output.Text = result;
            _status.Text = S.TranslatorService == "DeepL" && S.DeepLKey.Length > 0 ? "DeepL" : "MyMemory";
        }
        catch (Exception ex)
        {
            if (id != _request) return;
            _status.Text = "No se pudo traducir";
            Log.Error("Translate", ex);
        }
    }

    async Task SpeakAsync()
    {
        string text = _output.Text.Length > 0 ? _output.Text : _input.Text;
        string lang = _output.Text.Length > 0 ? S.TranslateTo : (S.TranslateFrom == "auto" ? "es" : S.TranslateFrom);
        try
        {
            if (!await Speech.SpeakAsync(text, lang))
                _status.Text = "Instala la voz del idioma en Configuración > Hora e idioma > Voz";
        }
        catch (Exception ex)
        {
            _status.Text = "No se pudo reproducir la voz";
            Log.Error("Speech", ex);
        }
    }

    void CopyResult()
    {
        if (_output.Text.Length == 0) return;
        ClipboardHistory.CopySilently(_output.Text);
        _status.Text = "Copiado";
    }
}

// ---------------------------------------------------------------------
// Audio: salida, micrófono, brillo y volumen por aplicación
// ---------------------------------------------------------------------
public sealed class AudioPage
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly StackPanel _apps = new();
    readonly StackPanel _bt = new();
    readonly StackPanel _btCaption;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly Row _output, _input, _brightness;
    readonly Dictionary<int, Row> _appRows = new();
    int _pendingBrightness = -1, _ticks;

    sealed class Row
    {
        public readonly Grid Root = new() { Margin = new Thickness(0, 0, 0, 6), Height = 30 };
        public readonly TextBlock Icon, Name, Value;
        public readonly LevelSlider Slider = new();
        public readonly Border Mute;

        public Row(string glyph, string name, Action<double> onChange, Action? onMute)
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            Icon = UI.Icon(glyph, 14, UI.Accent);
            Icon.HorizontalAlignment = HorizontalAlignment.Left;
            Root.Children.Add(Icon);
            Name = UI.Label(name, 12, UI.White, FontWeights.SemiBold);
            Name.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(Name, 1);
            Root.Children.Add(Name);
            Slider.ValueChanged += v => { Value.Text = $"{v * 100:0}"; onChange(v); };
            Grid.SetColumn(Slider, 2);
            Root.Children.Add(Slider);
            Value = UI.Label("", 12, UI.Dim);
            Value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(Value, 3);
            Root.Children.Add(Value);
            Mute = UI.Button(UI.Icon(UI.GMute, 12, UI.Dim), () => onMute?.Invoke(), 26, 26, 13, Brushes.Transparent);
            Mute.HorizontalAlignment = HorizontalAlignment.Right;
            Mute.Visibility = onMute == null ? Visibility.Hidden : Visibility.Visible;
            Grid.SetColumn(Mute, 4);
            Root.Children.Add(Mute);
        }

        public void Set(string name, double value, bool muted)
        {
            Name.Text = name;
            Slider.Value = value;
            Value.Text = muted ? "0" : $"{value * 100:0}";
            Icon.Opacity = muted ? 0.4 : 1;
            if (Mute.Child is TextBlock t) t.Foreground = muted ? UI.Red : UI.Dim;
        }
    }

    public AudioPage()
    {
        _root = new Grid { Margin = new Thickness(18, 2, 18, 12) };
        var stack = new StackPanel();
        _output = new Row(UI.GSpeaker, "Salida", AudioControl.SetOutputVolume, () => { AudioControl.ToggleOutputMute(); Refresh(); });
        _input = new Row(UI.GMic, "Micrófono", AudioControl.SetInputVolume, () => { AudioControl.ToggleMicMute(); Refresh(); });
        _brightness = new Row(UI.GBrightness, "Brillo", v => { _pendingBrightness = (int)Math.Round(v * 100); _brightnessDebounce.Stop(); _brightnessDebounce.Start(); }, null);
        _brightness.Root.Visibility = Visibility.Collapsed;
        stack.Children.Add(_output.Root);
        stack.Children.Add(_input.Root);
        stack.Children.Add(_brightness.Root);
        var caption = UI.Caption(UI.GVolume, "Aplicaciones");
        caption.Margin = new Thickness(0, 4, 0, 8);
        stack.Children.Add(caption);
        stack.Children.Add(_apps);
        _btCaption = UI.Caption("\uE702", "Bluetooth");
        _btCaption.Margin = new Thickness(0, 8, 0, 8);
        stack.Children.Add(_btCaption);
        stack.Children.Add(_bt);
        _root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = stack });

        _brightnessDebounce.Tick += (_, _) =>
        {
            _brightnessDebounce.Stop();
            int v = _pendingBrightness;
            Task.Run(() => AudioControl.SetBrightness(v));
        };
        _tick.Tick += (_, _) => Refresh();
        _root.IsVisibleChanged += (_, _) =>
        {
            if (_root.IsVisible) { _ticks = 0; Refresh(); _tick.Start(); } else _tick.Stop();
        };
    }

    void Refresh()
    {
        var o = AudioControl.Output();
        if (o != null) _output.Set(o.Name, o.Volume, o.Muted);
        var i = AudioControl.Input();
        _input.Root.Visibility = i == null ? Visibility.Collapsed : Visibility.Visible;
        if (i != null) _input.Set(i.Name, i.Volume, i.Muted);

        // El brillo vía WMI es lento: se consulta cada 3 s en segundo plano.
        if (_ticks++ % 3 == 0)
        {
            Task.Run(() => AudioControl.GetBrightness()).ContinueWith(t =>
            {
                int? b = t.Result;
                _brightness.Root.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
                if (b != null) _brightness.Set("Brillo", b.Value / 100.0, false);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Dispositivos Bluetooth (los actualiza el módulo de Bluetooth cada pocos segundos)
        var bt = Modules.BluetoothInfo.Last;
        _btCaption.Visibility = bt.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _bt.Children.Clear();
        foreach (var d in bt)
        {
            var row = new Grid { Height = 26 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = UI.Icon(d.Battery is int lv ? Modules.Battery.Glyph(lv / 100.0, false) : "\uE702", 14, d.Battery is <= 20 ? UI.Red : UI.Accent);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(icon);
            var name = UI.Label(d.Name, 12, UI.White, FontWeights.SemiBold);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var value = UI.Label(d.Battery is int pct ? $"{pct} %" : "Conectado", 12, UI.Dim);
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            _bt.Children.Add(row);
        }

        var apps = AudioControl.Apps();
        var pids = apps.Select(a => a.Pid).ToHashSet();
        foreach (var gone in _appRows.Keys.Where(p => !pids.Contains(p)).ToList())
        {
            _apps.Children.Remove(_appRows[gone].Root);
            _appRows.Remove(gone);
        }
        foreach (var app in apps)
        {
            if (!_appRows.TryGetValue(app.Pid, out var row))
            {
                int pid = app.Pid;
                row = new Row(UI.GMusic, app.Name, v => AudioControl.SetAppVolume(pid, v), () => { AudioControl.ToggleAppMute(pid); Refresh(); });
                _appRows[pid] = row;
                _apps.Children.Add(row.Root);
            }
            row.Set(app.Name, app.Volume, app.Muted);
            row.Icon.Foreground = app.Peak > 0.01 ? UI.Accent : UI.Dim;
        }
    }
}

// ---------------------------------------------------------------------
// Ajustes (se guardan en settings.json y se mantienen al apagar el PC)
// ---------------------------------------------------------------------
public sealed class SettingsPage
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly StackPanel _stack = new();

    static Settings S => Settings.Current;

    public SettingsPage()
    {
        _root = new Grid { Margin = new Thickness(18, 2, 10, 10) };
        _root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _stack, Padding = new Thickness(0, 0, 8, 0) });

        Section("General");
        Toggle("Iniciar con Windows", "Abre la isla al encender el PC", () => S.StartWithWindows, v => { S.StartWithWindows = v; App.ApplyStartup(); });
        Toggle("Ejecutar como administrador", "Activa la temperatura del CPU, los FPS de cualquier juego y el arrastre de archivos desde apps de administrador",
            () => S.RunAsAdmin, v => { S.RunAsAdmin = v; App.ApplyStartup(); }, badge: "Recomendado");
        if (!Native.IsElevated())
            ActionCard(UI.GShield, "Reiniciar como administrador", "Aplica el modo administrador sin cerrar la app.",
                () => null, "Reiniciar", App.RestartElevated);
        Choice("Tamaño de la isla", new[] { (0.9, "Pequeña"), (1.0, "Normal"), (1.15, "Grande") }, S.IslandScale, v => S.IslandScale = v);
        Choice("Abrir al pasar el mouse", new[] { (60, "Rápido"), (120, "Normal"), (350, "Lento") }, S.HoverDelayMs, v => S.HoverDelayMs = v);
        Toggle("Atajos de teclado", "Ctrl+Shift+Espacio panel · Ctrl+Shift+Y traducir · Ctrl+Shift+F9 micrófono · Ctrl+Shift+F10/F11/F12 anterior, pausa, siguiente",
            () => S.HotkeysEnabled, v => S.HotkeysEnabled = v);

        Section("Rendimiento y FPS");
        ActionCard(UI.GGame, "Medidor de FPS",
            FpsService.PresentMonAvailable
                ? "Intel PresentMon: FPS de cualquier juego o app (DirectX, Vulkan, OpenGL)."
                : "PresentMon no encontrado: solo FPS de DirectX. Ejecuta build.bat de nuevo para descargarlo.",
            () => FpsService.PresentMonAvailable ? "Listo" : null, null, null);
        ActionCard(UI.GShield, "Permitir FPS sin administrador",
            "Se hace una sola vez. Después cierra sesión en Windows y vuelve a entrar.",
            () => HasFpsPermission() ? "Activo" : null, "Permitir", AllowFpsWithoutAdmin);
        ActionCard(UI.GCpu, "Controlador de sensores (PawnIO)",
            "Necesario para la temperatura del CPU. Se instala con build.bat; aquí puedes repetir la instalación.",
            () => SensorDriverInstalled() ? "Instalado" : null, "Instalar", InstallSensorDriver);

        Section("Pantalla completa y juegos");
        Toggle("Mostrar la isla en pantalla completa", "Sobre juegos en modo ventana sin bordes, videos y presentaciones", () => S.ShowInFullscreen, v => S.ShowInFullscreen = v);
        Toggle("Rendimiento en juegos", "Datos de rendimiento en la isla mientras juegas", () => S.GameHud, v => S.GameHud = v);
        MultiChoice("Qué mostrar en la isla", $"Elige de 1 a {Modules.GameModule.MaxItems}. El detalle completo aparece al abrir la isla.",
            Modules.GameModule.Items, S.HudItems, Modules.GameModule.MaxItems);

        Section("Inicio");
        Toggle("Clima", "Temperatura y pronóstico del día con Open-Meteo", () => S.WeatherEnabled, v => S.WeatherEnabled = v);
        Toggle("Usar la ubicación de Windows", "Si está desactivada o no hay permiso, se usa una ubicación aproximada por internet",
            () => S.UseWindowsLocation, v => { S.UseWindowsLocation = v; Services.WeatherService.ResetLocation(); });
        CityPicker();

        Section("Música");
        Toggle("Efectos dinámicos", "La isla reacciona al ritmo de lo que suena", () => S.MusicVisualizer, v => S.MusicVisualizer = v);
        Toggle("Letras sincronizadas", "Descarga la letra de LRCLIB y la muestra al ritmo de la canción", () => S.LyricsEnabled, v => S.LyricsEnabled = v);
        Toggle("Letra en la isla", "Muestra la línea actual también con la isla cerrada", () => S.LyricsInIsland, v => S.LyricsInIsland = v);

        Section("Portapapeles y capturas");
        Toggle("Portapapeles inteligente", "Colores, operaciones, enlaces y textos en otro idioma", () => S.SmartClipboard, v => S.SmartClipboard = v);
        Toggle("Capturas de pantalla", "Al copiar una imagen (Win+Shift+S): extraer texto, guardar o traducir", () => S.ScreenshotTools, v => S.ScreenshotTools = v);

        Section("Concentración");
        Choice("Enfoque", new[] { (25, "25 min"), (45, "45 min"), (50, "50 min") }, S.FocusMinutes, v => S.FocusMinutes = v);
        Choice("Descanso", new[] { (5, "5 min"), (10, "10 min"), (15, "15 min") }, S.BreakMinutes, v => S.BreakMinutes = v);
        Toggle("Activar No molestar al concentrarte", "Silencia las notificaciones de Windows durante el enfoque", () => S.FocusDoNotDisturb, v => S.FocusDoNotDisturb = v);

        Section("Bluetooth");
        Toggle("Batería de dispositivos", "Avisa al conectar audífonos, mouse o teclados y muestra su batería", () => S.BluetoothBattery, v => S.BluetoothBattery = v);

        Section("Avisos");
        Toggle("Volumen y salida de audio", null, () => S.NotifyVolume, v => S.NotifyVolume = v);
        Toggle("Micrófono", "Cambio de dispositivo y silencio", () => S.NotifyMicrophone, v => S.NotifyMicrophone = v);
        Toggle("Brillo", "Solo pantallas de portátil", () => S.NotifyBrightness, v => S.NotifyBrightness = v);
        Toggle("Bloq Mayús", null, () => S.NotifyCapsLock, v => S.NotifyCapsLock = v);
        Toggle("Conexión a internet", null, () => S.NotifyNetwork, v => S.NotifyNetwork = v);
        Toggle("Cargador", null, () => S.NotifyPower, v => S.NotifyPower = v);
        Toggle("Descargas", null, () => S.NotifyDownloads, v => S.NotifyDownloads = v);
        Toggle("Llamadas activas", "Teams, Zoom, Discord, Meet y otras", () => S.ShowCalls, v => S.ShowCalls = v);
        Toggle("Uso de micrófono y cámara", null, () => S.ShowPrivacy, v => S.ShowPrivacy = v);

        Section("Privacidad");
        Toggle("Historial del portapapeles", "Solo en memoria, nunca se guarda en disco", () => S.ClipboardHistory, v => S.ClipboardHistory = v);

        Section("Traductor");
        Choice("Servicio", new[] { ("MyMemory", "MyMemory (gratis)"), ("DeepL", "DeepL") }, S.TranslatorService, v => S.TranslatorService = v);
        var keyRow = new Grid { Margin = new Thickness(0, 2, 0, 10) };
        var key = UI.Input(32);
        key.Text = S.DeepLKey;
        key.LostFocus += (_, _) => { S.DeepLKey = key.Text.Trim(); S.Save(); };
        keyRow.Children.Add(key);
        var hint = UI.Label("Clave de API de DeepL (opcional)", 11, UI.Dim);
        hint.Margin = new Thickness(0, 0, 0, 4);
        _stack.Children.Add(hint);
        _stack.Children.Add(keyRow);

        Section("Acerca de");
        _stack.Children.Add(UI.Label($"Dynamic Island {AppVersion()} · Licencia MIT · 2026", 11, UI.Dim));
        var open = UI.IconTextButton(UI.GOpenFolder, "Abrir carpeta de ajustes", () =>
        {
            try { Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true }); } catch { }
        }, 190, 30);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 8, 0, 6);
        _stack.Children.Add(open);
    }

    /// <summary>
    /// Fila de acción en forma de tarjeta: icono, título, descripción y a la derecha
    /// un estado ("Instalado", "Activo") o un botón.
    /// </summary>
    void ActionCard(string glyph, string title, string description, Func<string?> status, string? buttonText, Action? onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = UI.AccentFaint,
            VerticalAlignment = VerticalAlignment.Center,
            Child = UI.Icon(glyph, 15, UI.Accent),
        };
        grid.Children.Add(icon);

        var texts = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(UI.Label(title, 13, UI.White, FontWeights.SemiBold));
        var d = UI.Label(description, 11, UI.Dim);
        d.TextTrimming = TextTrimming.None;
        d.TextWrapping = TextWrapping.Wrap;
        d.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(d);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var right = new Grid { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        void Refresh()
        {
            right.Children.Clear();
            string? done = status();
            if (done != null)
            {
                var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                chip.Children.Add(UI.Icon("\uE73E", 11, UI.Green));
                var t = UI.Label(done, 12, UI.Green, FontWeights.SemiBold);
                t.Margin = new Thickness(5, 0, 2, 0);
                chip.Children.Add(t);
                right.Children.Add(chip);
            }
            else if (buttonText != null && onClick != null)
            {
                right.Children.Add(UI.Button(UI.CenterLabel(buttonText, 12, UI.Black, FontWeights.SemiBold), onClick, 96, 30, 15, UI.Accent));
            }
        }
        Refresh();
        _root.IsVisibleChanged += (_, _) => { if (_root.IsVisible) Refresh(); };

        var card = new Border
        {
            Background = UI.CardBg,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid,
        };
        _stack.Children.Add(card);
    }

    static bool HasFpsPermission()
    {
        try
        {
            if (Native.IsElevated()) return true;
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var perfLogUsers = new System.Security.Principal.SecurityIdentifier("S-1-5-32-559");
            return id.Groups?.Contains(perfLogUsers) == true;
        }
        catch { return false; }
    }

    public static bool SensorDriverInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key != null;
        }
        catch { return false; }
    }

    static TextBlock Note(string text)
    {
        var t = UI.Label(text, 11, UI.Dim);
        t.TextTrimming = TextTrimming.None;
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    /// <summary>
    /// Agrega al usuario al grupo "Usuarios del registro de rendimiento" (SID S-1-5-32-559),
    /// que permite leer eventos ETW sin ejecutar como administrador. Requiere cerrar sesión.
    /// </summary>
    static void AllowFpsWithoutAdmin()
    {
        // Se usa el SID del usuario actual (se calcula antes de elevar, así es el usuario correcto).
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        if (sid.Length == 0) return;
        string script =
            $"try {{ Add-LocalGroupMember -SID 'S-1-5-32-559' -Member '{sid}' -ErrorAction Stop }} " +
            "catch { if ($_.FullyQualifiedErrorId -notlike '*MemberExists*') { Write-Host $_; Start-Sleep 10; exit } }; " +
            "Write-Host 'Listo. Cierra sesion en Windows y vuelve a entrar para aplicar el permiso.'; Start-Sleep 6";
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Exception ex) { Log.Error("FPS permission", ex); }
    }

    /// <summary>Instala PawnIO, el controlador que usa LibreHardwareMonitor para leer sensores del CPU.</summary>
    static void InstallSensorDriver()
    {
        try
        {
            Process.Start(new ProcessStartInfo("winget.exe", "install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Error("PawnIO", ex); }
    }

    static string AppVersion() =>
        "v" + (typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "");

    void Section(string title)
    {
        var t = UI.Label(title.ToUpperInvariant(), 11, UI.Accent, FontWeights.SemiBold);
        t.Margin = new Thickness(0, _stack.Children.Count == 0 ? 0 : 10, 0, 6);
        _stack.Children.Add(t);
    }

    void Toggle(string title, string? description, Func<bool> get, Action<bool> set, string? badge = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(UI.Label(title, 13, UI.White, FontWeights.SemiBold));
        if (badge != null)
        {
            titleRow.Children.Add(new Border
            {
                Background = UI.AccentSoft,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = UI.Label(badge, 10, UI.Accent, FontWeights.SemiBold),
            });
        }
        texts.Children.Add(titleRow);
        if (description != null)
        {
            var d = UI.Label(description, 11, UI.Dim);
            d.TextTrimming = TextTrimming.None;
            d.TextWrapping = TextWrapping.Wrap;
            d.Margin = new Thickness(0, 1, 12, 0);
            texts.Children.Add(d);
        }
        row.Children.Add(texts);
        var sw = new ToggleSwitch(get()) { VerticalAlignment = VerticalAlignment.Center };
        sw.Toggled += v => { set(v); S.Save(); };
        Grid.SetColumn(sw, 1);
        row.Children.Add(sw);
        _stack.Children.Add(row);
    }

    void Choice<T>(string title, (T value, string label)[] options, T current, Action<T> set)
    {
        var t = UI.Label(title, 13, UI.White, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 0, 0, 6);
        _stack.Children.Add(t);
        var seg = UI.Segmented(options, current, v => { set(v); S.Save(); });
        seg.Margin = new Thickness(0, 0, 0, 4);
        _stack.Children.Add(seg);
    }

    /// <summary>Selección múltiple con fichas (por ejemplo, los datos del modo juego).</summary>
    void MultiChoice(string title, string hint, (string Key, string Label)[] options, List<string> selected, int max)
    {
        var t = UI.Label(title, 13, UI.White, FontWeights.SemiBold);
        _stack.Children.Add(t);
        var h = UI.Label(hint, 11, UI.Dim);
        h.TextWrapping = TextWrapping.Wrap;
        h.TextTrimming = TextTrimming.None;
        h.Margin = new Thickness(0, 1, 0, 6);
        _stack.Children.Add(h);

        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var chips = new List<(Border Border, TextBlock Text, string Key)>();
        void Paint()
        {
            foreach (var (border, text, key) in chips)
            {
                bool on = selected.Contains(key);
                border.Background = on ? UI.AccentSoft : UI.ButtonBg;
                text.Foreground = on ? UI.Accent : UI.White;
            }
        }
        foreach (var (key, label) in options)
        {
            var text = UI.Label(label, 12, UI.White, FontWeights.SemiBold);
            var border = new Border
            {
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(14),
                Child = text,
                Cursor = Cursors.Hand,
            };
            border.MouseLeftButtonDown += (_, e) => e.Handled = true;
            border.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (selected.Contains(key))
                {
                    if (selected.Count <= 1) return; // siempre queda al menos uno
                    selected.Remove(key);
                }
                else
                {
                    if (selected.Count >= max) return;
                    selected.Add(key);
                }
                Paint();
                S.Save();
            };
            chips.Add((border, text, key));
            panel.Children.Add(border);
        }
        Paint();
        _stack.Children.Add(panel);
    }

    /// <summary>Ciudad del clima elegida a mano, por si la ubicación automática no es la correcta.</summary>
    void CityPicker()
    {
        var t = UI.Label("Ciudad del clima", 13, UI.White, FontWeights.SemiBold);
        _stack.Children.Add(t);
        var status = UI.Label(S.WeatherCity.Length > 0 ? $"Usando: {S.WeatherCity}" : "Automática (Windows o internet)", 11, UI.Dim);
        status.TextWrapping = TextWrapping.Wrap;
        status.TextTrimming = TextTrimming.None;
        status.Margin = new Thickness(0, 1, 0, 6);
        _stack.Children.Add(status);

        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var input = UI.Input(32);
        input.Text = S.WeatherCity.Split(',')[0];
        row.Children.Add(input);

        async void Search()
        {
            string q = input.Text.Trim();
            if (q.Length < 2) return;
            status.Text = "Buscando...";
            try
            {
                var found = await Services.WeatherService.FindCityAsync(q);
                if (found == null) { status.Text = "No se encontró esa ciudad. Prueba con \"Ciudad, País\"."; return; }
                S.WeatherCity = found.Value.Name;
                S.WeatherLat = found.Value.Lat;
                S.WeatherLon = found.Value.Lon;
                S.Save();
                Services.WeatherService.ResetLocation();
                status.Text = $"Usando: {S.WeatherCity}";
            }
            catch (Exception ex)
            {
                Log.Error("City search", ex);
                status.Text = "Sin conexión. Inténtalo de nuevo.";
            }
        }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Search(); } };

        var find = UI.Button(UI.CenterLabel("Buscar", 12, UI.Black, FontWeights.SemiBold), Search, 80, 32, 16, UI.Accent);
        find.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(find, 1);
        row.Children.Add(find);
        var auto = UI.Button(UI.CenterLabel("Automática", 12, UI.White, FontWeights.SemiBold), () =>
        {
            S.WeatherCity = "";
            S.WeatherLat = 0;
            S.WeatherLon = 0;
            S.Save();
            Services.WeatherService.ResetLocation();
            input.Text = "";
            status.Text = "Automática (Windows o internet)";
        }, 96, 32, 16);
        auto.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(auto, 2);
        row.Children.Add(auto);
        _stack.Children.Add(row);
    }

    void AddAction(string text, string glyph, Action onClick)
    {
        var b = UI.IconTextButton(glyph, text, onClick, 260, 30, UI.AccentSoft, UI.Accent);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Margin = new Thickness(0, 0, 0, 10);
        _stack.Children.Add(b);
    }
}
