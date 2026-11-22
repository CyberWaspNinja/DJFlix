using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DJFlix.Desktop;

/// <summary>Application shell: collapsible sidebar, global search, back stack and toasts.</summary>
public sealed class MainWindow : Window
{
    readonly Grid _root = new();
    readonly NavigationView _nav = new();
    readonly ContentPresenter _host = new();
    readonly Grid _overlay = new();           // full-window pages (profiles, player, wizard)
    readonly InfoBar _toast = new() { IsOpen = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 28), MaxWidth = 640 };
    readonly AutoSuggestBox _search = new() { PlaceholderText = "Search titles, actors, directors, genres  (Ctrl+K)", QueryIcon = new SymbolIcon(Symbol.Find), Width = 380 };
    readonly Stack<UIElement> _stack = new();
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    readonly Button _profileBtn = new() { CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 6, 12, 6) };
    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);
    public bool PlayerOpen => _overlay.Children.OfType<PlayerPage>().Any();

    public MainWindow()
    {
        Title = "DJFlix";
        try
        {
            // Fit the work area of the current display; small screens get a maximised-size window instead of an oversized one.
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int w = Math.Min(1440, area.Width), h = Math.Min(900, area.Height);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
            var tb = AppWindow.TitleBar; var dark = Ui.Dark;
            var bg = dark ? Windows.UI.Color.FromArgb(255, 11, 11, 13) : Windows.UI.Color.FromArgb(255, 245, 245, 247); var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            tb.BackgroundColor = bg; tb.InactiveBackgroundColor = bg; tb.ButtonBackgroundColor = bg; tb.ButtonInactiveBackgroundColor = bg;
            tb.ForegroundColor = fg; tb.ButtonForegroundColor = fg; tb.InactiveForegroundColor = fg;
        }
        catch { }
        _root.Background = Ui.Bg;
        _root.RequestedTheme = Ui.Dark ? ElementTheme.Dark : ElementTheme.Light;
        Content = _root;

        _nav.IsSettingsVisible = false; _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible; _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.OpenPaneLength = 210; _nav.IsPaneOpen = Local.Get("pane", "open") == "open"; _nav.PaneTitle = "DJFLIX"; _nav.AlwaysShowHeader = false;
        NavigationViewItem Item(string label, Symbol icon, string tag) => new() { Content = label, Icon = new SymbolIcon(icon), Tag = tag };
        _nav.MenuItems.Add(Item("Home", Symbol.Home, "home"));
        _nav.MenuItems.Add(Item("Movies", Symbol.Video, "movies"));
        _nav.MenuItems.Add(Item("TV Shows", Symbol.SlideShow, "shows"));
        _nav.MenuItems.Add(Item("My List", Symbol.Favorite, "mylist"));
        _nav.MenuItems.Add(Item("Collections", Symbol.Library, "collections"));
        _nav.MenuItems.Add(Item("Statistics", Symbol.ThreeBars, "stats"));
        _nav.FooterMenuItems.Add(Item("Server", Symbol.MapDrive, "server"));
        _nav.FooterMenuItems.Add(Item("Settings", Symbol.Setting, "settings"));
        _nav.ItemInvoked += (_, e) => { if (e.InvokedItemContainer?.Tag is string tag) Root(tag); };
        _nav.BackRequested += (_, _) => Back();
        _nav.PaneClosed += (_, _) => Local.Set("pane", "closed"); _nav.PaneOpened += (_, _) => Local.Set("pane", "open");

        var header = new Grid { Padding = new Thickness(40, 14, 40, 6) };
        header.Children.Add(_search.With(s => s.HorizontalAlignment = HorizontalAlignment.Left));
        var random = Ui.Btn("🎲  Surprise me", async () => { try { var r = await G.Hub.Get($"/random?type=all&profileId={G.Pid}&unwatched=true"); Go(new DetailsPage(r.O("card").L("id"))); } catch (Exception e) { Toast(e.Message, true); } }, tip: "Pick a random unwatched title");
        _profileBtn.Click += (_, _) => ShowProfiles();
        header.Children.Add(Ui.Row(8, random, _profileBtn).With(r => r.HorizontalAlignment = HorizontalAlignment.Right));
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); body.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(header); body.Children.Add(_host.InGrid(1, 0));
        _nav.Content = body;

        _search.TextChanged += (_, e) => { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { _searchTimer.Stop(); _searchTimer.Start(); } };
        _search.QuerySubmitted += (_, _) => RunSearch(true);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(false); };

        _root.Children.Add(_nav); _root.Children.Add(_overlay); _root.Children.Add(_toast);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.IsOpen = false; };

        // Global shortcuts: Ctrl+K search, Alt+Left / Backspace-free back via mouse button and Esc.
        var find = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        find.Invoked += (_, e) => { e.Handled = true; _search.Focus(FocusState.Programmatic); };
        _nav.KeyboardAccelerators.Add(find);
        var back = new KeyboardAccelerator { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu };
        back.Invoked += (_, e) => { e.Handled = true; Back(); };
        _nav.KeyboardAccelerators.Add(back);

        // Leaving with a video open: save the position, give the request a moment, then end the process outright
        // rather than tearing the video pipeline down mid-frame.
        Closed += (_, _) => { var playing = _overlay.Children.OfType<PlayerPage>().ToList(); foreach (var p in playing) p.SaveNow(); if (playing.Count > 0) Thread.Sleep(600); HubHost.OnAppExit(); if (playing.Count > 0) Environment.Exit(0); };
        _ = Startup();
    }

    async Task Startup()
    {
        _overlay.Children.Add(new Grid { Background = Ui.Bg, Children = { Ui.Col(14, new TextBlock { Text = "DJFLIX", FontSize = 44, FontWeight = Microsoft.UI.Text.FontWeights.Black, Foreground = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Center }, Ui.Loading(), Ui.Text("Starting the DJFlix service…", color: Ui.Dim).With(t => t.HorizontalAlignment = HorizontalAlignment.Center)).With(c => c.VerticalAlignment = VerticalAlignment.Center) } });
        G.Hub.Configure();
        var (ok, msg) = await HubHost.Start();
        G.Hub.Configure();
        _overlay.Children.Clear();
        if (!ok) { _overlay.Children.Add(new Grid { Background = Ui.Bg, Children = { Ui.Empty("DJFlix can't start its service", msg, Ui.BtnAsync("Try again", async () => { _overlay.Children.Clear(); await Startup(); }, true)) } }); return; }
        try
        {
            var setup = await G.Hub.Get("/admin/setup");
            if (!setup.B("completed")) { ShowOverlay(new SetupWizard(() => { _overlay.Children.Clear(); ShowProfiles(); })); return; }
        }
        catch (Exception e) { Toast(e.Message, true); }
        ShowProfiles();
        if (Environment.GetEnvironmentVariable("DJFLIX_AUTOPILOT") == "1") _ = Autopilot.Run();
    }

    // ---- navigation ------------------------------------------------------------------------
    async Task ClearOverlay() { foreach (var p in _overlay.Children.OfType<PlayerPage>().ToList()) await p.ShutdownAsync(); _overlay.Children.Clear(); }
    public async void ShowOverlay(UIElement page) { await ClearOverlay(); _overlay.Children.Add(page); }
    public async void CloseOverlay() { await ClearOverlay(); if (_host.Content is IRefresh r) r.Refresh(); }
    public void ShowProfiles() => ShowOverlay(new ProfilesPage());

    public void SetProfile(JsonNode p)
    {
        G.Profile = p; Local.Set("profile", p.L("id").ToString());
        _profileBtn.Content = Ui.Row(8, new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = Ui.Hex(p.S("color") ?? "#E50914"), Child = Ui.Text((p.S("name") ?? "?")[..1].ToUpperInvariant(), 12, true, new SolidColorBrush(Microsoft.UI.Colors.White)).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }) }, Ui.Text(p.S("name"), 13, wrap: false));
        ToolTipService.SetToolTip(_profileBtn, "Switch profile");
        _overlay.Children.Clear();
        Root("home");
    }

    public void Root(string tag)
    {
        _stack.Clear();
        foreach (var o in _nav.MenuItems.Concat(_nav.FooterMenuItems)) if (o is NavigationViewItem i && (string)i.Tag == tag) _nav.SelectedItem = i;
        Show(tag switch
        {
            "movies" => new BrowsePage("movie"), "shows" => new BrowsePage("show"), "mylist" => new MyListPage(), "collections" => new CollectionsPage(),
            "stats" => new StatsPage(), "server" => new ServerPage(), "settings" => new SettingsPage(), _ => new HomePage(),
        });
    }

    void Show(UIElement page) { _stack.Push(page); _host.Content = page; _nav.IsBackEnabled = _stack.Count > 1; }
    public void Go(UIElement page) { if (page is ServerPage) { Root("server"); return; } Show(page); }

    public void Back()
    {
        if (_stack.Count <= 1) return;
        _stack.Pop(); var page = _stack.Peek();
        _host.Content = page; _nav.IsBackEnabled = _stack.Count > 1;
        if (page is IRefresh r) r.Refresh();
    }

    void RunSearch(bool submitted)
    {
        if (G.Profile == null) return;
        var q = _search.Text.Trim();
        if (_host.Content is SearchPage sp) sp.Query(q, submitted);
        else if (q.Length > 0) Show(new SearchPage(q));
    }

    public void Play(long mediaId, long? episodeId = null, bool startOver = false, long? sourceId = null) => ShowOverlay(new PlayerPage(mediaId, episodeId, startOver, sourceId));

    public void Toast(string message, bool error = false)
    {
        _toast.Message = message; _toast.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; _toast.IsClosable = error; _toast.IsOpen = true;
        _toastTimer.Stop(); _toastTimer.Interval = TimeSpan.FromSeconds(error ? 7 : 3.5); _toastTimer.Start();
    }

    public void SetFullScreen(bool on) { try { AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default); } catch { } }
    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
    public void SetCompact(bool on) { try { AppWindow.SetPresenter(on ? AppWindowPresenterKind.CompactOverlay : AppWindowPresenterKind.Default); } catch { } }
    public bool IsCompact => AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;
}

/// <summary>Scripted walk through the app for the CI screenshot run (DJFLIX_AUTOPILOT=1).</summary>
public static class Autopilot
{
    public static async Task Run()
    {
        try
        {
            await Task.Delay(6000);
            var profiles = (await G.Hub.Get("/profiles")).List();
            if (profiles.Count == 0) return;
            G.Window.SetProfile(profiles[0]); await Task.Delay(9000);
            G.Window.Root("movies"); await Task.Delay(7000);
            var first = (await G.Hub.Get($"/movies?profileId={G.Pid}&sort=title")).A("items").FirstOrDefault();
            if (first == null) return;
            G.Window.Go(new DetailsPage(first.L("id"))); await Task.Delay(7000);
            G.Window.Play(first.L("id")); await Task.Delay(24000);
            G.Window.CloseOverlay(); await Task.Delay(1000);
            G.Window.Root("server"); await Task.Delay(8000);
            G.Window.Root("settings"); await Task.Delay(8000);
            G.Window.Root("home"); await Task.Delay(6000);
            Local.Log("autopilot finished");
        }
        catch (Exception e) { Local.Log("autopilot failed: " + e); }
    }
}
