using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DJFlix.Desktop;

public sealed class DetailsPage(long id) : PageBase
{
    int _season = -1;

    protected override async Task<UIElement> BuildAsync()
    {
        var d = await G.Hub.Get($"/media/{id}?profileId={G.Pid}");
        var c = d.O("card"); bool show = c.S("type") == "show"; bool available = d.S("availability") == "available";
        var upNext = d.O("upNext"); var prog = c.O("progress");
        bool inProgress = prog.L("positionMs") > 0 && !prog.B("isCompleted");
        async Task Do(Func<Task> a) { await a(); Refresh(); }

        // ---- header ---------------------------------------------------------------------------
        var head = new Grid { MinHeight = 430 };
        head.Children.Add(Ui.Img(d.S("backdrop")).With(i => { i.HorizontalAlignment = HorizontalAlignment.Right; i.VerticalAlignment = VerticalAlignment.Top; i.Height = 430; i.Opacity = 0.75; }));
        head.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0), GradientStops = { new GradientStop { Color = Ui.Bg.Color, Offset = 0.25 }, new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0.95 } } } });
        head.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1), GradientStops = { new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0.5 }, new GradientStop { Color = Ui.Bg.Color, Offset = 1 } } } });

        var playLabel = show && upNext != null ? $"{(upNext.O("progress").L("positionMs") > 0 ? "▶  Resume" : "▶  Play")}  S{upNext.L("season")} E{upNext.L("episode")}"
            : inProgress ? $"▶  Resume from {Ui.Time(prog.L("positionMs"))}" : "▶  Play";
        var actions = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
        void Act(UIElement? e) { if (e is FrameworkElement f) { f.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(f); } }
        if (available) Act(Ui.Btn(playLabel, () => G.Window.Play(id, show ? upNext?.L("episodeId") : null), true));
        if (available && !show && inProgress) Act(Ui.Btn("Play from beginning", () => G.Window.Play(id, null, true)));
        if (d.O("trailer") != null) Act(Ui.Btn("Watch trailer", () => Ui.OpenTrailer(d.O("trailer"))));
        Act(Ui.BtnAsync(c.B("inWatchlist") ? "✓  My List" : "+  My List", () => Ui.ToggleList(c!, Refresh)));
        Act(Ui.BtnAsync(c.B("isFavourite") ? "♥  Favourite" : "♡  Favourite", () => Do(() => c.B("isFavourite") ? G.Hub.Delete($"/profiles/{G.Pid}/favourites/{id}") : G.Hub.Put($"/profiles/{G.Pid}/favourites/{id}"))));
        Act(Ui.BtnAsync(c.B("watched") ? "Mark unwatched" : "Mark watched", () => Do(() => G.Hub.Post($"/profiles/{G.Pid}/watched", new { mediaId = id, watched = !c.B("watched") }))));
        if (inProgress || (show && prog != null)) Act(Ui.BtnAsync("Reset progress", () => Do(() => G.Hub.Delete($"/profiles/{G.Pid}/progress/{id}"))));
        Act(Ui.BtnAsync("Add to playlist", AddToPlaylist));
        if (show && available) Act(Ui.BtnAsync("🎲  Random episode", async () => { var eps = (await G.Hub.Get($"/shows/{id}/episodes")).List().Where(e => e.B("available")).ToList(); if (eps.Count > 0) G.Window.Play(id, eps[Random.Shared.Next(eps.Count)].L("id"), true); }));

        var rating = new RatingControl { Caption = "Your rating", MaxRating = 5, IsClearEnabled = true, Value = c.O("userRating") is JsonValue ? c.D("userRating") : -1 };
        rating.ValueChanged += async (s, _) => { try { await G.Hub.Put($"/profiles/{G.Pid}/ratings/{id}", new { rating = s.Value < 1 ? 0 : (int)s.Value }); } catch (Exception e) { G.Window.Toast(e.Message, true); } };

        FrameworkElement? Line(string label, string? value) => string.IsNullOrEmpty(value) ? null : Ui.Text($"{label}:  {value}", 13, color: Ui.Dim, maxLines: 2);
        var status = available ? (d.S("metadataSource") == "TMDB" ? null : "Named from the file; no metadata match yet") : d.S("availability") == "missing" ? "The file for this title is missing from its folder or provider." : "Not currently playable.";
        var info = Ui.Col(8,
            new TextBlock { Text = c.S("title"), FontSize = 42, FontWeight = Microsoft.UI.Text.FontWeights.Black, Foreground = Ui.Fg, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2 },
            d.S("originalTitle") is { } ot && ot != c.S("title") ? Ui.Text(ot, 14, color: Ui.Faint) : null,
            Ui.Text(Ui.Meta(c) + (d.S("releaseDate") is { } rd ? "  ·  " + rd : ""), 15),
            Ui.Text(string.Join("  ·  ", c.Strings("genres")), 13, color: Ui.Dim),
            Ui.Text(c.S("overview") ?? "No description is available for this title.", 15, color: Ui.Dim, maxLines: 6),
            actions.M(0, 8, 0, 0), rating,
            Line(show ? "Created by" : "Director", string.Join(", ", d.Strings("directors"))),
            Line("Studios", string.Join(", ", d.Strings("studios"))),
            Line("Collection", d.S("collection")),
            status == null ? null : Ui.Text(status, 13, color: Ui.Accent),
            d.S("imdbUrl") is { } imdb ? new HyperlinkButton { Content = $"IMDb {d.S("imdbId")}", NavigateUri = new Uri(imdb), Padding = new Thickness(0) } : null);
        info.MaxWidth = 720; info.HorizontalAlignment = HorizontalAlignment.Left; info.Margin = new Thickness(40, 30, 40, 10);
        head.Children.Add(info);

        var col = new StackPanel();
        col.Children.Add(head);

        // ---- episodes -------------------------------------------------------------------------
        if (show)
        {
            var seasons = d.A("seasons");
            if (_season == -1) _season = (int)(upNext != null ? upNext.L("season") : seasons.FirstOrDefault().L("season"));
            col.Children.Add(Ui.Row(12,
                Ui.Combo(seasons.Select(s => (s.L("season").ToString(), $"{s.S("name") ?? "Season " + s.L("season")}  ({s.L("availableCount")}/{s.L("episodeCount")})")), _season.ToString(), v => { _season = int.Parse(v); Refresh(); }, "Season", 260),
                Ui.BtnAsync("Mark season watched", () => Do(() => G.Hub.Post($"/profiles/{G.Pid}/watched", new { mediaId = id, season = _season, watched = true }))).With(b => b.VerticalAlignment = VerticalAlignment.Bottom)).M(40, 6, 40, 8));
            foreach (var e in (await G.Hub.Get($"/shows/{id}/episodes?season={_season}&profileId={G.Pid}")).List()) col.Children.Add(Episode(e));
        }
        else if (G.Hub.Url("x") != null && d.A("sources").Count > 0)
        {
            col.Children.Add(Ui.Text("Files", 16, true).M(40, 10, 40, 4));
            foreach (var s in d.A("sources"))
                col.Children.Add(Ui.Row(10,
                    Ui.Text($"{(s.S("kind") == "local" ? "Local" : "Debrid")}  ·  {s.S("quality") ?? "SD"}  ·  {s.D("size") / 1073741824.0:0.##} GB  ·  {s.S("path") ?? s.S("fileName")}" + (s.B("isMissing") ? "   (missing)" : ""), 12, color: Ui.Faint, wrap: false),
                    s.B("isMissing") ? null : Ui.Btn("Play this file", () => G.Window.Play(id, null, false, s.L("id")))).M(40, 2, 40, 0));
        }

        // ---- cast, similar, tools ---------------------------------------------------------------
        var cast = d.A("cast");
        if (cast.Count > 0)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(40, 8, 40, 10) };
            foreach (var p in cast.Take(16))
            {
                var photo = new Grid { Width = 92, Height = 92, CornerRadius = new CornerRadius(46), Background = Ui.Panel };
                if (p.S("photo") != null) photo.Children.Add(Ui.Img(p.S("photo"), decodeWidth: 185)); else photo.Children.Add(Ui.Text((p.S("name") ?? "?")[..1], 28, true).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }));
                var b = new Button { Content = Ui.Col(4, photo, Ui.Text(p.S("name"), 12, true, maxLines: 1, wrap: false).With(t => { t.Width = 104; t.TextAlignment = TextAlignment.Center; }), Ui.Text(p.S("character"), 11, color: Ui.Faint, maxLines: 1, wrap: false).With(t => { t.Width = 104; t.TextAlignment = TextAlignment.Center; })), Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
                var name = p.S("name")!; b.Click += (_, _) => G.Window.Go(new PersonPage(name));
                ToolTipService.SetToolTip(b, $"More with {name}");
                strip.Children.Add(b);
            }
            col.Children.Add(Ui.Text("Cast", 18, true).M(40, 14, 0, 0));
            col.Children.Add(new ScrollViewer { Content = strip, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }
        if (d.A("similar").Count > 0) col.Children.Add(Ui.CardRow("More Like This", d.A("similar"), Refresh));
        col.Children.Add(Ui.Row(8, Ui.BtnAsync("Fix match…", () => FixMatch(c!)), Ui.BtnAsync("Edit details…", () => EditMeta(d!)), c.B("isMatched") ? Ui.BtnAsync("Refresh metadata", () => Do(() => G.Hub.Post($"/admin/media/{id}/refresh"))) : null,
            Ui.BtnAsync("Remove from library", async () => { if (await Ui.Confirm("Remove from library?", "The title is removed from DJFlix. Your files are not deleted.", "Remove")) { await G.Hub.Delete($"/admin/media/{id}"); G.Window.Back(); } })).M(40, 18, 40, 40));
        return col;
    }

    FrameworkElement Episode(JsonNode e)
    {
        bool available = e.B("available"); var p = e.O("progress"); var eid = e.L("id");
        var thumb = new Grid { Width = 200, Height = 112, CornerRadius = new CornerRadius(6), Background = Ui.Panel };
        if (e.S("still") != null) thumb.Children.Add(Ui.Img(e.S("still"), decodeWidth: 300));
        if (p.D("percent") > 0 && !e.B("watched")) thumb.Children.Add(new ProgressBar { Value = p.D("percent"), Maximum = 100, VerticalAlignment = VerticalAlignment.Bottom, Foreground = Ui.Accent });
        var text = Ui.Col(3,
            Ui.Text($"{e.L("episode")}.  {e.S("title") ?? "Episode " + e.L("episode")}{(e.B("watched") ? "   ✓" : "")}", 16, true, available ? null : Ui.Faint, 1, false),
            Ui.Text(string.Join("  ·  ", new[] { e.L("runtime") > 0 ? $"{e.L("runtime")} min" : null, e.S("airDate"), available ? null : "Not in library", p.L("positionMs") > 0 && !e.B("watched") ? $"{Ui.Time(p.L("positionMs"))} watched" : null }.Where(x => x != null)), 12, color: Ui.Faint),
            Ui.Text(e.S("overview"), 13, color: Ui.Dim, maxLines: 2));
        var g = new Grid { ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(thumb); g.Children.Add(text.InGrid(0, 1).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        var b = new Button { Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8), Margin = new Thickness(40, 2, 40, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10), IsEnabled = available };
        b.Click += (_, _) => G.Window.Play(id, eid);
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play from beginning" }; play.Click += (_, _) => G.Window.Play(id, eid, true); menu.Items.Add(play);
        var mark = new MenuFlyoutItem { Text = e.B("watched") ? "Mark as unwatched" : "Mark as watched" };
        mark.Click += async (_, _) => { try { await G.Hub.Post($"/profiles/{G.Pid}/watched", new { mediaId = id, episodeId = eid, watched = !e.B("watched") }); Refresh(); } catch (Exception ex) { G.Window.Toast(ex.Message, true); } };
        menu.Items.Add(mark);
        var intro = new MenuFlyoutItem { Text = "Set intro / credits markers…" }; intro.Click += async (_, _) => { try { await Markers(e); } catch (Exception ex) { G.Window.Toast(ex.Message, true); } }; menu.Items.Add(intro);
        b.ContextFlyout = menu;
        return b;
    }

    /// <summary>Intro and credits markers are entered by hand (mm:ss); they drive Skip Intro / Skip Credits in both players.</summary>
    async Task Markers(JsonNode e)
    {
        static string Fmt(JsonNode? m, string k) => m.O(k) is JsonValue ? Ui.Time(m.L(k)) : "";
        static long? Parse(string s) { var p = s.Trim().Split(':'); if (s.Trim().Length == 0) return null; try { return p.Length == 2 ? (long.Parse(p[0]) * 60 + long.Parse(p[1])) * 1000 : p.Length == 3 ? (long.Parse(p[0]) * 3600 + long.Parse(p[1]) * 60 + long.Parse(p[2])) * 1000 : long.Parse(p[0]) * 1000; } catch { return null; } }
        var m = e.O("markers");
        var a = new TextBox { Header = "Intro starts (m:ss)", Text = Fmt(m, "introStartMs") }; var b = new TextBox { Header = "Intro ends (m:ss)", Text = Fmt(m, "introEndMs") }; var cr = new TextBox { Header = "Credits start (m:ss)", Text = Fmt(m, "creditsStartMs") };
        if (!await Ui.Show("Intro and credits markers", Ui.Col(10, Ui.Text("Leave a box empty to remove that marker.", color: Ui.Dim), a, b, cr))) return;
        await G.Hub.Patch($"/admin/episodes/{e.L("id")}", new JsonObject { ["introStartMs"] = Parse(a.Text), ["introEndMs"] = Parse(b.Text), ["creditsStartMs"] = Parse(cr.Text) });
        Refresh();
    }

    async Task AddToPlaylist()
    {
        var lists = (await G.Hub.Get($"/profiles/{G.Pid}/playlists")).List();
        var pick = Ui.Combo(lists.Select(l => (l.L("id").ToString(), l.S("name") ?? "")).Append(("new", "New playlist…")), lists.Count > 0 ? lists[0].L("id").ToString() : "new", _ => { }, "Playlist", 300);
        var name = new TextBox { Header = "Name for a new playlist", Width = 300 };
        if (!await Ui.Show("Add to playlist", Ui.Col(10, pick, name), "Add")) return;
        var sel = (string)((ComboBoxItem)pick.SelectedItem).Tag; long pl; var ids = new List<long>();
        if (sel == "new")
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { G.Window.Toast("Type a name for the new playlist.", true); return; }
            pl = (await G.Hub.Post($"/profiles/{G.Pid}/playlists", new { name = name.Text.Trim() })).L("id");
        }
        else { pl = long.Parse(sel); ids = lists.First(l => l.L("id") == pl).A("items").Select(i => i.L("id")).ToList(); }
        if (!ids.Contains(id)) ids.Add(id);
        await G.Hub.Put($"/profiles/{G.Pid}/playlists/{pl}", new { mediaIds = ids });
        G.Window.Toast("Added to playlist");
    }

    async Task FixMatch(JsonNode c)
    {
        var type = c.S("type") ?? "movie";
        var q = new TextBox { Header = "Title to search for", Text = c.S("title") ?? "", Width = 420 };
        var year = new TextBox { Header = "Year (optional)", Text = c.L("year") > 0 ? c.L("year").ToString() : "", Width = 140 };
        var results = new ListView { Height = 300, SelectionMode = ListViewSelectionMode.Single };
        async Task Search()
        {
            results.Items.Clear();
            try
            {
                foreach (var h in (await G.Hub.Get($"/admin/metadata/search?type={type}&q={Uri.EscapeDataString(q.Text)}" + (int.TryParse(year.Text, out var y) ? "&year=" + y : ""))).List())
                    results.Items.Add(new ListViewItem { Tag = h.L("id"), Content = Ui.Row(10, new Border { Width = 40, Height = 60, Child = Ui.Img(h.S("poster"), decodeWidth: 92) }, Ui.Col(2, Ui.Text($"{h.S("title")}  ({h.L("year")})", 14, true), Ui.Text(h.S("overview"), 12, color: Ui.Dim, maxLines: 2).With(t => t.MaxWidth = 380))) });
                if (results.Items.Count == 0) results.Items.Add(new ListViewItem { Content = Ui.Text("No matches. Try a shorter title.", color: Ui.Dim), IsEnabled = false });
            }
            catch (Exception e) { results.Items.Add(new ListViewItem { Content = Ui.Text(e.Message, color: Ui.Accent), IsEnabled = false }); }
        }
        var go = Ui.BtnAsync("Search", Search).With(b => b.VerticalAlignment = VerticalAlignment.Bottom);
        await Search();
        if (!await Ui.Show("Fix match", Ui.Col(10, Ui.Row(10, q, year, go), results), "Use selected")) return;
        if (results.SelectedItem is ListViewItem { Tag: long tmdbId }) { await G.Hub.Post($"/admin/media/{id}/match", new { tmdbId }); G.Window.Toast("Match updated"); Refresh(); }
    }

    async Task EditMeta(JsonNode d)
    {
        var c = d.O("card");
        var title = new TextBox { Header = "Title", Text = c.S("title") ?? "" }; var year = new NumberBox { Header = "Year", Value = c.L("year") > 0 ? c.L("year") : double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var overview = new TextBox { Header = "Synopsis", Text = c.S("overview") ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
        var genres = new TextBox { Header = "Genres (comma separated)", Text = string.Join(", ", c.Strings("genres")) };
        var cert = new TextBox { Header = "Certification (G, PG, PG-13, R, TV-MA…)", Text = c.S("certification") ?? "" };
        var poster = new TextBox { Header = "Poster image URL (leave as is to keep)", PlaceholderText = "https://…" }; var backdrop = new TextBox { Header = "Backdrop image URL", PlaceholderText = "https://…" };
        var trailer = new TextBox { Header = "YouTube trailer ID", Text = d.O("trailer").S("key") ?? "" };
        if (!await Ui.Show("Edit details", new ScrollViewer { MaxHeight = 520, Content = Ui.Col(10, title, year, overview, genres, cert, poster, backdrop, trailer).With(x => { x.Width = 460; x.Padding = new Thickness(0, 0, 16, 0); }) })) return;
        var body = new JsonObject { ["title"] = title.Text.Trim(), ["overview"] = overview.Text, ["certification"] = cert.Text.Trim(), ["trailerKey"] = trailer.Text.Trim().Length == 0 ? null : trailer.Text.Trim(), ["genres"] = new JsonArray(genres.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()) };
        if (!double.IsNaN(year.Value)) body["year"] = (int)year.Value;
        if (poster.Text.Trim().StartsWith("http")) body["poster"] = poster.Text.Trim();
        if (backdrop.Text.Trim().StartsWith("http")) body["backdrop"] = backdrop.Text.Trim();
        await G.Hub.Patch($"/admin/media/{id}", body);
        G.Window.Toast("Details saved"); Refresh();
    }
}
