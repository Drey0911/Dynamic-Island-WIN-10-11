// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Modules;

/// <summary>
/// Música y video vía los controles multimedia de Windows (Spotify, navegadores, VLC...).
/// Los puntitos y el agua siguen el espectro real de lo que suena.
/// </summary>
public sealed class MediaModule : IslandModule
{
    public override string Name => "Música";
    public override string Glyph => UI.GMusic;
    public override int Priority => 60;
    public override IslandModuleCategory Category => IslandModuleCategory.Media;
    public override Size CompactSize => ShowLyricInIsland ? new(330, 28) : new(150, 28);

    /// <summary>La isla cerrada se ensancha para mostrar la línea actual de la letra.</summary>
    bool ShowLyricInIsland => Settings.Current.ShowMediaActivity && Settings.Current.LyricsEnabled &&
                              Settings.Current.LyricsInIsland && _lyrics != null && _playing;

    public bool IsPlaying => _playing;
    public override bool WantsFrames => _playing && Settings.Current.MusicVisualizer &&
                                        Settings.Current.ShowMediaActivity && Settings.Current.ShowDynamicActivities;

    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _boundId;
    int _version;
    bool _playing, _hasTitle;
    DateTime _lastPlaying = DateTime.MinValue;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // Visualizador
    readonly double[] _bands = new double[3];
    readonly double[] _bands4 = new double[4];
    double _energy, _frameAcc;
    int _lastBeats;
    DateTime _lastRestart = DateTime.MinValue;

    readonly Grid _compact, _expanded, _bubble;
    readonly Border _artCompact, _artExpanded, _artBubble;
    readonly AudioDots _dotsCompact = new(3, 3.5, 3, 9), _dotsExpanded = new(4, 5, 4, 16);
    readonly WaterLayer _waterCompact = new(0.8), _waterExpanded = new(0.88);
    readonly TextBlock _title, _artist, _elapsed, _remaining, _playGlyph;

    // Letras sincronizadas
    readonly TextBlock _lyricCompact, _lyricExpanded;
    readonly DispatcherTimer _lyricTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    List<LyricLine>? _lyrics;
    string _lyricKey = "", _album = "";
    int _lyricIndex = -2;
    double _posSeconds, _lengthSeconds;
    DateTime _posStamp = DateTime.Now;
    bool _lyricWide;
    readonly ThinBar _progress;
    readonly Grid _progressRow;

    public override FrameworkElement CompactView => _compact;
    public override FrameworkElement ExpandedView => _expanded;
    public override FrameworkElement BubbleView => _bubble;

    public MediaModule()
    {
        _compact = new Grid();
        _compact.Children.Add(_waterCompact);
        var mini = new Grid { Margin = new Thickness(6, 0, 14, 0) };
        mini.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mini.ColumnDefinitions.Add(new ColumnDefinition());
        mini.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _artCompact = UI.ArtBox(18, 5);
        mini.Children.Add(_artCompact);
        _lyricCompact = UI.Label("", 12, UI.White, FontWeights.SemiBold);
        _lyricCompact.Margin = new Thickness(10, 0, 10, 0);
        Grid.SetColumn(_lyricCompact, 1);
        mini.Children.Add(_lyricCompact);
        Grid.SetColumn(_dotsCompact, 2);
        mini.Children.Add(_dotsCompact);
        _compact.Children.Add(mini);

        _artBubble = UI.ArtBox(22, 11);
        _artBubble.HorizontalAlignment = HorizontalAlignment.Center;
        _bubble = new Grid();
        _bubble.Children.Add(_artBubble);

        _expanded = new Grid();
        _expanded.Children.Add(_waterExpanded);
        var content = new Grid { Margin = new Thickness(24, 14, 24, 12) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _artExpanded = UI.ArtBox(68, 14);
        top.Children.Add(_artExpanded);
        _title = UI.Label("", 16, UI.White, FontWeights.SemiBold, display: true);
        _artist = UI.Label("", 13, UI.Dim);
        var texts = new StackPanel { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_title);
        texts.Children.Add(_artist);
        _lyricExpanded = UI.Label("", 13, UI.Accent, FontWeights.SemiBold);
        _lyricExpanded.Margin = new Thickness(0, 4, 0, 0);
        _lyricExpanded.Visibility = Visibility.Collapsed;
        texts.Children.Add(_lyricExpanded);
        Grid.SetColumn(texts, 1);
        top.Children.Add(texts);
        Grid.SetColumn(_dotsExpanded, 2);
        top.Children.Add(_dotsExpanded);
        content.Children.Add(top);

        _progressRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition());
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        _elapsed = UI.Label("0:00", 11, UI.Dim);
        _remaining = UI.Label("-0:00", 11, UI.Dim);
        _remaining.HorizontalAlignment = HorizontalAlignment.Right;
        _progress = new ThinBar(4, UI.Accent, UI.Faint) { Margin = new Thickness(4, 0, 4, 0) };
        _progressRow.Children.Add(_elapsed);
        Grid.SetColumn(_progress, 1);
        _progressRow.Children.Add(_progress);
        Grid.SetColumn(_remaining, 2);
        _progressRow.Children.Add(_remaining);
        Grid.SetRow(_progressRow, 1);
        content.Children.Add(_progressRow);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _playGlyph = UI.Icon(UI.GPlay, 20, UI.Accent);
        controls.Children.Add(UI.Button(UI.Icon(UI.GPrev, 16), Previous, 44, 44, 22, Brushes.Transparent));
        controls.Children.Add(new Border { Width = 16 });
        controls.Children.Add(UI.Button(_playGlyph, PlayPause, 48, 48, 24, UI.AccentSoft));
        controls.Children.Add(new Border { Width = 16 });
        controls.Children.Add(UI.Button(UI.Icon(UI.GNext, 16), Next, 44, 44, 22, Brushes.Transparent));
        Grid.SetRow(controls, 2);
        content.Children.Add(controls);
        _expanded.Children.Add(content);

        _poll.Tick += (_, _) => Poll();
        _lyricTimer.Tick += (_, _) => UpdateLyric();
    }

    public override async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Error("SMTC", ex);
            return;
        }
        _manager.SessionsChanged += (_, _) => Ui(Poll);
        _manager.CurrentSessionChanged += (_, _) => Ui(Poll);
        Settings.Changed += () => Ui(() =>
        {
            UpdateVisualizerState();
            if (Settings.Current.ShowMediaActivity && Settings.Current.LyricsEnabled && _lyrics == null)
            {
                _lyricKey = "";
                _ = LoadLyricsAsync(_title.Text, _artist.Text, _album);
            }
            UpdateLyricState();
        });
        Poll();
        _poll.Start();
    }

    public override void Stop()
    {
        _poll.Stop();
        _lyricTimer.Stop();
        AudioAnalyzer.Instance.Stop();
    }

    public void PlayPause() => Do(s => s.TryTogglePlayPauseAsync());
    public void Next() => Do(s => s.TrySkipNextAsync());
    public void Previous() => Do(s => s.TrySkipPreviousAsync());

    // ---------- Visualizador ----------

    public override void OnFrame(double dt, double time)
    {
        _frameAcc += dt;
        if (_frameAcc < 1 / 50.0) return;
        double step = _frameAcc;
        _frameAcc = 0;

        var a = AudioAnalyzer.Instance;
        if (!a.IsRunning || a.SinceLastData.TotalSeconds > 3)
        {
            // Sin datos estando en reproducción: probablemente cambió la salida de audio.
            if ((DateTime.Now - _lastRestart).TotalSeconds > 6)
            {
                _lastRestart = DateTime.Now;
                a.Restart();
            }
        }

        Smooth(ref _bands[0], a.Bass, step);
        Smooth(ref _bands[1], a.Mid, step);
        Smooth(ref _bands[2], a.Treble, step);
        Smooth(ref _energy, a.Energy, step);
        _bands4[0] = _bands[0];
        _bands4[1] = _bands[1];
        _bands4[2] = _bands[2];
        _bands4[3] = (_bands[0] + _bands[1]) / 2;

        int beats = a.Beats;
        bool beat = beats != _lastBeats;
        _lastBeats = beats;

        if (_compact.IsVisible)
        {
            _dotsCompact.Update(_bands);
            _waterCompact.Update(step, _bands[0], _energy, beat);
        }
        if (_expanded.IsVisible)
        {
            _dotsExpanded.Update(_bands4);
            _waterExpanded.Update(step, _bands[0] * 0.8, _energy, beat);
        }
    }

    /// <summary>Sube rápido y baja suave, como un vúmetro.</summary>
    static void Smooth(ref double current, double target, double dt)
    {
        double k = target > current ? 0.55 : 0.14;
        current += (target - current) * Math.Min(1, k * dt * 60);
    }

    void UpdateVisualizerState()
    {
        bool on = _playing && Settings.Current.MusicVisualizer &&
                  Settings.Current.ShowMediaActivity && Settings.Current.ShowDynamicActivities;
        if (on) AudioAnalyzer.Instance.Start();
        else
        {
            AudioAnalyzer.Instance.Stop();
            Array.Clear(_bands);
            Array.Clear(_bands4);
            _energy = 0;
            _dotsCompact.Update(_bands);
            _dotsExpanded.Update(_bands4);
            _waterCompact.Calm();
            _waterExpanded.Calm();
        }
    }

    // ---------- Sesiones multimedia ----------

    void Do(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
    {
        var s = _session;
        if (s == null) return;
        try { _ = action(s); } catch (Exception ex) { Log.Error("Media control", ex); }
    }

    static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession s) =>
        s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        try
        {
            var sessions = _manager.GetSessions();
            foreach (var s in sessions)
            {
                try { if (IsSessionPlaying(s)) return s; } catch { }
            }
            if (_boundId != null)
            {
                var same = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _boundId);
                if (same != null) return same;
            }
            return _manager.GetCurrentSession();
        }
        catch { return null; }
    }

    void Poll()
    {
        if (!Settings.Current.ShowMediaActivity)
        {
            _session = null;
            _boundId = null;
            _playing = false;
            _hasTitle = false;
            _lastPlaying = DateTime.MinValue;
            if (IsActive) SetActive(false);
            return;
        }

        var picked = PickSession();
        bool boundPlaying = false, dead = false, pickedPlaying = false;
        if (_session != null)
        {
            try { boundPlaying = IsSessionPlaying(_session); } catch { dead = true; }
        }
        if (picked != null)
        {
            try { pickedPlaying = IsSessionPlaying(picked); } catch { }
        }
        if (picked?.SourceAppUserModelId != _boundId || dead ||
            (pickedPlaying && !boundPlaying && !ReferenceEquals(picked, _session)))
            Bind(picked);

        bool wasPlaying = _playing;
        if (_session != null)
        {
            try { _playing = IsSessionPlaying(_session); }
            catch { _playing = false; }
            if (_playing) _lastPlaying = DateTime.Now;
            UpdateTimeline();
        }
        else _playing = false;

        _playGlyph.Text = _playing ? UI.GPause : UI.GPlay;
        if (wasPlaying != _playing) UpdateVisualizerState();
        UpdateLyricState();

        // Visible 20 s después de pausar, como en iPhone.
        bool active = Settings.Current.ShowMediaActivity && _session != null && _hasTitle &&
                      (_playing || (DateTime.Now - _lastPlaying).TotalSeconds < 20);
        if (active != IsActive) SetActive(active);
        else if (wasPlaying != _playing) Raise();
    }

    void Bind(GlobalSystemMediaTransportControlsSession? s)
    {
        if (_session != null)
        {
            try
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch { }
        }

        _session = s;
        _boundId = s?.SourceAppUserModelId;
        _hasTitle = false;
        _version++;
        if (s == null) return;
        try
        {
            s.MediaPropertiesChanged += OnMediaPropertiesChanged;
            s.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
        catch (Exception ex) { Log.Error("Media session", ex); }
        RefreshProperties();
    }

    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => Ui(RefreshProperties);

    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => Ui(Poll);

    async void RefreshProperties()
    {
        var s = _session;
        if (s == null) return;
        int version = ++_version;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            if (version != _version || p == null) return;

            string title = p.Title ?? "";
            string artist = !string.IsNullOrWhiteSpace(p.Artist) ? p.Artist : (p.AlbumArtist ?? "");
            if (string.IsNullOrWhiteSpace(artist)) artist = FriendlyAppName(s.SourceAppUserModelId);
            _title.Text = title;
            _album = p.AlbumTitle ?? "";
            _ = LoadLyricsAsync(title, artist, _album);
            _artist.Text = artist;
            _hasTitle = !string.IsNullOrWhiteSpace(title);

            var image = await LoadThumbnail(p.Thumbnail);
            if (version != _version) return;
            ApplyArt(image);

            Poll();
            Raise();
        }
        catch (Exception ex) { Log.Error("Media properties", ex); }
    }

    // ---------- Letras ----------

    async System.Threading.Tasks.Task LoadLyricsAsync(string title, string artist, string album)
    {
        string key = $"{artist}|{title}";
        if (key == _lyricKey) return;
        _lyricKey = key;
        SetLyrics(null);
        if (!Settings.Current.LyricsEnabled || title.Length == 0) return;
        var lines = await LyricsService.GetAsync(title, artist, album, _lengthSeconds);
        if (key != _lyricKey) return; // cambió la canción mientras se descargaba
        SetLyrics(lines);
    }

    void SetLyrics(List<LyricLine>? lines)
    {
        _lyrics = lines;
        _lyricIndex = -2;
        _lyricCompact.Text = "";
        _lyricExpanded.Text = "";
        _lyricExpanded.Visibility = lines == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateLyricState();
    }

    /// <summary>Activa el temporizador de letras y ensancha o encoge la isla si hace falta.</summary>
    void UpdateLyricState()
    {
        if (_lyrics != null && _playing && Settings.Current.ShowMediaActivity && Settings.Current.LyricsEnabled) _lyricTimer.Start();
        else _lyricTimer.Stop();
        bool wide = ShowLyricInIsland;
        _lyricCompact.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        if (wide != _lyricWide)
        {
            _lyricWide = wide;
            Raise();
        }
        UpdateLyric();
    }

    void UpdateLyric()
    {
        if (_lyrics == null) return;
        double pos = _posSeconds + (_playing ? (DateTime.Now - _posStamp).TotalSeconds : 0);
        int idx = LyricsService.IndexAt(_lyrics, pos + 0.15);
        if (idx == _lyricIndex) return;
        _lyricIndex = idx;
        string text = idx >= 0 ? _lyrics[idx].Text : "";
        if (text.Length == 0) text = idx >= 0 ? "..." : "";
        foreach (var t in new[] { _lyricCompact, _lyricExpanded })
        {
            t.Text = text;
            t.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(220)));
        }
    }

    void UpdateTimeline()
    {
        try
        {
            var t = _session!.GetTimelineProperties();
            var length = t.EndTime - t.StartTime;
            if (length.TotalSeconds < 1)
            {
                _progressRow.Visibility = Visibility.Hidden;
                return;
            }
            var pos = t.Position - t.StartTime;
            if (_playing) pos += DateTimeOffset.Now - t.LastUpdatedTime;
            if (pos > length) pos = length;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

            _posSeconds = pos.TotalSeconds;
            _lengthSeconds = length.TotalSeconds;
            _posStamp = DateTime.Now;
            _progressRow.Visibility = Visibility.Visible;
            _progress.SetValue01(pos.TotalSeconds / length.TotalSeconds, animate: true);
            _elapsed.Text = UI.Fmt(pos);
            _remaining.Text = "-" + UI.Fmt(length - pos);
        }
        catch { _progressRow.Visibility = Visibility.Hidden; }
    }

    void ApplyArt(BitmapSource? image)
    {
        Brush? brush = null;
        if (image != null)
        {
            var ib = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            ib.Freeze();
            brush = ib;
        }
        foreach (var box in new[] { _artCompact, _artExpanded, _artBubble })
        {
            box.Background = brush ?? UI.AccentFaint;
            if (box.Child is UIElement icon) icon.Visibility = brush == null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    static async Task<BitmapSource?> LoadThumbnail(IRandomAccessStreamReference? reference)
    {
        if (reference == null) return null;
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error("Thumbnail", ex);
            return null;
        }
    }

    static string FriendlyAppName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string s = id.ToLowerInvariant();
        if (s.Contains("spotify")) return "Spotify";
        if (s.Contains("chrome")) return "Google Chrome";
        if (s.Contains("msedge")) return "Microsoft Edge";
        if (s.Contains("firefox")) return "Firefox";
        if (s.Contains("opera")) return "Opera";
        if (s.Contains("brave")) return "Brave";
        if (s.Contains("vlc")) return "VLC";
        if (s.Contains("zune") || s.Contains("media")) return "Reproductor multimedia";
        string name = id.Split('!')[0];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
