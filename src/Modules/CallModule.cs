// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Modules;

/// <summary>
/// Llamadas activas: se detectan cuando una app de llamadas usa el micrófono.
/// Silenciar actúa sobre el micrófono del sistema (funciona en cualquier app);
/// colgar usa el atajo de la app cuando existe.
/// </summary>
public sealed class CallModule : IslandModule
{
    sealed record CallApp(string Match, string Name, string Process, ushort[]? HangUp);

    // Teclas virtuales: Ctrl 0x11, Shift 0x10, Alt 0x12, H 0x48, Q 0x51
    static readonly CallApp[] Apps =
    {
        new("teams", "Teams", "ms-teams|Teams", new ushort[] { 0x11, 0x10, 0x48 }),
        new("zoom", "Zoom", "Zoom", new ushort[] { 0x12, 0x51 }),
        new("discord", "Discord", "Discord", null),
        new("slack", "Slack", "slack", null),
        new("whatsapp", "WhatsApp", "WhatsApp", null),
        new("skype", "Skype", "Skype", null),
        new("telegram", "Telegram", "Telegram", null),
        new("webex", "Webex", "CiscoCollabHost", null),
        new("chrome", "Llamada en Chrome", "chrome", null),
        new("msedge", "Llamada en Edge", "msedge", null),
        new("firefox", "Llamada en Firefox", "firefox", null),
        new("brave", "Llamada en Brave", "brave", null),
        new("opera", "Llamada en Opera", "opera", null),
    };

    public override string Name => "Llamada";
    public override string Glyph => UI.GPhone;
    public override int Priority => 75;
    public override Size CompactSize => new(190, 28);

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Grid _compact, _bubble = new(), _expanded;
    readonly TextBlock _cName, _cTime, _eTitle, _eTime, _micGlyph, _micText;
    readonly Border _micButton, _hangButton;
    CallApp? _app;
    DateTime _started;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement BubbleView => _bubble;
    public override FrameworkElement ExpandedView => _expanded;

    /// <summary>App de llamada activa (la usa el módulo de privacidad para no duplicar).</summary>
    public static string? ActiveKey { get; private set; }

    public CallModule()
    {
        _compact = new Grid { Margin = new Thickness(12, 0, 14, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = UI.Icon(UI.GPhone, 12, UI.Green);
        dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
        _compact.Children.Add(dot);
        _cName = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _cName.Margin = new Thickness(8, 0, 6, 0);
        Grid.SetColumn(_cName, 1);
        _compact.Children.Add(_cName);
        _cTime = UI.Label("0:00", 12, UI.Green, FontWeights.SemiBold);
        Grid.SetColumn(_cTime, 2);
        _compact.Children.Add(_cTime);

        _bubble.Children.Add(UI.Icon(UI.GPhone, 12, UI.Green));

        _expanded = new Grid { Margin = new Thickness(26, 18, 24, 18) };
        _expanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _expanded.ColumnDefinitions.Add(new ColumnDefinition());
        var avatar = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = UI.Hex("#3330D158"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = UI.Icon(UI.GPhone, 22, UI.Green),
        };
        _expanded.Children.Add(avatar);

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        _eTitle = UI.Label("", 17, UI.White, FontWeights.SemiBold, display: true);
        _eTime = UI.Label("", 13, UI.Green);
        right.Children.Add(_eTitle);
        right.Children.Add(_eTime);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        _micGlyph = UI.Icon(UI.GMic, 13);
        _micText = UI.Label("Silenciar", 12, UI.White, FontWeights.SemiBold);
        _micText.Margin = new Thickness(6, 0, 0, 0);
        var micContent = new StackPanel { Orientation = Orientation.Horizontal };
        micContent.Children.Add(_micGlyph);
        micContent.Children.Add(_micText);
        _micButton = UI.Button(micContent, ToggleMic, 120, 38, 19);
        buttons.Children.Add(_micButton);
        buttons.Children.Add(new Border { Width = 8 });
        buttons.Children.Add(UI.IconTextButton(UI.GOpenFolder, "Ir a la llamada", Focus, 140, 38));
        buttons.Children.Add(new Border { Width = 8 });
        _hangButton = UI.IconTextButton(UI.GHangUp, "Colgar", HangUp, 100, 38, UI.Red);
        buttons.Children.Add(_hangButton);
        right.Children.Add(buttons);
        Grid.SetColumn(right, 1);
        _expanded.Children.Add(right);

        _poll.Tick += (_, _) => Check();
        _clock.Tick += (_, _) => UpdateTime();
    }

    public override void Start()
    {
        Check();
        _poll.Start();
    }

    public override void Stop()
    {
        _poll.Stop();
        _clock.Stop();
    }

    void Check()
    {
        CallApp? found = null;
        string? key = null;
        if (Settings.Current.ShowCalls)
        {
            foreach (var (k, name) in CapabilityUsage.InUse("microphone"))
            {
                var app = Apps.FirstOrDefault(a => k.Contains(a.Match, StringComparison.OrdinalIgnoreCase));
                if (app == null) continue;
                found = app;
                key = k;
                break;
            }
        }

        if (found == null)
        {
            ActiveKey = null;
            if (IsActive) { _clock.Stop(); SetActive(false); }
            return;
        }

        ActiveKey = key;
        if (!IsActive || _app != found)
        {
            _app = found;
            _started = DateTime.Now;
            _cName.Text = found.Name;
            _eTitle.Text = found.Name.StartsWith("Llamada") ? found.Name : "Llamada en " + found.Name;
            _hangButton.Visibility = found.HangUp != null ? Visibility.Visible : Visibility.Collapsed;
            UpdateTime();
            UpdateMic();
            _clock.Start();
            SetActive(true);
        }
    }

    void UpdateTime()
    {
        string t = UI.Fmt(DateTime.Now - _started);
        _cTime.Text = t;
        _eTime.Text = "En curso · " + t;
        UpdateMic();
    }

    void UpdateMic()
    {
        bool muted = AudioControl.IsMicMuted();
        _micText.Text = muted ? "Activar mic" : "Silenciar";
        _micGlyph.Foreground = muted ? UI.Red : UI.White;
        _micButton.Background = muted ? UI.RedSoft : UI.ButtonBg;
    }

    void ToggleMic()
    {
        AudioControl.ToggleMicMute();
        UpdateMic();
    }

    IntPtr FindWindow()
    {
        if (_app == null) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        foreach (var name in _app.Process.Split('|'))
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (found == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) found = p.MainWindowHandle;
                p.Dispose();
            }
        }
        return found;
    }

    void Focus() => Native.BringToFront(FindWindow());

    void HangUp()
    {
        if (_app?.HangUp == null) return;
        var hwnd = FindWindow();
        if (hwnd == IntPtr.Zero) return;
        Native.BringToFront(hwnd);
        var keys = _app.HangUp;
        // Pequeña espera para que la app reciba el foco antes del atajo.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) => { t.Stop(); Native.SendKeys(keys); };
        t.Start();
    }
}
