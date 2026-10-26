// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Forms = System.Windows.Forms;

namespace DynamicIsland.Modules;

/// <summary>Plantilla de los avisos cortos: icono, texto y valor o barra.</summary>
internal sealed class ToastView
{
    public readonly Grid Root;
    public readonly TextBlock Icon, Text, Value;
    public readonly ThinBar Bar = new(5, UI.Accent, UI.Faint);

    public ToastView()
    {
        Root = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Root.ColumnDefinitions.Add(new ColumnDefinition());
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Icon = UI.Icon("", 14, UI.Accent);
        Root.Children.Add(Icon);
        Text = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        Text.Margin = new Thickness(9, 0, 8, 0);
        Text.MaxWidth = 190;
        Grid.SetColumn(Text, 1);
        Root.Children.Add(Text);
        Bar.Margin = new Thickness(2, 0, 10, 0);
        Grid.SetColumn(Bar, 2);
        Root.Children.Add(Bar);
        Value = UI.Label("", 12, UI.Accent, FontWeights.SemiBold);
        Grid.SetColumn(Value, 3);
        Root.Children.Add(Value);
    }

    public void Show(string glyph, string text, string value, double? bar, System.Windows.Media.Brush? color = null, bool animateBar = false)
    {
        Icon.Text = glyph;
        Icon.Foreground = color ?? UI.Accent;
        Value.Foreground = color ?? UI.Accent;
        Text.Text = text;
        Value.Text = value;
        Bar.Visibility = bar.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (bar.HasValue) Bar.SetValue01(bar.Value, animateBar);
    }
}

/// <summary>Escucha cambios de dispositivos de audio predeterminados.</summary>
internal sealed class DeviceNotifier : IMMNotificationClient
{
    readonly Action<DataFlow> _onDefaultChanged;
    public DeviceNotifier(Action<DataFlow> onDefaultChanged) => _onDefaultChanged = onDefaultChanged;

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if ((flow == DataFlow.Render && role == Role.Multimedia) || (flow == DataFlow.Capture && role == Role.Communications))
            _onDefaultChanged(flow);
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}

// ---------------------------------------------------------------------
// Volumen y dispositivo de salida
// ---------------------------------------------------------------------
public sealed class VolumeModule : TransientModule
{
    public override string Name => "Volumen";
    public override int Priority => 110;
    public override Size CompactSize => _wide ? new Size(300, 32) : new Size(250, 32);
    public override FrameworkElement CompactView => _toast.Root;

    readonly ToastView _toast = new();
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;
    DeviceNotifier? _notifier;
    bool _wide, _ready;

    public override void Start()
    {
        _enumerator = new MMDeviceEnumerator();
        _notifier = new DeviceNotifier(flow => { if (flow == DataFlow.Render) Ui(() => Bind(announce: true)); });
        _enumerator.RegisterEndpointNotificationCallback(_notifier);
        Bind(announce: false);
        _ready = true;
    }

    public override void Stop()
    {
        try
        {
            if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
            _device?.Dispose();
            if (_enumerator != null && _notifier != null) _enumerator.UnregisterEndpointNotificationCallback(_notifier);
            _enumerator?.Dispose();
        }
        catch { }
    }

    void Bind(bool announce)
    {
        try
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                _device.Dispose();
            }
            _device = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
            if (announce && _ready && Settings.Current.NotifyVolume)
            {
                _wide = true;
                _toast.Show(UI.GSpeaker, "Salida: " + _device.FriendlyName, "", null);
                Flash(2.4);
            }
        }
        catch (Exception ex)
        {
            _device = null;
            Log.Error("Audio device", ex);
        }
    }

    void OnVolume(AudioVolumeNotificationData data)
    {
        float vol = data.MasterVolume;
        bool muted = data.Muted;
        Ui(() => Show(vol, muted));
    }

    void Show(float volume, bool muted)
    {
        if (!Settings.Current.NotifyVolume) return;
        int pct = (int)Math.Round(volume * 100);
        string glyph = muted || pct == 0 ? UI.GMute : pct < 34 ? "\uE993" : pct < 67 ? "\uE994" : "\uE995";
        bool wasActive = IsActive && !_wide;
        _wide = false;
        _toast.Show(glyph, "", muted ? "0" : pct.ToString(), muted ? 0 : volume, animateBar: wasActive);
        Flash(1.6);
    }
}

// ---------------------------------------------------------------------
// Micrófono: cambio de dispositivo y silencio
// ---------------------------------------------------------------------
public sealed class MicrophoneModule : TransientModule
{
    public override string Name => "Micrófono";
    public override int Priority => 108;
    public override Size CompactSize => new(280, 32);
    public override FrameworkElement CompactView => _toast.Root;

    readonly ToastView _toast = new();
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;
    DeviceNotifier? _notifier;
    bool _lastMute, _ready;

    public override void Start()
    {
        _enumerator = new MMDeviceEnumerator();
        _notifier = new DeviceNotifier(flow => { if (flow == DataFlow.Capture) Ui(() => Bind(announce: true)); });
        _enumerator.RegisterEndpointNotificationCallback(_notifier);
        Bind(announce: false);
        _ready = true;
    }

    public override void Stop()
    {
        try
        {
            if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
            _device?.Dispose();
            if (_enumerator != null && _notifier != null) _enumerator.UnregisterEndpointNotificationCallback(_notifier);
            _enumerator?.Dispose();
        }
        catch { }
    }

    void Bind(bool announce)
    {
        try
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                _device.Dispose();
            }
            _device = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _lastMute = _device.AudioEndpointVolume.Mute;
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
            if (announce && _ready && Settings.Current.NotifyMicrophone)
            {
                _toast.Show(UI.GMic, "Micrófono: " + _device.FriendlyName, "", null);
                Flash(2.4);
            }
        }
        catch
        {
            _device = null; // equipo sin micrófono
        }
    }

    void OnVolume(AudioVolumeNotificationData data)
    {
        bool muted = data.Muted;
        Ui(() =>
        {
            if (muted == _lastMute) return;
            _lastMute = muted;
            if (!Settings.Current.NotifyMicrophone) return;
            _toast.Show(UI.GMic, muted ? "Micrófono silenciado" : "Micrófono activado", "", null, muted ? UI.Red : UI.Accent);
            Flash(1.8);
        });
    }
}

// ---------------------------------------------------------------------
// Brillo (pantallas de portátil)
// ---------------------------------------------------------------------
public sealed class BrightnessModule : TransientModule
{
    public override string Name => "Brillo";
    public override int Priority => 106;
    public override Size CompactSize => new(250, 32);
    public override FrameworkElement CompactView => _toast.Root;

    readonly ToastView _toast = new();
    ManagementEventWatcher? _watcher;
    bool _shown;

    public override void Start()
    {
        // El watcher de WMI tarda en arrancar: se crea en segundo plano.
        Task.Run(() =>
        {
            try
            {
                if (AudioControl.GetBrightness() == null) return; // sin pantalla integrada
                var w = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                w.EventArrived += (_, e) =>
                {
                    int value = Convert.ToInt32(e.NewEvent["Brightness"]);
                    Ui(() => Show(value));
                };
                w.Start();
                _watcher = w;
            }
            catch (Exception ex) { Log.Error("Brightness watcher", ex); }
        });
    }

    public override void Stop()
    {
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
    }

    void Show(int value)
    {
        if (!Settings.Current.NotifyBrightness) return;
        _toast.Show(UI.GBrightness, "", value.ToString(), value / 100.0, animateBar: _shown && IsActive);
        _shown = true;
        Flash(1.6);
    }
}

// ---------------------------------------------------------------------
// Bloq Mayús
// ---------------------------------------------------------------------
public sealed class CapsLockModule : TransientModule
{
    public override string Name => "Bloq Mayús";
    public override int Priority => 100;
    public override Size CompactSize => new(220, 32);
    public override FrameworkElement CompactView => _toast.Root;

    readonly ToastView _toast = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(150) };
    bool _last;

    public CapsLockModule() => _poll.Tick += (_, _) => Check();

    public override void Start()
    {
        _last = Native.IsCapsLockOn();
        _poll.Start();
    }

    public override void Stop() => _poll.Stop();

    void Check()
    {
        bool on = Native.IsCapsLockOn();
        if (on == _last) return;
        _last = on;
        if (!Settings.Current.NotifyCapsLock) return;
        _toast.Show(UI.GKeyboard, "Bloq Mayús", on ? "Activado" : "Desactivado", null, on ? UI.Accent : UI.Dim);
        Flash(1.5);
    }
}

// ---------------------------------------------------------------------
// Cargador (portátiles)
// ---------------------------------------------------------------------
public sealed class PowerModule : TransientModule
{
    public override string Name => "Energía";
    public override int Priority => 90;
    public override Size CompactSize => new(240, 32);
    public override FrameworkElement CompactView => _toast.Root;

    readonly ToastView _toast = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    Forms.PowerLineStatus _last;

    public PowerModule() => _poll.Tick += (_, _) => Check();

    public override void Start()
    {
        if (!Battery.HasBattery) return;
        _last = Forms.SystemInformation.PowerStatus.PowerLineStatus;
        _poll.Start();
    }

    public override void Stop() => _poll.Stop();

    void Check()
    {
        var status = Forms.SystemInformation.PowerStatus;
        if (status.PowerLineStatus == _last || status.PowerLineStatus == Forms.PowerLineStatus.Unknown) return;
        _last = status.PowerLineStatus;
        if (!Settings.Current.NotifyPower) return;
        bool charging = _last == Forms.PowerLineStatus.Online;
        double level = Battery.Level;
        var color = charging ? UI.Green : (level <= 0.2 ? UI.Red : UI.Accent);
        _toast.Show(Battery.Glyph(level, charging), charging ? "Cargando" : "Usando batería", $"{Math.Round(level * 100)} %", null, color);
        Flash(2.5);
    }
}

// ---------------------------------------------------------------------
// Conexión a internet
// ---------------------------------------------------------------------
public sealed class NetworkModule : TransientModule
{
    public override string Name => "Red";
    public override int Priority => 88;
    public override Size CompactSize => new(240, 32);
    public override FrameworkElement CompactView => _toast.Root;

    readonly ToastView _toast = new();
    bool _last;

    public override void Start()
    {
        _last = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnChanged;
    }

    public override void Stop() => NetworkChange.NetworkAvailabilityChanged -= OnChanged;

    void OnChanged(object? sender, NetworkAvailabilityEventArgs e) => Ui(() => Show(e.IsAvailable));

    void Show(bool available)
    {
        if (available == _last) return;
        _last = available;
        if (!Settings.Current.NotifyNetwork) return;
        _toast.Show(available ? UI.GWifi : UI.GWifiOff, available ? "Conectado" : "Sin conexión",
            available ? NetworkInfo.ActiveName() : "", null, available ? UI.Accent : UI.Red);
        Flash(2.2);
    }
}

public static class NetworkInfo
{
    public static string ActiveName()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (n.GetIPProperties().GatewayAddresses.Count == 0) continue;
                return n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : n.Name;
            }
        }
        catch { }
        return "";
    }
}

public static class Battery
{
    public static bool HasBattery =>
        (Forms.SystemInformation.PowerStatus.BatteryChargeStatus & Forms.BatteryChargeStatus.NoSystemBattery) == 0
        && Forms.SystemInformation.PowerStatus.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown;

    public static double Level => Math.Clamp(Forms.SystemInformation.PowerStatus.BatteryLifePercent, 0f, 1f);

    public static bool Charging => Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Online;

    public static string Glyph(double level, bool charging)
    {
        int idx = (int)Math.Round(Math.Clamp(level, 0, 1) * 10);
        return char.ConvertFromUtf32((charging ? 0xEBAB : 0xEBA0) + idx);
    }
}
