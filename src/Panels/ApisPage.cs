// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Panels;

/// <summary>
/// Tarjeta de una API: título, estado, datos y la vista principal (gráfica, tabla, imagen o texto).
/// Se usa en la pestaña APIs, en la isla abierta y como vista previa en el editor.
/// </summary>
public sealed class ApiCard
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly TextBlock _title, _status, _change;
    readonly WrapPanel _values = new();
    readonly ContentPresenter _view = new();
    readonly ChartView _chart = new();
    readonly Grid _table = new();
    readonly ScrollViewer _tableScroll;
    readonly Grid _images = new();
    readonly TextBlock _text;
    readonly ScrollViewer _textScroll;
    readonly Border _refresh, _edit;
    ApiWidget? _api;
    string _imageKey = "";

    /// <param name="withButtons">false = sin botones de actualizar y editar (vista previa del editor).</param>
    public ApiCard(bool withButtons = true)
    {
        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _title = UI.Label("", 15, UI.White, FontWeights.SemiBold, display: true);
        titles.Children.Add(_title);
        _change = UI.Label("", 12, UI.Green, FontWeights.SemiBold);
        _change.Margin = new Thickness(8, 2, 0, 0);
        titles.Children.Add(_change);
        _status = UI.Label("", 11, UI.Dim);
        _status.Margin = new Thickness(10, 2, 0, 0);
        titles.Children.Add(_status);
        head.Children.Add(titles);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _refresh = UI.Button(UI.Icon(UI.GReset, 12, UI.Dim), () => { if (_api != null) ApiHub.Instance.RefreshNow(_api); }, 28, 28, 14, Brushes.Transparent);
        _refresh.ToolTip = "Actualizar ahora";
        _edit = UI.Button(UI.Icon("\uE70F", 12, UI.Dim), () => { if (_api != null) ApiEditorWindow.Open(_api); }, 28, 28, 14, Brushes.Transparent);
        _edit.ToolTip = "Editar";
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_edit);
        buttons.Visibility = withButtons ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        _root.Children.Add(head);

        _values.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(_values, 1);
        _root.Children.Add(_values);

        _tableScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _table };
        _text = UI.Label("", 13, UI.White);
        _text.TextWrapping = TextWrapping.Wrap;
        _text.TextTrimming = TextTrimming.None;
        _text.LineHeight = 19;
        _textScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _text };

        _view.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetRow(_view, 2);
        _root.Children.Add(_view);
    }

    public void Show(ApiWidget api, ApiState state, IReadOnlyList<double> chart)
    {
        _api = api;
        _title.Text = api.Title;

        if (state.Error != null)
        {
            _status.Text = state.Error;
            _status.Foreground = UI.Red;
        }
        else if (state.Loading && state.Updated == null)
        {
            _status.Text = "Cargando...";
            _status.Foreground = UI.Dim;
        }
        else
        {
            _status.Text = state.Updated is DateTime t ? "Actualizado " + t.ToString("t") : "";
            _status.Foreground = UI.Dim;
        }

        _values.Children.Clear();
        bool compactValues = api.Fields.Count > 2 || api.Chart.Kind is ApiChartKind.Table or ApiChartKind.Text or ApiChartKind.Image;
        for (int i = 0; i < api.Fields.Count; i++)
        {
            var f = api.Fields[i];
            string value = i < state.Values.Count ? state.Values[i] : "--";
            double? n = i < state.Numbers.Count ? state.Numbers[i] : null;
            var block = new StackPanel { Margin = new Thickness(0, 0, 22, 2) };
            block.Children.Add(UI.Label(f.Label.Length > 0 ? f.Label : ApiExplorer.PrettyLabel(f.Path), 11, UI.Dim));
            var v = UI.Label(value, compactValues ? 15 : 20, UI.White, FontWeights.SemiBold, display: true);
            if (f.ColorSign && n is double d) v.Foreground = d > 0 ? UI.Green : d < 0 ? UI.Red : UI.White;
            v.MaxWidth = 300;
            block.Children.Add(v);
            _values.Children.Add(block);
        }
        _values.Visibility = api.Fields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _change.Text = "";
        switch (api.Chart.Kind)
        {
            case ApiChartKind.Line:
            case ApiChartKind.Bars:
                _view.Content = _chart;
                var labels = api.Chart.SeriesPath.Length > 0 ? state.SeriesLabels : null;
                _chart.SetData(chart, labels, api.Chart.Kind == ApiChartKind.Bars);
                if (ChartView.Change(chart) is double change && api.Chart.Kind == ApiChartKind.Line)
                {
                    if (Math.Abs(change) < 0.005)
                    {
                        _change.Text = "= 0 %";
                        _change.Foreground = UI.Dim;
                    }
                    else
                    {
                        _change.Text = (change > 0 ? "▲ " : "▼ ") + Math.Abs(change).ToString("0.00") + " %";
                        _change.Foreground = change > 0 ? UI.Green : UI.Red;
                    }
                }
                break;
            case ApiChartKind.Table:
                _view.Content = _tableScroll;
                BuildTable(state);
                break;
            case ApiChartKind.Image:
                _view.Content = _images;
                ShowImages(state.ImageUrls);
                break;
            case ApiChartKind.Text:
                _view.Content = _textScroll;
                _text.Text = state.Text.Length > 0 ? state.Text : (state.Updated == null ? "Esperando datos..." : "Sin texto en esa ruta.");
                _text.Foreground = state.Text.Length > 0 ? UI.White : UI.Dim;
                break;
            default:
                _view.Content = null;
                break;
        }
    }

    /// <summary>Tabla con encabezado fijo arriba y filas alternadas.</summary>
    void BuildTable(ApiState state)
    {
        _table.Children.Clear();
        _table.ColumnDefinitions.Clear();
        _table.RowDefinitions.Clear();
        if (state.TableHeaders.Count == 0)
        {
            _table.Children.Add(UI.Label(state.Updated == null ? "Esperando datos..." : "Sin filas en esa ruta.", 12, UI.Dim));
            return;
        }
        foreach (var _ in state.TableHeaders) _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        for (int c = 0; c < state.TableHeaders.Count; c++)
        {
            var h = UI.Label(state.TableHeaders[c], 11, UI.Accent, FontWeights.SemiBold);
            h.Margin = new Thickness(8, 0, 6, 0);
            h.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(h, c);
            _table.Children.Add(h);
        }
        for (int r = 0; r < state.TableRows.Count; r++)
        {
            _table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            if (r % 2 == 0)
            {
                var stripe = new Border { Background = UI.CardBg, CornerRadius = new CornerRadius(6) };
                Grid.SetRow(stripe, r + 1);
                Grid.SetColumnSpan(stripe, state.TableHeaders.Count);
                _table.Children.Add(stripe);
            }
            var row = state.TableRows[r];
            for (int c = 0; c < state.TableHeaders.Count && c < row.Count; c++)
            {
                var cell = UI.Label(row[c], 12, UI.White);
                cell.Margin = new Thickness(8, 0, 6, 0);
                cell.VerticalAlignment = VerticalAlignment.Center;
                cell.ToolTip = row[c].Length > 24 ? row[c] : null;
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                _table.Children.Add(cell);
            }
        }
    }

    /// <summary>Una imagen grande o varias en mosaico. Clic = abrirla en el navegador.</summary>
    void ShowImages(List<string> urls)
    {
        string key = string.Join("|", urls);
        if (key == _imageKey && _images.Children.Count > 0) return;
        _imageKey = key;
        _images.Children.Clear();
        _images.ColumnDefinitions.Clear();
        if (urls.Count == 0)
        {
            _images.Children.Add(UI.Label("Esperando imagen...", 12, UI.Dim));
            return;
        }
        int n = Math.Min(urls.Count, 4);
        for (int i = 0; i < n; i++) _images.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < n; i++)
        {
            string url = urls[i];
            var label = UI.CenterLabel("Cargando imagen...", 11, UI.Dim);
            var box = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = UI.CardBg,
                Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                Child = label,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = url,
            };
            box.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            };
            Grid.SetColumn(box, i);
            _images.Children.Add(box);
            _ = LoadInto(box, label, url, single: n == 1);
        }
    }

    async System.Threading.Tasks.Task LoadInto(Border box, TextBlock label, string url, bool single)
    {
        var img = await ApiImages.LoadAsync(url);
        if (img == null) { label.Text = "No se pudo cargar la imagen"; return; }
        box.Child = null;
        box.Background = new ImageBrush(img)
        {
            Stretch = single ? Stretch.Uniform : Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };
    }
}

/// <summary>Descarga y guarda en memoria las imágenes de las APIs (las últimas 40).</summary>
public static class ApiImages
{
    static readonly System.Net.Http.HttpClient Http = CreateClient();
    static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource?> Cache = new();
    static readonly Queue<string> Order = new();

    static System.Net.Http.HttpClient CreateClient()
    {
        var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/0.9 (Windows; modo DEV)");
        return c;
    }

    public static async System.Threading.Tasks.Task<System.Windows.Media.Imaging.BitmapSource?> LoadAsync(string url)
    {
        if (Cache.TryGetValue(url, out var cached)) return cached;
        System.Windows.Media.Imaging.BitmapSource? result = null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length <= 15 * 1024 * 1024)
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 640;
                bmp.StreamSource = new System.IO.MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                result = bmp;
            }
        }
        catch { }
        Cache[url] = result;
        Order.Enqueue(url);
        while (Order.Count > 40) Cache.Remove(Order.Dequeue());
        return result;
    }
}

/// <summary>Pestaña APIs (modo Beta DEV): una ficha por API y la tarjeta de la elegida.</summary>
public sealed class ApisPage
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly WrapPanel _chips = new();
    readonly ContentPresenter _body = new();
    readonly ApiCard _card = new();
    readonly StackPanel _empty;
    string? _selected;

    public ApisPage()
    {
        _root = new Grid { Margin = new Thickness(18, 2, 18, 10) };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chipsScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _chips,
        };
        // Con muchas APIs las fichas no caben: la rueda del mouse las desplaza de lado.
        chipsScroll.PreviewMouseWheel += (_, e) =>
        {
            chipsScroll.ScrollToHorizontalOffset(chipsScroll.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        };
        top.Children.Add(chipsScroll);
        _chips.Orientation = Orientation.Horizontal;
        var add = UI.IconTextButton(UI.GAdd, "API", () => ApiEditorWindow.Open(null), 70, 28, UI.AccentSoft, UI.Accent);
        add.ToolTip = "Agregar una API";
        add.Margin = new Thickness(6, 0, 0, 6);
        Grid.SetColumn(add, 1);
        top.Children.Add(add);
        _root.Children.Add(top);

        _empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var t1 = UI.CenterLabel("Todavía no hay APIs", 14, UI.White, FontWeights.SemiBold);
        var t2 = UI.CenterLabel("Pega la URL de una API gratuita o empieza con una plantilla.", 12, UI.Dim);
        t2.Margin = new Thickness(0, 4, 0, 10);
        _empty.Children.Add(t1);
        _empty.Children.Add(t2);
        var templates = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var tpl in ApiTemplates.All)
        {
            var b = UI.IconTextButton(UI.GAdd, tpl.Name, () => AddTemplate(tpl), 40 + tpl.Name.Length * 7.5, 28);
            b.Margin = new Thickness(3);
            b.ToolTip = tpl.Description;
            templates.Children.Add(b);
        }
        _empty.Children.Add(templates);

        Grid.SetRow(_body, 1);
        _root.Children.Add(_body);

        ApiHub.Instance.Updated += id => { if (id == _selected && _root.IsVisible) ShowSelected(); };
        Settings.Changed += () => { if (_root.IsVisible) Rebuild(); };
        _root.IsVisibleChanged += (_, _) => { if (_root.IsVisible) Rebuild(); };
    }

    static void AddTemplate(ApiTemplates.Template tpl)
    {
        var api = tpl.Create();
        Settings.Current.Apis.Add(api);
        Settings.Current.Save();
        ApiHub.Instance.RefreshNow(api);
    }

    public void Select(string id)
    {
        _selected = id;
        Rebuild();
    }

    void Rebuild()
    {
        var apis = Settings.Current.Apis;
        if (_selected == null || apis.All(a => a.Id != _selected)) _selected = apis.FirstOrDefault()?.Id;

        _chips.Children.Clear();
        foreach (var api in apis)
        {
            string id = api.Id;
            bool on = id == _selected;
            var text = UI.Label(api.Title, 12, on ? UI.Accent : (api.Enabled ? UI.White : UI.Dim), FontWeights.SemiBold);
            var chip = new Border
            {
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(14),
                Background = on ? UI.AccentSoft : UI.ButtonBg,
                Child = text,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; Select(id); };
            _chips.Children.Add(chip);
            if (on) chip.Loaded += (_, _) => chip.BringIntoView();
        }

        if (apis.Count == 0) { _body.Content = _empty; return; }
        _body.Content = _card.View;
        ShowSelected();
    }

    void ShowSelected()
    {
        var api = Settings.Current.Apis.FirstOrDefault(a => a.Id == _selected);
        if (api == null) return;
        var state = ApiHub.Instance.State(api.Id);
        if (state.Updated == null && state.Error == null && !state.Loading && api.Enabled) ApiHub.Instance.RefreshNow(api);
        _card.Show(api, state, ApiHub.Instance.ChartValues(api));
    }
}
