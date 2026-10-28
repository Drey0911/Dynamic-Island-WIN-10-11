// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Modules;
using DynamicIsland.Panels;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Ventana de la isla: elige qué mostrar según prioridades, anima la forma con resortes
/// y deja de animar cuando todo está quieto.
/// </summary>
public partial class MainWindow : Window
{
    const double IdleWidth = 104, IdleHeight = 20;
    const double BubbleGap = 8;
    const double ExpandedRadius = 24;
    const double FloatGap = 8;
    const double HotZoneWidth = 280, HotZoneHeight = 6;

    enum Hotkey { ToggleHiddenMode = 1, Translate, Mic, Previous, PlayPause, Next }

    readonly Spring _w = new(IdleWidth), _h = new(IdleHeight), _r = new(IdleHeight / 2);
    readonly Spring _offsetX = new(0, 200, 0.8);
    readonly Spring _bubbleR = new(0, 240, 0.62), _bubbleDx = new(-IdleHeight / 2, 150, 0.55);
    readonly Spring _visible = new(1, 170, 0.9, 0.002);
    readonly Spring[] _springs;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly ScaleTransform _contentScale = new(1, 1);
    readonly ScaleTransform _islandScale = new(1, 1);

    readonly List<IslandModule> _modules = new();
    readonly MediaModule _media = new();
    readonly GameModule _game = new();
    readonly ClipboardHistory _clipboard = new();
    readonly FileShelf _shelf = new();
    readonly Dashboard _dashboard;

    bool _expanded, _fullscreen, _userHidden, _animating, _dragging, _pinned, _keyboard, _hotkeysOn;

    // Con permisos de administrador el arrastre OLE desde el Explorador está bloqueado:
    // se usa WM_DROPFILES y se detecta el arrastre sobre la isla consultando el mouse.
    readonly bool _elevated = Native.IsElevated();
    readonly DispatcherTimer _dragPoll = new() { Interval = TimeSpan.FromMilliseconds(120) };
    bool _localPress, _shellDrag;
    int _dragTicks;
    IslandModule? _primary, _secondary, _focus;
    FrameworkElement? _currentView, _pendingView, _currentBubbleView;
    Size _currentSize, _pendingSize, _bubbleViewSize;
    bool _hasPending;
    double _contentOpacity;
    TimeSpan _lastRender = TimeSpan.Zero;
    IntPtr _hwnd;

    readonly DispatcherTimer _hoverTimer = new();
    readonly DispatcherTimer _openWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    DateTime? _outsideSince;
    DashTab? _requestedTab;
    int _fullscreenPid;
    readonly DispatcherTimer _systemTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    bool Hidden => _userHidden || (_fullscreen && !Settings.Current.ShowInFullscreen);

    static bool Floating => Settings.Current.IslandStyle == "floating";
    static bool Discrete => Settings.Current.IslandStyle == "discrete";

    // Modo discreto: sin actividad la isla no se ve; se abre al llevar el cursor al centro del borde superior.
    readonly DispatcherTimer _hotZone = new() { Interval = TimeSpan.FromMilliseconds(90) };
    bool _idleHidden, _hotArmed = true;
    int _hotTicks;

    public MainWindow()
    {
        UI.ApplyAccent(Settings.Current.AccentColor); // los pinceles del acento nacen en el hilo de la interfaz
        InitializeComponent();
#if TESTBACKDROP
        // Solo en compilaciones de prueba: fondo de color para ver la isla en capturas sin transparencia.
        Background = new SolidColorBrush(Color.FromRgb(0x5d, 0x86, 0xa8));
#endif
        _springs = new[] { _w, _h, _r, _offsetX, _bubbleR, _bubbleDx, _visible };
        MainPresenter.RenderTransform = _contentScale;
        Root.RenderTransform = _islandScale;

        var timer = new TimerModule();
        var stopwatch = new StopwatchModule();
        var alarm = new AlarmModule();
        var focus = new FocusModule();
        _modules.AddRange(new IslandModule[]
        {
            _media, timer, stopwatch, alarm, focus, _game, new CallModule(),
            new VolumeModule(), new MicrophoneModule(), new BrightnessModule(), new CapsLockModule(),
            new PowerModule(), new NetworkModule(), new PrivacyModule(), new DownloadsModule(),
            new ScreenshotModule(_clipboard), new SmartClipboardModule(_clipboard), new BluetoothModule(),
            new ApiModule(),
        });
        _dashboard = new Dashboard(timer, stopwatch, alarm, focus, _shelf, _clipboard);

        foreach (var m in _modules) m.StateChanged += _ => Evaluate();

        IslandHost.RequestKeyboard = RequestKeyboard;
        IslandHost.Collapse = () => { _pinned = false; SetExpanded(false); };
        IslandHost.OpenTranslate = text =>
        {
            _pinned = true;
            SetExpanded(true, DashTab.Translate);
            _dashboard.Translate.TranslateText(text);
        };

        Root.MouseEnter += (_, _) => { _pinned = false; if (!_expanded) _hoverTimer.Start(); };
        Root.MouseLeave += (_, _) => _hoverTimer.Stop();
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (!IsCursorOverIsland()) return;
            _focus = BubbleHost.IsMouseOver && _secondary != null ? _secondary : _primary;
            SetExpanded(true, DashTab.Now);
        };
        _openWatch.Tick += (_, _) => WatchOpenPanel();
        _hotZone.Tick += (_, _) => WatchHotZone();

        Root.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            _dragging = true;
            SetExpanded(true, DashTab.Files);
        };
        Root.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Root.DragLeave += (_, _) => { _dragging = false; _outsideSince = null; };
        Root.Drop += (_, e) =>
        {
            _dragging = false;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _shelf.Add(files);
            _dashboard.Select(DashTab.Files);
            _outsideSince = null;
        };

        Deactivated += (_, _) =>
        {
            ReleaseKeyboard();
            if (_expanded && !_pinned && !IsCursorOverIsland()) SetExpanded(false);
        };

        _systemTimer.Tick += (_, _) => CheckSystem();
        Settings.Changed += ApplySettings;

        PositionWindow();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.SetNoActivate(_hwnd, true);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            Native.AddClipboardFormatListener(_hwnd);
            ApplySettings();
        };
        Root.PreviewMouseLeftButtonDown += (_, _) => _localPress = true;
        Root.PreviewMouseRightButtonDown += (_, _) => _localPress = true;
        Root.PreviewMouseLeftButtonUp += (_, _) => _localPress = false;
        _dragPoll.Tick += (_, _) => PollShellDrag();

        Loaded += (_, _) =>
        {
            if (_elevated)
            {
                Native.EnableElevatedFileDrop(_hwnd);
                _dragPoll.Start();
            }
            foreach (var m in _modules)
            {
                try { m.Start(); } catch (Exception ex) { Log.Error("Start " + m.Name, ex); }
            }
            _systemTimer.Start();
            Evaluate();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    // ---------- Sistema ----------

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE) _clipboard.OnClipboardChanged();
        else if (msg == Native.WM_DROPFILES)
        {
            handled = true;
            var files = Native.ReadDroppedFiles(wParam);
            _shelf.Add(files);
            _shellDrag = false;
            _dragging = false;
            SetExpanded(true, DashTab.Files);
        }
        else if (msg == Native.WM_HOTKEY)
        {
            handled = true;
            OnHotkey((Hotkey)wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    void ApplySettings()
    {
        var s = Settings.Current;
        _islandScale.ScaleX = _islandScale.ScaleY = s.IslandScale;
        _islandScale.CenterX = Width / 2;
        _islandScale.CenterY = 0;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.HoverDelayMs, 0, 1000));
        _hotZone.Stop();
        _hotTicks = 0;
        if (_hwnd != IntPtr.Zero) RegisterHotkeys(s.HotkeysEnabled);
        UI.ApplyAccent(s.AccentColor);
        ApplyBackground();
        if (s.AlwaysHiddenUntilHover && _expanded) SetExpanded(false);
        Evaluate();
    }

    /// <summary>Fondo negro translúcido recortado al contorno de la isla, o negro sólido.</summary>
    void ApplyBackground()
    {
        byte alpha = Settings.Current.IslandBackground == "solid" ? (byte)0xFF : (byte)0xE8;
        var fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        fill.Freeze();
        Blob.Fill = fill;
    }

    void RegisterHotkeys(bool on)
    {
        if (on == _hotkeysOn) return;
        _hotkeysOn = on;
        foreach (Hotkey k in Enum.GetValues(typeof(Hotkey))) Native.UnregisterHotKey(_hwnd, (int)k);
        if (!on) return;

        const uint mods = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
        Register(Hotkey.ToggleHiddenMode, mods, 0x20); // Espacio
        Register(Hotkey.Translate, mods, 0x59); // Y
        Register(Hotkey.Mic, mods, 0x78);       // F9
        Register(Hotkey.Previous, mods, 0x79);  // F10
        Register(Hotkey.PlayPause, mods, 0x7A); // F11
        Register(Hotkey.Next, mods, 0x7B);      // F12
    }

    void Register(Hotkey id, uint mods, uint vk)
    {
        if (!Native.RegisterHotKey(_hwnd, (int)id, mods, vk))
            Log.Error("Hotkey", new InvalidOperationException($"{id} ya está en uso por otra app"));
    }

    void OnHotkey(Hotkey key)
    {
        switch (key)
        {
            case Hotkey.ToggleHiddenMode:
                Settings.Current.AlwaysHiddenUntilHover = !Settings.Current.AlwaysHiddenUntilHover;
                Settings.Current.Save();
                break;
            case Hotkey.Translate:
                _pinned = true;
                SetExpanded(true, DashTab.Translate);
                _dashboard.Translate.TranslateClipboard();
                break;
            case Hotkey.Mic:
                AudioControl.ToggleMicMute();
                break;
            case Hotkey.Previous: _media.Previous(); break;
            case Hotkey.PlayPause: _media.PlayPause(); break;
            case Hotkey.Next: _media.Next(); break;
        }
    }

    /// <summary>Permite escribir (traductor, ajustes): la ventana pasa a aceptar el foco.</summary>
    void RequestKeyboard()
    {
        if (_hwnd == IntPtr.Zero) return;
        _keyboard = true;
        Native.SetNoActivate(_hwnd, false);
        Activate();
    }

    void ReleaseKeyboard()
    {
        if (!_keyboard || _hwnd == IntPtr.Zero) return;
        _keyboard = false;
        Keyboard.ClearFocus();
        Native.SetNoActivate(_hwnd, true);
    }

    /// <summary>
    /// Mientras el panel está abierto: se cierra al hacer clic fuera o cuando el cursor
    /// lleva un momento fuera de la isla (salvo si está fijado, escribiendo o arrastrando).
    /// </summary>
    void WatchOpenPanel()
    {
        if (!_expanded) { _openWatch.Stop(); return; }
        if (IsCursorOverIsland()) { _outsideSince = null; return; }
        // Un arrastre siempre lleva el botón presionado: si ya se soltó, el estado de arrastre quedó viejo.
        if (_dragging && !Native.IsMouseButtonDown() && !_shellDrag) _dragging = false;
        if (_dragging || IslandHost.DraggingOut) return;

        if (Native.IsMouseButtonDown())
        {
            // Solo cuenta como "clic fuera" si la pulsación no empezó dentro de la isla.
            if (!_localPress && Mouse.Captured == null) SetExpanded(false);
            return;
        }
        _localPress = false;
        if (_pinned || _keyboard) return;

        _outsideSince ??= DateTime.Now;
        if ((DateTime.Now - _outsideSince.Value).TotalMilliseconds > 350) SetExpanded(false);
    }

    /// <summary>
    /// Modo administrador: si se mantiene presionado el botón sobre la isla sin que el clic
    /// haya empezado en ella, es un archivo que viene arrastrándose desde otra app.
    /// </summary>
    void PollShellDrag()
    {
        bool down = Native.IsMouseButtonDown();
        if (_shellDrag)
        {
            if (!down) { _shellDrag = false; _dragging = false; }
            return;
        }
        if (!down) _localPress = false;
        if (down && !_localPress && !_expanded && IsCursorOverIsland())
        {
            if (++_dragTicks >= 2)
            {
                _dragTicks = 0;
                _shellDrag = true;
                _dragging = true;
                SetExpanded(true, DashTab.Files);
            }
        }
        else _dragTicks = 0;
    }

    /// <summary>¿El cursor está sobre la forma de la isla? Se consulta la posición real a Windows.</summary>
    bool IsCursorOverIsland()
    {
        try
        {
            if (!Native.GetCursorPos(out var c)) return Root.IsMouseOver;
            var p = Root.PointFromScreen(new Point(c.X, c.Y));
            return Blob.Data != null && Blob.Data.FillContains(p, 2, ToleranceType.Absolute);
        }
        catch { return Root.IsMouseOver; }
    }

    /// <summary>
    /// Modo discreto con la isla oculta: si el cursor se queda en el centro del borde superior, se abre el panel.
    /// Si llega arrastrando un archivo, se abre directo en Archivos.
    /// </summary>
    void WatchHotZone()
    {
        if (!_idleHidden || _expanded || Hidden || _fullscreen) { _hotZone.Stop(); _hotTicks = 0; return; }
        bool inside = false;
        try
        {
            if (Native.GetCursorPos(out var c))
            {
                var p = PointFromScreen(new Point(c.X, c.Y));
                double scale = Math.Max(0.5, Settings.Current.IslandScale);
                inside = p.Y <= HotZoneHeight && Math.Abs(p.X - Width / 2) <= HotZoneWidth * scale / 2;
            }
        }
        catch { }
        if (!inside) { _hotTicks = 0; _hotArmed = true; return; }
        // Tras cerrar el panel hay que salir de la zona antes de que vuelva a abrirse.
        if (!_hotArmed) return;

        // Con el botón presionado (un clic en pestañas o barras de título arriba de la pantalla) no se abre;
        // solo si se mantiene un momento, que es lo que pasa al arrastrar un archivo hasta aquí.
        bool down = Native.IsMouseButtonDown();
        int needed = down
            ? (int)Math.Ceiling(650 / 90.0)
            : Math.Max(1, (int)Math.Ceiling(Settings.Current.HoverDelayMs / 90.0));
        if (++_hotTicks < needed) return;
        _hotTicks = 0;
        _hotArmed = false;
        _pinned = false;
        SetExpanded(true, down ? DashTab.Files : DashTab.Now);
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(PositionWindow));

    void PositionWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    public bool ToggleUserHidden()
    {
        _userHidden = !_userHidden;
        Evaluate();
        return _userHidden;
    }

    public void StopModules()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                Native.RemoveClipboardFormatListener(_hwnd);
                RegisterHotkeys(false);
            }
        }
        catch { }
        foreach (var m in _modules)
        {
            try { m.Stop(); } catch { }
        }
        HardwareMonitor.Instance.Shutdown();
        ApiHub.Instance.SaveHistory();
        Speech.Stop();
    }

    void CheckSystem()
    {
        bool fs = Native.IsFullscreenAppRunning(_hwnd, out int pid);
        if (!fs) pid = 0;
        if (fs != _fullscreen || pid != _fullscreenPid)
        {
            _fullscreen = fs;
            _fullscreenPid = pid;
            _game.SetFullscreen(fs, pid);
            Evaluate();
        }
        if (!Hidden) Native.KeepTopmost(_hwnd);
    }

    // ---------- Estado ----------

    void SetExpanded(bool value, DashTab tab = DashTab.Now)
    {
        if (value)
        {
            if (!_expanded) _localPress = false; // un clic viejo que terminó en otra ventana no cuenta
            _requestedTab = tab;
            _outsideSince = null;
            _openWatch.Start();
        }
        if (_expanded == value)
        {
            if (value) Evaluate();
            return;
        }
        _expanded = value;
        IslandHost.IsExpanded = value;
        if (!value)
        {
            _focus = null;
            _pinned = false;
            _openWatch.Stop();
            ReleaseKeyboard();
        }
        if (value) { _w.Configure(230, 0.64); _h.Configure(210, 0.68); _r.Configure(230, 0.85); }
        else { _w.Configure(260, 0.74); _h.Configure(260, 0.8); _r.Configure(260, 0.9); }
        _dashboard.SetVisible(value);
        Evaluate();
    }

    void Evaluate()
    {
        var active = _modules.Where(m => m.IsActive).OrderByDescending(m => m.Priority).ToList();
        var visible = active.Where(IsDynamicActivityVisible).ToList();
        FrameworkElement? view;
        Size size;
        IslandModule? secondary = null;

        if (_expanded)
        {
            IslandModule? now =
                _focus != null && _focus.IsActive && _focus.ExpandedView != null
                    ? _focus
                    : active.FirstOrDefault(m => !m.IsTransient && m.ExpandedView != null);
            _dashboard.SetNow(now);
            if (_requestedTab is DashTab tab)
            {
                _dashboard.Select(tab);
                _requestedTab = null;
            }
            view = _dashboard.View;
            size = Dashboard.Size;
        }
        else
        {
            _primary = visible.FirstOrDefault();
            if (_primary != null) { view = _primary.CompactView; size = _primary.CompactSize; }
            else { view = null; size = Floating ? new Size(96, 26) : new Size(IdleWidth, IdleHeight); }

            if (_primary != null && !_primary.IsTransient)
                secondary = visible.FirstOrDefault(m => m != _primary && !m.IsTransient && m.BubbleView != null);
        }

        _idleHidden = !_expanded && (Settings.Current.AlwaysHiddenUntilHover || (Discrete && _primary == null));
        if (_idleHidden && !Hidden) _hotZone.Start();
        _visible.Target = Hidden || _idleHidden ? 0 : 1;
        _w.Target = size.Width;
        _h.Target = size.Height;
        _r.Target = _expanded ? ExpandedRadius : size.Height / 2;

        _secondary = secondary;
        if (secondary != null)
        {
            double br = size.Height / 2;
            if (_currentBubbleView != secondary.BubbleView)
            {
                _currentBubbleView = secondary.BubbleView;
                BubblePresenter.Content = _currentBubbleView;
            }
            _bubbleViewSize = new Size(br * 2, br * 2);
            _bubbleR.Target = br;
            _bubbleDx.Target = BubbleGap + br;
            _offsetX.Target = -(BubbleGap + br * 2) / 2;
        }
        else
        {
            _bubbleR.Target = 0;
            _bubbleDx.Target = -size.Height / 2;
            _offsetX.Target = 0;
        }

        SetContent(view, size);
        StartAnimating();
    }

    static bool IsDynamicActivityVisible(IslandModule module)
    {
        var settings = Settings.Current;
        if (!settings.ShowDynamicActivities) return false;
        return module.Category switch
        {
            IslandModuleCategory.Media => settings.ShowMediaActivity,
            IslandModuleCategory.Calls => settings.ShowCalls,
            IslandModuleCategory.Timers => settings.ShowTimerActivities,
            IslandModuleCategory.System => settings.ShowSystemActivities,
            _ => settings.ShowOtherActivities,
        };
    }

    void SetContent(FrameworkElement? view, Size size)
    {
        if (view == _currentView)
        {
            _hasPending = false;
            _pendingView = null;
            _currentSize = size;
            return;
        }
        _hasPending = true;
        _pendingView = view;
        _pendingSize = size;
    }

    // ---------- Animación ----------

    bool AnyModuleWantsFrames => !Hidden && _modules.Any(m => m.IsActive && m.WantsFrames);

    void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastRender = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastRender) return;
        double dt = _lastRender == TimeSpan.Zero ? 1 / 60.0 : (now - _lastRender).TotalSeconds;
        _lastRender = now;
        dt = Math.Clamp(dt, 0.001, 1 / 30.0);
        double time = _clock.Elapsed.TotalSeconds;

        bool frames = AnyModuleWantsFrames;
        if (frames)
        {
            foreach (var m in _modules)
            {
                if (!m.IsActive || !m.WantsFrames) continue;
                try { m.OnFrame(dt, time); } catch (Exception ex) { Log.Error("Frame " + m.Name, ex); }
            }
        }

        const int substeps = 4;
        for (int i = 0; i < substeps; i++)
            foreach (var s in _springs) s.Step(dt / substeps);

        UpdateContentFade(dt);
        Render();

        bool contentSettled = !_hasPending && (_currentView == null || _contentOpacity >= 1);
        if (!frames && _springs.All(s => s.IsAtRest) && contentSettled)
        {
            foreach (var s in _springs) s.Snap();
            Render();
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    void UpdateContentFade(double dt)
    {
        if (_hasPending)
        {
            _contentOpacity = Math.Max(0, _contentOpacity - dt * 14);
            if (_contentOpacity <= 0)
            {
                MainPresenter.Content = _pendingView;
                _currentView = _pendingView;
                _currentSize = _pendingSize;
                _hasPending = false;
                _pendingView = null;
            }
        }
        else if (_currentView != null)
        {
            double remaining = Math.Abs(_w.Value - _w.Target) / 70 + Math.Abs(_h.Value - _h.Target) / 45;
            if (remaining < 0.65) _contentOpacity = Math.Min(1, _contentOpacity + dt * 5.5);
        }

        MainPresenter.Opacity = _contentOpacity;
        double s = 0.93 + 0.07 * _contentOpacity;
        _contentScale.ScaleX = s;
        _contentScale.ScaleY = s;
    }

    void Render()
    {
        double vis = Math.Clamp(_visible.Value, 0, 1);
        double cx = Width / 2 + _offsetX.Value;
        double w = Math.Max(0, _w.Value), h = Math.Max(0, _h.Value);

        // Squash & stretch: al estirarse rápido hacia los lados se aplana un poco.
        double squashH = Math.Clamp(-_w.Velocity * 0.003, -4, 4);
        double squashW = Math.Clamp(-_h.Velocity * 0.01, -10, 10);
        double dw = Math.Max(0, (w + squashW) * (0.7 + 0.3 * vis));
        double dh = Math.Max(0, (h + squashH) * vis);
        double r = Math.Min(Math.Max(0, _r.Value), Math.Min(dw, dh) / 2);
        double fillet = Math.Clamp(dh * 0.28, 0, 12);
        double left = cx - dw / 2, right = cx + dw / 2;
        bool floating = Floating;
        double top = floating ? FloatGap * vis : 0;

        var sb = new StringBuilder("F1 ");
        if (floating) IslandGeometry.AppendRoundedRect(sb, left, top, right, top + dh, r);
        else IslandGeometry.AppendNotch(sb, left, right, dh, r, fillet);

        double br = Math.Max(0, _bubbleR.Value) * vis;
        double capR = dh / 2;
        var capCenter = new Vec2(right - capR, top + dh / 2);
        var bubbleCenter = new Vec2(right + _bubbleDx.Value, top + dh / 2);
        if (br > 0.5)
        {
            IslandGeometry.AppendCircle(sb, bubbleCenter.X, bubbleCenter.Y, br);
            if (LiquidMath.Bridge(capCenter, capR, bubbleCenter, br) is { } bridge)
                IslandGeometry.AppendBridge(sb, bridge);
        }
        Blob.Data = dh > 0.5 ? Geometry.Parse(sb.ToString()) : Geometry.Empty;

        Canvas.SetLeft(ContentHost, left);
        Canvas.SetTop(ContentHost, top);
        ContentHost.Width = dw;
        ContentHost.Height = dh;
        var clip = new StringBuilder("F1 ");
        if (floating) IslandGeometry.AppendRoundedRect(clip, 0, 0, dw, dh, r);
        else IslandGeometry.AppendNotch(clip, 0, dw, dh, r, 0);
        ContentHost.Clip = dh > 0.5 ? Geometry.Parse(clip.ToString()) : Geometry.Empty;
        Canvas.SetLeft(MainPresenter, (dw - _currentSize.Width) / 2);
        Canvas.SetTop(MainPresenter, dh - _currentSize.Height);
        MainPresenter.Width = _currentSize.Width;
        MainPresenter.Height = _currentSize.Height;

        if (br > 0.5)
        {
            BubbleHost.Visibility = Visibility.Visible;
            Canvas.SetLeft(BubbleHost, bubbleCenter.X - br);
            Canvas.SetTop(BubbleHost, bubbleCenter.Y - br);
            BubbleHost.Width = BubbleHost.Height = br * 2;
            BubbleHost.Clip = new EllipseGeometry(new Point(br, br), br, br);
            Canvas.SetLeft(BubblePresenter, br - _bubbleViewSize.Width / 2);
            Canvas.SetTop(BubblePresenter, br - _bubbleViewSize.Height / 2);
            BubblePresenter.Width = _bubbleViewSize.Width;
            BubblePresenter.Height = _bubbleViewSize.Height;
            double target = Math.Max(1, _bubbleR.Target);
            BubblePresenter.Opacity = Math.Clamp((br / target - 0.6) / 0.4, 0, 1);
        }
        else
        {
            BubbleHost.Visibility = Visibility.Collapsed;
            if (_bubbleR.Target <= 0 && _currentBubbleView != null)
            {
                BubblePresenter.Content = null;
                _currentBubbleView = null;
            }
        }

        Root.Opacity = Math.Clamp(vis * 1.5, 0, 1);
        Root.IsHitTestVisible = vis > 0.5;
    }
}
