using System.Text.Json.Nodes;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DJFlix.Desktop;

sealed class TimeTip : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => Ui.Time((long)((double)value * 1000));
    public object ConvertBack(object value, Type targetType, object parameter, string language) => 0d;
}

/// <summary>
/// Full-window player on LibVLC. Reports progress to the hub every ten seconds and on pause, stop and end,
/// so the Fire TV can pick up where this PC left off.
/// </summary>
public sealed class PlayerPage : UserControl
{
    static readonly float[] Speeds = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f, 4f];
    long _mediaId; long? _episodeId; bool _startOver; long? _sourceId; bool _ready; Grid? _errorPanel;
    public bool Active { get; private set; }
    readonly Grid _root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
    readonly VideoView _video = new();
    readonly Grid _chrome = new();
    readonly Grid _layer = new();
    readonly Slider _seek = new() { Minimum = 0, Maximum = 1, StepFrequency = 1, ThumbToolTipValueConverter = new TimeTip() };
    readonly Slider _vol = new() { Minimum = 0, Maximum = 100, Width = 110, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _time = Ui.Text("0:00 / 0:00", 14, color: new SolidColorBrush(Microsoft.UI.Colors.White), wrap: false);
    readonly TextBlock _title = Ui.Text("", 20, true, new SolidColorBrush(Microsoft.UI.Colors.White), 1, false);
    readonly TextBlock _sub = Ui.Text("", 14, color: new SolidColorBrush(Microsoft.UI.Colors.White), maxLines: 1, wrap: false);
    readonly TextBlock _osd = new() { FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
    readonly Border _osdBox = new() { Background = Ui.Argb(170, 0, 0, 0), CornerRadius = new CornerRadius(12), Padding = new Thickness(26, 12, 26, 14), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly ProgressRing _busy = new() { IsActive = true, Width = 56, Height = 56, IsHitTestVisible = false };
    readonly Button _playBtn = Icon("", "Play / pause (Space)");
    readonly Button _skipIntro = Ui.Btn("Skip intro", () => { }, true);
    readonly StackPanel _extra = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 40, 150), Spacing = 8 };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    readonly DropDownButton _speedBtn = new(), _subBtn = new() { Content = "Subtitles" }, _audioBtn = new() { Content = "Audio" }, _aspectBtn = new() { Content = "Fit" }, _qualityBtn = new() { Content = "Version" };

    LibVLC? _vlc; MediaPlayer? _mp; JsonNode? _session;
    bool _settingSeek, _closed, _refreshed, _ended, _started; int _countdown = -1; float _speed;
    DateTime _lastMove = DateTime.Now, _lastReport = DateTime.MinValue, _lastSeek = DateTime.MinValue;
    long _pos, _len; string[] _swap = []; Action? _nextUpdate; DateTime _countdownAt;

    static Button Icon(string glyph, string tip)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 18 }, Width = 44, Height = 40, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        ToolTipService.SetToolTip(b, tip); return b;
    }

    /// <summary>
    /// One player lives for the whole session and is reused for every title. LibVLC's WinUI video view does not
    /// survive being torn down and rebuilt, so the view is created once and only ever stopped, never disposed.
    /// </summary>
    public PlayerPage()
    {
        _speed = 1;
        RequestedTheme = ElementTheme.Dark;
        BuildChrome();
        _root.Children.Add(_video); _root.Children.Add(_layer); _root.Children.Add(_busy); _root.Children.Add(_osdBox);
        _osdBox.Child = _osd;
        Content = _root;
        IsTabStop = true;

        _video.Initialized += (_, e) => { _swap = e.SwapChainOptions; _ready = true; if (Active) _ = Begin(); };
        _tick.Tick += (_, _) => Tick();
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); _osdBox.Visibility = Visibility.Collapsed; };
        _root.PointerMoved += (_, _) => Wake();
        _root.PreviewKeyDown += OnKey;
    }

    public void Start(long mediaId, long? episodeId, bool startOver, long? sourceId)
    {
        _mediaId = mediaId; _episodeId = episodeId; _startOver = startOver; _sourceId = sourceId;
        Active = true; _closed = false; _refreshed = false; _session = null; _pos = 0; _len = 0;
        _speed = (float)G.PrefNum("playbackSpeed", 1); BuildSpeedMenu();
        if (_errorPanel != null) { _root.Children.Remove(_errorPanel); _errorPanel = null; }
        _title.Text = ""; _sub.Text = ""; _time.Text = "0:00 / 0:00"; _busy.Visibility = Visibility.Visible;
        _playBtn.Focus(FocusState.Programmatic);
        if (_ready) _ = Begin();   // otherwise playback begins as soon as the video surface reports ready
    }

    // ---- UI -------------------------------------------------------------------------------
    void BuildChrome()
    {
        // Click on the picture toggles playback; double-click toggles full screen.
        var hit = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        hit.Tapped += (_, _) => Toggle();
        hit.DoubleTapped += (_, _) => { G.Window.SetFullScreen(!G.Window.IsFullScreen); };
        _layer.Children.Add(hit);

        var top = new Grid { VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(28, 18, 28, 50), Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1), GradientStops = { new GradientStop { Color = Windows.UI.Color.FromArgb(210, 0, 0, 0), Offset = 0 }, new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 1 } } } };
        var back = Icon("", "Back (Esc)"); back.Click += (_, _) => Exit();
        top.Children.Add(Ui.Row(12, back, Ui.Col(0, _title, _sub)));

        var prev = Icon("", "Previous episode"); prev.Click += (_, _) => GoEpisode(_session.O("previous"));
        var next = Icon("", "Next episode (N)"); next.Click += (_, _) => GoEpisode(_session.O("next"));
        var rew = Icon("", "Back (←)"); rew.Click += (_, _) => SeekBy(-SeekStep);
        var fwd = Icon("", "Forward (→)"); fwd.Click += (_, _) => SeekBy(SeekStep);
        var restart = Icon("", "Restart"); restart.Click += (_, _) => { if (_mp != null) { _mp.Time = 0; Osd("Restarted"); } };
        var mute = Icon("", "Mute (M)"); mute.Click += (_, _) => ToggleMute();
        var pip = Icon("", "Picture-in-picture"); pip.Click += (_, _) => G.Window.SetCompact(!G.Window.IsCompact);
        var full = Icon("", "Full screen (F)"); full.Click += (_, _) => G.Window.SetFullScreen(!G.Window.IsFullScreen);
        _playBtn.Click += (_, _) => Toggle();
        _vol.Value = double.TryParse(Local.Get("volume", "100"), out var v) ? v : 100;
        _vol.ValueChanged += (_, e) => { if (_mp != null) { _mp.Volume = (int)e.NewValue; Local.Set("volume", ((int)e.NewValue).ToString()); } };
        _seek.ValueChanged += (_, e) =>
        {
            if (_settingSeek || _mp == null) return;
            if ((DateTime.Now - _lastSeek).TotalMilliseconds < 120) return;   // scrubbing sends a stream of values
            _lastSeek = DateTime.Now; _mp.Time = (long)(e.NewValue * 1000); _pos = _mp.Time; Wake();
        };
        foreach (var d in new[] { _speedBtn, _subBtn, _audioBtn, _aspectBtn, _qualityBtn }) { d.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White); d.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); d.BorderThickness = new Thickness(0); d.VerticalAlignment = VerticalAlignment.Center; }
        BuildSpeedMenu(); BuildAspectMenu();

        var left = Ui.Row(2, _playBtn, rew, fwd, prev, next, restart, _time.With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(10, 0, 0, 0); }));
        var right = Ui.Row(2, mute, _vol, _speedBtn, _subBtn, _audioBtn, _aspectBtn, _qualityBtn, pip, full).With(r => r.HorizontalAlignment = HorizontalAlignment.Right);
        var buttons = new Grid(); buttons.Children.Add(left); buttons.Children.Add(right);
        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(28, 60, 28, 18), Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1), GradientStops = { new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0 }, new GradientStop { Color = Windows.UI.Color.FromArgb(230, 0, 0, 0), Offset = 1 } } } };
        bottom.Children.Add(_seek); bottom.Children.Add(buttons);

        _chrome.Children.Add(top); _chrome.Children.Add(bottom);
        _layer.Children.Add(_chrome);
        _skipIntro.Visibility = Visibility.Collapsed;
        _skipIntro.Click += (_, _) => { var end = _session.O("markers").L("introEndMs"); if (_mp != null && end > 0) { _mp.Time = end; Osd("Intro skipped"); } };
        _extra.Children.Add(_skipIntro);
        _layer.Children.Add(_extra);
    }

    void BuildSpeedMenu()
    {
        var m = new MenuFlyout();
        foreach (var s in Speeds) { var i = new RadioMenuFlyoutItem { Text = $"{s}x", IsChecked = Math.Abs(s - _speed) < 0.01, GroupName = "speed" }; i.Click += (_, _) => SetSpeed(s); m.Items.Add(i); }
        _speedBtn.Flyout = m; _speedBtn.Content = $"{_speed}x";
        ToolTipService.SetToolTip(_speedBtn, "Playback speed ( [ and ] )");
    }

    void BuildAspectMenu()
    {
        var m = new MenuFlyout();
        foreach (var (label, ratio) in new (string, string?)[] { ("Fit", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("1:1", "1:1") })
        { var i = new MenuFlyoutItem { Text = label }; i.Click += (_, _) => { if (_mp != null) { _mp.AspectRatio = ratio; _aspectBtn.Content = label; } }; m.Items.Add(i); }
        _aspectBtn.Flyout = m; ToolTipService.SetToolTip(_aspectBtn, "Aspect ratio");
    }

    void BuildTrackMenus()
    {
        if (_mp == null) return;
        var subs = new MenuFlyout();
        foreach (var t in _mp.SpuDescription) { var id = t.Id; var i = new RadioMenuFlyoutItem { Text = id == -1 ? "Off" : t.Name, IsChecked = _mp.Spu == id, GroupName = "spu" }; i.Click += (_, _) => { _mp?.SetSpu(id); }; subs.Items.Add(i); }
        if (subs.Items.Count == 0) subs.Items.Add(new MenuFlyoutItem { Text = "No subtitles in this file", IsEnabled = false });
        _subBtn.Flyout = subs;
        var audio = new MenuFlyout();
        foreach (var t in _mp.AudioTrackDescription) { var id = t.Id; if (id == -1) continue; var i = new RadioMenuFlyoutItem { Text = t.Name, IsChecked = _mp.AudioTrack == id, GroupName = "audio" }; i.Click += (_, _) => { _mp?.SetAudioTrack(id); }; audio.Items.Add(i); }
        if (audio.Items.Count == 0) audio.Items.Add(new MenuFlyoutItem { Text = "One audio track", IsEnabled = false });
        _audioBtn.Flyout = audio;
    }

    // ---- playback -------------------------------------------------------------------------
    long SeekStep => (long)G.PrefNum("seekSeconds", 10) * 1000;

    async Task Begin()
    {
        if (_closed) return;
        try
        {
            _busy.Visibility = Visibility.Visible; _ended = false; _started = false; _countdown = -1; _extra.Children.Clear(); _extra.Children.Add(_skipIntro);
            var restart = _startOver || G.PrefStr("resumeBehaviour", "resume") == "restart";
            _session = await G.Hub.Post("/playback/sessions", new { profileId = G.Pid, mediaId = _mediaId, episodeId = _episodeId, startOver = restart, sourceId = _sourceId });
            if (_closed) return;
            var s = _session; var ep = s.O("episode");
            _title.Text = s.S("title") ?? ""; _sub.Text = ep == null ? (s.S("quality") ?? "") : $"S{ep.L("season")} E{ep.L("episode")}  ·  {ep.S("title")}";
            _episodeId = ep == null ? null : ep.L("episodeId");

            var alts = s.A("alternatives");
            var qm = new MenuFlyout();
            foreach (var a in alts) { var sid = a.L("id"); var i = new RadioMenuFlyoutItem { Text = $"{(a.S("kind") == "local" ? "Local" : "Debrid")}  ·  {a.S("quality") ?? "SD"}  ·  {a.S("container")?.ToUpperInvariant()}", IsChecked = sid == s.L("sourceId"), GroupName = "q" }; i.Click += (_, _) => { Report("stop"); _sourceId = sid; _startOver = false; StopPlayback(); _ = Begin(); }; qm.Items.Add(i); }
            _qualityBtn.Flyout = qm; _qualityBtn.Visibility = alts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            if (_vlc == null)
            {
                Core.Initialize();
                var size = G.PrefStr("subtitleSize", "medium") switch { "small" => "80", "large" => "150", _ => "100" };
                var opts = _swap.Concat(new[] { "--sub-text-scale=" + size, "--audio-language=" + G.PrefStr("audioLanguage", "en"), "--sub-language=" + (G.PrefStr("subtitleLanguage", "off") is var sl && sl != "off" ? sl : "none"), "--freetype-color=" + (G.PrefStr("subtitleColor", "white") == "yellow" ? "16776960" : "16777215"), "--freetype-background-opacity=" + (G.PrefBool("subtitleBackground", false) ? "140" : "0") }).ToArray();
                try { _vlc = new LibVLC(false, opts); } catch { _vlc = new LibVLC(false, _swap); }   // an option this build does not know must not stop playback
                _mp = new MediaPlayer(_vlc) { Volume = (int)_vol.Value };
                _video.MediaPlayer = _mp;
                var q = DispatcherQueue;
                _mp.Playing += (_, _) => q.TryEnqueue(OnPlaying);
                _mp.Paused += (_, _) => q.TryEnqueue(() => { _playBtn.Content = new FontIcon { Glyph = "", FontSize = 18 }; Report("pause"); });
                _mp.Buffering += (_, e) => { var done = e.Cache >= 100; q.TryEnqueue(() => _busy.Visibility = done ? Visibility.Collapsed : Visibility.Visible); };
                _mp.EndReached += (_, _) => q.TryEnqueue(OnEnded);
                _mp.EncounteredError += (_, _) => q.TryEnqueue(OnError);
            }
            Open(s.L("resumePositionMs"), false);
            _tick.Start();
            Wake();
        }
        catch (Exception e) { ShowError(e.Message); }
    }

    void Open(long startMs, bool refresh)
    {
        if (_vlc == null || _mp == null || _session == null) return;
        var url = G.Hub.Base + _session.S("streamUrl") + (refresh ? "?refresh=1" : "");
        using var media = new Media(_vlc, new Uri(url));
        if (startMs > 0) media.AddOption($":start-time={startMs / 1000.0:0.###}");
        media.AddOption(":network-caching=1500");
        _mp.Play(media);
        _mp.SetRate(_speed);
    }

    void OnPlaying()
    {
        _busy.Visibility = Visibility.Collapsed;
        _playBtn.Content = new FontIcon { Glyph = "", FontSize = 18 };
        if (_started || _mp == null || _session == null) return;
        _started = true;
        // Side-loaded subtitle files next to the video are attached once playback is running.
        foreach (var sub in _session.A("subtitles")) _mp.AddSlave(MediaSlaveType.Subtitle, G.Hub.Base + sub.S("url"), G.PrefStr("subtitleLanguage", "off") != "off" && sub.S("language") == G.PrefStr("subtitleLanguage", "off"));
        if (G.PrefStr("subtitleLanguage", "off") == "off") _mp.SetSpu(-1);
        _ = Task.Delay(1200).ContinueWith(_ => DispatcherQueue.TryEnqueue(BuildTrackMenus));
    }

    void OnEnded()
    {
        if (_ended) return;
        _ended = true; Report("ended");
        var next = _session.O("next");
        if (next == null) { Exit(false); return; }
        if (G.PrefBool("autoplayNext", true)) { _countdown = 10; ShowNextCard(next); }
        else ShowNextCard(next);
    }

    void ShowNextCard(JsonNode next)
    {
        _extra.Children.Clear();
        var label = Ui.Text("", 13, color: new SolidColorBrush(Microsoft.UI.Colors.White));
        void Update() => label.Text = _countdown >= 0 ? $"Next episode in {_countdown}" : "Up next";
        Update(); _nextUpdate = Update; _countdownAt = DateTime.Now;
        var card = new Border { Background = Ui.Argb(240, 23, 23, 27), CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Width = 360,
            Child = Ui.Col(8, label, Ui.Text($"S{next.L("season")} E{next.L("episode")}  ·  {next.S("title")}", 17, true, new SolidColorBrush(Microsoft.UI.Colors.White), 2),
                Ui.Row(8, Ui.Btn("Play now", () => GoEpisode(next), true), Ui.Btn("Cancel", () => { _countdown = -1; if (_ended) Exit(false); else _extra.Children.Clear(); }))) };
        _extra.Children.Add(card);
        Wake();
    }

    void OnError()
    {
        if (_closed) return;
        // Provider links expire: ask the hub for a fresh one once before giving up.
        if (_session.S("kind") == "debrid" && !_refreshed) { _refreshed = true; Open(_pos, true); return; }
        Report("pause");
        ShowError($"This file could not be played ({_session.S("container")?.ToUpperInvariant()}). If it is a network or provider problem, try again; your position is saved.");
    }

    void ShowError(string message)
    {
        _busy.Visibility = Visibility.Collapsed;
        if (_errorPanel != null) _root.Children.Remove(_errorPanel);
        var panel = new Grid { Background = Ui.Argb(235, 0, 0, 0) }; _errorPanel = panel;
        panel.Children.Add(Ui.Col(10, Ui.Text("Can't play this title", 22, true, new SolidColorBrush(Microsoft.UI.Colors.White)).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            Ui.Text(message, 14, color: Ui.Argb(255, 190, 190, 196)).With(t => { t.MaxWidth = 560; t.TextAlignment = TextAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Center; }),
            Ui.Row(10, Ui.Btn("Try again", () => { _root.Children.Remove(panel); _refreshed = false; if (_session != null && _mp != null) { _started = false; Open(_pos, _session.S("kind") == "debrid"); } else _ = Begin(); }, true), Ui.Btn("Close", () => Exit(false))).With(r => r.HorizontalAlignment = HorizontalAlignment.Center)).With(c => c.VerticalAlignment = VerticalAlignment.Center));
        _root.Children.Add(panel);
    }

    void Tick()
    {
        if (_mp == null || _closed) return;
        if (_mp.Length > 0) _len = _mp.Length;
        if (!_ended && _mp.Time >= 0 && (_mp.IsPlaying || _mp.Time > 0)) _pos = _mp.Time;
        _settingSeek = true; _seek.Maximum = Math.Max(1, _len / 1000.0); _seek.Value = _pos / 1000.0; _settingSeek = false;
        _time.Text = $"{Ui.Time(_pos)} / {Ui.Time(_len)}";
        if (_mp.IsPlaying && (DateTime.Now - _lastReport).TotalSeconds >= 10) Report("tick");

        var m = _session.O("markers");
        var inIntro = m.L("introEndMs") > 0 && _pos >= m.L("introStartMs") && _pos < m.L("introEndMs");
        _skipIntro.Visibility = inIntro ? Visibility.Visible : Visibility.Collapsed;
        // Credits marker: offer the next episode early, with the same countdown.
        if (!_ended && _countdown < 0 && _extra.Children.Count == 1 && m.L("creditsStartMs") > 0 && _pos >= m.L("creditsStartMs") && _session.O("next") is { } nx) { if (G.PrefBool("autoplayNext", true)) _countdown = 15; ShowNextCard(nx); }

        if (_countdown >= 0 && (DateTime.Now - _countdownAt).TotalSeconds >= 1)
        {
            _countdown--; _countdownAt = DateTime.Now;
            _nextUpdate?.Invoke();
            if (_countdown < 0 && _session.O("next") is { } n) { GoEpisode(n); return; }
        }
        // Controls fade away while playing and the mouse is still.
        var idle = (DateTime.Now - _lastMove).TotalSeconds > 3 && _mp.IsPlaying && _extra.Children.Count <= 1;
        _chrome.Opacity = idle ? 0 : 1; _chrome.IsHitTestVisible = !idle;
    }

    void Wake() { _lastMove = DateTime.Now; _chrome.Opacity = 1; _chrome.IsHitTestVisible = true; }

    void Osd(string text) { _osd.Text = text; _osdBox.Visibility = Visibility.Visible; _osdTimer.Stop(); _osdTimer.Start(); }
    void Toggle() { if (_mp == null) return; if (_mp.IsPlaying) { _mp.SetPause(true); Osd("Paused"); } else { _mp.SetPause(false); Osd("Play"); } Wake(); }
    void SeekBy(long delta) { if (_mp == null) return; var t = Math.Clamp(_mp.Time + delta, 0, Math.Max(0, _len - 1000)); _mp.Time = t; _pos = t; Osd((delta > 0 ? "+" : "−") + Math.Abs(delta / 1000) + "s"); Wake(); }
    void SetSpeed(float s) { _speed = s; _mp?.SetRate(s); _speedBtn.Content = $"{s}x"; BuildSpeedMenu(); Osd($"{s}x"); }
    void ToggleMute() { if (_mp == null) return; _mp.Mute = !_mp.Mute; Osd(_mp.Mute ? "Muted" : "Sound on"); }
    void Volume(int delta) { _vol.Value = Math.Clamp(_vol.Value + delta, 0, 100); Osd($"Volume {(int)_vol.Value}%"); }

    void GoEpisode(JsonNode? target)
    {
        if (target == null) return;
        if (!_ended) Report("stop");
        _episodeId = target.L("episodeId"); _startOver = false; _sourceId = null; _refreshed = false;
        StopPlayback(); _ = Begin();
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (_mp == null) { if (e.Key == VirtualKey.Escape) { Exit(); e.Handled = true; } return; }
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Space: Toggle(); break;
            case VirtualKey.Left: SeekBy(-SeekStep); break;
            case VirtualKey.Right: SeekBy(SeekStep); break;
            case VirtualKey.Up: Volume(5); break;
            case VirtualKey.Down: Volume(-5); break;
            case VirtualKey.M: ToggleMute(); break;
            case VirtualKey.F: G.Window.SetFullScreen(!G.Window.IsFullScreen); break;
            case VirtualKey.N: GoEpisode(_session.O("next")); break;
            case VirtualKey.Escape: if (G.Window.IsFullScreen) G.Window.SetFullScreen(false); else if (G.Window.IsCompact) G.Window.SetCompact(false); else Exit(); break;
            case (VirtualKey)219: SetSpeed(Speeds[Math.Max(0, Array.FindIndex(Speeds, s => Math.Abs(s - _speed) < 0.01) - 1)]); break;                       // [
            case (VirtualKey)221: SetSpeed(Speeds[Math.Min(Speeds.Length - 1, Array.FindIndex(Speeds, s => Math.Abs(s - _speed) < 0.01) + 1)]); break;        // ]
            case >= VirtualKey.Number0 and <= VirtualKey.Number9: if (_len > 0) { _mp.Time = _len * (e.Key - VirtualKey.Number0) / 10; Osd($"{(e.Key - VirtualKey.Number0) * 10}%"); } break;
            case >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9: if (_len > 0) { _mp.Time = _len * (e.Key - VirtualKey.NumberPad0) / 10; Osd($"{(e.Key - VirtualKey.NumberPad0) * 10}%"); } break;
            default: e.Handled = false; break;
        }
    }

    // ---- progress & shutdown ----------------------------------------------------------------
    void Report(string ev)
    {
        if (_session == null || (_len <= 0 && ev != "ended")) return;
        _lastReport = DateTime.Now;
        var body = new { mediaId = _mediaId, episodeId = _session.O("episode") is { } ep ? ep.L("episodeId") : (long?)null, positionMs = ev == "ended" ? _len : _pos, durationMs = _len, sessionId = _session.S("sessionId"), @event = ev };
        var pid = G.Pid;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await G.Hub.Put($"/profiles/{pid}/progress", body);
                if (r.O("otherSession").S("deviceName") is { } other) DispatcherQueue.TryEnqueue(() => { if (!_closed) _sub.Text = $"Also playing on {other}. The most recent position wins."; });
            }
            catch { }
        });
    }

    void StopPlayback()
    {
        var mp = _mp; if (mp == null) return;
        _tick.Stop();
        // LibVLC must not be stopped from its own callback thread or the UI thread while it is rendering.
        Task.Run(() => { try { mp.Stop(); } catch { } }).Wait(3000);
    }

    void Exit(bool report = true)
    {
        if (_closed) return;
        G.Window.ClosePlayer();
    }

    /// <summary>Saves the position and stops playback. The view and LibVLC stay alive for the next title.</summary>
    public async Task CloseAsync()
    {
        if (!Active) return;
        if (!_ended && _started) Report("stop");
        Active = false; _closed = true; _tick.Stop(); _osdTimer.Stop(); _countdown = -1; _extra.Children.Clear();
        if (G.Window.IsFullScreen) G.Window.SetFullScreen(false);
        if (G.Window.IsCompact) G.Window.SetCompact(false);
        var mp = _mp;
        if (mp != null) { try { await Task.Run(() => mp.Stop()); } catch { } }
        _busy.Visibility = Visibility.Collapsed;
    }

    /// <summary>Last-moment save when the whole window is closing.</summary>
    public void SaveNow() { if (Active && !_ended && _started) Report("stop"); }
}
