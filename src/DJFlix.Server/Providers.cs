using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace DJFlix;

// ======================= metadata =========================================================
public sealed class SeasonMeta { public int Number; public string? Name, Overview, Poster, AirDate; }
public sealed class EpisodeMeta { public int Season, Episode; public string? Title, Overview, Still, AirDate; public int? Runtime; }
public sealed record SearchHit(long Id, string Type, string Title, int? Year, string? Overview, string? Poster);
public sealed class MediaMeta
{
    public long TmdbId; public string? ImdbId; public string Type = "movie"; public string Title = ""; public string? OriginalTitle; public int? Year; public string? ReleaseDate, Overview;
    public string[] Genres = [], Directors = [], Studios = [], Keywords = []; public Person[] Cast = [];
    public double Rating, Popularity; public long Votes; public int? Runtime, SeasonsCount; public string? Certification, Poster, Backdrop, TrailerKey, Collection;
    public List<SeasonMeta> Seasons = [];
}

/// <summary>Pluggable metadata source. TMDB is the shipped implementation; IMDb IDs are kept for matching.</summary>
public interface IMetadataProvider
{
    string Id { get; }
    string Name { get; }
    bool Configured { get; }
    Task<List<SearchHit>> SearchAsync(string type, string title, int? year);
    Task<MediaMeta?> DetailsAsync(string type, long id);
    Task<List<EpisodeMeta>> SeasonAsync(long showId, int season);
    Task<(bool ok, string message)> TestAsync();
}

public sealed class ProviderException(string kind, string message) : Exception(message)
{
    /// <summary>auth | rate_limit | unavailable | expired | not_configured | error</summary>
    public string Kind { get; } = kind;
}

public sealed class TmdbProvider(Hub hub, HttpClient http) : IMetadataProvider
{
    public string Id => "tmdb";
    public string Name => "The Movie Database (TMDB)";
    string Base => Environment.GetEnvironmentVariable("DJFLIX_TMDB_BASE") ?? "https://api.themoviedb.org/3";
    string? Key => hub.Secret("tmdb.apiKey");
    public bool Configured => Key != null;
    string Lang => hub.Db.Get("tmdb.language", "en-US");

    async Task<JsonNode?> GetAsync(string path, bool cache = true)
    {
        var key = Key ?? throw new ProviderException("not_configured", "No TMDB API key is configured.");
        var ck = "tmdb:" + Lang + ":" + path;
        if (cache)
        {
            var days = int.TryParse(hub.Db.Get("cache.metadataDays"), out var d) ? d : 7;
            var c = hub.Db.One("SELECT value, at FROM cache WHERE key=?", ck);
            if (c != null && Db.Now() - c.L("at") < days * 86400_000L) return J.Parse(c.S("value"));
        }
        for (int attempt = 0; ; attempt++)
        {
            var url = Base + path + (path.Contains('?') ? "&" : "?") + "language=" + Lang;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // v4 read tokens are JWTs; v3 keys are 32 hex chars passed as a query parameter.
            if (key.Length > 40) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            else req.RequestUri = new Uri(url + "&api_key=" + Uri.EscapeDataString(key));
            HttpResponseMessage res;
            try { res = await http.SendAsync(req); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new ProviderException("unavailable", "TMDB could not be reached: " + e.Message); }
            using (res)
            {
                if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3) { await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2)); continue; }
                if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ProviderException("auth", "TMDB rejected the API key.");
                if (res.StatusCode == HttpStatusCode.NotFound) return null;
                if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new ProviderException("rate_limit", "TMDB rate limit reached.");
                if (!res.IsSuccessStatusCode) throw new ProviderException("unavailable", $"TMDB returned HTTP {(int)res.StatusCode}.");
                var body = await res.Content.ReadAsStringAsync();
                if (cache) hub.Db.Tx(() => { hub.Db.Exec("DELETE FROM cache WHERE key=?", ck); hub.Db.Exec("INSERT INTO cache(key,value,at) VALUES(?,?,?)", ck, body, Db.Now()); });
                return J.Parse(body);
            }
        }
    }

    static int? YearOf(string? date) => date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;
    static string? S(JsonNode? n) => n?.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() is { Length: > 0 } s ? s : null : null;
    static double Dbl(JsonNode? n) => n?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? n.GetValue<double>() : 0;
    static IEnumerable<JsonNode> Arr(JsonNode? n) => n is JsonArray a ? a.OfType<JsonNode>() : [];

    public async Task<List<SearchHit>> SearchAsync(string type, string title, int? year)
    {
        var ep = type == "movie" ? "movie" : "tv";
        async Task<List<SearchHit>> Run(int? y)
        {
            var path = $"/search/{ep}?query={Uri.EscapeDataString(title)}" + (y != null ? (ep == "movie" ? "&year=" : "&first_air_date_year=") + y : "");
            var j = await GetAsync(path);
            return Arr(j?["results"]).Select(r => new SearchHit((long)Dbl(r["id"]), type, S(r[ep == "movie" ? "title" : "name"]) ?? "",
                YearOf(S(r[ep == "movie" ? "release_date" : "first_air_date"])), S(r["overview"]), S(r["poster_path"]))).ToList();
        }
        var hits = await Run(year);
        if (hits.Count == 0 && year != null) hits = await Run(null);
        return hits;
    }

    public async Task<MediaMeta?> DetailsAsync(string type, long id)
    {
        bool movie = type == "movie";
        var j = await GetAsync(movie ? $"/movie/{id}?append_to_response=credits,videos,keywords,release_dates,external_ids"
                                     : $"/tv/{id}?append_to_response=credits,videos,keywords,content_ratings,external_ids");
        if (j == null) return null;
        var m = new MediaMeta
        {
            TmdbId = id, Type = type, Title = S(j[movie ? "title" : "name"]) ?? "", OriginalTitle = S(j[movie ? "original_title" : "original_name"]),
            ReleaseDate = S(j[movie ? "release_date" : "first_air_date"]), Overview = S(j["overview"]),
            Genres = Arr(j["genres"]).Select(g => S(g["name"])).Where(x => x != null).ToArray()!,
            Studios = Arr(j["production_companies"]).Select(g => S(g["name"])).Where(x => x != null).Take(6).ToArray()!,
            Rating = Dbl(j["vote_average"]), Votes = (long)Dbl(j["vote_count"]), Popularity = Dbl(j["popularity"]),
            Poster = S(j["poster_path"]), Backdrop = S(j["backdrop_path"]),
            ImdbId = S(j["imdb_id"]) ?? S(j["external_ids"]?["imdb_id"]),
            Cast = Arr(j["credits"]?["cast"]).Take(20).Select(c => new Person(S(c["name"]) ?? "", S(c["character"]), S(c["profile_path"]))).Where(p => p.Name != "").ToArray(),
            Keywords = Arr(j["keywords"]?[movie ? "keywords" : "results"]).Select(k => S(k["name"])).Where(x => x != null).Take(30).ToArray()!,
        };
        m.Year = YearOf(m.ReleaseDate);
        if (movie)
        {
            m.Runtime = (int)Dbl(j["runtime"]) is > 0 and var rt ? rt : null;
            m.Directors = Arr(j["credits"]?["crew"]).Where(c => S(c["job"]) == "Director").Select(c => S(c["name"])).Where(x => x != null).ToArray()!;
            m.Collection = S(j["belongs_to_collection"]?["name"]);
            var us = Arr(j["release_dates"]?["results"]).FirstOrDefault(r => S(r["iso_3166_1"]) == "US");
            m.Certification = Arr(us?["release_dates"]).Select(r => S(r["certification"])).FirstOrDefault(c => c != null);
        }
        else
        {
            m.Runtime = Arr(j["episode_run_time"]).Select(x => (int)Dbl(x)).FirstOrDefault() is > 0 and var rt ? rt : null;
            m.Directors = Arr(j["created_by"]).Select(c => S(c["name"])).Where(x => x != null).ToArray()!;
            m.SeasonsCount = (int)Dbl(j["number_of_seasons"]);
            m.Certification = S(Arr(j["content_ratings"]?["results"]).FirstOrDefault(r => S(r["iso_3166_1"]) == "US")?["rating"]);
            m.Seasons = Arr(j["seasons"]).Select(s => new SeasonMeta { Number = (int)Dbl(s["season_number"]), Name = S(s["name"]), Overview = S(s["overview"]), Poster = S(s["poster_path"]), AirDate = S(s["air_date"]) }).ToList();
        }
        var vids = Arr(j["videos"]?["results"]).Where(v => S(v["site"]) == "YouTube").ToList();
        var trailer = vids.FirstOrDefault(v => S(v["type"]) == "Trailer" && v["official"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
                   ?? vids.FirstOrDefault(v => S(v["type"]) == "Trailer") ?? vids.FirstOrDefault(v => S(v["type"]) == "Teaser");
        m.TrailerKey = S(trailer?["key"]);
        return m;
    }

    public async Task<List<EpisodeMeta>> SeasonAsync(long showId, int season)
    {
        var j = await GetAsync($"/tv/{showId}/season/{season}");
        return Arr(j?["episodes"]).Select(e => new EpisodeMeta
        {
            Season = season, Episode = (int)Dbl(e["episode_number"]), Title = S(e["name"]), Overview = S(e["overview"]), Still = S(e["still_path"]),
            AirDate = S(e["air_date"]), Runtime = (int)Dbl(e["runtime"]) is > 0 and var rt ? rt : null,
        }).ToList();
    }

    public async Task<(bool ok, string message)> TestAsync()
    {
        try { var j = await GetAsync("/configuration", cache: false); return j?["images"] != null ? (true, "Connected to TMDB.") : (false, "Unexpected response from TMDB."); }
        catch (ProviderException e) { return (false, e.Message); }
    }
}

// ======================= debrid ===========================================================
public sealed record DebridAccount(string Username, string Plan, bool Premium, string? Expiration);
/// <summary>A playable file in the user's own debrid cloud. Link is the provider's restricted link, resolved at play time.</summary>
public sealed record DebridFile(string Ref, string Link, string FileName, string? Folder, long Size);
public sealed record ResolvedLink(string Url, string? FileName, long Size, string? MimeType);

/// <summary>Pluggable debrid connector. Implementations use only the provider's documented API.</summary>
public interface IDebridProvider
{
    string Id { get; }
    string Name { get; }
    bool Configured { get; }
    Task<DebridAccount> AccountAsync();
    Task<List<DebridFile>> ListFilesAsync();
    Task<ResolvedLink> ResolveAsync(string link);
}

public sealed class RealDebridProvider(Hub hub, HttpClient http) : IDebridProvider
{
    public string Id => "realdebrid";
    public string Name => "Real-Debrid";
    string Base => Environment.GetEnvironmentVariable("DJFLIX_RD_BASE") ?? "https://api.real-debrid.com/rest/1.0";
    string? Token => hub.Secret("debrid.realdebrid.token");
    public bool Configured => Token != null;

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, Dictionary<string, string>? form = null)
    {
        var token = Token ?? throw new ProviderException("not_configured", "Real-Debrid is not connected.");
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, Base + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (form != null) req.Content = new FormUrlEncodedContent(form);
            HttpResponseMessage res;
            try { res = await http.SendAsync(req); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new ProviderException("unavailable", "Real-Debrid could not be reached: " + e.Message); }
            using (res)
            {
                var code = (int)res.StatusCode;
                if (code == 429 && attempt < 4) { await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1))); continue; }
                var body = await res.Content.ReadAsStringAsync();
                if (res.IsSuccessStatusCode) return J.Parse(body);
                var err = J.Parse(body)?["error"]?.ToString();
                throw code switch
                {
                    401 => new ProviderException("auth", "Real-Debrid rejected the API token (expired or invalid). Reconnect in Settings."),
                    403 => new ProviderException("auth", "Real-Debrid denied access: " + (err ?? "account locked or not premium") + "."),
                    429 => new ProviderException("rate_limit", "Real-Debrid rate limit reached. Try again in a minute."),
                    404 or 410 => new ProviderException("expired", "The link is no longer available on Real-Debrid."),
                    503 when err is "hoster_unavailable" or "unavailable_file" => new ProviderException("expired", "The file is no longer available on Real-Debrid."),
                    >= 500 => new ProviderException("unavailable", "Real-Debrid is temporarily unavailable (HTTP " + code + ")."),
                    _ => new ProviderException("error", "Real-Debrid error: " + (err ?? "HTTP " + code)),
                };
            }
        }
    }

    public async Task<DebridAccount> AccountAsync()
    {
        var j = await SendAsync(HttpMethod.Get, "/user");
        var type = j?["type"]?.ToString() ?? "free";
        return new DebridAccount(j?["username"]?.ToString() ?? "", type, type == "premium", j?["expiration"]?.ToString());
    }

    public async Task<List<DebridFile>> ListFilesAsync()
    {
        var files = new List<DebridFile>();
        for (int page = 1; page <= 50; page++)
        {
            if (await SendAsync(HttpMethod.Get, $"/torrents?limit=100&page={page}") is not JsonArray list || list.Count == 0) break;
            foreach (var t in list)
            {
                if (t?["status"]?.ToString() != "downloaded") continue;
                var id = t["id"]!.ToString();
                // Finished items never change, so their file listing is cached indefinitely.
                var ck = "rd:torrent:" + id;
                var info = J.Parse(hub.Db.Str("SELECT value FROM cache WHERE key=?", ck));
                if (info == null)
                {
                    info = await SendAsync(HttpMethod.Get, "/torrents/info/" + id);
                    if (info == null) continue;
                    hub.Db.Exec("INSERT INTO cache(key,value,at) VALUES(?,?,?)", ck, info.ToJsonString(), Db.Now());
                    await Task.Delay(260);   // stay well inside the documented 250 requests/minute
                }
                var selected = (info["files"] as JsonArray ?? []).Where(f => f?["selected"]?.ToString() == "1").ToList();
                var links = (info["links"] as JsonArray ?? []).Select(l => l!.ToString()).ToList();
                if (selected.Count != links.Count) continue;   // packed archives: no per-file link to play
                var folder = info["filename"]?.ToString();
                for (int i = 0; i < selected.Count; i++)
                {
                    var path = selected[i]!["path"]?.ToString() ?? "";
                    var name = path.Split('/').Last();
                    if (!Library.IsVideo(name)) continue;
                    long.TryParse(selected[i]!["bytes"]?.ToString(), out var size);
                    files.Add(new DebridFile($"{id}:{selected[i]!["id"]}", links[i], name, folder, size));
                }
            }
            if (list.Count < 100) break;
        }
        return files;
    }

    public async Task<ResolvedLink> ResolveAsync(string link)
    {
        var j = await SendAsync(HttpMethod.Post, "/unrestrict/link", new() { ["link"] = link });
        var url = j?["download"]?.ToString();
        if (string.IsNullOrEmpty(url)) throw new ProviderException("expired", "Real-Debrid did not return a playable link.");
        long.TryParse(j?["filesize"]?.ToString(), out var size);
        return new ResolvedLink(url, j?["filename"]?.ToString(), size, j?["mimeType"]?.ToString());
    }
}

/// <summary>Resolves the configured providers. New providers are added here and nowhere else.</summary>
public sealed class Providers(Hub hub, IHttpClientFactory f)
{
    readonly Dictionary<string, IDebridProvider> _debrid = new IDebridProvider[] { new RealDebridProvider(hub, f.CreateClient("upstream")) }.ToDictionary(p => p.Id);
    readonly Dictionary<string, IMetadataProvider> _meta = new IMetadataProvider[] { new TmdbProvider(hub, f.CreateClient("upstream")) }.ToDictionary(p => p.Id);
    public IEnumerable<IDebridProvider> DebridProviders => _debrid.Values;
    public IEnumerable<IMetadataProvider> MetadataProviders => _meta.Values;
    public IDebridProvider Debrid => _debrid.GetValueOrDefault(hub.Db.Get("debrid.provider", "realdebrid")) ?? _debrid["realdebrid"];
    public IMetadataProvider Metadata => _meta.GetValueOrDefault(hub.Db.Get("metadata.provider", "tmdb")) ?? _meta["tmdb"];
}
