using System.Text.Json;
using System.Text.Json.Nodes;

namespace DJFlix;

public sealed record Person(string Name, string? Character, string? Photo);

public sealed class MediaItem
{
    public long Id; public string Type = "movie"; public long? TmdbId; public string? ImdbId;
    public string Title = ""; public string? OriginalTitle; public int? Year; public string? ReleaseDate; public string? Overview;
    public string[] Genres = []; public Person[] Cast = []; public string[] Directors = []; public string[] Studios = []; public string[] Keywords = [];
    public double Rating; public long Votes; public double Popularity; public int? Runtime; public string? Certification; public int Maturity;
    public string? Poster; public string? Backdrop; public string? TrailerKey; public int? SeasonsCount; public string? Collection;
    public bool IsMatched; public bool IsManual; public long AddedAt;
    public int SourceCount; public int MissingCount;
    public bool Available => SourceCount > 0;
    public bool IsMovie => Type == "movie";
}

public sealed class EpisodeItem
{
    public long Id; public long MediaId; public int Season; public int Episode; public string? Title; public string? Overview; public string? Still;
    public int? Runtime; public string? AirDate; public long? IntroStart, IntroEnd, CreditsStart; public bool Available;
}

/// <summary>Per-profile state loaded once per request; keeps profile data isolated.</summary>
public sealed class ProfileState
{
    public long Id; public int MaxMaturity = 5; public string[] FavGenres = []; public JsonObject Prefs = new();
    public HashSet<long> Watchlist = []; public HashSet<long> Favourites = []; public Dictionary<long, int> Ratings = [];
    public Dictionary<long, List<Row>> Progress = [];   // mediaId → progress rows (one per episode; episode_id 0 for movies)
    /// <summary>Restricted profiles do not see unrated titles unless the profile opts in (allowUnrated).</summary>
    public bool Allows(MediaItem m) => MaxMaturity >= 5 || (m.Maturity == 0 ? PrefBool("allowUnrated", false) : m.Maturity <= MaxMaturity);
    public bool PrefBool(string k, bool def) => Prefs[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
}

/// <summary>
/// In-memory view of the library used for browsing, search and recommendations.
/// Rebuilt lazily after any change; a home library fits comfortably in memory and this
/// keeps every list/search request off the disk.
/// </summary>
public sealed class Catalog(Hub hub)
{
    readonly object _gate = new();
    List<MediaItem>? _items; Dictionary<long, MediaItem> _byId = []; Dictionary<long, List<EpisodeItem>> _eps = []; Dictionary<long, EpisodeItem> _epById = [];
    public void Invalidate() { lock (_gate) _items = null; }

    void Ensure()
    {
        lock (_gate)
        {
            if (_items != null) return;
            var db = hub.Db;
            var counts = db.Query("SELECT media_id, SUM(CASE WHEN is_missing=0 THEN 1 ELSE 0 END) ok, SUM(is_missing) miss FROM sources GROUP BY media_id").ToDictionary(r => r.L("media_id"));
            var epAvail = db.Query("SELECT DISTINCT episode_id FROM sources WHERE is_missing=0 AND episode_id IS NOT NULL").Select(r => r.L("episode_id")).ToHashSet();
            var items = new List<MediaItem>();
            foreach (var r in db.Query("SELECT * FROM media"))
            {
                counts.TryGetValue(r.L("id"), out var c);
                items.Add(new MediaItem
                {
                    Id = r.L("id"), Type = r.S("type"), TmdbId = r.LN("tmdb_id"), ImdbId = r.SN("imdb_id"), Title = r.S("title"), OriginalTitle = r.SN("original_title"),
                    Year = (int?)r.LN("year"), ReleaseDate = r.SN("release_date"), Overview = r.SN("overview"), Genres = J.StrArr(r.SN("genres_json")),
                    Cast = ParseCast(r.SN("cast_json")), Directors = J.StrArr(r.SN("directors_json")), Studios = J.StrArr(r.SN("studios_json")), Keywords = J.StrArr(r.SN("keywords_json")),
                    Rating = r.D("rating"), Votes = r.L("votes"), Popularity = r.D("popularity"), Runtime = (int?)r.LN("runtime"), Certification = r.SN("certification"), Maturity = (int)r.L("maturity"),
                    Poster = r.SN("poster"), Backdrop = r.SN("backdrop"), TrailerKey = r.SN("trailer_key"), SeasonsCount = (int?)r.LN("seasons_count"), Collection = r.SN("collection_name"),
                    IsMatched = r.B("is_matched"), IsManual = r.B("is_manual"), AddedAt = r.L("added_at"),
                    SourceCount = (int)(c?.L("ok") ?? 0), MissingCount = (int)(c?.L("miss") ?? 0),
                });
            }
            var eps = new Dictionary<long, List<EpisodeItem>>(); var epById = new Dictionary<long, EpisodeItem>();
            foreach (var r in db.Query("SELECT * FROM episodes ORDER BY media_id, season, episode"))
            {
                var e = new EpisodeItem
                {
                    Id = r.L("id"), MediaId = r.L("media_id"), Season = (int)r.L("season"), Episode = (int)r.L("episode"), Title = r.SN("title"), Overview = r.SN("overview"), Still = r.SN("still"),
                    Runtime = (int?)r.LN("runtime"), AirDate = r.SN("air_date"), IntroStart = r.LN("intro_start_ms"), IntroEnd = r.LN("intro_end_ms"), CreditsStart = r.LN("credits_start_ms"), Available = epAvail.Contains(r.L("id")),
                };
                if (!eps.TryGetValue(e.MediaId, out var l)) eps[e.MediaId] = l = [];
                l.Add(e); epById[e.Id] = e;
            }
            _byId = items.ToDictionary(i => i.Id); _eps = eps; _epById = epById; _items = items;
        }
    }

    static Person[] ParseCast(string? json)
    {
        try { return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Person[]>(json, J.Opt) ?? []; } catch { return []; }
    }

    public List<MediaItem> All { get { Ensure(); return _items!; } }
    public MediaItem? Get(long id) { Ensure(); return _byId.GetValueOrDefault(id); }
    public List<EpisodeItem> Episodes(long mediaId) { Ensure(); return _eps.GetValueOrDefault(mediaId) ?? []; }
    public EpisodeItem? Episode(long id) { Ensure(); return _epById.GetValueOrDefault(id); }

    // ---- profile state -------------------------------------------------------------------
    public ProfileState? State(long profileId)
    {
        var db = hub.Db;
        var p = db.One("SELECT * FROM profiles WHERE id=?", profileId);
        if (p == null) return null;
        var prefs = J.Parse(p.SN("prefs_json")) as JsonObject ?? new JsonObject();
        var ps = new ProfileState
        {
            Id = profileId, MaxMaturity = (int)p.L("max_maturity"), Prefs = prefs,
            FavGenres = prefs["favouriteGenres"] is JsonArray a ? a.Select(x => x!.ToString()).ToArray() : [],
            Watchlist = db.Query("SELECT media_id FROM watchlist WHERE profile_id=?", profileId).Select(r => r.L("media_id")).ToHashSet(),
            Favourites = db.Query("SELECT media_id FROM favourites WHERE profile_id=?", profileId).Select(r => r.L("media_id")).ToHashSet(),
            Ratings = db.Query("SELECT media_id, rating FROM ratings WHERE profile_id=?", profileId).ToDictionary(r => r.L("media_id"), r => (int)r.L("rating")),
        };
        foreach (var r in db.Query("SELECT * FROM progress WHERE profile_id=? ORDER BY updated_at DESC", profileId))
        {
            if (!ps.Progress.TryGetValue(r.L("media_id"), out var l)) ps.Progress[r.L("media_id")] = l = [];
            l.Add(r);
        }
        return ps;
    }

    public IEnumerable<MediaItem> Visible(ProfileState? ps, bool includeUnavailable = false) =>
        All.Where(m => (includeUnavailable || m.Available) && (ps == null || ps.Allows(m)));

    public static string? Img(string? path, string size) =>
        string.IsNullOrEmpty(path) ? null : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"/api/v1/images/{size}{path}";

    // ---- watch state ---------------------------------------------------------------------
    public bool IsWatched(MediaItem m, ProfileState ps)
    {
        if (!ps.Progress.TryGetValue(m.Id, out var rows)) return false;
        if (m.IsMovie) return rows.Any(r => r.B("is_completed"));
        var avail = Episodes(m.Id).Where(e => e.Available).Select(e => e.Id).ToHashSet();
        if (avail.Count == 0) return false;
        var done = rows.Where(r => r.B("is_completed")).Select(r => r.L("episode_id")).ToHashSet();
        return avail.All(done.Contains);
    }

    /// <summary>The episode to play next for a show: the one in progress, else the first unwatched after the latest watched.</summary>
    public (EpisodeItem? ep, Row? progress) UpNext(MediaItem show, ProfileState? ps)
    {
        var eps = Episodes(show.Id).Where(e => e.Available).OrderBy(e => e.Season == 0 ? int.MaxValue : e.Season).ThenBy(e => e.Episode).ToList();
        if (eps.Count == 0) return (null, null);
        List<Row>? rows = null; ps?.Progress.TryGetValue(show.Id, out rows);
        var latest = rows?.FirstOrDefault();   // rows are newest first
        if (latest == null) return (eps[0], null);
        var idx = eps.FindIndex(e => e.Id == latest.L("episode_id"));
        if (!latest.B("is_completed") && idx >= 0) return (eps[idx], latest);
        var done = rows!.Where(r => r.B("is_completed")).Select(r => r.L("episode_id")).ToHashSet();
        var next = eps.Skip(idx + 1).FirstOrDefault(e => !done.Contains(e.Id)) ?? eps.FirstOrDefault(e => !done.Contains(e.Id));
        return (next, next == null ? null : rows!.FirstOrDefault(r => r.L("episode_id") == next.Id && !r.B("is_completed")));
    }

    public static object? ProgressOut(Row? p, EpisodeItem? e) => p == null && e == null ? null : new
    {
        positionMs = p?.L("position_ms") ?? 0, durationMs = p?.L("duration_ms") ?? 0,
        percent = p != null && p.L("duration_ms") > 0 ? Math.Round(100.0 * p.L("position_ms") / p.L("duration_ms"), 1) : 0,
        isCompleted = p?.B("is_completed") ?? false, updatedAt = p?.LN("updated_at"),
        episodeId = e?.Id, season = e?.Season, episode = e?.Episode, episodeTitle = e?.Title,
    };

    public object Card(MediaItem m, ProfileState? ps)
    {
        object? prog = null; bool watched = false;
        if (ps != null)
        {
            watched = IsWatched(m, ps);
            if (m.IsMovie) { var r = ps.Progress.GetValueOrDefault(m.Id)?.FirstOrDefault(); prog = r == null ? null : ProgressOut(r, null); }
            else if (ps.Progress.ContainsKey(m.Id)) { var (e, r) = UpNext(m, ps); prog = ProgressOut(r, e); }
        }
        return new
        {
            id = m.Id, type = m.Type, title = m.Title, year = m.Year, rating = Math.Round(m.Rating, 1), certification = m.Certification,
            runtime = m.Runtime, seasonsCount = m.SeasonsCount, genres = m.Genres, overview = m.Overview,
            poster = Img(m.Poster, "w342"), backdrop = Img(m.Backdrop, "w780"), hasTrailer = m.TrailerKey != null,
            available = m.Available, isMatched = m.IsMatched, addedAt = m.AddedAt,
            inWatchlist = ps?.Watchlist.Contains(m.Id) ?? false, isFavourite = ps?.Favourites.Contains(m.Id) ?? false,
            userRating = ps != null && ps.Ratings.TryGetValue(m.Id, out var ur) ? ur : (int?)null, watched, progress = prog,
        };
    }
    public List<object> Cards(IEnumerable<MediaItem> items, ProfileState? ps) => items.Select(m => Card(m, ps)).ToList();

    // ---- similarity & recommendations ----------------------------------------------------
    static double Similarity(MediaItem a, MediaItem b)
    {
        if (a.Id == b.Id) return -1;
        double s = 0;
        var g = a.Genres.Intersect(b.Genres).Count();
        if (a.Genres.Length + b.Genres.Length > 0) s += 4.0 * g / Math.Max(1, a.Genres.Union(b.Genres).Count());
        s += Math.Min(3, a.Cast.Take(8).Select(c => c.Name).Intersect(b.Cast.Take(8).Select(c => c.Name)).Count()) * 1.2;
        if (a.Directors.Intersect(b.Directors).Any()) s += 2.5;
        s += Math.Min(4, a.Keywords.Intersect(b.Keywords).Count()) * 0.6;
        if (a.Collection != null && a.Collection == b.Collection) s += 5;
        return s;
    }

    public List<MediaItem> Similar(MediaItem m, ProfileState? ps, int n = 20) =>
        Visible(ps).Where(x => x.Type == m.Type).Select(x => (x, s: Similarity(m, x))).Where(t => t.s > 0.8)
            .OrderByDescending(t => t.s).ThenByDescending(t => t.x.Rating).Take(n).Select(t => t.x).ToList();

    public List<MediaItem> Recommended(ProfileState ps, int n = 30)
    {
        var gw = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); var pw = new Dictionary<string, double>();
        void Add(MediaItem m, double w)
        {
            foreach (var g in m.Genres) gw[g] = gw.GetValueOrDefault(g) + w;
            foreach (var c in m.Cast.Take(5)) pw[c.Name] = pw.GetValueOrDefault(c.Name) + w * 0.5;
            foreach (var d in m.Directors) pw[d] = pw.GetValueOrDefault(d) + w * 0.8;
        }
        foreach (var g in ps.FavGenres) gw[g] = gw.GetValueOrDefault(g) + 3;
        var seen = new HashSet<long>();
        foreach (var (mid, rows) in ps.Progress)
        {
            if (Get(mid) is not { } m) continue;
            seen.Add(mid);
            Add(m, rows.Any(r => r.B("is_completed")) ? 1.5 : 0.7);
        }
        foreach (var (mid, r) in ps.Ratings) if (Get(mid) is { } m) Add(m, r >= 4 ? 2.5 : r <= 2 ? -2.5 : 0.3);
        foreach (var mid in ps.Watchlist) if (Get(mid) is { } m) Add(m, 1);
        foreach (var mid in ps.Favourites) if (Get(mid) is { } m) Add(m, 2);
        var max = Math.Max(1, gw.Values.DefaultIfEmpty(1).Max());
        double Score(MediaItem m) =>
            m.Genres.Sum(g => gw.GetValueOrDefault(g)) / max / Math.Max(1, Math.Sqrt(m.Genres.Length))
            + Math.Min(2, m.Cast.Take(5).Sum(c => pw.GetValueOrDefault(c.Name)) + m.Directors.Sum(d => pw.GetValueOrDefault(d))) * 0.4
            + m.Rating / 10.0 + Math.Min(1, m.Popularity / 200.0) * 0.3;
        return Visible(ps).Where(m => !seen.Contains(m.Id) && !(ps.Ratings.TryGetValue(m.Id, out var r) && r <= 2))
            .OrderByDescending(Score).Take(n).ToList();
    }

    public List<MediaItem> ByPerson(string name, ProfileState? ps) =>
        Visible(ps).Where(m => m.Directors.Contains(name, StringComparer.OrdinalIgnoreCase) || m.Cast.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.Popularity).ToList();

    // ---- continue watching ---------------------------------------------------------------
    public List<object> ContinueWatching(ProfileState ps)
    {
        var keepCompleted = ps.PrefBool("keepCompletedInContinue", false);
        var list = new List<(long at, object card)>();
        foreach (var (mid, rows) in ps.Progress)
        {
            if (Get(mid) is not { } m || !m.Available || !ps.Allows(m)) continue;
            var latest = rows[0];
            if (m.IsMovie)
            {
                if (latest.B("is_completed") && !keepCompleted) continue;
                if (latest.L("position_ms") < 5000 && !latest.B("is_completed")) continue;
                list.Add((latest.L("updated_at"), Card(m, ps)));
            }
            else
            {
                var (e, _) = UpNext(m, ps);
                if (e == null && !keepCompleted) continue;   // whole show finished
                list.Add((latest.L("updated_at"), Card(m, ps)));
            }
        }
        return list.OrderByDescending(t => t.at).Select(t => t.card).ToList();
    }

    // ---- home ----------------------------------------------------------------------------
    static readonly (string id, string title, string[] genres)[] GenreRows =
    [
        ("action", "Action & Adventure", ["Action", "Adventure", "Action & Adventure"]), ("comedy", "Comedy", ["Comedy"]),
        ("scifi", "Sci-Fi", ["Science Fiction", "Sci-Fi & Fantasy", "Sci-Fi"]), ("thriller", "Thriller", ["Thriller", "Mystery", "Crime"]),
        ("family", "Family", ["Family", "Kids", "Animation"]),
    ];
    public static readonly string[] DefaultRowOrder = ["continue", "mylist", "recent", "trending", "recommended", "because", "popular_movies", "popular_shows", "top_rated", "action", "comedy", "scifi", "thriller", "family", "hidden_gems", "recently_watched"];

    public object Home(ProfileState ps)
    {
        var vis = Visible(ps).ToList();
        var rows = new Dictionary<string, (string title, List<object> items)>();
        void Row(string id, string title, IEnumerable<MediaItem> items) { var l = Cards(items.Take(30), ps); if (l.Count > 0) rows[id] = (title, l); }

        var cw = ContinueWatching(ps); if (cw.Count > 0) rows["continue"] = ("Continue Watching", cw);
        var wl = hub.Db.Query("SELECT media_id FROM watchlist WHERE profile_id=? ORDER BY added_at DESC", ps.Id).Select(r => Get(r.L("media_id"))).Where(m => m != null && vis.Contains(m)).Select(m => m!);
        Row("mylist", "My List", wl);
        Row("recent", "Recently Added", vis.OrderByDescending(m => m.AddedAt));
        var now = Db.Now();
        Row("trending", "Trending", vis.OrderByDescending(m => m.Popularity * (now - m.AddedAt < 14L * 86400_000 ? 1.5 : 1)));
        Row("popular_movies", "Popular Movies", vis.Where(m => m.IsMovie).OrderByDescending(m => m.Popularity));
        Row("popular_shows", "Popular TV Shows", vis.Where(m => !m.IsMovie).OrderByDescending(m => m.Popularity));
        var rec = Recommended(ps);
        Row("recommended", "Recommended For You", rec);
        var lastId = hub.Db.One("SELECT media_id FROM history WHERE profile_id=? ORDER BY watched_at DESC LIMIT 1", ps.Id)?.L("media_id");
        if (lastId != null && Get(lastId.Value) is { } last) Row("because", $"Because You Watched {last.Title}", Similar(last, ps));
        Row("top_rated", "Top Rated", vis.Where(m => m.Votes >= 50 || m.Rating > 0).OrderByDescending(m => m.Rating));
        foreach (var (id, title, genres) in GenreRows) Row(id, title, vis.Where(m => m.Genres.Intersect(genres, StringComparer.OrdinalIgnoreCase).Any()).OrderByDescending(m => m.Popularity));
        var medianPop = vis.Count > 0 ? vis.Select(m => m.Popularity).OrderBy(x => x).ElementAt(vis.Count / 2) : 0;
        Row("hidden_gems", "Hidden Gems", vis.Where(m => m.Rating >= 7 && m.Popularity <= medianPop).OrderByDescending(m => m.Rating));
        var rw = hub.Db.Query("SELECT media_id, MAX(watched_at) at FROM history WHERE profile_id=? GROUP BY media_id ORDER BY at DESC LIMIT 30", ps.Id).Select(r => Get(r.L("media_id"))).Where(m => m != null && vis.Contains(m)).Select(m => m!);
        Row("recently_watched", "Recently Watched", rw);

        // Profile-defined order and visibility; rows the profile has not placed keep the default order.
        var order = new List<string>(); var hidden = new HashSet<string>();
        if (ps.Prefs["homeRows"] is JsonArray cfg)
            foreach (var n in cfg.OfType<JsonObject>())
            {
                var id = n["id"]?.ToString(); if (id == null) continue;
                order.Add(id); if (n["enabled"] is JsonValue v && v.TryGetValue<bool>(out var en) && !en) hidden.Add(id);
            }
        foreach (var id in DefaultRowOrder) if (!order.Contains(id)) order.Add(id);

        var heroPool = rec.Concat(vis.OrderByDescending(m => m.Popularity)).Where(m => m.Backdrop != null).Distinct().Take(6).ToList();
        var hero = heroPool.Count == 0 ? vis.FirstOrDefault() : heroPool[(int)(DateTimeOffset.UtcNow.DayOfYear % heroPool.Count)];
        return new
        {
            hero = hero == null ? null : Hero(hero, ps),
            rows = order.Where(id => rows.ContainsKey(id) && !hidden.Contains(id)).Select(id => new { id, title = rows[id].title, items = rows[id].items }).ToList(),
            rowCatalog = DefaultRowOrder,
        };
    }

    object Hero(MediaItem m, ProfileState ps) => new { card = Card(m, ps), backdrop = Img(m.Backdrop, "w1280"), overview = m.Overview, trailer = Trailer(m) };
    public static object? Trailer(MediaItem m) => m.TrailerKey == null ? null : new { site = "youtube", key = m.TrailerKey, url = "https://www.youtube.com/watch?v=" + m.TrailerKey, embedUrl = "https://www.youtube-nocookie.com/embed/" + m.TrailerKey + "?autoplay=1" };

    // ---- search --------------------------------------------------------------------------
    public object Search(string q, string type, ProfileState? ps)
    {
        q = q.Trim();
        if (q.Length == 0) return new { query = q, results = Array.Empty<object>(), people = Array.Empty<object>(), genres = Array.Empty<string>(), collections = Array.Empty<string>() };
        bool Has(string? s) => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var pool = Visible(ps, includeUnavailable: true).Where(m => type == "all" || m.Type == type).ToList();
        var scored = new List<(MediaItem m, int s)>();
        var people = new Dictionary<string, (string role, int n, string? photo)>();
        foreach (var m in pool)
        {
            int s = 0;
            if (m.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) s = 100; else if (Has(m.Title)) s = 70; else if (Has(m.OriginalTitle)) s = 60;
            foreach (var c in m.Cast.Where(c => Has(c.Name))) { s = Math.Max(s, 35); var p = people.GetValueOrDefault(c.Name); people[c.Name] = (p.role ?? "actor", p.n + 1, p.photo ?? c.Photo); }
            foreach (var d in m.Directors.Where(d => Has(d))) { s = Math.Max(s, 35); var p = people.GetValueOrDefault(d); people[d] = ("director", p.n + 1, p.photo); }
            if (m.Genres.Any(g => Has(g))) s = Math.Max(s, 25);
            if (Has(m.Collection)) s = Math.Max(s, 30);
            if (m.Keywords.Any(k => k.Equals(q, StringComparison.OrdinalIgnoreCase))) s = Math.Max(s, 15);
            if (s > 0) scored.Add((m, s + (m.Available ? 5 : 0)));
        }
        return new
        {
            query = q,
            results = Cards(scored.OrderByDescending(t => t.s).ThenByDescending(t => t.m.Popularity).Take(60).Select(t => t.m), ps),
            people = people.OrderByDescending(p => p.Value.n).Take(12).Select(p => new { name = p.Key, role = p.Value.role, titles = p.Value.n, photo = Img(p.Value.photo, "w185") }).ToList(),
            genres = pool.SelectMany(m => m.Genres).Distinct().Where(g => Has(g)).ToList(),
            collections = pool.Select(m => m.Collection).Where(c => Has(c)).Distinct().ToList(),
        };
    }
}
