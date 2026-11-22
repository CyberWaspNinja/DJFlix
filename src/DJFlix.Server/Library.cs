using System.Text.RegularExpressions;

namespace DJFlix;

public sealed record Parsed(string Type, string Title, int? Year, int? Season, int? Episode, string? Quality);

public sealed class ScanStatus
{
    public bool Running; public string Phase = "idle"; public long? StartedAt, FinishedAt; public int FilesSeen, Added, Missing, Matched; public string? LastError; public string? ProviderWarning;
}

/// <summary>Builds the library from local folders and the user's debrid cloud, and matches items to metadata.</summary>
public sealed partial class Library(Hub hub, Providers prov)
{
    static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".ts", ".m2ts", ".wmv", ".mpg", ".mpeg" };
    public static bool IsVideo(string name) => VideoExt.Contains(Path.GetExtension(name));

    [GeneratedRegex(@"^(?<t>.*?)[\s\._\-\[\(]*(?:s(?<s>\d{1,2})[\s\._\-]*e(?<e>\d{1,3})|(?<![\dx])(?<s>\d{1,2})x(?<e>\d{2,3})(?!\d))", RegexOptions.IgnoreCase)] private static partial Regex TvRx();
    [GeneratedRegex(@"^(?<t>.+?)[\s\._\-\(\[]+(?<y>19\d{2}|20\d{2})(?:[\)\]\s\._\-]|$)")] private static partial Regex YearRx();
    [GeneratedRegex(@"[\s\._\-\(\[]+(2160p|1080p|720p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|webrip|web-dl|webdl|hdtv|dvdrip|hdrip|x264|x265|h264|h265|hevc|remux|proper|repack|extended|unrated|multi|dts|aac|ac3)\b.*$", RegexOptions.IgnoreCase)] private static partial Regex JunkRx();
    [GeneratedRegex(@"^(season|series|staffel)\s*\d+$|^s\d{1,2}$|^specials$", RegexOptions.IgnoreCase)] private static partial Regex SeasonDirRx();
    [GeneratedRegex(@"(2160p|4k|uhd|1080p|720p|480p)", RegexOptions.IgnoreCase)] private static partial Regex QualityRx();

    static string Clean(string s) => Regex.Replace(s.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim(' ', '-', '(', '[', ')', ']');
    public static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    static (string title, int? year) TitleYear(string raw)
    {
        var m = YearRx().Match(raw);
        if (m.Success) return (Clean(m.Groups["t"].Value), int.Parse(m.Groups["y"].Value));
        return (Clean(JunkRx().Replace(raw, "")), null);
    }

    public static Parsed Parse(string fileName, string? parent = null, string? grand = null)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var q = QualityRx().Match(name) is { Success: true } qm ? qm.Value.ToLowerInvariant().Replace("4k", "2160p").Replace("uhd", "2160p") : null;
        var tv = TvRx().Match(name);
        if (tv.Success)
        {
            var (title, year) = TitleYear(tv.Groups["t"].Value);
            if (title.Length == 0)
            {
                var folder = parent != null && SeasonDirRx().IsMatch(parent.Trim()) ? grand : parent;
                if (folder != null) (title, year) = TitleYear(TvRx().Match(folder) is { Success: true } fm ? fm.Groups["t"].Value : folder);
            }
            if (title.Length == 0) title = Clean(name);
            return new Parsed("show", title, year, int.Parse(tv.Groups["s"].Value), int.Parse(tv.Groups["e"].Value), q);
        }
        var (t, y) = TitleYear(name);
        if (y == null && parent != null && YearRx().Match(parent) is { Success: true }) (t, y) = TitleYear(parent);   // "Movie (2010)/movie.mkv"
        if (t.Length == 0) t = Clean(name);
        return new Parsed("movie", t, y, null, null, q);
    }

    public static int Maturity(string? cert) => cert?.ToUpperInvariant() switch
    {
        "G" or "TV-Y" or "TV-G" or "TV-Y7" => 1, "PG" or "TV-PG" => 2, "PG-13" or "TV-14" => 3, "R" or "TV-MA" => 4, "NC-17" => 5, _ => 0,
    };

    // ---- scanning ------------------------------------------------------------------------
    public ScanStatus Status { get; private set; } = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    Db Db => hub.Db;

    public async Task ScanAsync()
    {
        if (!await _gate.WaitAsync(0)) return;   // a scan is already running
        var st = Status = new ScanStatus { Running = true, StartedAt = Db.Now(), Phase = "local" };
        try
        {
            var memo = new Dictionary<string, long>();
            foreach (var lib in Db.Query("SELECT * FROM libraries"))
            {
                var root = lib.S("path");
                if (!Directory.Exists(root)) { hub.Log("warn", $"Library folder is unavailable: {root}"); continue; }
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
                foreach (var file in Directory.EnumerateFiles(root, "*", opts))
                {
                    if (!IsVideo(file) || Path.GetFileName(file).Contains("sample", StringComparison.OrdinalIgnoreCase)) continue;
                    st.FilesSeen++;
                    if (Db.One("SELECT id, is_missing FROM sources WHERE kind='local' AND ref=?", file) is { } ex)
                    {
                        if (ex.B("is_missing")) Db.Exec("UPDATE sources SET is_missing=0 WHERE id=?", ex.L("id"));
                        continue;
                    }
                    var dir = Path.GetDirectoryName(file);
                    var parent = dir != null && !PathEq(dir, root) ? Path.GetFileName(dir) : null;
                    var gdir = dir != null ? Path.GetDirectoryName(dir) : null;
                    var grand = parent != null && gdir != null && !PathEq(gdir, root) ? Path.GetFileName(gdir) : null;
                    var p = Parse(Path.GetFileName(file), parent, grand);
                    long size = 0; try { size = new FileInfo(file).Length; } catch { }
                    await AddSourceAsync(p, "local", null, file, null, Path.GetFileName(file), size, lib.L("id"), memo, st);
                }
                Db.Exec("UPDATE libraries SET last_scan=? WHERE id=?", Db.Now(), lib.L("id"));
            }
            foreach (var s in Db.Query("SELECT id, ref FROM sources WHERE kind='local' AND is_missing=0"))
                if (!File.Exists(s.S("ref"))) { Db.Exec("UPDATE sources SET is_missing=1 WHERE id=?", s.L("id")); st.Missing++; }

            if (prov.Debrid.Configured)
            {
                st.Phase = "debrid";
                try { await SyncDebridAsync(memo, st); }
                catch (ProviderException e) { st.ProviderWarning = e.Message; hub.Log("warn", "Debrid sync skipped: " + e.Message); }
            }

            if (prov.Metadata.Configured)
            {
                st.Phase = "matching";
                foreach (var m in Db.Query("SELECT id, type, title, year FROM media WHERE is_matched=0 AND is_manual=0 LIMIT 200"))
                {
                    try
                    {
                        var hit = Best(await prov.Metadata.SearchAsync(m.S("type"), m.S("title"), (int?)m.LN("year")), m.S("title"));
                        if (hit != null && await MatchAsync(m.L("id"), hit.Id)) st.Matched++;
                    }
                    catch (ProviderException e) { st.ProviderWarning = e.Message; break; }
                }
            }
        }
        catch (Exception e) { st.LastError = e.Message; hub.Log("error", "Library scan failed: " + e.Message); }
        finally
        {
            st.Running = false; st.Phase = "idle"; st.FinishedAt = Db.Now();
            hub.Catalog.Invalidate(); _gate.Release();
            hub.Log("info", $"Library scan finished: {st.FilesSeen} local files, {st.Added} added, {st.Missing} missing, {st.Matched} newly matched");
        }
    }

    static bool PathEq(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    async Task SyncDebridAsync(Dictionary<string, long> memo, ScanStatus st)
    {
        var p = prov.Debrid;
        var files = await p.ListFilesAsync();
        var seen = new HashSet<string>();
        foreach (var f in files)
        {
            seen.Add(f.Ref);
            if (Db.One("SELECT id FROM sources WHERE kind='debrid' AND ref=?", f.Ref) is { } ex)
            {
                Db.Exec("UPDATE sources SET link=?, is_missing=0 WHERE id=?", f.Link, ex.L("id"));
                continue;
            }
            await AddSourceAsync(Parse(f.FileName, f.Folder), "debrid", p.Id, f.Ref, f.Link, f.FileName, f.Size, null, memo, st);
        }
        foreach (var s in Db.Query("SELECT id, ref FROM sources WHERE kind='debrid' AND provider=? AND is_missing=0", p.Id))
            if (!seen.Contains(s.S("ref"))) { Db.Exec("UPDATE sources SET is_missing=1 WHERE id=?", s.L("id")); st.Missing++; }
    }

    async Task AddSourceAsync(Parsed p, string kind, string? provider, string reference, string? link, string fileName, long size, long? libraryId, Dictionary<string, long> memo, ScanStatus st)
    {
        var mediaId = await ResolveMediaAsync(p, memo, st);
        long? epId = p.Type == "show" ? EnsureEpisode(mediaId, p.Season ?? 1, p.Episode ?? 1) : null;
        Db.Exec("INSERT INTO sources(media_id,episode_id,kind,provider,ref,link,file_name,size,quality,library_id,added_at) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
            mediaId, epId, kind, provider, reference, link, fileName, size, p.Quality, libraryId, Db.Now());
        st.Added++;
    }

    static SearchHit? Best(List<SearchHit> hits, string title) =>
        hits.FirstOrDefault(h => Norm(h.Title) == Norm(title)) ?? hits.FirstOrDefault();

    async Task<long> ResolveMediaAsync(Parsed p, Dictionary<string, long> memo, ScanStatus st)
    {
        var key = $"{p.Type}|{Norm(p.Title)}|{p.Year}";
        if (memo.TryGetValue(key, out var known)) return known;
        long id = 0;
        if (prov.Metadata.Configured && st.ProviderWarning == null)
        {
            try
            {
                var hit = Best(await prov.Metadata.SearchAsync(p.Type, p.Title, p.Year), p.Title);
                if (hit != null)
                {
                    id = Db.One("SELECT id FROM media WHERE type=? AND tmdb_id=?", p.Type, hit.Id)?.L("id") ?? 0;
                    if (id == 0 && await prov.Metadata.DetailsAsync(p.Type, hit.Id) is { } meta)
                    {
                        id = Db.Insert("INSERT INTO media(type,title,added_at) VALUES(?,?,?)", p.Type, meta.Title, Db.Now());
                        await ApplyMetaAsync(id, meta);
                    }
                }
            }
            catch (ProviderException e) { st.ProviderWarning = e.Message; hub.Log("warn", "Metadata lookup paused: " + e.Message); }
        }
        if (id == 0)
        {
            var norm = Norm(p.Title);
            id = Db.One("SELECT id FROM media WHERE type=? AND is_matched=0 AND sort_title=? AND IFNULL(year,0)=?", p.Type, norm, p.Year ?? 0)?.L("id") ?? 0;
            if (id == 0) id = Db.Insert("INSERT INTO media(type,title,sort_title,year,added_at,updated_at) VALUES(?,?,?,?,?,?)", p.Type, p.Title, norm, p.Year, Db.Now(), Db.Now());
        }
        return memo[key] = id;
    }

    public long EnsureEpisode(long mediaId, int season, int episode)
    {
        return Db.Locked(() =>
        {
            var e = Db.One("SELECT id FROM episodes WHERE media_id=? AND season=? AND episode=?", mediaId, season, episode);
            if (e != null) return e.L("id");
            if (Db.One("SELECT 1 x FROM seasons WHERE media_id=? AND season=?", mediaId, season) == null)
                Db.Exec("INSERT INTO seasons(media_id,season,name) VALUES(?,?,?)", mediaId, season, season == 0 ? "Specials" : "Season " + season);
            return Db.Insert("INSERT INTO episodes(media_id,season,episode,title) VALUES(?,?,?,?)", mediaId, season, episode, "Episode " + episode);
        });
    }

    /// <summary>Writes provider metadata onto a media row, including seasons and episodes for shows.</summary>
    public async Task ApplyMetaAsync(long id, MediaMeta m)
    {
        Db.Exec("""
            UPDATE media SET tmdb_id=?, imdb_id=?, title=?, original_title=?, sort_title=?, year=?, release_date=?, overview=?, genres_json=?, cast_json=?, directors_json=?,
              studios_json=?, keywords_json=?, rating=?, votes=?, popularity=?, runtime=?, certification=?, maturity=?, poster=?, backdrop=?, trailer_key=?, seasons_count=?,
              collection_name=?, is_matched=1, is_manual=0, updated_at=? WHERE id=?
            """, m.TmdbId, m.ImdbId, m.Title, m.OriginalTitle, Norm(m.Title), m.Year, m.ReleaseDate, m.Overview, J.Ser(m.Genres), J.Ser(m.Cast), J.Ser(m.Directors),
            J.Ser(m.Studios), J.Ser(m.Keywords), m.Rating, m.Votes, m.Popularity, m.Runtime, m.Certification, Maturity(m.Certification), m.Poster, m.Backdrop, m.TrailerKey, m.SeasonsCount,
            m.Collection, Db.Now(), id);
        if (m.Type != "show") return;
        foreach (var s in m.Seasons)
        {
            Db.Tx(() =>
            {
                if (Db.Exec("UPDATE seasons SET name=?, overview=?, poster=?, air_date=? WHERE media_id=? AND season=?", s.Name, s.Overview, s.Poster, s.AirDate, id, s.Number) == 0)
                    Db.Exec("INSERT INTO seasons(media_id,season,name,overview,poster,air_date) VALUES(?,?,?,?,?,?)", id, s.Number, s.Name, s.Overview, s.Poster, s.AirDate);
            });
            var eps = await prov.Metadata.SeasonAsync(m.TmdbId, s.Number);
            Db.Tx(() =>
            {
                foreach (var e in eps)
                    if (Db.Exec("UPDATE episodes SET title=?, overview=?, still=?, runtime=?, air_date=? WHERE media_id=? AND season=? AND episode=?", e.Title, e.Overview, e.Still, e.Runtime, e.AirDate, id, e.Season, e.Episode) == 0)
                        Db.Exec("INSERT INTO episodes(media_id,season,episode,title,overview,still,runtime,air_date) VALUES(?,?,?,?,?,?,?,?)", id, e.Season, e.Episode, e.Title, e.Overview, e.Still, e.Runtime, e.AirDate);
            });
        }
    }

    /// <summary>Matches (or re-matches) a media row to a provider ID. Merges into an existing row when that title is already in the library.</summary>
    public async Task<bool> MatchAsync(long mediaId, long tmdbId)
    {
        var row = Db.One("SELECT id, type FROM media WHERE id=?", mediaId);
        if (row == null) return false;
        var type = row.S("type");
        var existing = Db.One("SELECT id FROM media WHERE type=? AND tmdb_id=? AND id<>?", type, tmdbId, mediaId)?.L("id");
        if (existing != null)
        {
            Db.Tx(() =>
            {
                foreach (var s in Db.Query("SELECT s.id, e.season, e.episode FROM sources s LEFT JOIN episodes e ON e.id=s.episode_id WHERE s.media_id=?", mediaId))
                {
                    long? ep = type == "show" ? EnsureEpisode(existing.Value, (int)s.L("season"), (int)s.L("episode")) : null;
                    Db.Exec("UPDATE sources SET media_id=?, episode_id=? WHERE id=?", existing, ep, s.L("id"));
                }
                Db.Exec("DELETE FROM media WHERE id=?", mediaId);
            });
            hub.Catalog.Invalidate();
            return true;
        }
        var meta = await prov.Metadata.DetailsAsync(type, tmdbId);
        if (meta == null) return false;
        await ApplyMetaAsync(mediaId, meta);
        hub.Catalog.Invalidate();
        return true;
    }

    public object Duplicates()
    {
        var groups = Db.Query("""
            SELECT s.media_id, IFNULL(s.episode_id,0) episode_id, COUNT(*) n FROM sources s WHERE s.is_missing=0 AND s.kind='local'
            GROUP BY s.media_id, IFNULL(s.episode_id,0) HAVING COUNT(*)>1
            """);
        return groups.Select(g => new
        {
            mediaId = g.L("media_id"), title = hub.Catalog.Get(g.L("media_id"))?.Title, episodeId = g.L("episode_id") == 0 ? (long?)null : g.L("episode_id"),
            files = J.Out(Db.Query("SELECT id, ref path, file_name, size, quality FROM sources WHERE media_id=? AND IFNULL(episode_id,0)=? AND is_missing=0 AND kind='local'", g.L("media_id"), g.L("episode_id"))),
        }).ToList();
    }
}

/// <summary>Runs scans at startup, on a timer, and shortly after files change in a library folder.</summary>
public sealed class LibraryWorker(Hub hub, Library lib) : BackgroundService
{
    readonly List<FileSystemWatcher> _watchers = [];
    long _dirtyAt; string _watched = "";

    void RefreshWatchers()
    {
        var paths = hub.Db.Query("SELECT path FROM libraries").Select(r => r.S("path")).Where(Directory.Exists).ToList();
        var sig = string.Join("|", paths);
        if (sig == _watched) return;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear(); _watched = sig;
        foreach (var p in paths)
        {
            try
            {
                var w = new FileSystemWatcher(p) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size };
                FileSystemEventHandler h = (_, e) => { if (Library.IsVideo(e.FullPath) || Directory.Exists(e.FullPath)) Interlocked.Exchange(ref _dirtyAt, Db.Now()); };
                w.Created += h; w.Deleted += h; w.Renamed += (_, _) => Interlocked.Exchange(ref _dirtyAt, Db.Now());
                w.EnableRaisingEvents = true; _watchers.Add(w);
            }
            catch (Exception e) { hub.Log("warn", $"Cannot watch {p}: {e.Message}"); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(1500, ct).ContinueWith(_ => { });
        long last = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RefreshWatchers();
                var interval = (int.TryParse(hub.Db.Get("scan.intervalMinutes"), out var m) ? Math.Max(5, m) : 30) * 60_000L;
                var dirty = Interlocked.Read(ref _dirtyAt);
                // Wait for ten quiet seconds after a file change so large copies finish first.
                if (last == 0 || Db.Now() - last > interval || (dirty > last && Db.Now() - dirty > 10_000)) { last = Db.Now(); await lib.ScanAsync(); }
            }
            catch (Exception e) { hub.Log("error", "Library worker: " + e.Message); }
            await Task.Delay(3000, ct).ContinueWith(_ => { });
        }
    }
}
