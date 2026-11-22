using System.Text.Json.Nodes;

namespace DJFlix;

public static partial class Api
{
    static IResult Err(int status, string code, string message, object? extra = null) => Results.Json(new { error = code, message, details = extra }, J.Opt, statusCode: status);
    static IResult NotFound(string what = "Not found") => Err(404, "not_found", what);
    static string? Str(JsonNode? n) => n?.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n?.GetValueKind() is System.Text.Json.JsonValueKind.Number ? n.ToString() : null;
    static long? Lng(JsonNode? n) => n?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (long)n.GetValue<double>() : long.TryParse(Str(n), out var l) ? l : null;
    static bool? Bool(JsonNode? n) => n?.GetValueKind() switch { System.Text.Json.JsonValueKind.True => true, System.Text.Json.JsonValueKind.False => false, _ => null };
    static string DeviceId(HttpContext c) => c.Items["deviceId"] as string ?? "local";
    static bool IsAdmin(HttpContext c) => c.Items["admin"] is true;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";

    static object ProfileOut(Row p) => new
    {
        id = p.L("id"), name = p.S("name"), avatar = p.SN("avatar"), color = p.SN("color"), hasPin = p.SN("pin_hash") != null, language = p.SN("language"),
        maxMaturity = p.L("max_maturity"), prefs = J.Parse(p.SN("prefs_json")) ?? new JsonObject(), createdAt = p.LN("created_at"),
    };

    /// <summary>PIN-protected profiles need the PIN for changes, except from the hub's own desktop app.</summary>
    static bool PinOk(HttpContext c, Row p, JsonObject? body)
    {
        var h = p.SN("pin_hash");
        if (h == null || IsAdmin(c)) return true;
        var pin = Str(body?["currentPin"]) ?? c.Request.Headers["X-Profile-Pin"].FirstOrDefault();
        return pin != null && Sec.VerifyPin(pin, h);
    }

    public static void MapClient(RouteGroupBuilder g)
    {
        // ---- health, pairing (no token required) ------------------------------------------
        g.MapGet("/health", (Hub hub, Providers prov) => Results.Json(new
        {
            name = hub.ServerName, serverId = hub.ServerId, version = Hub.Version, apiVersion = Hub.ApiVersion, minClientApiVersion = 1,
            requiresPairing = true, status = "ok", time = Db.Now(),
            services = new { database = true, metadata = prov.Metadata.Configured, debrid = prov.Debrid.Configured },
        }, J.Opt));

        g.MapPost("/pair/request", (Hub hub, HttpContext c, JsonObject body) =>
        {
            var ip = Ip(c);
            var at = hub.PairAttempts.GetValueOrDefault(ip);
            if (Db.Now() - at.at > 5 * 60_000) at = (0, Db.Now());
            if (at.n >= 10) return Err(429, "too_many_attempts", "Too many pairing attempts. Wait five minutes and try again.");
            hub.PairAttempts[ip] = (at.n + 1, at.at);

            var name = (Str(body["deviceName"]) ?? "Device").Trim(); if (name.Length > 60) name = name[..60];
            var kind = Str(body["deviceKind"]) ?? "firetv";
            var code = Str(body["code"]);
            if (!string.IsNullOrWhiteSpace(code))
            {
                if (!hub.ConsumePairCode(code)) { hub.Log("warn", $"Rejected pairing code from {ip}"); return Err(403, "invalid_code", "That pairing code is wrong or has expired. Generate a new code in DJFlix on the PC."); }
                var (id, token) = hub.CreateDevice(name, kind, ip);
                return Results.Json(new { status = "paired", deviceId = id, token, serverId = hub.ServerId, serverName = hub.ServerName, version = Hub.Version }, J.Opt);
            }
            // No code: queue a request for approval in the Windows app.
            foreach (var old in hub.Pending.Values.Where(x => Db.Now() - x.At > 10 * 60_000).ToList()) hub.Pending.TryRemove(old.Id, out _);
            if (hub.Pending.Count >= 20) return Err(429, "too_many_requests", "Too many pending pairing requests.");
            var pp = new PendingPair(Sec.NewToken(9), Sec.NewToken(), name, kind, ip, Db.Now());
            hub.Pending[pp.Id] = pp;
            hub.Log("info", $"Pairing request from '{name}' at {ip} awaiting approval");
            return Results.Json(new { status = "pending", requestId = pp.Id, secret = pp.Secret, expiresAt = pp.At + 10 * 60_000 }, J.Opt);
        });

        g.MapGet("/pair/status/{id}", (Hub hub, string id, string secret) =>
        {
            if (!hub.Pending.TryGetValue(id, out var pp) || !Sec.Eq(pp.Secret, secret)) return NotFound("Unknown pairing request.");
            if (pp.State == "approved")
            {
                hub.Pending.TryRemove(id, out _);   // the token is handed out exactly once
                return Results.Json(new { status = "paired", deviceId = pp.DeviceId, token = pp.Token, serverId = hub.ServerId, serverName = hub.ServerName, version = Hub.Version }, J.Opt);
            }
            if (pp.State == "denied") { hub.Pending.TryRemove(id, out _); return Results.Json(new { status = "denied" }, J.Opt); }
            return Results.Json(new { status = Db.Now() - pp.At > 10 * 60_000 ? "expired" : "pending" }, J.Opt);
        });

        // ---- session / client settings ----------------------------------------------------
        g.MapGet("/me", (Hub hub, HttpContext c, Providers prov) =>
        {
            var d = hub.Db.One("SELECT id, name, kind, created_at FROM devices WHERE id=?", DeviceId(c));
            return Results.Json(new
            {
                device = d == null ? null : J.Out(d), isAdmin = IsAdmin(c), serverName = hub.ServerName, serverId = hub.ServerId, version = Hub.Version, apiVersion = Hub.ApiVersion,
                features = new { metadata = prov.Metadata.Configured, debrid = prov.Debrid.Configured, metadataProvider = prov.Metadata.Name },
                playback = new { speeds = new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 3, 4 }, completeThreshold = Threshold(hub) },
                attribution = "This product uses the TMDB API but is not endorsed or certified by TMDB.",
            }, J.Opt);
        });

        // ---- profiles ---------------------------------------------------------------------
        g.MapGet("/profiles", (Hub hub) => Results.Json(hub.Db.Query("SELECT * FROM profiles ORDER BY id").Select(ProfileOut), J.Opt));

        g.MapPost("/profiles", (Hub hub, JsonObject body) =>
        {
            var name = Str(body["name"])?.Trim();
            if (string.IsNullOrEmpty(name)) return Err(400, "invalid", "A profile name is required.");
            if (hub.Db.Long("SELECT COUNT(*) FROM profiles") >= 12) return Err(400, "limit", "The maximum of 12 profiles has been reached.");
            var pin = Str(body["pin"]);
            if (pin != null && (pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsDigit))) return Err(400, "invalid", "A PIN must be 4 to 8 digits.");
            var id = hub.Db.Insert("INSERT INTO profiles(name,avatar,color,pin_hash,language,max_maturity,prefs_json,created_at) VALUES(?,?,?,?,?,?,?,?)",
                name, Str(body["avatar"]) ?? "avatar-1", Str(body["color"]) ?? "#E50914", pin == null ? null : Sec.HashPin(pin), Str(body["language"]) ?? "en",
                Lng(body["maxMaturity"]) ?? 5, (body["prefs"] as JsonObject)?.ToJsonString() ?? "{}", Db.Now());
            return Results.Json(ProfileOut(hub.Db.One("SELECT * FROM profiles WHERE id=?", id)!), J.Opt, statusCode: 201);
        });

        g.MapPatch("/profiles/{id:long}", (Hub hub, HttpContext c, long id, JsonObject body) =>
        {
            var p = hub.Db.One("SELECT * FROM profiles WHERE id=?", id);
            if (p == null) return NotFound("Profile not found.");
            if (!PinOk(c, p, body)) return Err(403, "pin_required", "This profile is protected by a PIN.");
            if (body.ContainsKey("name")) { var n = Str(body["name"])?.Trim(); if (string.IsNullOrEmpty(n)) return Err(400, "invalid", "A profile name is required."); hub.Db.Exec("UPDATE profiles SET name=? WHERE id=?", n, id); }
            if (body.ContainsKey("avatar")) hub.Db.Exec("UPDATE profiles SET avatar=? WHERE id=?", Str(body["avatar"]), id);
            if (body.ContainsKey("color")) hub.Db.Exec("UPDATE profiles SET color=? WHERE id=?", Str(body["color"]), id);
            if (body.ContainsKey("language")) hub.Db.Exec("UPDATE profiles SET language=? WHERE id=?", Str(body["language"]), id);
            if (body.ContainsKey("maxMaturity")) hub.Db.Exec("UPDATE profiles SET max_maturity=? WHERE id=?", Math.Clamp(Lng(body["maxMaturity"]) ?? 5, 1, 5), id);
            if (body.ContainsKey("pin"))
            {
                var pin = Str(body["pin"]);
                if (!string.IsNullOrEmpty(pin) && (pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsDigit))) return Err(400, "invalid", "A PIN must be 4 to 8 digits.");
                hub.Db.Exec("UPDATE profiles SET pin_hash=? WHERE id=?", string.IsNullOrEmpty(pin) ? null : Sec.HashPin(pin), id);
            }
            if (body["prefs"] is JsonObject patch)
            {
                // Merge so a client that only knows some preferences does not erase the others.
                var cur = J.Parse(p.SN("prefs_json")) as JsonObject ?? new JsonObject();
                foreach (var (k, v) in patch) cur[k] = v?.DeepClone();
                hub.Db.Exec("UPDATE profiles SET prefs_json=? WHERE id=?", cur.ToJsonString(), id);
            }
            return Results.Json(ProfileOut(hub.Db.One("SELECT * FROM profiles WHERE id=?", id)!), J.Opt);
        });

        g.MapDelete("/profiles/{id:long}", (Hub hub, HttpContext c, long id) =>
        {
            var p = hub.Db.One("SELECT * FROM profiles WHERE id=?", id);
            if (p == null) return NotFound("Profile not found.");
            if (!PinOk(c, p, null)) return Err(403, "pin_required", "This profile is protected by a PIN.");
            hub.Db.Exec("DELETE FROM profiles WHERE id=?", id);
            return Results.NoContent();
        });

        g.MapPost("/profiles/{id:long}/verify-pin", (Hub hub, HttpContext c, long id, JsonObject body) =>
        {
            var p = hub.Db.One("SELECT pin_hash FROM profiles WHERE id=?", id);
            if (p == null) return NotFound("Profile not found.");
            if (p.SN("pin_hash") == null) return Results.Json(new { ok = true }, J.Opt);
            var key = $"pin:{id}:{Ip(c)}";
            var at = hub.PairAttempts.GetValueOrDefault(key);
            if (Db.Now() - at.at > 5 * 60_000) at = (0, Db.Now());
            if (at.n >= 5) return Err(429, "too_many_attempts", "Too many incorrect PIN attempts. Try again in a few minutes.");
            var ok = Sec.VerifyPin(Str(body["pin"]) ?? "", p.S("pin_hash"));
            if (ok) hub.PairAttempts.TryRemove(key, out _); else hub.PairAttempts[key] = (at.n + 1, at.at);
            return Results.Json(new { ok }, J.Opt);
        });

        // ---- browse -----------------------------------------------------------------------
        g.MapGet("/home", (Hub hub, long profileId) => hub.Catalog.State(profileId) is { } ps ? Results.Json(hub.Catalog.Home(ps), J.Opt) : NotFound("Profile not found."));
        g.MapGet("/movies", (Hub hub, HttpContext c) => Browse(hub, c, "movie"));
        g.MapGet("/shows", (Hub hub, HttpContext c) => Browse(hub, c, "show"));

        g.MapGet("/genres", (Hub hub, string? type, long? profileId) =>
        {
            var ps = profileId == null ? null : hub.Catalog.State(profileId.Value);
            return Results.Json(hub.Catalog.Visible(ps).Where(m => type == null || m.Type == type).SelectMany(m => m.Genres).GroupBy(x => x)
                .OrderBy(x => x.Key).Select(x => new { name = x.Key, count = x.Count() }), J.Opt);
        });

        g.MapGet("/collections", (Hub hub, long? profileId) =>
        {
            var ps = profileId == null ? null : hub.Catalog.State(profileId.Value);
            return Results.Json(hub.Catalog.Visible(ps).Where(m => m.Collection != null).GroupBy(m => m.Collection!).OrderBy(x => x.Key)
                .Select(x => new { name = x.Key, count = x.Count(), poster = Catalog.Img(x.OrderBy(m => m.Year).First().Poster, "w342"), items = hub.Catalog.Cards(x.OrderBy(m => m.Year), ps) }), J.Opt);
        });

        g.MapGet("/media/{id:long}", (Hub hub, HttpContext c, long id, long? profileId) =>
        {
            var cat = hub.Catalog; var m = cat.Get(id);
            if (m == null) return NotFound("Title not found.");
            var ps = profileId == null ? null : cat.State(profileId.Value);
            if (ps != null && !ps.Allows(m)) return Err(403, "restricted", "This title is restricted for the current profile.");
            var admin = IsAdmin(c);
            var sources = hub.Db.Query("SELECT id, episode_id, kind, provider, ref, file_name, size, quality, is_missing FROM sources WHERE media_id=?", id);
            object? upNext = null;
            if (!m.IsMovie) { var (e, pr) = cat.UpNext(m, ps); upNext = e == null ? null : new { episodeId = e.Id, season = e.Season, episode = e.Episode, title = e.Title, progress = Catalog.ProgressOut(pr, e) }; }
            var seasons = hub.Db.Query("SELECT * FROM seasons WHERE media_id=? ORDER BY CASE WHEN season=0 THEN 9999 ELSE season END", id);
            var eps = cat.Episodes(id);
            return Results.Json(new
            {
                card = cat.Card(m, ps), originalTitle = m.OriginalTitle, releaseDate = m.ReleaseDate, backdrop = Catalog.Img(m.Backdrop, "w1280"), posterLarge = Catalog.Img(m.Poster, "w500"),
                cast = m.Cast.Select(p => new { name = p.Name, character = p.Character, photo = Catalog.Img(p.Photo, "w185") }),
                directors = m.Directors, studios = m.Studios, keywords = m.Keywords, collection = m.Collection, imdbId = m.ImdbId, tmdbId = m.TmdbId,
                imdbUrl = m.ImdbId == null ? null : "https://www.imdb.com/title/" + m.ImdbId + "/",
                trailer = Catalog.Trailer(m),
                availability = m.Available ? "available" : m.MissingCount > 0 ? "missing" : "unavailable",
                sources = sources.Where(s => m.IsMovie).Select(s => SourceOut(s, admin)),
                seasons = seasons.Select(s => new
                {
                    season = s.L("season"), name = s.SN("name"), overview = s.SN("overview"), poster = Catalog.Img(s.SN("poster"), "w342"), airDate = s.SN("air_date"),
                    episodeCount = eps.Count(e => e.Season == s.L("season")), availableCount = eps.Count(e => e.Season == s.L("season") && e.Available),
                }),
                upNext, similar = cat.Cards(cat.Similar(m, ps, 18), ps),
                metadataSource = m.IsMatched ? "TMDB" : m.IsManual ? "manual" : "filename",
            }, J.Opt);
        });

        g.MapGet("/media/{id:long}/similar", (Hub hub, long id, long? profileId) =>
        {
            var m = hub.Catalog.Get(id); if (m == null) return NotFound("Title not found.");
            var ps = profileId == null ? null : hub.Catalog.State(profileId.Value);
            return Results.Json(hub.Catalog.Cards(hub.Catalog.Similar(m, ps, 30), ps), J.Opt);
        });

        g.MapGet("/shows/{id:long}/episodes", (Hub hub, HttpContext c, long id, int? season, long? profileId) =>
        {
            var m = hub.Catalog.Get(id); if (m == null || m.IsMovie) return NotFound("Show not found.");
            var ps = profileId == null ? null : hub.Catalog.State(profileId.Value);
            var prog = ps?.Progress.GetValueOrDefault(id)?.ToDictionary(r => r.L("episode_id")) ?? [];
            var srcs = hub.Db.Query("SELECT id, episode_id, kind, provider, ref, file_name, size, quality, is_missing FROM sources WHERE media_id=?", id).ToLookup(s => s.L("episode_id"));
            var admin = IsAdmin(c);
            return Results.Json(hub.Catalog.Episodes(id).Where(e => season == null || e.Season == season).Select(e => new
            {
                id = e.Id, season = e.Season, episode = e.Episode, title = e.Title, overview = e.Overview, still = Catalog.Img(e.Still, "w300"), runtime = e.Runtime, airDate = e.AirDate,
                available = e.Available, progress = prog.TryGetValue(e.Id, out var pr) ? Catalog.ProgressOut(pr, null) : null, watched = prog.TryGetValue(e.Id, out var pw) && pw.B("is_completed"),
                markers = new { introStartMs = e.IntroStart, introEndMs = e.IntroEnd, creditsStartMs = e.CreditsStart },
                sources = srcs[e.Id].Select(s => SourceOut(s, admin)),
            }), J.Opt);
        });

        g.MapGet("/people/{name}", (Hub hub, string name, long? profileId) =>
        {
            var ps = profileId == null ? null : hub.Catalog.State(profileId.Value);
            var items = hub.Catalog.ByPerson(name, ps);
            return Results.Json(new
            {
                name, asDirector = hub.Catalog.Cards(items.Where(m => m.Directors.Contains(name, StringComparer.OrdinalIgnoreCase)), ps),
                asActor = hub.Catalog.Cards(items.Where(m => m.Cast.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))), ps),
            }, J.Opt);
        });

        g.MapGet("/search", (Hub hub, string? q, string? type, long? profileId) =>
        {
            var ps = profileId == null ? null : hub.Catalog.State(profileId.Value);
            var t = type is "movie" or "show" ? type : "all";
            return Results.Json(hub.Catalog.Search(q ?? "", t, ps), J.Opt);
        });

        g.MapGet("/random", (Hub hub, string? type, long? profileId, bool? unwatched) =>
        {
            var cat = hub.Catalog; var ps = profileId == null ? null : cat.State(profileId.Value);
            var pool = cat.Visible(ps).Where(m => type == "episode" ? !m.IsMovie : type == null || type == "all" || m.Type == type).Where(m => unwatched != true || ps == null || !cat.IsWatched(m, ps)).ToList();
            if (pool.Count == 0) return NotFound("Nothing to pick from yet.");
            var m = pool[Random.Shared.Next(pool.Count)];
            if (type != "episode") return Results.Json(new { card = cat.Card(m, ps) }, J.Opt);
            var eps = cat.Episodes(m.Id).Where(e => e.Available).ToList();
            var e = eps[Random.Shared.Next(eps.Count)];
            return Results.Json(new { card = cat.Card(m, ps), episode = new { id = e.Id, season = e.Season, episode = e.Episode, title = e.Title } }, J.Opt);
        });

        // ---- per-profile lists ------------------------------------------------------------
        var p = g.MapGroup("/profiles/{pid:long}").AddEndpointFilter(async (ctx, next) =>
        {
            var hub = ctx.HttpContext.RequestServices.GetRequiredService<Hub>();
            var pid = long.Parse((string)ctx.HttpContext.Request.RouteValues["pid"]!);
            return hub.Db.One("SELECT 1 x FROM profiles WHERE id=?", pid) == null ? NotFound("Profile not found.") : await next(ctx);
        });

        foreach (var table in new[] { "watchlist", "favourites" })
        {
            p.MapGet("/" + table, (Hub hub, long pid, string? type, string? sort) =>
            {
                var ps = hub.Catalog.State(pid)!;
                var rows = hub.Db.Query($"SELECT media_id, added_at FROM {table} WHERE profile_id=? ORDER BY added_at DESC", pid);
                var items = rows.Select(r => hub.Catalog.Get(r.L("media_id"))).Where(m => m != null && (type is null or "all" || m.Type == type)).Select(m => m!);
                items = sort switch { "title" => items.OrderBy(m => m.Title), "year" => items.OrderByDescending(m => m.Year), "rating" => items.OrderByDescending(m => m.Rating), _ => items };
                return Results.Json(hub.Catalog.Cards(items, ps), J.Opt);
            });
            p.MapPut("/" + table + "/{mediaId:long}", (Hub hub, long pid, long mediaId) =>
            {
                if (hub.Catalog.Get(mediaId) == null) return NotFound("Title not found.");
                // The primary key makes duplicates impossible; adding twice is a no-op.
                if (hub.Db.One($"SELECT 1 x FROM {table} WHERE profile_id=? AND media_id=?", pid, mediaId) == null)
                    hub.Db.Exec($"INSERT INTO {table}(profile_id,media_id,added_at) VALUES(?,?,?)", pid, mediaId, Db.Now());
                return Results.Json(new { mediaId, added = true }, J.Opt);
            });
            p.MapDelete("/" + table + "/{mediaId:long}", (Hub hub, long pid, long mediaId) =>
            {
                hub.Db.Exec($"DELETE FROM {table} WHERE profile_id=? AND media_id=?", pid, mediaId);
                return Results.Json(new { mediaId, added = false }, J.Opt);
            });
        }

        p.MapPut("/ratings/{mediaId:long}", (Hub hub, long pid, long mediaId, JsonObject body) =>
        {
            if (hub.Catalog.Get(mediaId) == null) return NotFound("Title not found.");
            var r = Lng(body["rating"]);
            hub.Db.Exec("DELETE FROM ratings WHERE profile_id=? AND media_id=?", pid, mediaId);
            if (r is >= 1 and <= 5) hub.Db.Exec("INSERT INTO ratings(profile_id,media_id,rating,updated_at) VALUES(?,?,?,?)", pid, mediaId, r, Db.Now());
            return Results.Json(new { mediaId, rating = r is >= 1 and <= 5 ? r : null }, J.Opt);
        });

        p.MapGet("/continue-watching", (Hub hub, long pid) => Results.Json(hub.Catalog.ContinueWatching(hub.Catalog.State(pid)!), J.Opt));

        p.MapGet("/history", (Hub hub, long pid, int? limit) =>
        {
            var cat = hub.Catalog;
            return Results.Json(hub.Db.Query("SELECT * FROM history WHERE profile_id=? ORDER BY watched_at DESC LIMIT ?", pid, Math.Clamp(limit ?? 100, 1, 500)).Select(h =>
            {
                var m = cat.Get(h.L("media_id")); var e = cat.Episode(h.L("episode_id"));
                return new
                {
                    id = h.L("id"), mediaId = h.L("media_id"), title = m?.Title, type = m?.Type, poster = Catalog.Img(m?.Poster, "w342"), watchedAt = h.L("watched_at"),
                    positionMs = h.L("position_ms"), durationMs = h.L("duration_ms"), isCompleted = h.B("is_completed"),
                    episode = e == null ? null : new { id = e.Id, season = e.Season, episode = e.Episode, title = e.Title },
                };
            }), J.Opt);
        });
        p.MapDelete("/history", (Hub hub, long pid) => { hub.Db.Exec("DELETE FROM history WHERE profile_id=?", pid); return Results.NoContent(); });

        p.MapGet("/search-history", (Hub hub, long pid) => Results.Json(hub.Db.Query("SELECT query, MAX(at) at FROM search_history WHERE profile_id=? GROUP BY query ORDER BY at DESC LIMIT 15", pid).Select(r => r.S("query")), J.Opt));
        p.MapPost("/search-history", (Hub hub, long pid, JsonObject body) =>
        {
            var q = Str(body["query"])?.Trim();
            if (!string.IsNullOrEmpty(q)) { hub.Db.Exec("DELETE FROM search_history WHERE profile_id=? AND query=?", pid, q); hub.Db.Exec("INSERT INTO search_history(profile_id,query,at) VALUES(?,?,?)", pid, q, Db.Now()); }
            return Results.NoContent();
        });
        p.MapDelete("/search-history", (Hub hub, long pid) => { hub.Db.Exec("DELETE FROM search_history WHERE profile_id=?", pid); return Results.NoContent(); });

        p.MapGet("/recommendations", (Hub hub, long pid) =>
        {
            var cat = hub.Catalog; var ps = cat.State(pid)!;
            var liked = ps.Ratings.Where(r => r.Value >= 4).Select(r => cat.Get(r.Key)).Concat(ps.Favourites.Select(cat.Get)).Where(m => m != null).Select(m => m!).ToList();
            var actor = liked.SelectMany(m => m.Cast.Take(3).Select(x => x.Name)).GroupBy(x => x).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
            var director = liked.SelectMany(m => m.Directors).GroupBy(x => x).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
            var rec = cat.Recommended(ps, 40);
            return Results.Json(new
            {
                recommended = cat.Cards(rec.Take(24), ps), youMightLike = cat.Cards(rec.Skip(24), ps),
                moreFromActor = actor == null ? null : new { name = actor, items = cat.Cards(cat.ByPerson(actor, ps).Take(20), ps) },
                moreFromDirector = director == null ? null : new { name = director, items = cat.Cards(cat.ByPerson(director, ps).Take(20), ps) },
            }, J.Opt);
        });

        p.MapGet("/stats", (Hub hub, long pid) =>
        {
            var cat = hub.Catalog; var db = hub.Db;
            var hist = db.Query("SELECT media_id, episode_id, position_ms, is_completed, watched_at FROM history WHERE profile_id=?", pid);
            var byGenre = new Dictionary<string, long>();
            foreach (var h in hist) if (cat.Get(h.L("media_id")) is { } m) foreach (var gn in m.Genres) byGenre[gn] = byGenre.GetValueOrDefault(gn) + h.L("position_ms");
            return Results.Json(new
            {
                watchTimeMs = hist.Sum(h => h.L("position_ms")), titlesStarted = hist.Select(h => h.L("media_id")).Distinct().Count(),
                moviesCompleted = db.Long("SELECT COUNT(*) FROM progress p JOIN media m ON m.id=p.media_id WHERE p.profile_id=? AND p.is_completed=1 AND m.type='movie'", pid),
                episodesCompleted = db.Long("SELECT COUNT(*) FROM progress WHERE profile_id=? AND is_completed=1 AND episode_id<>0", pid),
                watchlistCount = db.Long("SELECT COUNT(*) FROM watchlist WHERE profile_id=?", pid), ratingsCount = db.Long("SELECT COUNT(*) FROM ratings WHERE profile_id=?", pid),
                genres = byGenre.OrderByDescending(x => x.Value).Select(x => new { name = x.Key, watchTimeMs = x.Value }),
                recentActivity = hist.OrderByDescending(h => h.L("watched_at")).Take(10).Select(h => new { title = cat.Get(h.L("media_id"))?.Title, watchedAt = h.L("watched_at"), isCompleted = h.B("is_completed") }),
            }, J.Opt);
        });

        // ---- playlists --------------------------------------------------------------------
        p.MapGet("/playlists", (Hub hub, long pid) =>
        {
            var ps = hub.Catalog.State(pid)!;
            return Results.Json(hub.Db.Query("SELECT * FROM playlists WHERE profile_id=? ORDER BY name", pid).Select(pl => new
            {
                id = pl.L("id"), name = pl.S("name"),
                items = hub.Catalog.Cards(hub.Db.Query("SELECT media_id FROM playlist_items WHERE playlist_id=? ORDER BY position", pl.L("id")).Select(r => hub.Catalog.Get(r.L("media_id"))).Where(m => m != null).Select(m => m!), ps),
            }), J.Opt);
        });
        p.MapPost("/playlists", (Hub hub, long pid, JsonObject body) =>
        {
            var name = Str(body["name"])?.Trim(); if (string.IsNullOrEmpty(name)) return Err(400, "invalid", "A playlist name is required.");
            return Results.Json(new { id = hub.Db.Insert("INSERT INTO playlists(profile_id,name,created_at) VALUES(?,?,?)", pid, name, Db.Now()), name }, J.Opt, statusCode: 201);
        });
        p.MapPut("/playlists/{id:long}", (Hub hub, long pid, long id, JsonObject body) =>
        {
            if (hub.Db.One("SELECT 1 x FROM playlists WHERE id=? AND profile_id=?", id, pid) == null) return NotFound("Playlist not found.");
            if (Str(body["name"]) is { Length: > 0 } n) hub.Db.Exec("UPDATE playlists SET name=? WHERE id=?", n.Trim(), id);
            if (body["mediaIds"] is JsonArray ids)   // full ordered list: handles add, remove and drag-to-reorder
                hub.Db.Tx(() =>
                {
                    hub.Db.Exec("DELETE FROM playlist_items WHERE playlist_id=?", id);
                    int pos = 0;
                    foreach (var mid in ids.Select(Lng).Where(x => x != null && hub.Catalog.Get(x.Value) != null).Distinct())
                        hub.Db.Exec("INSERT INTO playlist_items(playlist_id,media_id,position) VALUES(?,?,?)", id, mid, pos++);
                });
            return Results.NoContent();
        });
        p.MapDelete("/playlists/{id:long}", (Hub hub, long pid, long id) => { hub.Db.Exec("DELETE FROM playlists WHERE id=? AND profile_id=?", id, pid); return Results.NoContent(); });

        MapPlayback(g, p);
    }

    static object SourceOut(Row s, bool admin) => new
    {
        id = s.L("id"), kind = s.S("kind"), provider = s.SN("provider"), fileName = s.SN("file_name"), size = s.L("size"), quality = s.SN("quality"),
        isMissing = s.B("is_missing"), container = Path.GetExtension(s.S("file_name")).TrimStart('.').ToLowerInvariant(),
        path = admin && s.S("kind") == "local" ? s.S("ref") : null,   // filesystem paths stay on the PC
    };

    static IResult Browse(Hub hub, HttpContext c, string type)
    {
        var q = c.Request.Query; var cat = hub.Catalog;
        var ps = long.TryParse(q["profileId"], out var pid) ? cat.State(pid) : null;
        IEnumerable<MediaItem> items = cat.Visible(ps, includeUnavailable: q["includeUnavailable"] == "true").Where(m => m.Type == type);
        if (q["genre"].FirstOrDefault() is { Length: > 0 } genre) items = items.Where(m => m.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase));
        if (int.TryParse(q["year"], out var year)) items = items.Where(m => m.Year == year);
        if (int.TryParse(q["yearFrom"], out var yf)) items = items.Where(m => m.Year >= yf);
        if (int.TryParse(q["yearTo"], out var yt)) items = items.Where(m => m.Year <= yt);
        if (double.TryParse(q["minRating"], System.Globalization.CultureInfo.InvariantCulture, out var mr)) items = items.Where(m => m.Rating >= mr);
        if (q["collection"].FirstOrDefault() is { Length: > 0 } col) items = items.Where(m => m.Collection == col);
        if (q["q"].FirstOrDefault() is { Length: > 0 } text) items = items.Where(m => m.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || (m.OriginalTitle?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
        if (ps != null && q["watchlist"] == "true") items = items.Where(m => ps.Watchlist.Contains(m.Id));
        if (ps != null && q["watched"].FirstOrDefault() is "true" or "false") { var want = q["watched"] == "true"; items = items.Where(m => cat.IsWatched(m, ps) == want); }
        var desc = q["order"].FirstOrDefault() switch { "asc" => false, "desc" => true, _ => q["sort"].FirstOrDefault() is not ("title" or null) };
        Func<MediaItem, IComparable> key = q["sort"].FirstOrDefault() switch
        {
            "year" => m => m.ReleaseDate ?? (m.Year?.ToString() ?? ""), "added" => m => m.AddedAt, "rating" => m => m.Rating, "popularity" => m => m.Popularity,
            _ => m => m.Title.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? m.Title[4..].ToLowerInvariant() : m.Title.ToLowerInvariant(),
        };
        var list = (desc ? items.OrderByDescending(key) : items.OrderBy(key)).ToList();
        int page = Math.Max(1, int.TryParse(q["page"], out var pg) ? pg : 1), size = Math.Clamp(int.TryParse(q["pageSize"], out var sz) ? sz : 60, 1, 200);
        return Results.Json(new
        {
            total = list.Count, page, pageSize = size, items = cat.Cards(list.Skip((page - 1) * size).Take(size), ps),
            years = cat.Visible(ps).Where(m => m.Type == type && m.Year != null).Select(m => m.Year!.Value).Distinct().OrderByDescending(y => y),
        }, J.Opt);
    }

    static double Threshold(Hub hub) => double.TryParse(hub.Db.Get("playback.completeThreshold"), System.Globalization.CultureInfo.InvariantCulture, out var t) && t is > 0.5 and <= 1 ? t : 0.92;
}
