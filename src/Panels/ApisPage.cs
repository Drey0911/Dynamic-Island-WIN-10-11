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
/// Tarjeta de una API: título, estado, datos y gráfica. Se usa en la pestaña APIs,
/// en la isla abierta y como vista previa en el editor.
/// </summary>
public sealed class ApiCard
{
    public FrameworkElement View => _root;
    readonly Grid _root;
    readonly TextBlock _title, _status, _change;
    readonly WrapPanel _values = new();
    readonly ChartView _chart = new();
    readonly Border _refresh, _edit;
    ApiWidget? _api;

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

        _chart.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetRow(_chart, 2);
        _root.Children.Add(_chart);
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
        for (int i = 0; i < api.Fields.Count; i++)
        {
            var f = api.Fields[i];
            string value = i < state.Values.Count ? state.Values[i] : "--";
            double? n = i < state.Numbers.Count ? state.Numbers[i] : null;
            var block = new StackPanel { Margin = new Thickness(0, 0, 22, 2) };
            block.Children.Add(UI.Label(f.Label.Length > 0 ? f.Label : ApiExplorer.PrettyLabel(f.Path), 11, UI.Dim));
            var v = UI.Label(value, api.Fields.Count > 2 ? 16 : 20, UI.White, FontWeights.SemiBold, display: true);
            if (f.ColorSign && n is double d) v.Foreground = d > 0 ? UI.Green : d < 0 ? UI.Red : UI.White;
            v.MaxWidth = 260;
            block.Children.Add(v);
            _values.Children.Add(block);
        }

        bool hasChart = api.Chart.Kind != ApiChartKind.None;
        _chart.Visibility = hasChart ? Visibility.Visible : Visibility.Collapsed;
        _change.Text = "";
        if (hasChart)
        {
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
        }
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
        top.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _chips,
        });
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
