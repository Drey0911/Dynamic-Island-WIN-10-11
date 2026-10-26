// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Modules;

/// <summary>Aviso que se oculta solo pasados unos segundos, salvo mientras el panel está abierto.</summary>
public abstract class TimedModule : IslandModule
{
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _until;

    protected TimedModule()
    {
        _hide.Tick += (_, _) =>
        {
            if (IslandHost.IsExpanded) { _until = DateTime.Now.AddSeconds(4); return; }
            if (DateTime.Now < _until) return;
            _hide.Stop();
            SetActive(false);
        };
    }

    protected void Show(double seconds)
    {
        _until = DateTime.Now.AddSeconds(seconds);
        _hide.Start();
        SetActive(true);
    }

    protected void Hide()
    {
        _hide.Stop();
        SetActive(false);
    }
}

// ---------------------------------------------------------------------
// Captura de pantalla / imagen copiada
// ---------------------------------------------------------------------
public sealed class ScreenshotModule : TimedModule
{
    public override string Name => "Captura";
    public override string Glyph => "\uE722";
    public override int Priority => 98;
    public override Size CompactSize => new(210, 28);

    readonly Grid _compact, _expanded;
    readonly Border _thumb, _preview, _bubbleThumb;
    readonly Grid _bubble = new();
    readonly TextBlock _status, _text;
    readonly ScrollViewer _textScroll;
    BitmapSource? _image;
    string? _ocr;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement ExpandedView => _expanded;
    public override FrameworkElement BubbleView => _bubble;

    public ScreenshotModule(ClipboardHistory clipboard)
    {
        _compact = new Grid { Margin = new Thickness(8, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _thumb = new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        _compact.Children.Add(_thumb);
        var label = UI.Label("Captura copiada", 12, UI.White, FontWeights.SemiBold);
        label.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(label, 1);
        _compact.Children.Add(label);

        _bubbleThumb = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11) };
        _bubble.Children.Add(_bubbleThumb);

        _expanded = new Grid { Margin = new Thickness(22, 8, 22, 14) };
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        _expanded.ColumnDefinitions.Add(new ColumnDefinition());
        _preview = new Border { CornerRadius = new CornerRadius(12), Background = UI.CardBg };
        _expanded.Children.Add(_preview);

        var right = new Grid { Margin = new Thickness(16, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = UI.Caption("\uE722", "Captura de pantalla");
        right.Children.Add(head);
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = UI.White, FontSize = 12, FontFamily = UI.TextFont };
        _textScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _text, Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
        Grid.SetRow(_textScroll, 1);
        right.Children.Add(_textScroll);
        _status = UI.Label("Elige una acción", 11, UI.Dim);
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(_status, 1);
        right.Children.Add(_status);

        var actions = new WrapPanel();
        foreach (var (glyph, text, action) in new (string, string, Action)[]
        {
            ("\uE8C8", "Copiar texto", () => _ = ExtractAsync(copy: true)),
            ("\uE774", "Traducir", () => _ = TranslateAsync()),
            ("\uE74E", "Guardar", Save),
            (UI.GClose, "Cerrar", Hide),
        })
        {
            var b = UI.IconTextButton(glyph, text, action, text.Length > 8 ? 124 : 96, 32);
            b.Margin = new Thickness(0, 0, 6, 6);
            actions.Children.Add(b);
        }
        Grid.SetRow(actions, 2);
        right.Children.Add(actions);
        Grid.SetColumn(right, 1);
        _expanded.Children.Add(right);

        clipboard.ImageCopied += OnImage;
    }

    void OnImage(BitmapSource image)
    {
        if (!Settings.Current.ScreenshotTools) return;
        _image = OcrService.Opaque(image);
        _ocr = null;
        var brush = new ImageBrush(_image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        _thumb.Background = brush;
        _bubbleThumb.Background = brush;
        _preview.Background = new ImageBrush(_image) { Stretch = Stretch.Uniform };
        _text.Text = "";
        _textScroll.Visibility = Visibility.Collapsed;
        _status.Text = $"{_image.PixelWidth} × {_image.PixelHeight} px · elige una acción";
        _status.Visibility = Visibility.Visible;
        Show(10);
    }

    async System.Threading.Tasks.Task<string?> ExtractAsync(bool copy)
    {
        if (_image == null) return null;
        if (_ocr == null)
        {
            _status.Text = "Leyendo texto...";
            try { _ocr = await OcrService.RecognizeAsync(_image); }
            catch (Exception ex) { Log.Error("OCR", ex); }
            if (_ocr == null)
            {
                _status.Text = "No hay un idioma de reconocimiento de texto instalado en Windows";
                return null;
            }
        }
        if (_ocr.Trim().Length == 0)
        {
            _status.Text = "No se encontró texto en la imagen";
            return null;
        }
        _text.Text = _ocr;
        _textScroll.Visibility = Visibility.Visible;
        _status.Visibility = Visibility.Collapsed;
        if (copy) ClipboardHistory.CopySilently(_ocr);
        return _ocr;
    }

    async System.Threading.Tasks.Task TranslateAsync()
    {
        var text = await ExtractAsync(copy: false);
        if (text == null) return;
        Hide();
        IslandHost.OpenTranslate?.Invoke(text);
    }

    void Save()
    {
        if (_image == null) return;
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Capturas Dynamic Island");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"Captura {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            File.WriteAllBytes(file, OcrService.ToPng(_image));
            _status.Visibility = Visibility.Visible;
            _textScroll.Visibility = Visibility.Collapsed;
            _status.Text = "Guardada en Imágenes > Capturas Dynamic Island";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = "No se pudo guardar";
            Log.Error("Save capture", ex);
        }
    }
}

// ---------------------------------------------------------------------
// Portapapeles inteligente
// ---------------------------------------------------------------------
public sealed class SmartClipboardModule : TimedModule
{
    public override string Name => "Portapapeles";
    public override string Glyph => UI.GClipboard;
    public override int Priority => 97;
    public override Size CompactSize => new(260, 28);

    readonly Grid _compact, _expanded;
    readonly Border _swatch, _bigSwatch;
    readonly TextBlock _cIcon, _cText, _cValue, _eTitle, _eValue;
    readonly StackPanel _eActions;
    SmartResult? _current;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement ExpandedView => _expanded;

    public SmartClipboardModule(ClipboardHistory clipboard)
    {
        _compact = new Grid { Margin = new Thickness(12, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderBrush = UI.Faint, BorderThickness = new Thickness(1) };
        _cIcon = UI.Icon(UI.GClipboard, 13, UI.Accent);
        iconHost.Children.Add(_swatch);
        iconHost.Children.Add(_cIcon);
        _compact.Children.Add(iconHost);
        _cText = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _cText.Margin = new Thickness(9, 0, 8, 0);
        Grid.SetColumn(_cText, 1);
        _compact.Children.Add(_cText);
        _cValue = UI.Label("", 12, UI.Accent, FontWeights.SemiBold);
        _cValue.MaxWidth = 120;
        Grid.SetColumn(_cValue, 2);
        _compact.Children.Add(_cValue);

        _expanded = new Grid { Margin = new Thickness(26, 12, 24, 16) };
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _expanded.ColumnDefinitions.Add(new ColumnDefinition());
        _bigSwatch = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        _expanded.Children.Add(_bigSwatch);
        var body = new StackPanel();
        body.Children.Add(UI.Caption(UI.GClipboard, "Portapapeles inteligente"));
        _eTitle = UI.Label("", 13, UI.Dim);
        _eTitle.Margin = new Thickness(0, 8, 0, 0);
        _eValue = UI.Label("", 26, UI.White, FontWeights.SemiBold, display: true);
        body.Children.Add(_eTitle);
        body.Children.Add(_eValue);
        _eActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(_eActions);
        Grid.SetColumn(body, 1);
        _expanded.Children.Add(body);

        clipboard.TextCopied += OnText;
    }

    void OnText(string text)
    {
        if (!Settings.Current.SmartClipboard) return;
        var r = SmartText.Analyze(text);
        if (r.Kind == SmartKind.None) return;
        _current = r;
        _eActions.Children.Clear();
        _swatch.Visibility = Visibility.Collapsed;
        _cIcon.Visibility = Visibility.Visible;
        _bigSwatch.Visibility = Visibility.Collapsed;

        switch (r.Kind)
        {
            case SmartKind.Color:
                var brush = new SolidColorBrush(r.Color!.Value);
                brush.Freeze();
                _swatch.Background = _bigSwatch.Background = brush;
                _swatch.Visibility = _bigSwatch.Visibility = Visibility.Visible;
                _cIcon.Visibility = Visibility.Collapsed;
                _cText.Text = "Color";
                _cValue.Text = r.Value;
                _eTitle.Text = SmartText.ToRgb(r.Color.Value) + " · " + SmartText.ToHsl(r.Color.Value);
                _eValue.Text = r.Value;
                AddAction("HEX", () => ClipboardHistory.CopySilently(SmartText.ToHex(r.Color.Value)));
                AddAction("RGB", () => ClipboardHistory.CopySilently(SmartText.ToRgb(r.Color.Value)));
                AddAction("HSL", () => ClipboardHistory.CopySilently(SmartText.ToHsl(r.Color.Value)));
                break;

            case SmartKind.Math:
                _cIcon.Text = "\uE8EF";
                _cText.Text = "Resultado";
                _cValue.Text = "= " + r.Value;
                _eTitle.Text = r.Title;
                _eValue.Text = "= " + r.Value;
                AddAction("Copiar resultado", () => ClipboardHistory.CopySilently(r.Value));
                break;

            case SmartKind.Link:
                _cIcon.Text = "\uE71B";
                _cText.Text = "Enlace";
                _cValue.Text = r.Title;
                _eTitle.Text = "Enlace copiado";
                _eValue.Text = r.Title;
                AddAction("Abrir enlace", () =>
                {
                    try { Process.Start(new ProcessStartInfo(r.Value) { UseShellExecute = true }); } catch { }
                    Hide();
                });
                break;

            case SmartKind.Language:
                _cIcon.Text = UI.GGlobe;
                _cText.Text = r.Title;
                _cValue.Text = "Traducir";
                _eTitle.Text = r.Title;
                _eValue.Text = r.Value.Length > 60 ? r.Value[..60] + "..." : r.Value;
                AddAction("Traducir", () => { Hide(); IslandHost.OpenTranslate?.Invoke(r.Value); });
                break;
        }
        AddAction("Cerrar", Hide, secondary: true);
        Show(7);
    }

    void AddAction(string text, Action onClick, bool secondary = false)
    {
        var b = UI.Button(UI.CenterLabel(text, 12, secondary ? UI.White : UI.Black, FontWeights.SemiBold), onClick,
            Math.Max(70, text.Length * 8 + 28), 32, 16, secondary ? null : UI.Accent);
        b.Margin = new Thickness(0, 0, 8, 0);
        _eActions.Children.Add(b);
    }
}
