// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Panels;

/// <summary>
/// Editor de APIs del modo Beta DEV. Es una ventana normal (se puede escribir y pegar con comodidad):
/// URL, encabezados, intervalo, datos a mostrar, gráfica y vista previa en vivo.
/// </summary>
public sealed class ApiEditorWindow : Window
{
    static ApiEditorWindow? _open;
    static readonly HttpClient Http = CreateClient();

    readonly ApiWidget _api;
    readonly bool _isNew;
    readonly TextBox _title, _url, _seriesPath, _labelsPath, _tablePath, _tableColumns, _imagePath, _textPath;
    readonly List<ApiParam> _params;
    readonly StackPanel _paramsList = new();
    readonly StackPanel _fieldsList = new(), _leaves = new(), _historyChoices = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _testStatus, _fieldsEmpty;
    readonly ApiCard _preview = new(withButtons: false);
    readonly ApiState _previewState = new();
    readonly StackPanel _seriesBox, _historyBox, _tableBox, _imageBox, _textBox;
    readonly Border _previewBox;
    readonly WrapPanel _chartKind = new(), _chartSource = new();
    JsonDocument? _doc;
    CancellationTokenSource? _testCts;
    Border? _deleteButton;
    TextBlock? _deleteLabel;
    bool _confirmDelete;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/0.9 (Windows; modo DEV)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>Abre el editor (una sola ventana a la vez). null = API nueva.</summary>
    public static void Open(ApiWidget? api)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        IslandHost.Collapse?.Invoke();
        _open = new ApiEditorWindow(api);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    ApiEditorWindow(ApiWidget? existing)
    {
        _isNew = existing == null;
        _api = existing?.Clone() ?? new ApiWidget();

        Title = _isNew ? "Nueva API · Dynamic Island" : $"{_api.Title} · Dynamic Island";
        Width = 980;
        Height = 660;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Sin marco de Windows: WindowChrome quita la línea blanca de arriba que deja el borde redimensionable.
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        SourceInitialized += (_, _) => DarkFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Background = UI.Hex("#FF0C0D10");
        Foreground = UI.White;
        Topmost = true;
        ShowInTaskbar = true;
        FontFamily = UI.TextFont;
        if (UI.Logo != null) Icon = UI.Logo;
        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), DarkScrollBar());

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
        Content = root;

        // ---- Barra de título (se arrastra la ventana desde aquí) ----
        var titleBar = new Grid { Background = UI.Hex("#FF0C0D10") };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition());
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(UI.Icon("\uE943", 15, UI.Accent));
        var ht = UI.Label(_isNew ? "Nueva API" : "Editar API", 14, UI.White, FontWeights.SemiBold);
        ht.Margin = new Thickness(10, 0, 0, 0);
        heading.Children.Add(ht);
        var beta = new Border { Background = UI.AccentSoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(10, 0, 0, 0), Child = UI.Label("Beta DEV", 10, UI.Accent, FontWeights.SemiBold) };
        heading.Children.Add(beta);
        titleBar.Children.Add(heading);
        var close = UI.Button(UI.Icon(UI.GClose, 12, UI.Dim), Close, 46, 48, 0, Brushes.Transparent);
        Grid.SetColumn(close, 1);
        titleBar.Children.Add(close);
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        root.Children.Add(titleBar);

        // ---- Cuerpo: formulario a la izquierda, respuesta y vista previa a la derecha ----
        var body = new Grid { Margin = new Thickness(18, 12, 18, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.08, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var form = new StackPanel { Margin = new Thickness(0, 0, 10, 16) };
        body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form });

        // Plantillas
        form.Children.Add(Section("Empieza con una plantilla"));
        var templates = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var tpl in ApiTemplates.All)
        {
            var b = UI.IconTextButton(UI.GAdd, tpl.Name, () => LoadTemplate(tpl), 44 + tpl.Name.Length * 7.5, 28);
            b.ToolTip = tpl.Description;
            b.Margin = new Thickness(0, 0, 6, 6);
            templates.Children.Add(b);
        }
        form.Children.Add(templates);

        // Datos básicos
        form.Children.Add(Section("API"));
        form.Children.Add(Hint("Nombre"));
        _title = Input();
        _title.Text = _api.Title;
        form.Children.Add(_title);

        form.Children.Add(Hint("URL (GET que responda JSON)"));
        var urlRow = new Grid();
        urlRow.ColumnDefinitions.Add(new ColumnDefinition());
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _url = Input();
        _url.Text = _api.Url;
        _url.FontFamily = new FontFamily("Consolas, Segoe UI");
        _url.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = TestAsync(autoPick: _api.Fields.Count == 0); } };
        urlRow.Children.Add(_url);
        var test = UI.IconTextButton("\uE768", "Probar", () => _ = TestAsync(autoPick: _api.Fields.Count == 0), 96, 34, UI.Accent, UI.Black);
        test.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(test, 1);
        urlRow.Children.Add(test);
        form.Children.Add(urlRow);

        form.Children.Add(Hint("Claves y tokens (solo si la API los pide). Se guardan cifrados con tu usuario de Windows."));
        _params = _api.Params;
        form.Children.Add(_paramsList);
        var paramButtons = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var addParam = UI.IconTextButton(UI.GAdd, "Agregar clave", () => { _params.Add(new ApiParam()); RenderParams(focusLast: true); }, 150, 30, UI.AccentSoft, UI.Accent);
        addParam.Margin = new Thickness(0, 0, 6, 0);
        paramButtons.Children.Add(addParam);
        var addToken = UI.IconTextButton(UI.GKey, "Token (Bearer / JWT)", () => { _params.Add(new ApiParam { Name = "Authorization" }); RenderParams(focusLast: true); }, 190, 30);
        addToken.ToolTip = "Encabezado Authorization: pega solo el token y se envía como \"Bearer token\"";
        paramButtons.Children.Add(addToken);
        form.Children.Add(paramButtons);

        form.Children.Add(Hint("Actualizar cada"));
        form.Children.Add(Spaced(UI.Segmented(new[] { (30, "30 s"), (60, "1 min"), (300, "5 min"), (900, "15 min"), (3600, "1 h") },
            Nearest(_api.RefreshSeconds, new[] { 30, 60, 300, 900, 3600 }), v => _api.RefreshSeconds = v)));

        form.Children.Add(ToggleRow("Mostrar en la isla", "Se ve en la isla cerrada con su valor y una mini gráfica", _api.ShowInIsland, v => _api.ShowInIsland = v));
        form.Children.Add(ToggleRow("Activa", "Si la apagas deja de consultarse", _api.Enabled, v => _api.Enabled = v));

        // Datos a mostrar
        form.Children.Add(Section("Datos que se muestran"));
        form.Children.Add(Hint("Elígelos a la derecha con \"+ Dato\" después de probar, o escribe la ruta (por ejemplo  bitcoin.usd  o  prices[-1][1])."));
        _fieldsEmpty = UI.Label("Sin datos todavía.", 12, UI.Dim);
        _fieldsEmpty.Margin = new Thickness(0, 2, 0, 6);
        form.Children.Add(_fieldsEmpty);
        form.Children.Add(_fieldsList);
        var addField = UI.IconTextButton(UI.GAdd, "Agregar dato manual", () => { _api.Fields.Add(new ApiField { Label = "Valor" }); RenderFields(); UpdatePreview(); }, 190, 30, UI.AccentSoft, UI.Accent);
        addField.HorizontalAlignment = HorizontalAlignment.Left;
        addField.Margin = new Thickness(0, 4, 0, 0);
        form.Children.Add(addField);

        // Gráfica
        form.Children.Add(Section("Vista principal"));
        form.Children.Add(Hint("Cómo se muestra la respuesta debajo de los datos: gráfica, tabla, imagen o texto."));
        form.Children.Add(Spaced(_chartKind));
        _chartSource.Margin = new Thickness(0, 2, 0, 4);
        form.Children.Add(_chartSource);
        _seriesBox = new StackPanel();
        _seriesBox.Children.Add(Hint("Serie de la respuesta (ruta con [*])"));
        _seriesPath = Input();
        _seriesPath.FontFamily = new FontFamily("Consolas, Segoe UI");
        _seriesPath.Text = _api.Chart.SeriesPath;
        _seriesPath.TextChanged += (_, _) => { _api.Chart.SeriesPath = _seriesPath.Text.Trim(); UpdatePreview(); };
        _seriesBox.Children.Add(_seriesPath);
        _seriesBox.Children.Add(Hint("Etiquetas opcionales (por ejemplo  hourly.time[*])"));
        _labelsPath = Input();
        _labelsPath.FontFamily = new FontFamily("Consolas, Segoe UI");
        _labelsPath.Text = _api.Chart.LabelsPath;
        _labelsPath.TextChanged += (_, _) => { _api.Chart.LabelsPath = _labelsPath.Text.Trim(); UpdatePreview(); };
        _seriesBox.Children.Add(_labelsPath);
        form.Children.Add(_seriesBox);
        _historyBox = new StackPanel();
        _historyBox.Children.Add(Hint("Se guarda un punto en cada actualización (hasta 120). Dato a graficar:"));
        _historyBox.Children.Add(_historyChoices);
        form.Children.Add(_historyBox);

        _tableBox = new StackPanel();
        _tableBox.Children.Add(Hint("Lista que se muestra como tabla (por ejemplo  data.items  o  results)"));
        _tablePath = PathInput(_api.Chart.TablePath, v => _api.Chart.TablePath = v);
        _tableBox.Children.Add(_tablePath);
        _tableBox.Children.Add(Hint("Columnas separadas por coma (vacío = las primeras automáticamente). Ejemplo:  name, price, change"));
        _tableColumns = PathInput(string.Join(", ", _api.Chart.TableColumns),
            v => _api.Chart.TableColumns = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList());
        _tableBox.Children.Add(_tableColumns);
        form.Children.Add(_tableBox);

        _imageBox = new StackPanel();
        _imageBox.Children.Add(Hint("Ruta del enlace de la imagen (por ejemplo  url,  data.image  o  photos[*].url  para varias)"));
        _imagePath = PathInput(_api.Chart.ImagePath, v => _api.Chart.ImagePath = v);
        _imageBox.Children.Add(_imagePath);
        form.Children.Add(_imageBox);

        _textBox = new StackPanel();
        _textBox.Children.Add(Hint("Ruta del texto (por ejemplo  quote,  explanation  o  articles[*].title)"));
        _textPath = PathInput(_api.Chart.TextPath, v => _api.Chart.TextPath = v);
        _textBox.Children.Add(_textPath);
        form.Children.Add(_textBox);

        // ---- Derecha: respuesta y vista previa ----
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(200) });
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        right.Children.Add(Section("Respuesta de la API"));
        _testStatus = UI.Label("Pulsa Probar para ver qué datos trae la API.", 12, UI.Dim);
        _testStatus.TextWrapping = TextWrapping.Wrap;
        _testStatus.TextTrimming = TextTrimming.None;
        _testStatus.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(_testStatus, 1);
        right.Children.Add(_testStatus);
        var leavesBox = new Border
        {
            Background = UI.Hex("#FF0B0D10"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 6, 6),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _leaves },
        };
        Grid.SetRow(leavesBox, 2);
        right.Children.Add(leavesBox);
        var pv = Section("Vista previa");
        pv.Margin = new Thickness(0, 12, 0, 6);
        Grid.SetRow(pv, 3);
        right.Children.Add(pv);
        _previewBox = new Border
        {
            Background = UI.Black,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(18, 12, 18, 12),
            Child = _preview.View,
        };
        Grid.SetRow(_previewBox, 4);
        right.Children.Add(_previewBox);

        // ---- Pie: eliminar, cancelar y guardar ----
        var footer = new Grid { Background = UI.Hex("#FF111216") };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        if (!_isNew)
        {
            var del = UI.IconTextButton(UI.GDelete, "Eliminar", Delete, 120, 34, UI.RedSoft, UI.Red);
            _deleteButton = del;
            _deleteLabel = (del.Child as StackPanel)?.Children.OfType<TextBlock>().LastOrDefault();
            del.HorizontalAlignment = HorizontalAlignment.Left;
            del.Margin = new Thickness(18, 0, 0, 0);
            footer.Children.Add(del);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(UI.IconTextButton(UI.GClose, "Cancelar", Close, 110, 34));
        actions.Children.Add(new Border { Width = 8 });
        actions.Children.Add(UI.IconTextButton("\uE74E", "Guardar", Save, 120, 34, UI.Accent, UI.Black));
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save(); }
        };
        Closed += (_, _) => { _testCts?.Cancel(); _doc?.Dispose(); };

        RenderParams(focusLast: false);
        RenderFields();
        RenderChartOptions();
        UpdatePreview();
        if (!_isNew && _api.Url.Length > 0) Loaded += (_, _) => _ = TestAsync(autoPick: false);
    }

    // ------------------------------------------------------------------
    // Probar la API y explorar la respuesta
    // ------------------------------------------------------------------
    async Task TestAsync(bool autoPick)
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        _testStatus.Text = "Consultando...";
        _testStatus.Foreground = UI.Dim;
        _leaves.Children.Clear();
        try
        {
            var doc = await ApiHub.FetchAsync(Http, _url.Text, _params, cts.Token);
            if (cts.IsCancellationRequested) { doc.Dispose(); return; }
            _doc?.Dispose();
            _doc = doc;
            _api.Url = _url.Text.Trim();
            var leaves = ApiExplorer.Leaves(doc.RootElement);
            _testStatus.Text = $"Respuesta correcta: {leaves.Count} datos encontrados. Elige qué mostrar.";
            _testStatus.Foreground = UI.Green;
            if (autoPick)
            {
                var (fields, chart) = ApiExplorer.Suggest(leaves);
                _api.Fields = fields;
                _api.Chart = chart;
                if (_title.Text.Trim() is "" or "Nueva API") _title.Text = TitleFromUrl(_api.Url);
                RenderFields();
                RenderChartOptions();
            }
            RenderLeaves(leaves);
            UpdatePreview();
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            _testStatus.Text = ex switch
            {
                ApiException a => a.Message,
                TaskCanceledException => "Sin respuesta (tiempo agotado)",
                HttpRequestException => "No se pudo conectar. Revisa la URL y tu conexión.",
                _ => ex.Message,
            };
            _testStatus.Foreground = UI.Red;
        }
        catch { }
    }

    static string TitleFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (uri.IsLoopback || System.Net.IPAddress.TryParse(uri.Host, out _)) return "Mi API";
            var host = uri.Host.Replace("www.", "").Replace("api.", "");
            var name = host.Split('.')[0];
            return name.Length == 0 ? "Mi API" : char.ToUpper(name[0]) + name[1..];
        }
        catch { return "Mi API"; }
    }

    void RenderLeaves(List<ApiLeaf> leaves)
    {
        _leaves.Children.Clear();
        foreach (var leaf in leaves)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var path = UI.Label(leaf.Path, 12, leaf.IsSeries || leaf.IsTable || leaf.IsImage ? UI.Accent : UI.White);
            path.FontFamily = new FontFamily("Consolas, Segoe UI");
            texts.Children.Add(path);
            texts.Children.Add(UI.Label(leaf.Sample, 11, UI.Dim));
            row.Children.Add(texts);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (leaf.IsTable)
                buttons.Children.Add(SmallButton("Tabla", () => UseView(ApiChartKind.Table, leaf.Path)));
            else if (leaf.IsImage)
                buttons.Children.Add(SmallButton("Imagen", () => UseView(ApiChartKind.Image, leaf.Path)));
            else if (leaf.IsLongText)
            {
                buttons.Children.Add(SmallButton("Texto", () => UseView(ApiChartKind.Text, leaf.Path)));
                buttons.Children.Add(SmallButton("+ Dato", () => AddField(leaf)));
            }
            else if (leaf.IsSeries && leaf.IsNumber && ApiExplorer.IsTime(leaf))
                buttons.Children.Add(SmallButton("Etiquetas", () => { _api.Chart.LabelsPath = leaf.Path; RenderChartOptions(); UpdatePreview(); }));
            else if (leaf.IsSeries && leaf.IsNumber)
                buttons.Children.Add(SmallButton("Graficar", () => UseSeries(leaf.Path)));
            else if (leaf.IsSeries)
                buttons.Children.Add(SmallButton("Etiquetas", () => { _api.Chart.LabelsPath = leaf.Path; RenderChartOptions(); UpdatePreview(); }));
            else
                buttons.Children.Add(SmallButton("+ Dato", () => AddField(leaf)));
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            _leaves.Children.Add(row);
        }
        if (leaves.Count == 0) _leaves.Children.Add(UI.Label("La respuesta está vacía.", 12, UI.Dim));
    }

    /// <summary>Elige la vista principal desde la respuesta (tabla, imagen o texto).</summary>
    void UseView(ApiChartKind kind, string path)
    {
        _api.Chart.Kind = kind;
        switch (kind)
        {
            case ApiChartKind.Table:
                _api.Chart.TablePath = path;
                _api.Chart.TableColumns = new List<string>();
                _tablePath.Text = path;
                _tableColumns.Text = "";
                break;
            case ApiChartKind.Image:
                _api.Chart.ImagePath = path;
                _imagePath.Text = path;
                break;
            case ApiChartKind.Text:
                _api.Chart.TextPath = path;
                _textPath.Text = path;
                break;
        }
        RenderChartOptions();
        UpdatePreview();
    }

    void UseSeries(string path)
    {
        _api.Chart.SeriesPath = path;
        if (_api.Chart.Kind == ApiChartKind.None) _api.Chart.Kind = ApiChartKind.Line;
        RenderChartOptions();
        UpdatePreview();
    }

    void AddField(ApiLeaf leaf)
    {
        if (_api.Fields.Any(f => f.Path == leaf.Path)) return;
        _api.Fields.Add(new ApiField { Label = ApiExplorer.PrettyLabel(leaf.Path), Path = leaf.Path });
        RenderFields();
        RenderChartOptions();
        UpdatePreview();
    }

    // ------------------------------------------------------------------
    // Lista de datos (cada uno con etiqueta, ruta, prefijo, sufijo y formato)
    // ------------------------------------------------------------------
    void RenderFields()
    {
        _fieldsList.Children.Clear();
        _fieldsEmpty.Visibility = _api.Fields.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _api.Fields.Count; i++)
        {
            var f = _api.Fields[i];
            int index = i;
            var card = new Border { Background = UI.CardBg, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            grid.RowDefinitions.Add(new RowDefinition());

            var label = Input(30);
            label.Text = f.Label;
            label.ToolTip = "Etiqueta";
            label.TextChanged += (_, _) => { f.Label = label.Text; UpdatePreview(); };
            grid.Children.Add(label);
            var path = Input(30);
            path.Text = f.Path;
            path.ToolTip = "Ruta en el JSON";
            path.FontFamily = new FontFamily("Consolas, Segoe UI");
            path.TextChanged += (_, _) => { f.Path = path.Text.Trim(); UpdatePreview(); };
            Grid.SetColumn(path, 2);
            grid.Children.Add(path);
            var remove = UI.Button(UI.Icon(UI.GDelete, 12, UI.Dim), () => { _api.Fields.RemoveAt(index); RenderFields(); RenderChartOptions(); UpdatePreview(); }, 30, 30, 15, Brushes.Transparent);
            remove.ToolTip = "Quitar";
            remove.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);

            var options = new WrapPanel();
            var prefix = Input(28);
            prefix.Width = 60;
            prefix.Text = f.Prefix;
            prefix.ToolTip = "Antes del valor, por ejemplo $";
            prefix.TextChanged += (_, _) => { f.Prefix = prefix.Text; UpdatePreview(); };
            var suffix = Input(28);
            suffix.Width = 70;
            suffix.Text = f.Suffix;
            suffix.ToolTip = "Después del valor, por ejemplo °C o %";
            suffix.TextChanged += (_, _) => { f.Suffix = suffix.Text; UpdatePreview(); };
            options.Children.Add(Labeled("Antes", prefix));
            options.Children.Add(Labeled("Después", suffix));
            var dec = UI.Segmented(new[] { (-1, "Auto"), (0, "0"), (2, "2"), (4, "4") }, f.Decimals is -1 or 0 or 2 or 4 ? f.Decimals : -1,
                v => { f.Decimals = v; UpdatePreview(); });
            options.Children.Add(Labeled("Decimales", dec));
            options.Children.Add(CheckChip("+/- en color", f.ColorSign, v => { f.ColorSign = v; UpdatePreview(); }));
            options.Children.Add(CheckChip("Corto (K, M)", f.Compact, v => { f.Compact = v; UpdatePreview(); }));
            Grid.SetRow(options, 2);
            Grid.SetColumnSpan(options, 4);
            grid.Children.Add(options);

            card.Child = grid;
            _fieldsList.Children.Add(card);
        }
    }

    // ------------------------------------------------------------------
    // Gráfica: tipo y fuente (serie de la respuesta o historial de un dato)
    // ------------------------------------------------------------------
    void RenderChartOptions()
    {
        _chartKind.Children.Clear();
        _chartKind.Children.Add(UI.Segmented(new[]
            {
                (ApiChartKind.None, "Solo datos"), (ApiChartKind.Line, "Línea"), (ApiChartKind.Bars, "Barras"),
                (ApiChartKind.Table, "Tabla"), (ApiChartKind.Image, "Imagen"), (ApiChartKind.Text, "Texto"),
            },
            _api.Chart.Kind, v => { _api.Chart.Kind = v; RenderChartOptions(); UpdatePreview(); }));

        var kind = _api.Chart.Kind;
        _tableBox.Visibility = kind == ApiChartKind.Table ? Visibility.Visible : Visibility.Collapsed;
        _imageBox.Visibility = kind == ApiChartKind.Image ? Visibility.Visible : Visibility.Collapsed;
        _textBox.Visibility = kind == ApiChartKind.Text ? Visibility.Visible : Visibility.Collapsed;
        if (_tablePath.Text != _api.Chart.TablePath) _tablePath.Text = _api.Chart.TablePath;
        if (_imagePath.Text != _api.Chart.ImagePath) _imagePath.Text = _api.Chart.ImagePath;
        if (_textPath.Text != _api.Chart.TextPath) _textPath.Text = _api.Chart.TextPath;

        bool on = kind is ApiChartKind.Line or ApiChartKind.Bars;
        bool series = _api.Chart.SeriesPath.Length > 0;
        _chartSource.Children.Clear();
        _chartSource.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _chartSource.Children.Add(UI.Segmented(new[] { (true, "Serie de la respuesta"), (false, "Historial de un dato") }, series, v =>
        {
            if (v && _api.Chart.SeriesPath.Length == 0) _api.Chart.SeriesPath = _seriesPath.Text.Trim().Length > 0 ? _seriesPath.Text.Trim() : "datos[*]";
            if (!v) _api.Chart.SeriesPath = "";
            if (_seriesPath.Text != _api.Chart.SeriesPath) _seriesPath.Text = _api.Chart.SeriesPath;
            RenderChartOptions();
            UpdatePreview();
        }));
        if (_seriesPath.Text != _api.Chart.SeriesPath) _seriesPath.Text = _api.Chart.SeriesPath;
        if (_labelsPath.Text != _api.Chart.LabelsPath) _labelsPath.Text = _api.Chart.LabelsPath;
        _seriesBox.Visibility = on && series ? Visibility.Visible : Visibility.Collapsed;
        _historyBox.Visibility = on && !series ? Visibility.Visible : Visibility.Collapsed;

        _historyChoices.Children.Clear();
        if (_api.Fields.Count == 0)
            _historyChoices.Children.Add(UI.Label("Primero agrega un dato numérico.", 12, UI.Dim));
        else
        {
            var opts = _api.Fields.Select((f, i) => (i, f.Label.Length > 0 ? f.Label : $"Dato {i + 1}")).ToArray();
            _historyChoices.Children.Add(UI.Segmented(opts, Math.Clamp(_api.Chart.HistoryField, 0, _api.Fields.Count - 1),
                v => { _api.Chart.HistoryField = v; UpdatePreview(); }));
        }
    }

    // ------------------------------------------------------------------
    // Vista previa con la respuesta de prueba
    // ------------------------------------------------------------------
    void UpdatePreview()
    {
        var copy = _api.Clone();
        copy.Title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : "Mi API";
        if (_doc != null)
        {
            _previewState.Error = null;
            _previewState.Updated = DateTime.Now;
            ApiHub.Instance.Apply(copy, _previewState, _doc, recordHistory: false);
        }
        else
        {
            _previewState.Values.Clear();
            _previewState.Numbers.Clear();
            _previewState.Series.Clear();
            _previewState.SeriesLabels.Clear();
            _previewState.TableHeaders.Clear();
            _previewState.TableRows.Clear();
            _previewState.ImageUrls.Clear();
            _previewState.Text = "";
        }

        IReadOnlyList<double> chart = copy.Chart.Kind is not (ApiChartKind.Line or ApiChartKind.Bars) ? Array.Empty<double>()
            : copy.Chart.SeriesPath.Length > 0
            ? _previewState.Series
            : ApiHub.Instance.History(copy.Id, copy.Chart.HistoryField) is { Count: > 0 } h
                ? h
                : _previewState.Numbers.ElementAtOrDefault(copy.Chart.HistoryField) is double n ? new[] { n } : Array.Empty<double>();
        _preview.Show(copy, _previewState, chart);
    }

    // ------------------------------------------------------------------
    // Guardar y borrar (se guarda en settings.json de inmediato)
    // ------------------------------------------------------------------
    void Save()
    {
        string url = _url.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            _testStatus.Text = "Escribe una URL válida que empiece con https://";
            _testStatus.Foreground = UI.Red;
            _url.Focus();
            return;
        }
        _api.Title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : TitleFromUrl(url);
        _api.Url = url;
        _api.Params = _params.Where(p => p.Name.Trim().Length > 0).ToList();
        _api.Fields.RemoveAll(f => string.IsNullOrWhiteSpace(f.Path));
        if (_api.Chart.HistoryField >= _api.Fields.Count) _api.Chart.HistoryField = 0;

        var list = Settings.Current.Apis;
        int i = list.FindIndex(a => a.Id == _api.Id);
        if (i >= 0)
        {
            // Si cambió la fuente del historial, se empieza de cero.
            if (list[i].Url != _api.Url) ApiHub.Instance.ClearHistory(_api.Id);
            list[i] = _api;
        }
        else list.Add(_api);
        Settings.Current.DevMode = true;
        Settings.Current.Save();
        ApiHub.Instance.RefreshNow(_api);
        Close();
    }

    /// <summary>Primer clic: pide confirmar (el botón cambia unos segundos). Segundo clic: elimina.</summary>
    void Delete()
    {
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            _deleteLabel!.Text = "Confirmar";
            _deleteButton!.Background = UI.Red;
            foreach (var tb in ((StackPanel)_deleteButton.Child).Children.OfType<TextBlock>()) tb.Foreground = UI.White;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                _confirmDelete = false;
                _deleteLabel.Text = "Eliminar";
                _deleteButton.Background = UI.RedSoft;
                foreach (var tb in ((StackPanel)_deleteButton.Child).Children.OfType<TextBlock>()) tb.Foreground = UI.Red;
            };
            t.Start();
            return;
        }
        Settings.Current.Apis.RemoveAll(a => a.Id == _api.Id);
        Settings.Current.Save();
        ApiHub.Instance.ClearHistory(_api.Id);
        Close();
    }

    void LoadTemplate(ApiTemplates.Template tpl)
    {
        var t = tpl.Create();
        _title.Text = t.Title;
        _url.Text = t.Url;
        _api.RefreshSeconds = t.RefreshSeconds;
        _api.Fields = t.Fields;
        _api.Chart = t.Chart;
        RenderFields();
        RenderChartOptions();
        _ = TestAsync(autoPick: false);
    }

    // ------------------------------------------------------------------
    // Controles pequeños del editor
    // ------------------------------------------------------------------
    static TextBlock Section(string text)
    {
        var t = UI.Label(text.ToUpperInvariant(), 11, UI.Accent, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 10, 0, 6);
        return t;
    }

    static TextBlock Hint(string text)
    {
        var t = UI.Label(text, 11, UI.Dim);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(0, 6, 0, 4);
        return t;
    }

    static FrameworkElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 0, 0, 2);
        return e;
    }

    /// <summary>Caja de texto del editor (sin el truco de teclado de la isla: esta ventana sí recibe el foco).</summary>
    static TextBox Input(double height = 34, bool multiline = false)
    {
        var t = new TextBox
        {
            Height = height,
            Background = UI.ButtonBg,
            Foreground = UI.White,
            CaretBrush = UI.Accent,
            SelectionBrush = UI.Accent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, multiline ? 6 : 0, 10, 0),
            FontFamily = UI.TextFont,
            FontSize = 13,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        t.Loaded += (_, _) =>
        {
            if (t.Template?.FindName("border", t) is Border bd) bd.CornerRadius = new CornerRadius(8);
        };
        return t;
    }

    static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 4) };
        sp.Children.Add(UI.Label(label, 10, UI.Dim));
        control.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(control);
        return sp;
    }

    static FrameworkElement SmallButton(string text, Action onClick)
    {
        var b = UI.Button(UI.CenterLabel(text, 11, UI.Accent, FontWeights.SemiBold), onClick, 72, 24, 12, UI.AccentSoft);
        b.Margin = new Thickness(4, 0, 0, 0);
        return b;
    }

    static FrameworkElement CheckChip(string text, bool on, Action<bool> set)
    {
        var label = UI.Label(text, 11, on ? UI.Accent : UI.White, FontWeights.SemiBold);
        var chip = new Border
        {
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 14, 6, 4),
            CornerRadius = new CornerRadius(13),
            Background = on ? UI.AccentSoft : UI.ButtonBg,
            Child = label,
            Cursor = Cursors.Hand,
        };
        bool state = on;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            state = !state;
            chip.Background = state ? UI.AccentSoft : UI.ButtonBg;
            label.Foreground = state ? UI.Accent : UI.White;
            set(state);
        };
        return chip;
    }

    static FrameworkElement ToggleRow(string title, string description, bool value, Action<bool> set)
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel();
        texts.Children.Add(UI.Label(title, 13, UI.White, FontWeights.SemiBold));
        texts.Children.Add(UI.Label(description, 11, UI.Dim));
        row.Children.Add(texts);
        var sw = new ToggleSwitch(value) { VerticalAlignment = VerticalAlignment.Center };
        sw.Toggled += set;
        Grid.SetColumn(sw, 1);
        row.Children.Add(sw);
        return row;
    }

    /// <summary>Caja para escribir una ruta del JSON; actualiza el modelo y la vista previa al escribir.</summary>
    TextBox PathInput(string text, Action<string> set)
    {
        var t = Input();
        t.FontFamily = new FontFamily("Consolas, Segoe UI");
        t.Text = text;
        t.TextChanged += (_, _) => { set(t.Text.Trim()); UpdatePreview(); };
        return t;
    }

    // ------------------------------------------------------------------
    // Claves y tokens: nombre + valor (oculto), como encabezado o en la URL
    // ------------------------------------------------------------------
    void RenderParams(bool focusLast)
    {
        _paramsList.Children.Clear();
        Control? last = null;
        for (int i = 0; i < _params.Count; i++)
        {
            var p = _params[i];
            int index = i;
            var card = new Border { Background = UI.CardBg, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameHint = UI.Label("Nombre", 10, UI.Dim);
            grid.Children.Add(nameHint);
            var valueHint = UI.Label("Valor (token o clave)", 10, UI.Dim);
            Grid.SetColumn(valueHint, 2);
            grid.Children.Add(valueHint);

            var name = Input(32);
            name.Text = p.Name;
            name.FontFamily = new FontFamily("Consolas, Segoe UI");
            name.Margin = new Thickness(0, 3, 0, 0);
            name.TextChanged += (_, _) => p.Name = name.Text;
            Grid.SetRow(name, 1);
            grid.Children.Add(name);

            // Valor oculto con botón para verlo.
            var hidden = new PasswordBox
            {
                Height = 32,
                Password = p.Value,
                Background = UI.ButtonBg,
                Foreground = UI.White,
                CaretBrush = UI.Accent,
                SelectionBrush = UI.Accent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 0, 10, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            };
            var shown = Input(32);
            shown.Text = p.Value;
            shown.FontFamily = new FontFamily("Consolas, Segoe UI");
            shown.Margin = new Thickness(0, 3, 0, 0);
            shown.Visibility = Visibility.Collapsed;
            hidden.PasswordChanged += (_, _) => { p.Value = hidden.Password; if (shown.Text != p.Value) shown.Text = p.Value; };
            shown.TextChanged += (_, _) => { p.Value = shown.Text; if (hidden.Password != p.Value) hidden.Password = p.Value; };
            Grid.SetRow(hidden, 1);
            Grid.SetColumn(hidden, 2);
            Grid.SetRow(shown, 1);
            Grid.SetColumn(shown, 2);
            grid.Children.Add(hidden);
            grid.Children.Add(shown);

            var eyeIcon = UI.Icon("\uE7B3", 13, UI.Dim);
            var eye = UI.Button(eyeIcon, () =>
            {
                bool show = shown.Visibility != Visibility.Visible;
                shown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                hidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                eyeIcon.Foreground = show ? UI.Accent : UI.Dim;
            }, 32, 32, 16, Brushes.Transparent);
            eye.ToolTip = "Mostrar u ocultar";
            eye.Margin = new Thickness(4, 3, 0, 0);
            Grid.SetRow(eye, 1);
            Grid.SetColumn(eye, 3);
            grid.Children.Add(eye);

            var remove = UI.Button(UI.Icon(UI.GDelete, 12, UI.Dim), () => { _params.RemoveAt(index); RenderParams(focusLast: false); }, 32, 32, 16, Brushes.Transparent);
            remove.ToolTip = "Quitar";
            remove.Margin = new Thickness(0, 3, 0, 0);
            Grid.SetRow(remove, 1);
            Grid.SetColumn(remove, 4);
            grid.Children.Add(remove);

            var where = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var whereLabel = UI.Label("Enviar como", 11, UI.Dim);
            whereLabel.VerticalAlignment = VerticalAlignment.Center;
            whereLabel.Margin = new Thickness(0, 0, 8, 6);
            where.Children.Add(whereLabel);
            where.Children.Add(UI.Segmented(new[] { (false, "Encabezado"), (true, "Parámetro en la URL") }, p.InUrl, v => p.InUrl = v));
            Grid.SetRow(where, 2);
            Grid.SetColumnSpan(where, 5);
            grid.Children.Add(where);

            card.Child = grid;
            _paramsList.Children.Add(card);
            last = p.Name.Length == 0 ? name : hidden;
        }
        if (focusLast && last != null) Dispatcher.BeginInvoke(new Action(() => last.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Marco oscuro en Windows 11 (borde y modo oscuro); en Windows 10 no hace nada.</summary>
    static void DarkFrame(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));          // modo oscuro
            int border = 0x00100D0C;                                         // COLORREF (BGR) casi negro
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));      // color del borde
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Barra de desplazamiento delgada y oscura (la de Windows es clara y ancha).</summary>
    static Style DarkScrollBar()
    {
        const string xaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                                     xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>
            <Setter Property='Width' Value='8'/>
            <Setter Property='MinWidth' Value='8'/>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='Template'>
              <Setter.Value>
                <ControlTemplate TargetType='ScrollBar'>
                  <Track x:Name='PART_Track' IsDirectionReversed='True'>
                    <Track.Thumb>
                      <Thumb>
                        <Thumb.Template>
                          <ControlTemplate TargetType='Thumb'>
                            <Border CornerRadius='3' Background='#40FFFFFF' Margin='2,0,1,0'/>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    static int Nearest(int value, int[] options) => options.OrderBy(o => Math.Abs(o - value)).First();
}
