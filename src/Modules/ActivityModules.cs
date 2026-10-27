// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Modules;

// ---------------------------------------------------------------------
// Privacidad: apps usando micrófono o cámara
// ---------------------------------------------------------------------
public sealed class PrivacyModule : IslandModule
{
    public override string Name => "Privacidad";
    public override string Glyph => UI.GMic;
    public override int Priority => 35;
    public override Size CompactSize => new(170, 28);

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Grid _compact, _bubble = new(), _expanded;
    readonly TextBlock _cIcon, _cText, _bIcon, _micList, _camList;
    readonly StackPanel _micRow, _camRow;
    string _signature = "";

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement BubbleView => _bubble;
    public override FrameworkElement ExpandedView => _expanded;

    public PrivacyModule()
    {
        _compact = new Grid { Margin = new Thickness(12, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _cIcon = UI.Icon(UI.GMic, 13, UI.Orange);
        _compact.Children.Add(_cIcon);
        _cText = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _cText.HorizontalAlignment = HorizontalAlignment.Right;
        _cText.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_cText, 1);
        _compact.Children.Add(_cText);

        _bIcon = UI.Icon(UI.GMic, 12, UI.Orange);
        _bubble.Children.Add(_bIcon);

        _expanded = new Grid { Margin = new Thickness(26, 18, 24, 18) };
        var list = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        list.Children.Add(UI.Caption(UI.GShield, "En uso ahora"));
        _micRow = Row(UI.GMic, UI.Orange, "Micrófono", out _micList);
        _camRow = Row(UI.GCamera, UI.Green, "Cámara", out _camList);
        list.Children.Add(_micRow);
        list.Children.Add(_camRow);
        _expanded.Children.Add(list);

        _poll.Tick += (_, _) => Check();
    }

    static StackPanel Row(string glyph, System.Windows.Media.Brush color, string title, out TextBlock apps)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = UI.ButtonBg, Child = UI.Icon(glyph, 15, color) });
        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(UI.Label(title, 14, UI.White, FontWeights.SemiBold));
        apps = UI.Label("", 12, UI.Dim);
        apps.MaxWidth = 380;
        texts.Children.Add(apps);
        row.Children.Add(texts);
        return row;
    }

    public override void Start()
    {
        Check();
        _poll.Start();
    }

    public override void Stop() => _poll.Stop();

    void Check()
    {
        var mic = new List<string>();
        var cam = new List<string>();
        if (Settings.Current.ShowPrivacy)
        {
            // Si hay una llamada en curso, esa app la muestra el módulo de llamadas.
            string? call = CallModule.ActiveKey;
            var app = CallModule.ActiveApp;
            mic = CapabilityUsage.InUse("microphone")
                .Where(x => x.Key != call && (app == null || !x.Key.Contains(app.Key, StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Name).Distinct().ToList();
            cam = CapabilityUsage.InUse("webcam").Select(x => x.Name).Distinct().ToList();
        }
        string sig = string.Join("|", mic) + "#" + string.Join("|", cam);
        if (sig == _signature) return;
        _signature = sig;

        bool camOn = cam.Count > 0, micOn = mic.Count > 0;
        _cIcon.Text = _bIcon.Text = camOn ? UI.GCamera : UI.GMic;
        _cIcon.Foreground = _bIcon.Foreground = camOn ? UI.Green : UI.Orange;
        _cText.Text = (camOn ? cam : mic).FirstOrDefault() ?? "";
        _micRow.Visibility = micOn ? Visibility.Visible : Visibility.Collapsed;
        _camRow.Visibility = camOn ? Visibility.Visible : Visibility.Collapsed;
        _micList.Text = string.Join(", ", mic);
        _camList.Text = string.Join(", ", cam);
        SetActive(micOn || camOn);
    }
}

// ---------------------------------------------------------------------
// Descargas del navegador
// ---------------------------------------------------------------------
public sealed class DownloadsModule : IslandModule
{
    public override string Name => "Descargas";
    public override string Glyph => UI.GDownload;
    public override int Priority => _done ? 92 : 40;
    public override bool IsTransient => _done;
    public override Size CompactSize => _done ? new Size(270, 32) : new Size(170, 28);

    static readonly string[] TempExt = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };
    readonly string _folder = Native.GetDownloadsFolder();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _doneHide = new() { Interval = TimeSpan.FromSeconds(3.5) };
    readonly Dictionary<string, long> _sizes = new();
    bool _done;
    string _lastFinal = "";

    readonly Grid _compact, _doneView, _bubble = new(), _expanded;
    readonly TextBlock _cText, _doneText, _eName, _eInfo;

    public override FrameworkElement CompactView => _done ? _doneView : _compact;
    public override FrameworkElement BubbleView => _bubble;
    public override FrameworkElement ExpandedView => _expanded;

    public DownloadsModule()
    {
        _compact = new Grid { Margin = new Thickness(12, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.Children.Add(UI.Icon(UI.GDownload, 13, UI.Accent));
        _cText = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _cText.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_cText, 1);
        _compact.Children.Add(_cText);

        _doneView = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        _doneView.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _doneView.ColumnDefinitions.Add(new ColumnDefinition());
        _doneView.Children.Add(UI.Icon(UI.GDownload, 14, UI.Green));
        _doneText = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _doneText.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(_doneText, 1);
        _doneView.Children.Add(_doneText);

        _bubble.Children.Add(UI.Icon(UI.GDownload, 12, UI.Accent));

        _expanded = new Grid { Margin = new Thickness(26, 18, 24, 18) };
        _expanded.ColumnDefinitions.Add(new ColumnDefinition());
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(UI.Caption(UI.GDownload, "Descargando"));
        _eName = UI.Label("", 16, UI.White, FontWeights.SemiBold, display: true);
        _eName.Margin = new Thickness(0, 6, 0, 0);
        _eName.MaxWidth = 320;
        _eInfo = UI.Label("", 13, UI.Accent);
        info.Children.Add(_eName);
        info.Children.Add(_eInfo);
        _expanded.Children.Add(info);
        var btn = UI.IconTextButton(UI.GOpenFolder, "Abrir carpeta", OpenFolder, 130, 38);
        btn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(btn, 1);
        _expanded.Children.Add(btn);

        _poll.Tick += (_, _) => Scan();
        _doneHide.Tick += (_, _) =>
        {
            _doneHide.Stop();
            _done = false;
            SetActive(_sizes.Count > 0);
        };
    }

    public override void Start()
    {
        if (Directory.Exists(_folder)) _poll.Start();
    }

    public override void Stop() => _poll.Stop();

    void OpenFolder()
    {
        try
        {
            if (_lastFinal.Length > 0 && File.Exists(_lastFinal))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFinal}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(_folder) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("Downloads folder", ex); }
    }

    void Scan()
    {
        if (!Settings.Current.NotifyDownloads)
        {
            _sizes.Clear();
            if (IsActive) { _done = false; SetActive(false); }
            return;
        }

        var current = new Dictionary<string, long>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_folder))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(TempExt, ext) < 0) continue;
                try { current[f] = new FileInfo(f).Length; } catch { }
            }
        }
        catch { return; }

        foreach (var old in _sizes.Keys.Where(k => !current.ContainsKey(k)).ToList())
        {
            string final = Path.Combine(_folder, Path.GetFileNameWithoutExtension(old));
            if (!File.Exists(final)) continue;
            _lastFinal = final;
            _doneText.Text = "Descarga completa · " + Path.GetFileName(final);
            _done = true;
            _doneHide.Stop();
            _doneHide.Start();
        }

        double speed = 0;
        string? main = current.Keys.OrderByDescending(k => current[k]).FirstOrDefault();
        if (main != null && _sizes.TryGetValue(main, out long before))
            speed = Math.Max(0, current[main] - before) / _poll.Interval.TotalSeconds;

        _sizes.Clear();
        foreach (var kv in current) _sizes[kv.Key] = kv.Value;

        if (main != null)
        {
            _cText.Text = UI.Bytes(current[main]);
            _eName.Text = Path.GetFileNameWithoutExtension(main);
            _eInfo.Text = $"{UI.Bytes(current[main])} descargados" + (speed > 0 ? $" · {UI.Bytes(speed)}/s" : "") +
                          (current.Count > 1 ? $" · {current.Count} archivos" : "");
        }

        bool active = _done || current.Count > 0;
        if (active != IsActive) SetActive(active);
        else if (active) Raise();
    }
}
