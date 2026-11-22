using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DJFlix;

public static partial class Api
{
    static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mkv"] = "video/x-matroska", [".webm"] = "video/webm", [".avi"] = "video/x-msvideo", [".mov"] = "video/quicktime",
        [".ts"] = "video/mp2t", [".m2ts"] = "video/mp2t", [".wmv"] = "video/x-ms-wmv", [".mpg"] = "video/mpeg", [".mpeg"] = "video/mpeg",
        [".srt"] = "application/x-subrip", [".vtt"] = "text/vtt", [".ass"] = "text/x-ssa", [".ssa"] = "text/x-ssa",
    };
    static readonly string[] SubExt = [".srt", ".vtt", ".ass", ".ssa"];

    static List<(string path, string? lang, string label)> Sidecars(string videoPath)
    {
        var list = new List<(string, string?, string)>();
        try
        {
            var dir = Path.GetDirectoryName(videoPath); var stem = Path.GetFileNameWithoutExtension(videoPath);
            if (dir == null || !Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => SubExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
            {
                var tag = Path.GetFileNameWithoutExtension(f)[stem.Length..].Trim('.', ' ', '-', '_');
                var lang = tag.Split('.', '-', '_').FirstOrDefault(t => t.Length is 2 or 3 && t.All(char.IsLetter))?.ToLowerInvariant();
                list.Add((f, lang, tag.Length > 0 ? tag : "Subtitles"));
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Central progress write. Conflict rule: every update is placed on the hub's clock
    /// (now − ageMs, so updates queued while offline keep their real time). An update older
    /// than the stored one from a different session is rejected, so a stale client can never
    /// roll back newer progress. Two live sessions simply alternate last-writer-wins and each
    /// is told about the other so the player can warn the viewer.
    /// </summary>
    static IResult SaveProgress(Hub hub, HttpContext c, long pid, JsonObject b)
    {
        var db = hub.Db; var cat = hub.Catalog;
        var mediaId = Lng(b["mediaId"]) ?? 0; var epId = Lng(b["episodeId"]) ?? 0;
        var m = cat.Get(mediaId);
        if (m == null) return NotFound("Title not found.");
        if (!m.IsMovie && cat.Episode(epId)?.MediaId != mediaId) return Err(400, "invalid", "episodeId is required for TV shows.");
        if (m.IsMovie) epId = 0;
        long pos = Math.Max(0, Lng(b["positionMs"]) ?? 0), dur = Math.Max(0, Lng(b["durationMs"]) ?? 0);
        var ev = Str(b["event"]) ?? "tick"; var sid = Str(b["sessionId"]); var dev = DeviceId(c);
        var now = Db.Now(); var eff = now - Math.Clamp(Lng(b["ageMs"]) ?? 0, 0, 30L * 86400_000);
        if (dur > 0 && pos > dur) pos = dur;

        return db.Locked(() =>
        {
            var cur = db.One("SELECT * FROM progress WHERE profile_id=? AND media_id=? AND episode_id=?", pid, mediaId, epId);
            if (cur != null && cur.L("updated_at") > eff && cur.SN("session_id") != sid)
                return Results.Json(new { accepted = false, reason = "stale", current = Catalog.ProgressOut(cur, cat.Episode(epId)) }, J.Opt);
            if (dur == 0 && cur != null) dur = cur.L("duration_ms");
            var done = ev == "ended" || (dur > 0 && pos >= dur * Threshold(hub));
            db.Tx(() =>
            {
                if (db.Exec("UPDATE progress SET position_ms=?, duration_ms=?, is_completed=?, updated_at=?, device_id=?, session_id=? WHERE profile_id=? AND media_id=? AND episode_id=?", pos, dur, done, eff, dev, sid, pid, mediaId, epId) == 0)
                    db.Exec("INSERT INTO progress(profile_id,media_id,episode_id,position_ms,duration_ms,is_completed,updated_at,device_id,session_id) VALUES(?,?,?,?,?,?,?,?,?)", pid, mediaId, epId, pos, dur, done, eff, dev, sid);
                // One history entry per playback session, kept current as the session advances.
                if (sid == null || db.Exec("UPDATE history SET position_ms=?, duration_ms=?, is_completed=?, watched_at=? WHERE session_id=? AND profile_id=?", pos, dur, done, eff, sid, pid) == 0)
                    db.Exec("INSERT INTO history(profile_id,media_id,episode_id,watched_at,position_ms,duration_ms,is_completed,device_id,session_id) VALUES(?,?,?,?,?,?,?,?,?)", pid, mediaId, epId, eff, pos, dur, done, dev, sid ?? "manual-" + now);
                if (sid != null)
                    db.Exec("UPDATE sessions SET last_at=?, position_ms=?, state=? WHERE id=?", now, pos, ev switch { "pause" => "paused", "stop" => "stopped", "ended" => "ended", _ => "playing" }, sid);
            });
            var other = db.One("""
                SELECT s.id, s.position_ms, s.last_at, d.name FROM sessions s LEFT JOIN devices d ON d.id=s.device_id
                WHERE s.profile_id=? AND s.media_id=? AND s.episode_id=? AND s.id<>? AND s.state='playing' AND s.last_at>? ORDER BY s.last_at DESC LIMIT 1
                """, pid, mediaId, epId, sid ?? "", now - 30_000);
            return Results.Json(new
            {
                accepted = true, isCompleted = done, positionMs = pos, updatedAt = eff,
                otherSession = other == null ? null : new { deviceName = other.SN("name") ?? "another device", positionMs = other.L("position_ms"), lastAt = other.L("last_at") },
            }, J.Opt);
        });
    }

    static void MapPlayback(RouteGroupBuilder g, RouteGroupBuilder p)
    {
        p.MapPut("/progress", (Hub hub, HttpContext c, long pid, JsonObject body) => SaveProgress(hub, c, pid, body));

        p.MapGet("/progress/{mediaId:long}", (Hub hub, long pid, long mediaId, long? episodeId) =>
        {
            var r = hub.Db.One("SELECT * FROM progress WHERE profile_id=? AND media_id=? AND episode_id=?", pid, mediaId, episodeId ?? 0);
            return Results.Json(new { progress = r == null ? null : Catalog.ProgressOut(r, hub.Catalog.Episode(episodeId ?? 0)) }, J.Opt);
        });

        p.MapDelete("/progress/{mediaId:long}", (Hub hub, long pid, long mediaId, long? episodeId) =>
        {
            if (episodeId != null) hub.Db.Exec("DELETE FROM progress WHERE profile_id=? AND media_id=? AND episode_id=?", pid, mediaId, episodeId);
            else hub.Db.Exec("DELETE FROM progress WHERE profile_id=? AND media_id=?", pid, mediaId);
            return Results.NoContent();
        });

        // Watched / unwatched toggle for a movie, an episode, a season or a whole show.
        p.MapPost("/watched", (Hub hub, HttpContext c, long pid, JsonObject body) =>
        {
            var db = hub.Db; var cat = hub.Catalog;
            var m = cat.Get(Lng(body["mediaId"]) ?? 0); if (m == null) return NotFound("Title not found.");
            var watched = Bool(body["watched"]) ?? true; var epId = Lng(body["episodeId"]); var season = Lng(body["season"]);
            var targets = m.IsMovie ? [0L] : cat.Episodes(m.Id).Where(e => epId != null ? e.Id == epId : season == null || e.Season == season).Select(e => e.Id).ToList();
            db.Tx(() =>
            {
                foreach (var t in targets)
                {
                    db.Exec("DELETE FROM progress WHERE profile_id=? AND media_id=? AND episode_id=?", pid, m.Id, t);
                    if (!watched) continue;
                    long dur = ((m.IsMovie ? m.Runtime : cat.Episode(t)?.Runtime) ?? 0) * 60_000L;
                    db.Exec("INSERT INTO progress(profile_id,media_id,episode_id,position_ms,duration_ms,is_completed,updated_at,device_id) VALUES(?,?,?,?,?,1,?,?)", pid, m.Id, t, dur, dur, Db.Now(), DeviceId(c));
                }
            });
            return Results.Json(new { mediaId = m.Id, watched, count = targets.Count }, J.Opt);
        });

        // ---- playback sessions ------------------------------------------------------------
        g.MapPost("/playback/sessions", async (Hub hub, Providers prov, HttpContext c, JsonObject body) =>
        {
            var db = hub.Db; var cat = hub.Catalog;
            var pid = Lng(body["profileId"]) ?? 0; var ps = cat.State(pid);
            if (ps == null) return NotFound("Profile not found.");
            var m = cat.Get(Lng(body["mediaId"]) ?? 0);
            if (m == null) return NotFound("Title not found.");
            if (!ps.Allows(m)) return Err(403, "restricted", "This title is restricted for the current profile.");
            EpisodeItem? ep = null;
            if (!m.IsMovie)
            {
                ep = Lng(body["episodeId"]) is { } eid ? cat.Episode(eid) : cat.UpNext(m, ps).ep;
                if (ep == null || ep.MediaId != m.Id) return Err(404, "no_episode", "There is no playable episode for this show.");
            }
            var epKey = ep?.Id ?? 0;
            var candidates = db.Query("SELECT * FROM sources WHERE media_id=? AND IFNULL(episode_id,0)=? AND is_missing=0", m.Id, epKey);
            Row? src = Lng(body["sourceId"]) is { } wanted ? candidates.FirstOrDefault(s => s.L("id") == wanted)
                : candidates.OrderBy(s => s.S("kind") == "local" ? 0 : 1).ThenByDescending(s => s.SN("quality") switch { "2160p" => 4, "1080p" => 3, "720p" => 2, _ => 1 }).FirstOrDefault();
            if (src == null) return Err(404, "no_source", "No playable file is available for this title.", new { availability = m.MissingCount > 0 ? "missing" : "unavailable" });

            string? resolved = null;
            if (src.S("kind") == "local")
            {
                if (!File.Exists(src.S("ref"))) { db.Exec("UPDATE sources SET is_missing=1 WHERE id=?", src.L("id")); cat.Invalidate(); return Err(404, "file_missing", "The file for this title can no longer be found on the PC."); }
            }
            else
            {
                try { resolved = (await prov.Debrid.ResolveAsync(src.S("link"))).Url; }
                catch (ProviderException e)
                {
                    if (e.Kind == "expired") { db.Exec("UPDATE sources SET is_missing=1 WHERE id=?", src.L("id")); cat.Invalidate(); }
                    hub.Log("warn", "Playback link failed: " + e.Message);
                    return Err(e.Kind switch { "auth" => 502, "rate_limit" => 429, "expired" => 410, _ => 503 }, "provider_" + e.Kind, e.Message);
                }
            }

            var sid = Guid.NewGuid().ToString("N"); var playToken = Sec.NewToken(24); var now = Db.Now();
            db.Exec("UPDATE sessions SET state='stopped' WHERE device_id=? AND state IN ('playing','paused')", DeviceId(c));
            db.Exec("INSERT INTO sessions(id,profile_id,media_id,episode_id,source_id,device_id,play_token_hash,started_at,last_at,state,resolved_url,resolved_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                sid, pid, m.Id, epKey, src.L("id"), DeviceId(c), Sec.Hash(playToken), now, now, "playing", resolved, resolved == null ? null : now);
            db.Exec("DELETE FROM sessions WHERE started_at<?", now - 7L * 86400_000);

            var prog = db.One("SELECT * FROM progress WHERE profile_id=? AND media_id=? AND episode_id=?", pid, m.Id, epKey);
            var startOver = Bool(body["startOver"]) == true;
            long resume = prog != null && !prog.B("is_completed") && !startOver ? prog.L("position_ms") : 0;
            var ext = Path.GetExtension(src.S("file_name")); if (!Regex.IsMatch(ext, @"^\.[A-Za-z0-9]{2,5}$")) ext = ".mp4";
            var basePath = $"/api/v1/stream/{sid}/{playToken}";
            var subs = src.S("kind") == "local" ? Sidecars(src.S("ref")).Select((s, i) => new { index = i, language = s.lang, label = s.label, format = Path.GetExtension(s.path).TrimStart('.').ToLowerInvariant(), url = $"{basePath}/sub/{i}" }).ToList() : [];

            object? Nav(EpisodeItem? e) => e == null ? null : new { episodeId = e.Id, season = e.Season, episode = e.Episode, title = e.Title, still = Catalog.Img(e.Still, "w300"), runtime = e.Runtime };
            EpisodeItem? next = null, prev = null;
            if (ep != null)
            {
                var list = cat.Episodes(m.Id).Where(e => e.Available && e.Season > 0).ToList();
                var i = list.FindIndex(e => e.Id == ep.Id);
                if (i >= 0) { next = list.ElementAtOrDefault(i + 1); prev = i > 0 ? list[i - 1] : null; }
            }
            return Results.Json(new
            {
                sessionId = sid, streamUrl = $"{basePath}/video{ext}", kind = src.S("kind"), container = ext.TrimStart('.').ToLowerInvariant(), fileName = src.SN("file_name"),
                quality = src.SN("quality"), sourceId = src.L("id"),
                resumePositionMs = resume, durationMs = prog?.L("duration_ms") ?? 0, hasProgress = prog != null && !prog.B("is_completed") && prog.L("position_ms") > 0,
                title = m.Title, mediaId = m.Id, type = m.Type, episode = Nav(ep), next = Nav(next), previous = Nav(prev),
                markers = ep == null ? null : new { introStartMs = ep.IntroStart, introEndMs = ep.IntroEnd, creditsStartMs = ep.CreditsStart },
                subtitles = subs, alternatives = candidates.Select(s => SourceOut(s, false)),
                prefs = ps.Prefs, progressIntervalMs = 10_000,
            }, J.Opt, statusCode: 201);
        });

        g.MapGet("/playback/sessions/active", (Hub hub) => Results.Json(ActiveSessions(hub), J.Opt));

        // ---- streaming (authorised by the per-session play token in the path) --------------
        g.MapGet("/stream/{sid}/{token}/{name}", async (Hub hub, Providers prov, IHttpClientFactory f, HttpContext c, string sid, string token, string name) =>
        {
            var (s, src, fail) = StreamSession(hub, sid, token);
            if (fail != null) return fail;
            if (src!.S("kind") == "local")
            {
                var path = src.S("ref");
                if (!File.Exists(path)) return Err(404, "file_missing", "The file can no longer be found on the PC.");
                return Results.File(path, Mime.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream"), enableRangeProcessing: true);
            }
            var url = s!.SN("resolved_url");
            // Provider links expire; refresh on demand or when older than 30 minutes.
            if (url == null || c.Request.Query["refresh"] == "1" || Db.Now() - s.L("resolved_at") > 30 * 60_000)
            {
                try { url = (await prov.Debrid.ResolveAsync(src.S("link"))).Url; hub.Db.Exec("UPDATE sessions SET resolved_url=?, resolved_at=? WHERE id=?", url, Db.Now(), sid); }
                catch (ProviderException e) { return Err(e.Kind switch { "auth" => 502, "rate_limit" => 429, "expired" => 410, _ => 503 }, "provider_" + e.Kind, e.Message); }
            }
            if (hub.Db.Get("debrid.proxy") != "true") return Results.Redirect(url);

            // Proxy mode: the hub relays bytes so the TV only ever talks to the PC.
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (c.Request.Headers.Range.FirstOrDefault() is { } range && RangeHeaderValue.TryParse(range, out var rh)) req.Headers.Range = rh;
            var up = await f.CreateClient("stream").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, c.RequestAborted);
            c.Response.StatusCode = (int)up.StatusCode;
            c.Response.ContentType = up.Content.Headers.ContentType?.ToString() ?? Mime.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");
            if (up.Content.Headers.ContentLength is { } len) c.Response.ContentLength = len;
            if (up.Content.Headers.ContentRange is { } cr) c.Response.Headers.ContentRange = cr.ToString();
            c.Response.Headers.AcceptRanges = "bytes";
            try { await using var body = await up.Content.ReadAsStreamAsync(c.RequestAborted); await body.CopyToAsync(c.Response.Body, c.RequestAborted); }
            catch (OperationCanceledException) { }
            finally { up.Dispose(); }
            return Results.Empty;
        });

        g.MapGet("/stream/{sid}/{token}/sub/{index:int}", (Hub hub, string sid, string token, int index) =>
        {
            var (_, src, fail) = StreamSession(hub, sid, token);
            if (fail != null) return fail;
            if (src!.S("kind") != "local") return NotFound();
            var subs = Sidecars(src.S("ref"));
            if (index < 0 || index >= subs.Count) return NotFound("Subtitle not found.");
            return Results.File(subs[index].path, Mime.GetValueOrDefault(Path.GetExtension(subs[index].path), "text/plain"));
        });

        // ---- artwork cache ----------------------------------------------------------------
        g.MapGet("/images/{size}/{file}", async (Hub hub, IHttpClientFactory f, HttpContext c, string size, string file) =>
        {
            if (!Regex.IsMatch(size, "^(w92|w154|w185|w300|w342|w500|w780|w1280|original)$") || !Regex.IsMatch(file, @"^[A-Za-z0-9_\-]{4,64}\.(jpg|jpeg|png|webp|svg)$")) return NotFound();
            var path = Path.Combine(hub.DataDir, "cache", "images", size + "_" + file);
            if (!File.Exists(path))
            {
                var baseUrl = Environment.GetEnvironmentVariable("DJFLIX_TMDB_IMAGE_BASE") ?? "https://image.tmdb.org/t/p";
                try
                {
                    using var res = await f.CreateClient("upstream").GetAsync($"{baseUrl}/{size}/{file}");
                    if (!res.IsSuccessStatusCode) return NotFound();
                    var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(tmp, await res.Content.ReadAsByteArrayAsync());
                    File.Move(tmp, path, true);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return Err(503, "image_unavailable", "Artwork could not be loaded."); }
            }
            c.Response.Headers.CacheControl = "public, max-age=2592000, immutable";
            var ct = Path.GetExtension(file).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => "image/jpeg" };
            return Results.File(path, ct);
        });
    }

    static (Row? session, Row? source, IResult? fail) StreamSession(Hub hub, string sid, string token)
    {
        var s = hub.Db.One("SELECT * FROM sessions WHERE id=?", sid);
        if (s == null || !Sec.Eq(s.S("play_token_hash"), Sec.Hash(token)) || Db.Now() - s.L("started_at") > 24L * 3600_000)
            return (null, null, Err(401, "session_expired", "This playback session has expired. Start playback again."));
        var dev = hub.Db.One("SELECT is_revoked FROM devices WHERE id=?", s.S("device_id"));
        if (dev == null || dev.B("is_revoked")) return (null, null, Err(401, "device_revoked", "This device is no longer paired."));
        var src = hub.Db.One("SELECT * FROM sources WHERE id=?", s.L("source_id"));
        return src == null ? (null, null, NotFound("Source not found.")) : (s, src, null);
    }

    static object ActiveSessions(Hub hub) => hub.Db.Query("""
        SELECT s.id, s.profile_id, s.media_id, s.episode_id, s.state, s.position_ms, s.started_at, s.last_at, d.name device_name, d.kind device_kind, p.name profile_name
        FROM sessions s LEFT JOIN devices d ON d.id=s.device_id LEFT JOIN profiles p ON p.id=s.profile_id
        WHERE s.state IN ('playing','paused') AND s.last_at>? ORDER BY s.last_at DESC
        """, Db.Now() - 90_000).Select(r => new
        {
            id = r.S("id"), state = r.S("state"), positionMs = r.L("position_ms"), startedAt = r.L("started_at"), lastAt = r.L("last_at"),
            device = r.SN("device_name"), deviceKind = r.SN("device_kind"), profile = r.SN("profile_name"), title = hub.Catalog.Get(r.L("media_id"))?.Title,
        }).ToList();
}
