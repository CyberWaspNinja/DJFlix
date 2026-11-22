using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DJFlix.Desktop;

/// <summary>Base for content pages: loads asynchronously, shows a placeholder, an error state with retry, and keeps scroll position on refresh.</summary>
public abstract class PageBase : UserControl, IRefresh
{
    ScrollViewer? _sv; int _gen; bool _started;
    protected virtual bool Scrolls => true;
    protected abstract Task<UIElement> BuildAsync();
    protected PageBase() { Loaded += (_, _) => { if (!_started) { _started = true; Refresh(); } }; }

    public async void Refresh()
    {
        var gen = ++_gen; var offset = _sv?.VerticalOffset ?? 0;
        if (Content == null) Content = Ui.Skeleton();
        try
        {
            var ui = await BuildAsync();
            if (gen != _gen) return;
            if (!Scrolls) { Content = ui; return; }
            _sv = new ScrollViewer { Content = ui, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled };
            Content = _sv;
            if (offset > 0) { _sv.UpdateLayout(); _sv.ChangeView(null, offset, null, true); }
        }
        catch (Exception e) { if (gen == _gen) Content = Ui.Error(e, Refresh); }
    }
}

// ============================= profiles =======================================================
public sealed class ProfilesPage : UserControl
{
    static readonly string[] Avatars = ["", "🎬", "🍿", "🚀", "🦊", "🐼", "🎮", "🌟", "🐯", "🦄", "👾", "🎧"];
    static readonly string[] Colors = ["#E50914", "#2E86DE", "#27AE60", "#F39C12", "#8E44AD", "#16A085", "#D35400", "#C2185B"];
    bool _manage;

    public ProfilesPage() { Content = new Grid { Background = Ui.Bg, Children = { Ui.Loading() } }; _ = Load(); }

    async Task Load()
    {
        try
        {
            var profiles = (await G.Hub.Get("/profiles")).List();
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var p in profiles) strip.Children.Add(Tile(p));
            if (profiles.Count < 12) strip.Children.Add(TileShell("+", Ui.Argb(255, 42, 42, 48), "Add profile", false, () => _ = Edit(null)));
            var col = Ui.Col(18,
                new TextBlock { Text = "DJFLIX", FontSize = 34, FontWeight = Microsoft.UI.Text.FontWeights.Black, Foreground = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Center },
                Ui.H1(_manage ? "Manage profiles" : "Who's watching?").With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
                new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto, Padding = new Thickness(20) },
                Ui.Row(10, Ui.Btn(_manage ? "Done" : "Manage profiles", () => { _manage = !_manage; _ = Load(); }), Ui.Btn("Server status", () => { if (G.Profile == null && profiles.Count > 0) G.Window.SetProfile(profiles[0]); G.Window.Root("server"); G.Window.CloseOverlay(); })).With(r => r.HorizontalAlignment = HorizontalAlignment.Center));
            col.VerticalAlignment = VerticalAlignment.Center;
            Content = new Grid { Background = Ui.Bg, Children = { col } };
        }
        catch (Exception e) { Content = new Grid { Background = Ui.Bg, Children = { Ui.Empty("Couldn't load profiles", e.Message, Ui.Btn("Try again", () => _ = Load(), true)) } }; }
    }

    FrameworkElement Tile(JsonNode p)
    {
        var avatar = p.S("avatar"); var symbol = Avatars.Contains(avatar) && avatar != "" ? avatar! : (p.S("name") ?? "?")[..1].ToUpperInvariant();
        return TileShell(symbol, Ui.Hex(p.S("color") ?? Colors[0]), (p.S("name") ?? "") + (p.B("hasPin") ? "  🔒" : ""), _manage, () => _ = _manage ? Edit(p) : Select(p));
    }

    static FrameworkElement TileShell(string symbol, Brush color, string label, bool editing, Action click)
    {
        var box = new Grid { Width = 130, Height = 130, CornerRadius = new CornerRadius(14), Background = color };
        box.Children.Add(new TextBlock { Text = symbol, FontSize = 54, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (editing) box.Children.Add(new Border { Background = Ui.Argb(150, 0, 0, 0), Child = new SymbolIcon(Symbol.Edit) { Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } });
        var b = new Button { Content = Ui.Col(10, box, Ui.Text(label, 15, color: Ui.Dim, wrap: false).With(t => t.HorizontalAlignment = HorizontalAlignment.Center)), Padding = new Thickness(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(14) };
        b.Click += (_, _) => click();
        return b;
    }

    async Task Select(JsonNode p)
    {
        try
        {
            if (p.B("hasPin"))
            {
                var pin = await Ui.Prompt($"Enter the PIN for {p.S("name")}", "This profile is protected.", secret: true, ok: "Unlock");
                if (pin == null) return;
                if (!(await G.Hub.Post($"/profiles/{p.L("id")}/verify-pin", new { pin })).B("ok")) { G.Window.Toast("That PIN is not correct.", true); return; }
            }
            G.Window.SetProfile(p);
        }
        catch (Exception e) { G.Window.Toast(e.Message, true); }
    }

    async Task Edit(JsonNode? p)
    {
        try
        {
            var prefs = p.O("prefs");
            var name = new TextBox { Header = "Name", Text = p.S("name") ?? "", MaxLength = 24 };
            var avatar = Ui.Combo(Avatars.Select(a => (a, a == "" ? "First letter of the name" : a)), p.S("avatar") ?? "", _ => { }, "Avatar", 220);
            var color = Ui.Combo(Colors.Select((c, i) => (c, new[] { "Red", "Blue", "Green", "Amber", "Purple", "Teal", "Orange", "Pink" }[i])), p.S("color") ?? Colors[0], _ => { }, "Colour", 220);
            var maturity = Ui.Combo([("1", "Little kids (G)"), ("2", "Older kids (PG)"), ("3", "Teens (PG-13)"), ("4", "Adults (R)"), ("5", "No restriction")], (p == null ? 5 : p.L("maxMaturity")).ToString(), _ => { }, "Maturity limit", 220);
            var unrated = new ToggleSwitch { Header = "Show unrated titles on a restricted profile", IsOn = prefs.B("allowUnrated") };
            var lang = Ui.Combo([("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("it", "Italian"), ("pt", "Portuguese"), ("ar", "Arabic"), ("hi", "Hindi"), ("ml", "Malayalam")], p.S("language") ?? "en", _ => { }, "Language", 220);
            var pin = new PasswordBox { Header = p.B("hasPin") ? "New PIN (leave empty to keep the current one)" : "PIN (optional, 4–8 digits)", MaxLength = 8 };
            var removePin = new CheckBox { Content = "Remove the PIN", Visibility = p.B("hasPin") ? Visibility.Visible : Visibility.Collapsed };
            var genres = (await G.Hub.Get("/genres")).List().Select(g => g.S("name")!).ToList();
            var fav = prefs.Strings("favouriteGenres");
            var genreBoxes = genres.Select(g => new CheckBox { Content = g, IsChecked = fav.Contains(g), MinWidth = 150 }).ToList();
            var genreGrid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 3 };
            foreach (var g in genreBoxes) genreGrid.Children.Add(g);
            var delete = p == null ? null : new CheckBox { Content = "Delete this profile and all of its history", Foreground = Ui.Accent };
            var form = new ScrollViewer { MaxHeight = 520, Content = Ui.Col(12, name, Ui.Row(12, avatar, color), Ui.Row(12, maturity, lang), unrated, pin, removePin, genres.Count > 0 ? Ui.Text("Favourite genres", 14, true) : null, genreGrid, delete).With(c => c.Padding = new Thickness(0, 0, 16, 0)) };
            if (!await Ui.Show(p == null ? "New profile" : "Edit profile", form)) return;

            if (delete?.IsChecked == true)
            {
                if (!await Ui.Confirm("Delete profile?", $"\"{p.S("name")}\" and its watch history, list and ratings will be removed. This cannot be undone.", "Delete")) return;
                await G.Hub.Delete($"/profiles/{p!.L("id")}");
                if (G.Profile.L("id") == p.L("id")) G.Profile = null;
            }
            else
            {
                string Sel(ComboBox c) => (string)((ComboBoxItem)c.SelectedItem).Tag;
                var body = new JsonObject
                {
                    ["name"] = name.Text.Trim(), ["avatar"] = Sel(avatar), ["color"] = Sel(color), ["maxMaturity"] = int.Parse(Sel(maturity)), ["language"] = Sel(lang),
                    ["prefs"] = new JsonObject { ["allowUnrated"] = unrated.IsOn, ["favouriteGenres"] = new JsonArray(genreBoxes.Where(g => g.IsChecked == true).Select(g => (JsonNode)JsonValue.Create((string)g.Content)!).ToArray()) },
                };
                if (removePin.IsChecked == true) body["pin"] = ""; else if (pin.Password.Length > 0) body["pin"] = pin.Password;
                var saved = p == null ? await G.Hub.Post("/profiles", body) : await G.Hub.Patch($"/profiles/{p.L("id")}", body);
                if (p != null && G.Profile.L("id") == p.L("id")) G.Profile = saved;
            }
            await Load();
        }
        catch (Exception e) { G.Window.Toast(e.Message, true); }
    }
}

// ============================= home ===========================================================
public sealed class HomePage : PageBase
{
    protected override async Task<UIElement> BuildAsync()
    {
        var home = await G.Hub.Get($"/home?profileId={G.Pid}");
        var hero = home.O("hero"); var rows = home.A("rows");
        if (hero == null && rows.Count == 0)
            return Ui.Empty("Your library is empty", "Add a folder of your own media or connect a debrid account, and titles will appear here once the scan finishes.",
                Ui.Btn("Add media folders", () => G.Window.Root("settings"), true), Ui.Btn("Server status", () => G.Window.Root("server")));
        var col = new StackPanel();
        if (hero != null) col.Children.Add(Hero(hero));
        foreach (var r in rows) col.Children.Add(Ui.CardRow(r.S("title") ?? "", r.A("items"), Refresh));
        col.Children.Add(Ui.Row(8, Ui.BtnAsync("Customise home rows", () => Customise(home))).M(40, 16, 40, 40));
        return col;
    }

    FrameworkElement Hero(JsonNode hero)
    {
        var c = hero.O("card"); var id = c.L("id");
        var g = new Grid { Height = 460 };
        g.Children.Add(Ui.Img(hero.S("backdrop")).With(i => { i.HorizontalAlignment = HorizontalAlignment.Right; i.Opacity = 0.9; }));
        g.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0), GradientStops = { new GradientStop { Color = Ui.Bg.Color, Offset = 0 }, new GradientStop { Color = Ui.Bg.Color, Offset = 0.3 }, new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0.85 } } } });
        g.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1), GradientStops = { new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0.55 }, new GradientStop { Color = Ui.Bg.Color, Offset = 1 } } } });
        var inProgress = c.O("progress").L("positionMs") > 0 && !c.B("watched");
        var info = Ui.Col(10,
            new TextBlock { Text = c.S("title"), FontSize = 46, FontWeight = Microsoft.UI.Text.FontWeights.Black, Foreground = Ui.Fg, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2 },
            Ui.Text(Ui.Meta(c) + (c.Strings("genres").Count > 0 ? "  ·  " + string.Join(", ", c.Strings("genres").Take(3)) : ""), 15),
            Ui.Text(hero.S("overview"), 15, color: Ui.Dim, maxLines: 3),
            Ui.Row(10,
                c.B("available") ? Ui.Btn(inProgress ? "▶  Resume" : "▶  Play", () => G.Window.Play(id), true) : null,
                Ui.Btn("ⓘ  More Info", () => G.Window.Go(new DetailsPage(id))),
                Ui.BtnAsync(c.B("inWatchlist") ? "✓  My List" : "+  My List", () => Ui.ToggleList(c!, Refresh)),
                hero.O("trailer") != null ? Ui.Btn("Trailer", () => Ui.OpenTrailer(hero.O("trailer"))) : null).M(0, 8, 0, 0));
        info.MaxWidth = 620; info.HorizontalAlignment = HorizontalAlignment.Left; info.VerticalAlignment = VerticalAlignment.Bottom; info.Margin = new Thickness(40, 0, 0, 34);
        g.Children.Add(info);
        return g;
    }

    /// <summary>Lets the profile hide rows and change their order.</summary>
    async Task Customise(JsonNode? home)
    {
        var names = new Dictionary<string, string> { ["continue"] = "Continue Watching", ["mylist"] = "My List", ["recent"] = "Recently Added", ["trending"] = "Trending", ["recommended"] = "Recommended For You", ["because"] = "Because You Watched…", ["popular_movies"] = "Popular Movies", ["popular_shows"] = "Popular TV Shows", ["top_rated"] = "Top Rated", ["action"] = "Action & Adventure", ["comedy"] = "Comedy", ["scifi"] = "Sci-Fi", ["thriller"] = "Thriller", ["family"] = "Family", ["hidden_gems"] = "Hidden Gems", ["recently_watched"] = "Recently Watched" };
        var saved = G.Prefs.A("homeRows");
        var order = saved.Select(r => (id: r.S("id") ?? "", on: r.O("enabled") is not JsonValue v || !v.TryGetValue<bool>(out var b) || b)).Where(r => names.ContainsKey(r.id)).ToList();
        foreach (var id in home.Strings("rowCatalog")) if (names.ContainsKey(id) && order.All(o => o.id != id)) order.Add((id, true));
        var list = new StackPanel { Spacing = 4 };
        void Render()
        {
            list.Children.Clear();
            for (int i = 0; i < order.Count; i++)
            {
                int idx = i; var (id, on) = order[i];
                var sw = new ToggleSwitch { IsOn = on, OnContent = names[id], OffContent = names[id], MinWidth = 260 };
                sw.Toggled += (_, _) => order[idx] = (id, sw.IsOn);
                list.Children.Add(Ui.Row(6, sw, Ui.Btn("↑", () => { if (idx > 0) { (order[idx - 1], order[idx]) = (order[idx], order[idx - 1]); Render(); } }), Ui.Btn("↓", () => { if (idx < order.Count - 1) { (order[idx + 1], order[idx]) = (order[idx], order[idx + 1]); Render(); } })));
            }
        }
        Render();
        if (!await Ui.Show("Customise home", new ScrollViewer { Content = list, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) })) return;
        await G.SavePrefs(new { homeRows = order.Select(o => new { id = o.id, enabled = o.on }) });
        Refresh();
    }
}

// ============================= movies / shows =================================================
public sealed class BrowsePage(string type) : PageBase
{
    string _genre = "", _year = "", _rating = "", _sort = Local.Get("sort." + type, "popularity"), _watched = "all", _layout = Local.Get("layout." + type, "grid"), _q = "";
    int _pages = 1;
    string Path => type == "movie" ? "/movies" : "/shows";

    protected override async Task<UIElement> BuildAsync()
    {
        var genres = (await G.Hub.Get($"/genres?type={type}&profileId={G.Pid}")).List();
        var query = $"{Path}?profileId={G.Pid}&sort={_sort}&pageSize={60 * _pages}" + (_genre != "" ? "&genre=" + Uri.EscapeDataString(_genre) : "") + (_year != "" ? "&year=" + _year : "") + (_rating != "" ? "&minRating=" + _rating : "")
            + (_q != "" ? "&q=" + Uri.EscapeDataString(_q) : "") + _watched switch { "watched" => "&watched=true", "unwatched" => "&watched=false", "mylist" => "&watchlist=true", _ => "" };
        var res = await G.Hub.Get(query);
        var items = res.A("items");
        void Set(Action a) { a(); _pages = 1; Refresh(); }

        var search = new TextBox { PlaceholderText = type == "movie" ? "Search movies" : "Search TV shows", Text = _q, Width = 200, VerticalAlignment = VerticalAlignment.Bottom };
        search.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Set(() => _q = search.Text.Trim()); };
        var bar = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemHeight = 62, Margin = new Thickness(40, 0, 40, 0) };
        void AddBar(FrameworkElement e) { e.Margin = new Thickness(0, 0, 10, 0); e.VerticalAlignment = VerticalAlignment.Bottom; VariableSizedWrapGrid.SetColumnSpan(e, 1); bar.Children.Add(e); }
        bar.ItemWidth = 184;
        AddBar(Ui.Combo(new[] { ("", "All genres") }.Concat(genres.Select(g => (g.S("name")!, $"{g.S("name")} ({g.L("count")})"))), _genre, v => Set(() => _genre = v), "Genre"));
        AddBar(Ui.Combo(new[] { ("", "Any year") }.Concat(res.A("years").Select(y => (y.ToString(), y.ToString()))), _year, v => Set(() => _year = v), "Year"));
        AddBar(Ui.Combo([("", "Any rating"), ("8", "8+ ★"), ("7", "7+ ★"), ("6", "6+ ★"), ("5", "5+ ★")], _rating, v => Set(() => _rating = v), "Rating"));
        AddBar(Ui.Combo([("popularity", "Popularity"), ("title", "Title"), ("year", "Release year"), ("added", "Date added"), ("rating", "Rating")], _sort, v => Set(() => { _sort = v; Local.Set("sort." + type, v); }), "Sort by"));
        AddBar(Ui.Combo([("all", "Everything"), ("unwatched", "Unwatched"), ("watched", "Watched"), ("mylist", "On My List")], _watched, v => Set(() => _watched = v), "Show"));
        AddBar(Ui.Combo([("grid", "Grid"), ("rows", "Carousels")], _layout, v => Set(() => { _layout = v; Local.Set("layout." + type, v); }), "Layout"));
        AddBar(search); VariableSizedWrapGrid.SetColumnSpan(search, 2);

        var col = new StackPanel();
        col.Children.Add(Ui.Row(12, Ui.H1(type == "movie" ? "Movies" : "TV Shows"), Ui.Text($"{res.L("total")} titles", 14, color: Ui.Faint).With(t => t.VerticalAlignment = VerticalAlignment.Bottom).M(0, 0, 0, 6)).M(40, 10, 40, 6));
        col.Children.Add(bar);
        if (items.Count == 0) { col.Children.Add(Ui.Empty(type == "movie" ? "No movies match these filters" : "No TV shows match these filters", "Change or clear the filters to see more.", Ui.Btn("Clear filters", () => Set(() => { _genre = _year = _rating = _q = ""; _watched = "all"; }), true))); return col; }

        if (_layout == "rows" && _genre == "" && _q == "")
        {
            // Carousel layout: a featured strip, then one row per genre.
            col.Children.Add(Ui.CardRow("Featured", items.Take(20), Refresh));
            col.Children.Add(Ui.CardRow("Recently Added", items.OrderByDescending(i => i.L("addedAt")).Take(20), Refresh));
            col.Children.Add(Ui.CardRow("Top Rated", items.OrderByDescending(i => i.D("rating")).Take(20), Refresh));
            foreach (var g in genres.OrderByDescending(g => g.L("count")).Take(8))
            {
                var inGenre = items.Where(i => i.Strings("genres").Contains(g.S("name")!)).ToList();
                if (inGenre.Count > 0) col.Children.Add(Ui.CardRow(g.S("name")!, inGenre, Refresh));
            }
        }
        else
        {
            col.Children.Add(Ui.PosterGrid(items, Refresh));
            if (items.Count < res.L("total")) col.Children.Add(Ui.Btn($"Show more ({res.L("total") - items.Count} left)", () => { _pages++; Refresh(); }).With(b => b.HorizontalAlignment = HorizontalAlignment.Center).M(0, 0, 0, 40));
        }
        return col;
    }
}

public sealed class MyListPage : PageBase
{
    string _type = "all", _sort = "added", _which = "watchlist";
    protected override async Task<UIElement> BuildAsync()
    {
        var items = (await G.Hub.Get($"/profiles/{G.Pid}/{_which}?type={_type}&sort={_sort}")).List();
        var col = new StackPanel();
        col.Children.Add(Ui.H1(_which == "watchlist" ? "My List" : "Favourites").M(40, 10, 40, 6));
        col.Children.Add(Ui.Row(10,
            Ui.Combo([("watchlist", "My List"), ("favourites", "Favourites")], _which, v => { _which = v; Refresh(); }, "List"),
            Ui.Combo([("all", "Movies and TV"), ("movie", "Movies"), ("show", "TV Shows")], _type, v => { _type = v; Refresh(); }, "Show"),
            Ui.Combo([("added", "Recently added"), ("title", "Title"), ("year", "Release year"), ("rating", "Rating")], _sort, v => { _sort = v; Refresh(); }, "Sort by")).M(40, 0, 40, 0));
        col.Children.Add(items.Count == 0
            ? Ui.Empty("Nothing here yet", "Use \"+ My List\" on any title, or right-click a poster. Your list follows you to the Fire TV.", Ui.Btn("Browse movies", () => G.Window.Root("movies"), true))
            : Ui.PosterGrid(items, Refresh));
        return col;
    }
}

public sealed class CollectionsPage : PageBase
{
    protected override async Task<UIElement> BuildAsync()
    {
        var collections = (await G.Hub.Get($"/collections?profileId={G.Pid}")).List();
        var playlists = (await G.Hub.Get($"/profiles/{G.Pid}/playlists")).List();
        var col = new StackPanel();
        col.Children.Add(Ui.Row(12, Ui.H1("Collections & playlists"), Ui.BtnAsync("New playlist", async () =>
        {
            var name = await Ui.Prompt("New playlist", "Name", ok: "Create");
            if (!string.IsNullOrWhiteSpace(name)) { await G.Hub.Post($"/profiles/{G.Pid}/playlists", new { name }); Refresh(); }
        }).With(b => b.VerticalAlignment = VerticalAlignment.Bottom)).M(40, 10, 40, 0));
        foreach (var p in playlists)
        {
            var id = p.L("id"); var items = p.A("items");
            col.Children.Add(Ui.Row(10, Ui.Text("Playlist: " + p.S("name"), 18, true),
                Ui.BtnAsync("Rename", async () => { var n = await Ui.Prompt("Rename playlist", "Name", p.S("name") ?? ""); if (!string.IsNullOrWhiteSpace(n)) { await G.Hub.Put($"/profiles/{G.Pid}/playlists/{id}", new { name = n }); Refresh(); } }),
                Ui.BtnAsync("Delete", async () => { if (await Ui.Confirm("Delete playlist?", $"\"{p.S("name")}\" will be removed. The titles stay in your library.", "Delete")) { await G.Hub.Delete($"/profiles/{G.Pid}/playlists/{id}"); Refresh(); } })).M(40, 18, 40, 0));
            if (items.Count == 0) col.Children.Add(Ui.Text("Empty. Open a title and choose \"Add to playlist\".", color: Ui.Faint).M(40, 6, 40, 0));
            else
            {
                // Drag a poster to reorder within the playlist.
                var strip = new GridView { CanReorderItems = true, CanDragItems = true, AllowDrop = true, SelectionMode = ListViewSelectionMode.None, Margin = new Thickness(36, 4, 36, 0) };
                foreach (var c in items) strip.Items.Add(new ContentControl { Content = Ui.Poster(c, Refresh, 120), Tag = c.L("id") });
                strip.DragItemsCompleted += async (_, _) => { try { await G.Hub.Put($"/profiles/{G.Pid}/playlists/{id}", new { mediaIds = strip.Items.Select(i => (long)((ContentControl)i).Tag) }); } catch (Exception e) { G.Window.Toast(e.Message, true); } };
                col.Children.Add(strip);
            }
        }
        foreach (var c in collections) col.Children.Add(Ui.CardRow($"{c.S("name")}  ·  {c.L("count")}", c.A("items"), Refresh));
        if (collections.Count == 0 && playlists.Count == 0) col.Children.Add(Ui.Empty("No collections yet", "Film series are grouped here automatically once their titles are matched. You can also create your own playlists."));
        return col;
    }
}

public sealed class StatsPage : PageBase
{
    static FrameworkElement Stat(string value, string label) => Ui.Card(Ui.Col(2, Ui.Text(value, 26, true), Ui.Text(label, 13, color: Ui.Dim))).With(b => b.MinWidth = 170);
    static FrameworkElement Bars(IEnumerable<(string label, double value, string text)> rows)
    {
        var list = rows.ToList(); var max = Math.Max(1, list.Count > 0 ? list.Max(r => r.value) : 1);
        var g = new StackPanel { Spacing = 6 };
        foreach (var (label, value, text) in list)
            g.Children.Add(Ui.Row(10, Ui.Text(label, 13, wrap: false).With(t => t.Width = 150), new Border { Width = Math.Max(3, 360 * value / max), Height = 14, CornerRadius = new CornerRadius(3), Background = Ui.Accent, VerticalAlignment = VerticalAlignment.Center }, Ui.Text(text, 12, color: Ui.Dim)));
        return g;
    }

    protected override async Task<UIElement> BuildAsync()
    {
        var mine = await G.Hub.Get($"/profiles/{G.Pid}/stats"); var lib = await G.Hub.Get("/admin/library/stats");
        var hours = mine.D("watchTimeMs") / 3600000.0;
        var col = new StackPanel { Margin = new Thickness(40, 10, 40, 40) };
        col.Children.Add(Ui.H1("Statistics"));
        col.Children.Add(Ui.H2($"{G.Profile.S("name")}'s viewing"));
        col.Children.Add(Ui.Row(12, Stat($"{hours:0.#} h", "Watch time"), Stat(mine.L("moviesCompleted").ToString(), "Movies finished"), Stat(mine.L("episodesCompleted").ToString(), "Episodes finished"), Stat(mine.L("watchlistCount").ToString(), "On My List"), Stat(mine.L("ratingsCount").ToString(), "Titles rated")));
        if (mine.A("genres").Count > 0) { col.Children.Add(Ui.H2("Watch time by genre")); col.Children.Add(Bars(mine.A("genres").Take(10).Select(g => (g.S("name")!, g.D("watchTimeMs"), $"{g.D("watchTimeMs") / 3600000.0:0.#} h")))); }
        if (mine.A("recentActivity").Count > 0) { col.Children.Add(Ui.H2("Recent activity")); foreach (var a in mine.A("recentActivity")) col.Children.Add(Ui.Text($"{DateTimeOffset.FromUnixTimeMilliseconds(a.L("watchedAt")).LocalDateTime:g}   {a.S("title")}{(a.B("isCompleted") ? "  ✓" : "")}", 13, color: Ui.Dim)); }
        col.Children.Add(Ui.H2("Library"));
        col.Children.Add(Ui.Row(12, Stat(lib.L("movies").ToString(), "Movies"), Stat(lib.L("shows").ToString(), "TV shows"), Stat(lib.L("episodes").ToString(), "Episodes"), Stat($"{lib.D("localBytes") / 1073741824.0:0.#} GB", "Local files"), Stat(lib.L("debridFiles").ToString(), "Debrid files"), Stat(lib.L("unmatched").ToString(), "Unmatched")));
        if (lib.A("genres").Count > 0) { col.Children.Add(Ui.H2("Titles by genre")); col.Children.Add(Bars(lib.A("genres").Take(12).Select(g => (g.S("name")!, g.D("count"), g.L("count").ToString())))); }
        if (lib.A("byDecade").Count > 0) { col.Children.Add(Ui.H2("Titles by decade")); col.Children.Add(Bars(lib.A("byDecade").Select(g => ($"{g.L("decade")}s", g.D("count"), g.L("count").ToString())))); }
        return col;
    }
}

// ============================= search =========================================================
public sealed class SearchPage(string initial) : PageBase
{
    string _q = initial, _type = "all";
    public void Query(string q, bool submitted) { _q = q; Refresh(); if (submitted && q.Length > 0) _ = Remember(q); }
    static async Task Remember(string q) { try { await G.Hub.Post($"/profiles/{G.Pid}/search-history", new { query = q }); } catch { } }

    protected override async Task<UIElement> BuildAsync()
    {
        var col = new StackPanel();
        col.Children.Add(Ui.Row(14, Ui.H1(_q.Length == 0 ? "Search" : $"Results for \"{_q}\""), Ui.Combo([("all", "All"), ("movie", "Movies"), ("show", "TV Shows")], _type, v => { _type = v; Refresh(); }, width: 140).With(c => c.VerticalAlignment = VerticalAlignment.Bottom)).M(40, 10, 40, 6));
        if (_q.Length == 0)
        {
            var recent = (await G.Hub.Get($"/profiles/{G.Pid}/search-history")).List().Select(x => x.ToString()).ToList();
            if (recent.Count == 0) { col.Children.Add(Ui.Empty("Find something to watch", "Type in the search box at the top. You can search by title, actor, director, genre or collection.")); return col; }
            col.Children.Add(Ui.Text("Recent searches", 16, true).M(40, 10, 40, 6));
            var chips = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, Margin = new Thickness(40, 0, 40, 0) };
            foreach (var r in recent) chips.Children.Add(Ui.Btn(r, () => Query(r, false)).M(0, 0, 8, 8));
            col.Children.Add(chips);
            col.Children.Add(Ui.BtnAsync("Clear search history", async () => { await G.Hub.Delete($"/profiles/{G.Pid}/search-history"); Refresh(); }).M(40, 10, 40, 0));
            return col;
        }
        var res = await G.Hub.Get($"/search?q={Uri.EscapeDataString(_q)}&type={_type}&profileId={G.Pid}");
        var people = res.A("people");
        if (people.Count > 0)
        {
            var chips = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, Margin = new Thickness(40, 4, 40, 0) };
            foreach (var p in people) chips.Children.Add(Ui.Btn($"{p.S("name")}  ·  {p.S("role")}  ·  {p.L("titles")}", () => G.Window.Go(new PersonPage(p.S("name")!))).M(0, 0, 8, 8));
            col.Children.Add(chips);
        }
        foreach (var c in res.Strings("collections")) col.Children.Add(Ui.Text("Collection: " + c, 13, color: Ui.Dim).M(40, 2, 40, 0));
        var results = res.A("results");
        col.Children.Add(results.Count == 0
            ? Ui.Empty($"No results for \"{_q}\"", "Check the spelling, or try an actor, a director or a genre.")
            : Ui.PosterGrid(results, Refresh));
        var asked = _q;   // remember a query once the user has paused on it
        _ = Task.Delay(2500).ContinueWith(t => { if (asked.Length > 1 && asked == _q) return Remember(asked); return Task.CompletedTask; });
        return col;
    }
}

public sealed class PersonPage(string name) : PageBase
{
    protected override async Task<UIElement> BuildAsync()
    {
        var res = await G.Hub.Get($"/people/{Uri.EscapeDataString(name)}?profileId={G.Pid}");
        var col = new StackPanel();
        col.Children.Add(Ui.H1(name).M(40, 10, 40, 0));
        if (res.A("asDirector").Count > 0) col.Children.Add(Ui.CardRow($"More from this director", res.A("asDirector"), Refresh));
        if (res.A("asActor").Count > 0) col.Children.Add(Ui.CardRow($"More from this actor", res.A("asActor"), Refresh));
        if (res.A("asDirector").Count + res.A("asActor").Count == 0) col.Children.Add(Ui.Empty("Nothing else yet", $"No other titles with {name} are in your library."));
        return col;
    }
}

/// <summary>Plays a trailer from its official YouTube page inside the app; Back returns to where you were.</summary>
public sealed class TrailerPage : UserControl
{
    public TrailerPage(string url)
    {
        var web = new WebView2 { Source = new Uri(url) };
        var g = new Grid(); g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); g.RowDefinitions.Add(new RowDefinition());
        g.Children.Add(Ui.Row(10, Ui.Btn("←  Back", () => G.Window.Back()), Ui.Text("Trailer", 16, true).With(t => t.VerticalAlignment = VerticalAlignment.Center)).M(40, 8, 40, 8));
        g.Children.Add(web.InGrid(1, 0));
        Content = g;
        Unloaded += (_, _) => { try { web.Close(); } catch { } };
    }
}
