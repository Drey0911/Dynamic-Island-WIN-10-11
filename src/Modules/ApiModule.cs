// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Panels;
using DynamicIsland.Services;

namespace DynamicIsland.Modules;

/// <summary>
/// Modo Beta DEV: las APIs marcadas como "Mostrar en la isla" aparecen en la isla cerrada
/// (título, valor y una mini gráfica). Si hay varias, se turnan.
/// </summary>
public sealed class ApiModule : IslandModule
{
    public override string Name => "En vivo";
    public override string Glyph => "\uE9D2";
    public override int Priority => 30;
    public override Size CompactSize => new(_width, 28);

    readonly Grid _compact, _bubble = new();
    readonly Border _badge;
    readonly TextBlock _letter, _title, _value;
    readonly ChartView _mini = new(mini: true);
    readonly Grid _expanded;
    readonly ApiCard _card = new();
    readonly DispatcherTimer _rotate = new();
    readonly TextBlock _bubbleLetter;
    int _index;
    double _width = 200;
    string? _currentId, _thumbUrl;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement BubbleView => _bubble;
    public override FrameworkElement ExpandedView => _expanded;

    public ApiModule()
    {
        _compact = new Grid { Margin = new Thickness(6, 0, 14, 0) };
        for (int i = 0; i < 4; i++) _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _letter = UI.CenterLabel("", 11, UI.Black, FontWeights.Bold);
        _badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Background = UI.Accent, Child = _letter, VerticalAlignment = VerticalAlignment.Center };
        _compact.Children.Add(_badge);
        _title = UI.Label("", 12, UI.Dim, FontWeights.SemiBold);
        _title.Margin = new Thickness(8, 0, 8, 0);
        _title.MaxWidth = 110;
        Grid.SetColumn(_title, 1);
        _compact.Children.Add(_title);
        _value = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _value.MaxWidth = 150;
        Grid.SetColumn(_value, 2);
        _compact.Children.Add(_value);
        _mini.Width = 46;
        _mini.Height = 16;
        _mini.Margin = new Thickness(10, 0, 0, 0);
        _mini.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_mini, 3);
        _compact.Children.Add(_mini);

        _bubbleLetter = UI.CenterLabel("", 11, UI.Black, FontWeights.Bold);
        _bubble.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = UI.Accent, Child = _bubbleLetter, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

        _expanded = new Grid { Margin = new Thickness(22, 14, 22, 12) };
        _expanded.Children.Add(_card.View);

        _rotate.Tick += (_, _) => { _index++; Show(); };
    }

    static System.Collections.Generic.List<ApiWidget> Pinned() =>
        ApiHub.Active.Where(a => a.ShowInIsland).ToList();

    public override void Start()
    {
        ApiHub.Instance.Updated += id => { if (id == _currentId || _currentId == null) Show(); };
        Settings.Changed += Show;
        Show();
    }

    public override void Stop() => _rotate.Stop();

    void Show()
    {
        var pinned = Pinned();
        if (pinned.Count == 0)
        {
            _rotate.Stop();
            _currentId = null;
            if (IsActive) SetActive(false);
            return;
        }

        _rotate.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.Current.ApiRotateSeconds, 3, 60));
        if (pinned.Count > 1 && !_rotate.IsEnabled) _rotate.Start();
        if (pinned.Count <= 1) _rotate.Stop();

        var api = pinned[_index % pinned.Count];
        _currentId = api.Id;
        var state = ApiHub.Instance.State(api.Id);
        _letter.Text = _bubbleLetter.Text = Initial(api.Title);
        _title.Text = api.Title;
        _value.Text = state.Error != null ? "Error"
            : state.Values.Count > 0 ? state.Values[0]
            : api.Chart.Kind == ApiChartKind.Text && state.Text.Length > 0 ? Shorten(state.Text, 40)
            : api.Chart.Kind == ApiChartKind.Table && state.TableRows.Count > 0 ? $"{state.TableRows.Count} filas"
            : api.Chart.Kind == ApiChartKind.Image && state.ImageUrls.Count > 0 ? "Imagen"
            : "...";
        _value.Foreground = state.Error != null ? UI.Red : UI.White;
        if (api.Fields.Count > 0 && api.Fields[0].ColorSign && state.Numbers.Count > 0 && state.Numbers[0] is double d)
            _value.Foreground = d > 0 ? UI.Green : d < 0 ? UI.Red : UI.White;

        // Con vista de imagen, la miniatura reemplaza la letra.
        string? thumb = api.Chart.Kind == ApiChartKind.Image ? state.ImageUrls.FirstOrDefault() : null;
        if (thumb != _thumbUrl)
        {
            _thumbUrl = thumb;
            _badge.Background = UI.Accent;
            _letter.Visibility = Visibility.Visible;
            if (thumb != null) _ = LoadThumb(thumb);
        }

        var chart = ApiHub.Instance.ChartValues(api);
        bool hasChart = api.Chart.Kind is ApiChartKind.Line or ApiChartKind.Bars && chart.Count > 1;
        _mini.Visibility = hasChart ? Visibility.Visible : Visibility.Collapsed;
        if (hasChart)
        {
            var color = ChartView.Change(chart) is double c && c < 0 ? UI.Red : UI.Green;
            _mini.SetData(chart.Count > 60 ? chart.Skip(chart.Count - 60).ToList() : chart, null, api.Chart.Kind == ApiChartKind.Bars, color);
        }
        _card.Show(api, state, chart);

        // Ancho según el contenido, en pasos para que la isla no tiemble.
        _compact.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double target = Math.Max(150, Math.Ceiling((_compact.DesiredSize.Width + 8) / 20) * 20);
        bool resized = Math.Abs(target - _width) >= 20 || !IsActive;
        if (resized) _width = target;
        if (!IsActive) SetActive(true);
        else if (resized) Raise();
    }

    async System.Threading.Tasks.Task LoadThumb(string url)
    {
        var img = await ApiImages.LoadAsync(url);
        if (img == null || url != _thumbUrl) return;
        _badge.Background = new System.Windows.Media.ImageBrush(img) { Stretch = System.Windows.Media.Stretch.UniformToFill };
        _letter.Visibility = Visibility.Collapsed;
    }

    static string Shorten(string s, int max)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }

    static string Initial(string title)
    {
        var c = title.Trim().FirstOrDefault(char.IsLetterOrDigit);
        return c == default ? "A" : char.ToUpper(c).ToString();
    }
}
