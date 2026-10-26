// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows;
using DynamicIsland.Core;
using DynamicIsland.Modules;
using DynamicIsland.Services;

namespace DynamicIsland.Panels;

// ---------------------------------------------------------------------
// Inicio
// ---------------------------------------------------------------------
public sealed class HomePage
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly TextBlock _time, _date, _batteryIcon, _batteryText, _net, _wTemp, _wDesc, _wRange;
    readonly StackPanel _battery, _weather;
    readonly WeatherIcon _wIcon = new(30);
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _weatherAt = DateTime.MinValue;
    bool _weatherLoading;

    public HomePage()
    {
        _root = new Grid { Margin = new Thickness(30, 0, 30, 14) };
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _time = UI.Label("", 50, UI.White, FontWeights.SemiBold, display: true);
        _date = UI.Label("", 15, UI.Dim);
        _date.Margin = new Thickness(2, 0, 0, 0);
        clock.Children.Add(_time);
        clock.Children.Add(_date);

        // Clima
        _weather = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        _weather.Children.Add(_wIcon);
        _wTemp = UI.Label("", 22, UI.White, FontWeights.SemiBold, display: true);
        _wTemp.Margin = new Thickness(10, 0, 12, 0);
        _weather.Children.Add(_wTemp);
        var wText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _wDesc = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _wRange = UI.Label("", 11, UI.Dim);
        wText.Children.Add(_wDesc);
        wText.Children.Add(_wRange);
        _weather.Children.Add(wText);
        clock.Children.Add(_weather);
        _root.Children.Add(clock);

        var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        _battery = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10) };
        _batteryText = UI.Label("", 14, UI.White, FontWeights.SemiBold);
        _batteryIcon = UI.Icon("", 20);
        _batteryIcon.Margin = new Thickness(8, 0, 0, 0);
        _battery.Children.Add(_batteryText);
        _battery.Children.Add(_batteryIcon);
        status.Children.Add(_battery);
        var netRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _net = UI.Label("", 13, UI.Dim);
        netRow.Children.Add(_net);
        var netIcon = UI.Icon(UI.GWifi, 15, UI.Accent);
        netIcon.Margin = new Thickness(8, 0, 0, 0);
        netRow.Children.Add(netIcon);
        status.Children.Add(netRow);
        Grid.SetColumn(status, 1);
        _root.Children.Add(status);

        _tick.Tick += (_, _) => Update();
        _root.IsVisibleChanged += (_, _) =>
        {
            if (_root.IsVisible) { Update(); _tick.Start(); } else _tick.Stop();
        };
    }

    void Update()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        _time.Text = now.ToString("t", culture);
        string date = culture.TwoLetterISOLanguageName == "es"
            ? now.ToString("dddd, d 'de' MMMM 'de' yyyy", culture)
            : now.ToString("D", culture);
        _date.Text = date.Length > 0 ? char.ToUpper(date[0], culture) + date[1..] : date;

        if (Battery.HasBattery)
        {
            _battery.Visibility = Visibility.Visible;
            double level = Battery.Level;
            bool charging = Battery.Charging;
            _batteryText.Text = $"{Math.Round(level * 100)} %";
            _batteryIcon.Text = Battery.Glyph(level, charging);
            _batteryIcon.Foreground = charging ? UI.Green : level <= 0.2 ? UI.Red : UI.Accent;
        }
        else _battery.Visibility = Visibility.Collapsed;

        string net = NetworkInfo.ActiveName();
        _net.Text = net.Length > 0 ? net : "Sin conexión";

        if (!Settings.Current.WeatherEnabled) _weather.Visibility = Visibility.Collapsed;
        else if ((DateTime.Now - _weatherAt).TotalMinutes >= 15 && !_weatherLoading) _ = LoadWeatherAsync();
    }

    async System.Threading.Tasks.Task LoadWeatherAsync()
    {
        _weatherLoading = true;
        try
        {
            var w = await WeatherService.GetAsync();
            if (w == null) { _weatherAt = DateTime.Now.AddMinutes(-10); return; } // reintento en 5 min
            _weatherAt = DateTime.Now;
            _wIcon.Set(w.Code, w.IsDay);
            _wTemp.Text = $"{w.Temp:0}{w.Unit}";
            string place = w.Place.Length > 0 ? " · " + w.Place : "";
            _wDesc.Text = WeatherService.Describe(w.Code) + place;
            _wRange.Text = $"Máx {w.Max:0}° · Mín {w.Min:0}° · Sensación {w.Feels:0}° · Humedad {w.Humidity}%";
            _weather.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _weatherAt = DateTime.Now.AddMinutes(-10); // reintento en 5 min
            Log.Error("Weather", ex);
        }
        finally { _weatherLoading = false; }
    }
}

// ---------------------------------------------------------------------
// Tiempo: temporizador, cronómetro y alarma
// ---------------------------------------------------------------------
public sealed class TimePage
{
    public FrameworkElement View => _root;
    readonly UniformGrid _root;
    readonly StackPanel _timerPresets, _timerRunning;
    readonly TextBlock _timerText, _timerPauseGlyph, _swText, _swGlyph, _alHour, _alMinute, _focusInfo;
    readonly ToggleSwitch _alSwitch;
    readonly Border _focusButton;
    readonly TimerModule _timer;
    readonly StopwatchModule _sw;
    readonly AlarmModule _alarm;
    readonly FocusModule _focus;

    public TimePage(TimerModule timer, StopwatchModule sw, AlarmModule alarm, FocusModule focus)
    {
        _timer = timer;
        _sw = sw;
        _alarm = alarm;
        _focus = focus;
        _root = new UniformGrid { Columns = 4, Margin = new Thickness(8, 4, 8, 14) };

        // Temporizador
        var tCard = new StackPanel();
        tCard.Children.Add(UI.Caption(UI.GTimer, "Temporizador"));
        _timerPresets = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        int[][] rows = { new[] { 5, 15 }, new[] { 25, 45 } };
        foreach (var row in rows)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            foreach (int m in row)
            {
                int minutes = m;
                var b = UI.Button(UI.CenterLabel($"{m}m", 12, UI.White, FontWeights.SemiBold),
                    () => _timer.StartTimer(TimeSpan.FromMinutes(minutes)), 44, 34, 12);
                b.Margin = new Thickness(2, 0, 2, 0);
                sp.Children.Add(b);
            }
            _timerPresets.Children.Add(sp);
        }
        var hint = UI.CenterLabel("Elige minutos", 11, UI.Dim);
        hint.Margin = new Thickness(0, 4, 0, 0);
        _timerPresets.Children.Add(hint);
        tCard.Children.Add(_timerPresets);

        _timerRunning = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        _timerText = UI.CenterLabel("0:00", 28, UI.Accent, FontWeights.Light);
        _timerText.FontFamily = UI.DisplayFont;
        _timerRunning.Children.Add(_timerText);
        var tButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        _timerPauseGlyph = UI.Icon(UI.GPause, 14, UI.Accent);
        tButtons.Children.Add(UI.Button(UI.CenterLabel("+1", 12, UI.White, FontWeights.SemiBold), () => _timer.AddMinute(), 30, 30, 15));
        tButtons.Children.Add(new Border { Width = 4 });
        tButtons.Children.Add(UI.Button(_timerPauseGlyph, () => _timer.TogglePause(), 30, 30, 15, UI.AccentSoft));
        tButtons.Children.Add(new Border { Width = 4 });
        tButtons.Children.Add(UI.Button(UI.Icon(UI.GStop, 12), () => _timer.StopTimer(), 30, 30, 15));
        _timerRunning.Children.Add(tButtons);
        tCard.Children.Add(_timerRunning);
        AddCard(tCard, new Thickness(0, 0, 4, 0));

        // Cronómetro
        var sCard = new StackPanel();
        sCard.Children.Add(UI.Caption(UI.GClock, "Cronómetro"));
        _swText = UI.CenterLabel("0:00.0", 26, UI.White, FontWeights.Light);
        _swText.FontFamily = UI.DisplayFont;
        _swText.Margin = new Thickness(0, 20, 0, 0);
        sCard.Children.Add(_swText);
        var sButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        _swGlyph = UI.Icon(UI.GPlay, 14, UI.Accent);
        sButtons.Children.Add(UI.Button(_swGlyph, () => _sw.Toggle(), 44, 36, 18, UI.AccentSoft));
        sButtons.Children.Add(new Border { Width = 6 });
        sButtons.Children.Add(UI.Button(UI.Icon(UI.GReset, 13), () => _sw.Reset(), 44, 36, 18));
        sCard.Children.Add(sButtons);
        AddCard(sCard, new Thickness(4, 0, 4, 0));

        // Alarma
        var aCard = new StackPanel();
        var header = new Grid();
        header.Children.Add(UI.Caption(UI.GAlarm, "Alarma"));
        _alSwitch = new ToggleSwitch(alarm.Enabled) { HorizontalAlignment = HorizontalAlignment.Right };
        _alSwitch.Toggled += on => _alarm.SetEnabled(on);
        header.Children.Add(_alSwitch);
        aCard.Children.Add(header);

        var picker = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        picker.Children.Add(Spinner(out _alHour, () => _alarm.SetTime(_alarm.Hour + 1, _alarm.Minute), () => _alarm.SetTime(_alarm.Hour - 1, _alarm.Minute)));
        var colon = UI.CenterLabel(":", 26, UI.Dim, FontWeights.Light);
        colon.Margin = new Thickness(1, 0, 1, 4);
        picker.Children.Add(colon);
        picker.Children.Add(Spinner(out _alMinute, () => _alarm.SetTime(_alarm.Hour, _alarm.Minute + 5), () => _alarm.SetTime(_alarm.Hour, _alarm.Minute - 5),
            () => _alarm.SetTime(_alarm.Hour, _alarm.Minute + 1), () => _alarm.SetTime(_alarm.Hour, _alarm.Minute - 1)));
        aCard.Children.Add(picker);
        AddCard(aCard, new Thickness(4, 0, 4, 0));

        // Concentración
        var fCard = new StackPanel();
        fCard.Children.Add(UI.Caption(UI.GTimer, "Concentración"));
        var fTitle = UI.CenterLabel("Pomodoro", 18, UI.White, FontWeights.SemiBold);
        fTitle.Margin = new Thickness(0, 18, 0, 0);
        fCard.Children.Add(fTitle);
        _focusInfo = UI.CenterLabel("", 11, UI.Dim);
        fCard.Children.Add(_focusInfo);
        _focusButton = UI.Button(UI.CenterLabel("Empezar", 12, UI.Black, FontWeights.SemiBold), () =>
        {
            if (_focus.IsRunning) _focus.StopFocus(); else _focus.StartFocus();
            Refresh();
        }, 92, 34, 17, UI.Accent);
        _focusButton.Margin = new Thickness(0, 14, 0, 0);
        fCard.Children.Add(_focusButton);
        AddCard(fCard, new Thickness(4, 0, 0, 0));

        _timer.Changed += Refresh;
        _timer.StateChanged += _ => Refresh();
        _sw.Changed += Refresh;
        _alarm.Changed += Refresh;
        _focus.StateChanged += _ => Refresh();
        Refresh();
    }

    void AddCard(UIElement content, Thickness margin)
    {
        var card = UI.Card(content);
        card.Padding = new Thickness(8, 10, 8, 10);
        card.Margin = margin;
        _root.Children.Add(card);
    }

    static StackPanel Spinner(out TextBlock value, Action up, Action down, Action? wheelUp = null, Action? wheelDown = null)
    {
        var sp = new StackPanel { Width = 40, Background = Brushes.Transparent };
        sp.Children.Add(UI.Button(UI.Icon(UI.GUp, 10, UI.Dim), up, 40, 22, 8, Brushes.Transparent));
        value = UI.CenterLabel("00", 26, UI.White, FontWeights.Light);
        value.FontFamily = UI.DisplayFont;
        sp.Children.Add(value);
        sp.Children.Add(UI.Button(UI.Icon(UI.GDown, 10, UI.Dim), down, 40, 22, 8, Brushes.Transparent));
        sp.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (e.Delta > 0) (wheelUp ?? up)(); else (wheelDown ?? down)();
        };
        return sp;
    }

    void Refresh()
    {
        bool running = _timer.IsRunning;
        _timerPresets.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _timerRunning.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _timerText.Text = _timer.RemainingText;
        _timerText.Opacity = _timer.IsPaused ? 0.55 : 1;
        _timerPauseGlyph.Text = _timer.IsPaused ? UI.GPlay : UI.GPause;

        _swText.Text = _sw.ElapsedText;
        _swText.Foreground = _sw.IsRunning ? UI.Accent : UI.White;
        _swGlyph.Text = _sw.IsRunning ? UI.GPause : UI.GPlay;

        _alHour.Text = _alarm.Hour.ToString("00");
        _alMinute.Text = _alarm.Minute.ToString("00");
        _alSwitch.IsOn = _alarm.Enabled;
        _alHour.Foreground = _alMinute.Foreground = _alarm.Enabled ? UI.Accent : UI.White;

        var s = Settings.Current;
        _focusInfo.Text = _focus.IsRunning ? "En curso" : $"{s.FocusMinutes} min + {s.BreakMinutes} min";
        if (_focusButton.Child is TextBlock ft) ft.Text = _focus.IsRunning ? "Detener" : "Empezar";
    }
}

// ---------------------------------------------------------------------
// Archivos
// ---------------------------------------------------------------------
public sealed class FilesPage
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly StackPanel _list = new();
    readonly Border _empty;
    readonly ScrollViewer _scroll;
    readonly TextBlock _count;
    readonly FrameworkElement _clearBtn, _dragAllBtn, _copyBtn, _phoneBtn, _hint;
    readonly Border _qr;
    readonly Image _qrImage = new() { Width = 150, Height = 150 };
    readonly TextBlock _qrUrl;
    readonly FileShelf _shelf;
    readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public FilesPage(FileShelf shelf)
    {
        _shelf = shelf;
        _root = new Grid { Margin = new Thickness(16, 0, 16, 10) };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(2, 0, 0, 6) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(UI.Caption(UI.GFolder, "Archivos"));
        _count = UI.Label("", 12, UI.Accent, FontWeights.SemiBold);
        _count.Margin = new Thickness(8, 0, 0, 0);
        title.Children.Add(_count);
        header.Children.Add(title);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var phone = UI.IconTextButton("\uE8EA", "Celular", () => ShareToPhone(SelectionOrAll()), 92, 26);
        phone.ToolTip = "Envía los seleccionados (o todos) al celular con un código QR";
        actions.Children.Add(phone);
        actions.Children.Add(new Border { Width = 6 });
        _phoneBtn = phone;
        var copy = UI.IconTextButton(UI.GClipboard, "Copiar", CopySelection, 84, 26);
        copy.ToolTip = "Copia los seleccionados (o todos) para pegarlos con Ctrl+V en cualquier carpeta o app";
        actions.Children.Add(copy);
        actions.Children.Add(new Border { Width = 6 });
        var dragAll = UI.IconTextButton(UI.GCopy, "Arrastrar", () => { }, 100, 26, UI.AccentSoft, UI.Accent);
        dragAll.ToolTip = "Mantén presionado y arrastra para soltar todos (o los seleccionados) en otra app";
        dragAll.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && _shelf.Files.Count > 0)
                StartDrag(dragAll, SelectionOrAll());
        };
        _dragAllBtn = dragAll;
        _copyBtn = copy;
        actions.Children.Add(dragAll);
        actions.Children.Add(new Border { Width = 6 });
        var clear = UI.Button(UI.Icon(UI.GDelete, 12, UI.Dim), () => { _selected.Clear(); _shelf.Clear(); }, 28, 26, 13);
        clear.ToolTip = "Vaciar la bandeja";
        _clearBtn = clear;
        actions.Children.Add(clear);
        header.Children.Add(actions);
        _root.Children.Add(header);

        var emptyContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        emptyContent.Children.Add(UI.Icon(UI.GDownload, 22, UI.Accent));
        var t1 = UI.CenterLabel("Arrastra uno o varios archivos a la isla", 13, UI.White, FontWeights.SemiBold);
        t1.Margin = new Thickness(0, 6, 0, 0);
        emptyContent.Children.Add(t1);
        emptyContent.Children.Add(UI.CenterLabel("Quedan guardados aquí, incluso al reiniciar, para soltarlos en otra app o carpeta", 11, UI.Dim));
        _empty = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderBrush = UI.AccentSoft,
            BorderThickness = new Thickness(1.5),
            Background = UI.AccentFaint,
            Child = emptyContent,
        };
        Grid.SetRow(_empty, 1);
        _root.Children.Add(_empty);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list };
        Grid.SetRow(_scroll, 1);
        _root.Children.Add(_scroll);

        var hint = UI.Label(Native.IsElevated()
            ? "Modo administrador: usa Copiar y pega con Ctrl+V (Windows bloquea arrastrar hacia apps sin permisos)"
            : "Clic: seleccionar · Doble clic: abrir · Arrastrar: soltar en otra app · Clic derecho: quitar", 10, UI.Dim);
        hint.Margin = new Thickness(2, 6, 0, 0);
        _hint = hint;
        Grid.SetRow(hint, 2);
        _root.Children.Add(hint);

        // Panel del código QR (se superpone a la lista)
        var qrGrid = new Grid();
        qrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        qrGrid.ColumnDefinitions.Add(new ColumnDefinition());
        qrGrid.Children.Add(new Border { Background = UI.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Child = _qrImage, VerticalAlignment = VerticalAlignment.Center });
        var qrText = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        qrText.Children.Add(UI.Label("Escanea con la cámara del celular", 15, UI.White, FontWeights.SemiBold));
        foreach (var line in new[] { "El celular debe estar en la misma red Wi-Fi que este PC.", "Si Windows pregunta, permite el acceso en redes privadas.", "El enlace se desactiva solo en 15 minutos." })
        {
            var t = UI.Label(line, 11, UI.Dim);
            t.TextTrimming = TextTrimming.None;
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 4, 0, 0);
            qrText.Children.Add(t);
        }
        _qrUrl = UI.Label("", 11, UI.Accent);
        _qrUrl.Margin = new Thickness(0, 8, 0, 10);
        qrText.Children.Add(_qrUrl);
        var stop = UI.IconTextButton(UI.GClose, "Dejar de compartir", () => { PhoneShare.Instance.Stop(); _qr!.Visibility = Visibility.Collapsed; }, 170, 32);
        stop.HorizontalAlignment = HorizontalAlignment.Left;
        qrText.Children.Add(stop);
        Grid.SetColumn(qrText, 1);
        qrGrid.Children.Add(qrText);
        _qr = new Border
        {
            Background = UI.Hex("#F70B0D10"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Child = qrGrid,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRowSpan(_qr, 3);
        _root.Children.Add(_qr);

        _shelf.Files.CollectionChanged += (_, _) => Rebuild();
        _root.IsVisibleChanged += (_, _) => { if (_root.IsVisible) _shelf.Prune(); };
        Rebuild();
    }

    void Rebuild()
    {
        _selected.RemoveWhere(p => !_shelf.Files.Contains(p));
        _list.Children.Clear();
        foreach (var path in _shelf.Files) _list.Children.Add(Row(path));
        int n = _shelf.Files.Count;
        bool empty = n == 0;
        _count.Text = empty ? "" : (n == 1 ? "1 archivo" : $"{n} archivos");
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _clearBtn.Visibility = _dragAllBtn.Visibility = _copyBtn.Visibility = _phoneBtn.Visibility = _hint.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    FrameworkElement Row(string path)
    {
        bool dir = Directory.Exists(path);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = ShellIcon(path, dir);
        if (icon != null) grid.Children.Add(new Image { Source = icon, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
        else
        {
            var g = UI.Icon(dir ? UI.GFolder : UI.GFile, 15, UI.Accent);
            g.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(g);
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(UI.Label(Path.GetFileName(path.TrimEnd('\\')), 12, UI.White, FontWeights.SemiBold));
        texts.Children.Add(UI.Label(Describe(path, dir), 10, UI.Dim));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var rowActions = new StackPanel { Orientation = Orientation.Horizontal };
        if (!dir)
        {
            var send = UI.Button(UI.Icon("\uE8EA", 11, UI.Dim), () => ShareToPhone(new[] { path }), 24, 24, 12, Brushes.Transparent);
            send.ToolTip = "Enviar al celular";
            rowActions.Children.Add(send);
        }
        var remove = UI.Button(UI.Icon(UI.GClose, 10, UI.Dim), () => _shelf.Remove(path), 24, 24, 12, Brushes.Transparent);
        remove.ToolTip = "Quitar de la bandeja";
        rowActions.Children.Add(remove);
        Grid.SetColumn(rowActions, 2);
        grid.Children.Add(rowActions);

        bool selected = _selected.Contains(path);
        var row = new Border
        {
            Height = 38,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(10, 0, 6, 0),
            CornerRadius = new CornerRadius(10),
            Background = selected ? UI.AccentSoft : UI.ButtonBg,
            BorderBrush = UI.Accent,
            BorderThickness = new Thickness(selected ? 1 : 0),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = path,
        };
        row.MouseEnter += (_, _) => { if (!_selected.Contains(path)) row.Background = UI.ButtonHover; };
        row.MouseLeave += (_, _) => { if (!_selected.Contains(path)) row.Background = UI.ButtonBg; };

        Point? down = null;
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                down = null;
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("Open file", ex); }
                return;
            }
            down = e.GetPosition(row);
        };
        row.MouseMove += (_, e) =>
        {
            if (down == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(row);
            if (Math.Abs(p.X - down.Value.X) < 5 && Math.Abs(p.Y - down.Value.Y) < 5) return;
            down = null;
            // Si la fila está seleccionada se arrastran todos los seleccionados.
            var items = _selected.Contains(path) ? _shelf.Files.Where(_selected.Contains).ToArray() : new[] { path };
            StartDrag(row, items);
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (down == null) return;
            down = null;
            if (!_selected.Remove(path)) _selected.Add(path);
            bool on = _selected.Contains(path);
            row.Background = on ? UI.AccentSoft : UI.ButtonHover;
            row.BorderThickness = new Thickness(on ? 1 : 0);
        };
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; _shelf.Remove(path); };
        return row;
    }

    void ShareToPhone(string[] files)
    {
        var url = PhoneShare.Instance.Start(files.Where(File.Exists).ToArray());
        if (url == null)
        {
            _count.Text = "Sin red local para compartir";
            return;
        }
        _qrImage.Source = PhoneShare.QrImage(url);
        _qrUrl.Text = url;
        _qr.Visibility = Visibility.Visible;
    }

    string[] SelectionOrAll()
    {
        var sel = _shelf.Files.Where(_selected.Contains).ToArray();
        return sel.Length > 0 ? sel : _shelf.Files.ToArray();
    }

    /// <summary>Copia los archivos al portapapeles (funciona también en modo administrador).</summary>
    void CopySelection()
    {
        var files = SelectionOrAll();
        if (files.Length == 0) return;
        try
        {
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(files);
            Clipboard.SetFileDropList(list);
            _count.Text = files.Length == 1 ? "Copiado: pega con Ctrl+V" : $"{files.Length} copiados: pega con Ctrl+V";
        }
        catch (Exception ex) { Log.Error("Copy files", ex); }
    }

    void StartDrag(DependencyObject source, string[] files)
    {
        if (files.Length == 0) return;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, files);
            IslandHost.DraggingOut = true;
            try { DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            finally { IslandHost.DraggingOut = false; }
            _shelf.Prune(); // si se movieron a otra carpeta, ya no están aquí
        }
        catch (Exception ex) { Log.Error("Drag files", ex); }
    }

    static string Describe(string path, bool dir)
    {
        try
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(path.TrimEnd('\\')) ?? "") ?? "";
            if (dir) return "Carpeta" + (folder.Length > 0 ? " · en " + folder : "");
            var info = new FileInfo(path);
            return UI.Bytes(info.Length) + (folder.Length > 0 ? " · en " + folder : "");
        }
        catch { return ""; }
    }

    /// <summary>Icono real del archivo según Windows (en caché por extensión).</summary>
    static ImageSource? ShellIcon(string path, bool dir)
    {
        if (dir) return null;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string key = ext is ".exe" or ".lnk" or ".ico" or "" ? path : ext;
        if (IconCache.TryGetValue(key, out var cached)) return cached;
        ImageSource? result = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                result = bmp;
            }
        }
        catch { }
        IconCache[key] = result;
        return result;
    }
}

// ---------------------------------------------------------------------
// Portapapeles
// ---------------------------------------------------------------------
public sealed class ClipboardPage
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly StackPanel _list = new();
    readonly FrameworkElement _empty;
    readonly ClipboardHistory _history;

    public ClipboardPage(ClipboardHistory history)
    {
        _history = history;
        _root = new Grid { Margin = new Thickness(16, 2, 16, 14) };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());

        var header = new Grid { Margin = new Thickness(2, 0, 0, 8) };
        header.Children.Add(UI.Caption(UI.GClipboard, "Copiado recientemente"));
        var clear = UI.Button(UI.CenterLabel("Borrar", 11, UI.Dim, FontWeights.SemiBold), () => _history.Clear(), 60, 24, 12);
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(clear);
        _root.Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list };
        Grid.SetRow(scroll, 1);
        _root.Children.Add(scroll);

        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        empty.Children.Add(UI.Icon(UI.GClipboard, 22, UI.Accent));
        var e1 = UI.CenterLabel("Copia algo con Ctrl + C y aparecerá aquí", 12, UI.Dim);
        e1.Margin = new Thickness(0, 6, 0, 0);
        empty.Children.Add(e1);
        _empty = empty;
        Grid.SetRow(_empty, 1);
        _root.Children.Add(_empty);

        _history.Items.CollectionChanged += (_, _) => Rebuild();
        Rebuild();
    }

    void Rebuild()
    {
        _list.Children.Clear();
        foreach (var text in _history.Items) _list.Children.Add(Row(text));
        _empty.Visibility = _history.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement Row(string text)
    {
        string oneLine = text.Replace("\r", "").Replace('\n', ' ').Replace('\t', ' ').Trim();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = UI.Label(oneLine, 12, UI.White);
        grid.Children.Add(label);
        var icon = UI.Icon(UI.GCopy, 12, UI.Dim);
        icon.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        var row = new Border
        {
            Height = 30,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(9),
            Background = UI.ButtonBg,
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = text.Length > 400 ? text[..400] + "..." : text,
        };
        row.MouseEnter += (_, _) => { row.Background = UI.ButtonHover; icon.Foreground = UI.Accent; };
        row.MouseLeave += (_, _) => { row.Background = UI.ButtonBg; icon.Foreground = UI.Dim; };
        row.MouseLeftButtonDown += (_, e) => e.Handled = true;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _history.Copy(text);
            row.Background = UI.AccentSoft;
            label.Text = "Copiado";
            label.Foreground = UI.Accent;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.9) };
            t.Tick += (_, _) => { t.Stop(); Rebuild(); };
            t.Start();
        };
        return row;
    }
}

// ---------------------------------------------------------------------
// Sistema: rendimiento completo
// ---------------------------------------------------------------------
public sealed class SystemPage
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly PerfCards _cards = new();
    readonly StoragePanel _storage = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _acquired, _showStorage;

    public SystemPage()
    {
        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());

        var switcher = UI.Segmented(new[] { (false, "Rendimiento"), (true, "Almacenamiento") }, false, storage =>
        {
            _showStorage = storage;
            Apply();
        });
        switcher.Margin = new Thickness(18, 0, 18, 2);
        _root.Children.Add(switcher);

        Grid.SetRow(_cards.View, 1);
        _root.Children.Add(_cards.View);
        Grid.SetRow(_storage.View, 1);
        _root.Children.Add(_storage.View);

        _tick.Tick += (_, _) => _cards.Update(HardwareMonitor.Instance.Latest);
        _root.IsVisibleChanged += (_, _) => Apply();
        Apply();
    }

    void Apply()
    {
        bool visible = _root.IsVisible;
        _cards.View.Visibility = _showStorage ? Visibility.Collapsed : Visibility.Visible;
        _storage.View.Visibility = _showStorage ? Visibility.Visible : Visibility.Collapsed;

        bool perf = visible && !_showStorage;
        if (perf)
        {
            if (!_acquired) { HardwareMonitor.Instance.Acquire(); _acquired = true; }
            _cards.Update(HardwareMonitor.Instance.Latest);
            _tick.Start();
        }
        else
        {
            _tick.Stop();
            if (_acquired) { HardwareMonitor.Instance.Release(); _acquired = false; }
        }
        if (visible && _showStorage) _storage.Refresh();
    }
}

/// <summary>Unidades de almacenamiento, como en "Este equipo" del Explorador de archivos.</summary>
public sealed class StoragePanel
{
    public FrameworkElement View => _scroll;
    readonly ScrollViewer _scroll;
    readonly StackPanel _list = new() { Margin = new Thickness(18, 4, 18, 8) };

    public StoragePanel()
    {
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list };
    }

    public void Refresh()
    {
        _list.Children.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.TotalSize <= 0) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                _list.Children.Add(Row(d));
            }
            catch { }
        }
        if (_list.Children.Count == 0) _list.Children.Add(UI.Label("No se encontraron unidades", 12, UI.Dim));
    }

    static FrameworkElement Row(DriveInfo d)
    {
        string letter = d.Name.TrimEnd('\\');
        string label = d.VolumeLabel.Length > 0 ? d.VolumeLabel : d.DriveType switch
        {
            DriveType.Removable => "Unidad USB",
            DriveType.Network => "Unidad de red",
            _ => "Disco local",
        };
        double used = 1 - (double)d.AvailableFreeSpace / d.TotalSize;

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = UI.Icon(d.DriveType == DriveType.Removable ? "\uE88E" : d.DriveType == DriveType.Network ? "\uE8CE" : UI.GDisk, 20, UI.Accent);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(icon);

        var info = new StackPanel();
        var top = new Grid();
        top.Children.Add(UI.Label($"{label} ({letter})", 13, UI.White, FontWeights.SemiBold));
        var pct = UI.Label($"{used * 100:0} % usado", 11, UI.Dim);
        pct.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(pct);
        info.Children.Add(top);
        var bar = new ThinBar(6, used > 0.9 ? UI.Red : UI.Accent, UI.Faint) { Margin = new Thickness(0, 4, 0, 3) };
        bar.SetValue01(used, animate: false);
        info.Children.Add(bar);
        info.Children.Add(UI.Label($"{UI.Bytes(d.AvailableFreeSpace)} disponibles de {UI.Bytes(d.TotalSize)}", 11, UI.Dim));
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        return grid;
    }
}
