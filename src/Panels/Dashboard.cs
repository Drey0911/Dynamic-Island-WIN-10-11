// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Modules;
using DynamicIsland.Services;

namespace DynamicIsland.Panels;

public enum DashTab { Home, Now, Time, Translate, Audio, Files, Clipboard, System, Settings }

/// <summary>Panel expandido: barra de pestañas y página activa.</summary>
public sealed class Dashboard
{
    public static readonly Size Size = new(IslandModule.NowArea.Width, IslandModule.NowArea.Height + 42);
    public FrameworkElement View => _root;
    public DashTab Selected => _selected;
    public TranslatePage Translate { get; }

    readonly Grid _root;
    readonly ContentPresenter _page = new();
    readonly Dictionary<DashTab, (Border tab, TextBlock icon, TextBlock label)> _tabs = new();
    readonly Dictionary<DashTab, FrameworkElement> _pages = new();
    readonly HomePage _home;
    readonly TextBlock _clock;
    readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    DashTab _selected = DashTab.Now;
    IslandModule? _now;

    public Dashboard(TimerModule timer, StopwatchModule stopwatch, AlarmModule alarm, FocusModule focus, FileShelf shelf, ClipboardHistory clipboard)
    {
        _home = new HomePage();
        Translate = new TranslatePage();
        _pages[DashTab.Home] = _home.View;
        _pages[DashTab.Time] = new TimePage(timer, stopwatch, alarm, focus).View;
        _pages[DashTab.Translate] = Translate.View;
        _pages[DashTab.Audio] = new AudioPage().View;
        _pages[DashTab.Files] = new FilesPage(shelf).View;
        _pages[DashTab.Clipboard] = new ClipboardPage(clipboard).View;
        _pages[DashTab.System] = new SystemPage().View;
        _pages[DashTab.Settings] = new SettingsPage().View;

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IslandModule.NowArea.Height) });

        var bar = new Grid { Margin = new Thickness(18, 10, 18, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Background = UI.Logo is ImageSource src ? new ImageBrush(src) : UI.Accent,
        };
        bar.Children.Add(logo);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AddTab(tabs, DashTab.Home, UI.GHome, "Inicio");
        AddTab(tabs, DashTab.Now, UI.GMusic, "Ahora");
        AddTab(tabs, DashTab.Time, UI.GTimer, "Tiempo");
        AddTab(tabs, DashTab.Translate, UI.GGlobe, "Traducir");
        AddTab(tabs, DashTab.Audio, UI.GVolume, "Audio");
        AddTab(tabs, DashTab.Files, UI.GFolder, "Archivos");
        AddTab(tabs, DashTab.Clipboard, UI.GClipboard, "Portapapeles");
        AddTab(tabs, DashTab.System, UI.GChart, "Sistema");
        AddTab(tabs, DashTab.Settings, UI.GSettings, "Ajustes");
        Grid.SetColumn(tabs, 1);
        bar.Children.Add(tabs);

        _clock = UI.Label("", 12, UI.Dim, FontWeights.SemiBold);
        Grid.SetColumn(_clock, 2);
        bar.Children.Add(_clock);
        _root.Children.Add(bar);

        Grid.SetRow(_page, 1);
        _root.Children.Add(_page);

        _clockTimer.Tick += (_, _) => _clock.Text = DateTime.Now.ToString("t");
        _clock.Text = DateTime.Now.ToString("t");
        SetNow(null);
        Select(DashTab.Home, animate: false);
    }

    void AddTab(StackPanel host, DashTab id, string glyph, string label)
    {
        var icon = UI.Icon(glyph, 13, UI.Dim);
        var text = UI.Label(label, 12, UI.Accent, FontWeights.SemiBold);
        text.Margin = new Thickness(6, 0, 0, 0);
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(text);
        var tab = new Border
        {
            Height = 28,
            MinWidth = 30,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(0, 0, 2, 0),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Child = content,
            Cursor = Cursors.Hand,
            ToolTip = label,
        };
        tab.MouseEnter += (_, _) => { if (_selected != id) tab.Background = UI.ButtonBg; };
        tab.MouseLeave += (_, _) => { if (_selected != id) tab.Background = Brushes.Transparent; };
        tab.MouseLeftButtonUp += (_, e) => { e.Handled = true; Select(id); };
        tab.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _tabs[id] = (tab, icon, text);
        host.Children.Add(tab);
    }

    public void SetVisible(bool visible)
    {
        if (visible) { _clock.Text = DateTime.Now.ToString("t"); _clockTimer.Start(); }
        else _clockTimer.Stop();
    }

    /// <summary>Actividad en curso para la pestaña "Ahora". Sin actividad, la pestaña se oculta.</summary>
    public void SetNow(IslandModule? module)
    {
        _now = module?.ExpandedView != null ? module : null;
        var (tab, icon, label) = _tabs[DashTab.Now];
        tab.Visibility = _now == null ? Visibility.Collapsed : Visibility.Visible;
        if (_now != null)
        {
            icon.Text = _now.Glyph;
            label.Text = _now.Name;
        }
        if (_selected == DashTab.Now)
        {
            if (_now == null) Select(DashTab.Home, animate: false);
            else ShowPage(_now.ExpandedView!, animate: false);
        }
    }

    /// <summary>Selecciona una pestaña. "Ahora" sin actividad abre Inicio.</summary>
    public void Select(DashTab tab, bool animate = true)
    {
        if (tab == DashTab.Now && _now == null) tab = DashTab.Home;
        _selected = tab;
        foreach (var (id, t) in _tabs)
        {
            bool sel = id == tab;
            t.tab.Background = sel ? UI.AccentSoft : Brushes.Transparent;
            t.icon.Foreground = sel ? UI.Accent : UI.Dim;
            t.label.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
        }
        ShowPage(tab == DashTab.Now ? _now!.ExpandedView! : _pages[tab], animate);
    }

    void ShowPage(FrameworkElement page, bool animate)
    {
        if (_page.Content == page) return;
        _page.Content = page;
        if (animate) page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }
}
