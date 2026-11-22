using System.Diagnostics;
using System.Text.Json.Nodes;

namespace DJFlix;

public static partial class Api
{
    static readonly string[] PlainSettings = ["server.name", "server.port", "tmdb.language", "cache.metadataDays", "scan.intervalMinutes", "playback.completeThreshold", "debrid.proxy", "ui.theme", "startup.launchAtLogin"];

    static string FirewallCommand(int port) =>
        $"netsh advfirewall firewall add rule name=\"DJFlix\" dir=in action=allow protocol=TCP localport={port} profile=private,domain remoteip=localsubnet & " +
        $"netsh advfirewall firewall add rule name=\"DJFlix Discovery\" dir=in action=allow protocol=UDP localport={Hub.DiscoveryPort} profile=private,domain remoteip=localsubnet";

    static object Connection(Hub hub)
    {
        var ip = Hub.LanIp(); var (code, exp) = hub.CurrentPairCode();
        return new
        {
            lanIp = ip, port = hub.Port, address = ip == null ? null : $"http://{ip}:{hub.Port}", interfaces = Hub.Interfaces(), discoveryPort = Hub.DiscoveryPort,
            pairingCode = code, pairingCodeExpiresAt = code == null ? (long?)null : exp,
            // Encoded in the QR shown by the desktop app. It carries no credentials.
            qrPayload = ip == null ? null : $"djflix://connect?host={ip}&port={hub.Port}&id={hub.ServerId}",
        };
    }

    static async Task<object> ProviderStatus(Hub hub, Providers prov, bool live)
    {
        object debrid;
        var d = prov.Debrid;
        if (!d.Configured) debrid = new { id = d.Id, name = d.Name, configured = false, connected = false, message = "Not connected." };
        else if (!live) debrid = new { id = d.Id, name = d.Name, configured = true, connected = (bool?)null, message = "Configured." };
        else
        {
            try { var a = await d.AccountAsync(); debrid = new { id = d.Id, name = d.Name, configured = true, connected = true, message = a.Premium ? "Connected." : "Connected, but the account is not premium.", account = new { username = a.Username, plan = a.Plan, premium = a.Premium, expiration = a.Expiration } }; }
            catch (ProviderException e) { debrid = new { id = d.Id, name = d.Name, configured = true, connected = false, message = e.Message, errorKind = e.Kind }; }
        }
        var m = prov.Metadata; object meta;
        if (!m.Configured) meta = new { id = m.Id, name = m.Name, configured = false, connected = false, message = "No API key configured. Titles are named from their file names until a key is added." };
        else if (!live) meta = new { id = m.Id, name = m.Name, configured = true, connected = (bool?)null, message = "Configured." };
        else { var (ok, msg) = await m.TestAsync(); meta = new { id = m.Id, name = m.Name, configured = true, connected = ok, message = msg }; }
        return new
        {
            debrid, metadata = meta, proxyStreams = hub.Db.Get("debrid.proxy") == "true",
            available = new { debrid = prov.DebridProviders.Select(x => new { id = x.Id, name = x.Name }), metadata = prov.MetadataProviders.Select(x => new { id = x.Id, name = x.Name }) },
            imdbNote = "IMDb offers no free public API. Metadata comes from TMDB; IMDb IDs are stored for every matched title and linked from its details page.",
        };
    }

    public static void MapAdmin(RouteGroupBuilder a, IHostApplicationLifetime life)
    {
        // ---- status / dashboard -----------------------------------------------------------
        a.MapGet("/status", async (Hub hub, Providers prov, Library lib, bool? live) =>
        {
            var db = hub.Db; var now = Db.Now();
            return Results.Json(new
            {
                service = new { status = "running", version = Hub.Version, apiVersion = Hub.ApiVersion, startedAt = hub.StartedAt, uptimeMs = now - hub.StartedAt, serverName = hub.ServerName, serverId = hub.ServerId, dataDir = hub.DataDir, runsAsService = !Environment.UserInteractive },
                connection = Connection(hub),
                network = new { online = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() },
                clients = new
                {
                    paired = db.Long("SELECT COUNT(*) FROM devices WHERE is_revoked=0 AND id<>'local'"),
                    connected = J.Out(db.Query("SELECT id, name, kind, last_seen, last_ip FROM devices WHERE is_revoked=0 AND id<>'local' AND last_seen>?", now - 120_000)),
                    pendingRequests = hub.Pending.Values.Count(x => x.State == "pending"), activeSessions = ActiveSessions(hub),
                },
                providers = await ProviderStatus(hub, prov, live == true),
                library = new { movies = hub.Catalog.All.Count(m => m.IsMovie), shows = hub.Catalog.All.Count(m => !m.IsMovie), scan = lib.Status },
                setupCompleted = db.Get("setup.completed") == "true",
            }, new System.Text.Json.JsonSerializerOptions(J.Opt) { IncludeFields = true });
        });

        a.MapGet("/logs", (Hub hub, int? limit) => Results.Json(hub.Logs.Take(Math.Clamp(limit ?? 200, 1, 500)), J.Opt));
        a.MapPost("/restart", (Hub hub) => { hub.Log("info", "Restart requested"); hub.RestartRequested = true; _ = Task.Delay(300).ContinueWith(_ => life.StopApplication()); return Results.Accepted(); });
        a.MapPost("/shutdown", (Hub hub) => { hub.Log("info", "Shutdown requested"); _ = Task.Delay(300).ContinueWith(_ => life.StopApplication()); return Results.Accepted(); });

        // ---- first-run setup --------------------------------------------------------------
        a.MapGet("/setup", (Hub hub) => Results.Json(new
        {
            completed = hub.Db.Get("setup.completed") == "true", connection = Connection(hub),
            firewall = new { required = OperatingSystem.IsWindows(), command = FirewallCommand(hub.Port), note = "The installer adds this rule. It limits access to the local subnet on private networks." },
            advice = "Reserve this PC's IP address in your router's DHCP settings so the address does not change.",
        }, J.Opt));
        a.MapPost("/setup/complete", (Hub hub) => { hub.Db.Set("setup.completed", "true"); return Results.NoContent(); });
        a.MapPost("/firewall/apply", (Hub hub) =>
        {
            if (!OperatingSystem.IsWindows()) return Err(400, "unsupported", "Firewall rules are only managed on Windows.");
            try
            {
                using var pr = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + FirewallCommand(hub.Port)) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
                var output = pr.StandardOutput.ReadToEnd(); pr.WaitForExit(15000);
                return pr.ExitCode == 0 ? Results.Json(new { ok = true }, J.Opt) : Err(403, "needs_elevation", "Windows refused the change. Run DJFlix as administrator once, or run the command shown in an elevated prompt.", new { output = output.Trim() });
            }
            catch (Exception e) { return Err(500, "failed", e.Message); }
        });

        // ---- pairing & devices ------------------------------------------------------------
        a.MapPost("/pairing/code", (Hub hub) => { var (code, exp) = hub.NewPairCode(); return Results.Json(new { code, expiresAt = exp }, J.Opt); });
        a.MapGet("/pairing", (Hub hub) =>
        {
            var (code, exp) = hub.CurrentPairCode();
            return Results.Json(new { code, expiresAt = code == null ? (long?)null : exp, pending = hub.Pending.Values.Where(x => x.State == "pending" && Db.Now() - x.At < 10 * 60_000).Select(x => new { id = x.Id, deviceName = x.DeviceName, kind = x.Kind, ip = x.Ip, at = x.At }) }, J.Opt);
        });
        a.MapPost("/pairing/{id}/approve", (Hub hub, string id) =>
        {
            if (!hub.Pending.TryGetValue(id, out var pp) || pp.State != "pending") return NotFound("Pairing request not found.");
            (pp.DeviceId, pp.Token) = hub.CreateDevice(pp.DeviceName, pp.Kind, pp.Ip); pp.State = "approved";
            return Results.NoContent();
        });
        a.MapPost("/pairing/{id}/deny", (Hub hub, string id) => { if (hub.Pending.TryGetValue(id, out var pp)) pp.State = "denied"; return Results.NoContent(); });
        a.MapGet("/devices", (Hub hub) => Results.Json(J.Out(hub.Db.Query("SELECT id, name, kind, created_at, last_seen, last_ip, is_revoked FROM devices WHERE id<>'local' ORDER BY created_at DESC")), J.Opt));
        a.MapPatch("/devices/{id}", (Hub hub, string id, JsonObject body) => { if (Str(body["name"]) is { Length: > 0 } n) hub.Db.Exec("UPDATE devices SET name=? WHERE id=?", n, id); return Results.NoContent(); });
        a.MapDelete("/devices/{id}", (Hub hub, string id) =>
        {
            if (id == "local") return Err(400, "invalid", "The hub's own desktop app cannot be unpaired.");
            // Kept as a revoked row so its history stays attributable; the token stops working at once.
            if (hub.Db.Exec("UPDATE devices SET is_revoked=1, token_hash=NULL WHERE id=?", id) == 0) return NotFound("Device not found.");
            hub.Db.Exec("UPDATE sessions SET state='stopped' WHERE device_id=?", id);
            hub.Log("info", $"Revoked device {id}");
            return Results.NoContent();
        });

        // ---- settings & providers ---------------------------------------------------------
        a.MapGet("/settings", (Hub hub) =>
        {
            var o = new JsonObject();
            foreach (var k in PlainSettings) o[k] = hub.Db.Get(k);
            o["server.name"] ??= hub.ServerName; o["server.port"] = hub.Port.ToString();
            return Results.Json(o, J.Opt);
        });
        a.MapPut("/settings", (Hub hub, JsonObject body) =>
        {
            bool restart = false;
            foreach (var (k, v) in body)
            {
                if (!PlainSettings.Contains(k)) return Err(400, "invalid", $"Unknown setting '{k}'.");
                var val = v?.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? (Bool(v) == true ? "true" : "false") : Str(v);
                if (k == "server.port") { if (!int.TryParse(val, out var port) || port is < 1024 or > 65535) return Err(400, "invalid", "The port must be between 1024 and 65535."); restart = port != hub.Port; }
                hub.Db.Set(k, val);
            }
            return Results.Json(new { saved = true, restartRequired = restart }, J.Opt);
        });

        a.MapGet("/providers", async (Hub hub, Providers prov, bool? live) => Results.Json(await ProviderStatus(hub, prov, live != false), J.Opt));
        a.MapPut("/providers/metadata", async (Hub hub, Providers prov, JsonObject body) =>
        {
            if (Str(body["provider"]) is { } id) { if (!prov.MetadataProviders.Any(x => x.Id == id)) return Err(400, "invalid", "Unknown metadata provider."); hub.Db.Set("metadata.provider", id); }
            if (body.ContainsKey("apiKey")) hub.SetSecret("tmdb.apiKey", Str(body["apiKey"]));
            if (Str(body["language"]) is { } lang) hub.Db.Set("tmdb.language", lang);
            var (ok, msg) = prov.Metadata.Configured ? await prov.Metadata.TestAsync() : (false, "No API key configured.");
            hub.Log("info", "Metadata provider updated: " + msg);
            return Results.Json(new { configured = prov.Metadata.Configured, connected = ok, message = msg }, J.Opt);
        });
        a.MapPut("/providers/debrid", async (Hub hub, Providers prov, JsonObject body) =>
        {
            if (Str(body["provider"]) is { } id) { if (!prov.DebridProviders.Any(x => x.Id == id)) return Err(400, "invalid", "Unknown debrid provider."); hub.Db.Set("debrid.provider", id); }
            if (body.ContainsKey("token")) hub.SetSecret($"debrid.{prov.Debrid.Id}.token", Str(body["token"]));
            if (Bool(body["proxyStreams"]) is { } px) hub.Db.Set("debrid.proxy", px ? "true" : "false");
            hub.Log("info", "Debrid provider settings updated");
            return Results.Json(((dynamic)await ProviderStatus(hub, prov, true)).debrid, J.Opt);
        });
        a.MapDelete("/providers/debrid", (Hub hub, Providers prov) =>
        {
            hub.SetSecret($"debrid.{prov.Debrid.Id}.token", null);
            hub.Db.Exec("UPDATE sources SET is_missing=1 WHERE kind='debrid'"); hub.Catalog.Invalidate();
            hub.Log("info", "Debrid provider disconnected");
            return Results.NoContent();
        });

        // ---- libraries --------------------------------------------------------------------
        a.MapGet("/libraries", (Hub hub) => Results.Json(hub.Db.Query("SELECT * FROM libraries ORDER BY path").Select(l => new
        {
            id = l.L("id"), path = l.S("path"), lastScan = l.LN("last_scan"), available = Directory.Exists(l.S("path")),
            files = hub.Db.Long("SELECT COUNT(*) FROM sources WHERE library_id=? AND is_missing=0", l.L("id")), missing = hub.Db.Long("SELECT COUNT(*) FROM sources WHERE library_id=? AND is_missing=1", l.L("id")),
        }), J.Opt));
        a.MapPost("/libraries", (Hub hub, Library lib, JsonObject body) =>
        {
            var path = Str(body["path"])?.Trim();
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return Err(400, "invalid", "Enter the full path of a folder.");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(path)) return Err(400, "not_found", "That folder does not exist or cannot be read by the DJFlix service.");
            if (hub.Db.One("SELECT 1 x FROM libraries WHERE path=?", path) != null) return Err(409, "exists", "That folder is already in the library.");
            var id = hub.Db.Insert("INSERT INTO libraries(path,added_at) VALUES(?,?)", path, Db.Now());
            _ = Task.Run(lib.ScanAsync);
            return Results.Json(new { id, path }, J.Opt, statusCode: 201);
        });
        a.MapDelete("/libraries/{id:long}", (Hub hub, long id) =>
        {
            hub.Db.Tx(() =>
            {
                hub.Db.Exec("DELETE FROM sources WHERE library_id=?", id);
                hub.Db.Exec("DELETE FROM libraries WHERE id=?", id);
                hub.Db.Exec("DELETE FROM media WHERE id NOT IN (SELECT DISTINCT media_id FROM sources)");
            });
            hub.Catalog.Invalidate();
            return Results.NoContent();
        });
        a.MapPost("/library/scan", (Library lib, bool? wait) => wait == true ? ScanAndReport(lib) : Task.FromResult(Start(lib)));
        a.MapGet("/library/status", (Library lib) => Results.Json(lib.Status, new System.Text.Json.JsonSerializerOptions(J.Opt) { IncludeFields = true }));
        a.MapGet("/library/unmatched", (Hub hub) => Results.Json(hub.Catalog.Cards(hub.Catalog.All.Where(m => !m.IsMatched), null), J.Opt));
        a.MapGet("/library/duplicates", (Library lib) => Results.Json(lib.Duplicates(), J.Opt));
        a.MapGet("/library/missing", (Hub hub) => Results.Json(hub.Db.Query("SELECT s.id, s.media_id, s.kind, s.ref, s.file_name FROM sources s WHERE s.is_missing=1").Select(s => new
        { id = s.L("id"), mediaId = s.L("media_id"), title = hub.Catalog.Get(s.L("media_id"))?.Title, kind = s.S("kind"), fileName = s.SN("file_name"), path = s.S("kind") == "local" ? s.S("ref") : null }), J.Opt));
        a.MapDelete("/library/missing", (Hub hub) =>
        {
            hub.Db.Tx(() => { hub.Db.Exec("DELETE FROM sources WHERE is_missing=1"); hub.Db.Exec("DELETE FROM media WHERE id NOT IN (SELECT DISTINCT media_id FROM sources)"); });
            hub.Catalog.Invalidate(); return Results.NoContent();
        });
        a.MapGet("/library/stats", (Hub hub) =>
        {
            var all = hub.Catalog.All; var db = hub.Db;
            return Results.Json(new
            {
                movies = all.Count(m => m.IsMovie), shows = all.Count(m => !m.IsMovie), episodes = db.Long("SELECT COUNT(DISTINCT episode_id) FROM sources WHERE episode_id IS NOT NULL AND is_missing=0"),
                files = db.Long("SELECT COUNT(*) FROM sources WHERE is_missing=0"), localBytes = db.Long("SELECT IFNULL(SUM(size),0) FROM sources WHERE kind='local' AND is_missing=0"),
                debridFiles = db.Long("SELECT COUNT(*) FROM sources WHERE kind='debrid' AND is_missing=0"), missing = db.Long("SELECT COUNT(*) FROM sources WHERE is_missing=1"),
                unmatched = all.Count(m => !m.IsMatched), totalRuntimeMinutes = all.Where(m => m.IsMovie).Sum(m => m.Runtime ?? 0),
                genres = all.SelectMany(m => m.Genres).GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => new { name = x.Key, count = x.Count() }),
                byDecade = all.Where(m => m.Year != null).GroupBy(m => m.Year!.Value / 10 * 10).OrderBy(x => x.Key).Select(x => new { decade = x.Key, count = x.Count() }),
                recentlyAdded = hub.Catalog.Cards(all.Where(m => m.Available).OrderByDescending(m => m.AddedAt).Take(10), null),
            }, J.Opt);
        });

        // ---- metadata editing -------------------------------------------------------------
        a.MapGet("/metadata/search", async (Providers prov, string type, string q, int? year) =>
        {
            try { return Results.Json((await prov.Metadata.SearchAsync(type == "show" ? "show" : "movie", q, year)).Take(15).Select(h => new { id = h.Id, type = h.Type, title = h.Title, year = h.Year, overview = h.Overview, poster = Catalog.Img(h.Poster, "w185") }), J.Opt); }
            catch (ProviderException e) { return Err(503, "provider_" + e.Kind, e.Message); }
        });
        a.MapPost("/media/{id:long}/match", async (Library lib, long id, JsonObject body) =>
        {
            try { return await lib.MatchAsync(id, Lng(body["tmdbId"]) ?? 0) ? Results.NoContent() : NotFound("No metadata was found for that ID."); }
            catch (ProviderException e) { return Err(503, "provider_" + e.Kind, e.Message); }
        });
        a.MapPost("/media/{id:long}/refresh", async (Hub hub, Library lib, long id) =>
        {
            var m = hub.Catalog.Get(id); if (m?.TmdbId == null) return Err(400, "unmatched", "This title is not matched to a metadata entry yet.");
            hub.Db.Exec("DELETE FROM cache WHERE key LIKE ?", $"tmdb:%/{(m.IsMovie ? "movie" : "tv")}/{m.TmdbId}%");
            try { return await lib.MatchAsync(id, m.TmdbId.Value) ? Results.NoContent() : NotFound(); }
            catch (ProviderException e) { return Err(503, "provider_" + e.Kind, e.Message); }
        });
        a.MapPatch("/media/{id:long}", (Hub hub, long id, JsonObject body) =>
        {
            if (hub.Catalog.Get(id) == null) return NotFound("Title not found.");
            var map = new Dictionary<string, string> { ["title"] = "title", ["originalTitle"] = "original_title", ["overview"] = "overview", ["releaseDate"] = "release_date", ["certification"] = "certification", ["poster"] = "poster", ["backdrop"] = "backdrop", ["trailerKey"] = "trailer_key", ["collection"] = "collection_name" };
            hub.Db.Tx(() =>
            {
                foreach (var (k, col) in map) if (body.ContainsKey(k)) hub.Db.Exec($"UPDATE media SET {col}=? WHERE id=?", Str(body[k]), id);
                if (body.ContainsKey("year")) hub.Db.Exec("UPDATE media SET year=? WHERE id=?", Lng(body["year"]), id);
                if (body.ContainsKey("runtime")) hub.Db.Exec("UPDATE media SET runtime=? WHERE id=?", Lng(body["runtime"]), id);
                if (body.ContainsKey("rating")) hub.Db.Exec("UPDATE media SET rating=? WHERE id=?", body["rating"]?.GetValue<double>(), id);
                if (body.ContainsKey("certification")) hub.Db.Exec("UPDATE media SET maturity=? WHERE id=?", Library.Maturity(Str(body["certification"])), id);
                foreach (var (k, col) in new[] { ("genres", "genres_json"), ("directors", "directors_json"), ("studios", "studios_json"), ("keywords", "keywords_json") })
                    if (body[k] is JsonArray arr) hub.Db.Exec($"UPDATE media SET {col}=? WHERE id=?", arr.ToJsonString(), id);
                if (Str(body["title"]) is { } t) hub.Db.Exec("UPDATE media SET sort_title=? WHERE id=?", Library.Norm(t), id);
                hub.Db.Exec("UPDATE media SET is_manual=1, updated_at=? WHERE id=?", Db.Now(), id);
            });
            hub.Catalog.Invalidate();
            return Results.NoContent();
        });
        a.MapDelete("/media/{id:long}", (Hub hub, long id) => { hub.Db.Exec("DELETE FROM media WHERE id=?", id); hub.Catalog.Invalidate(); return Results.NoContent(); });
        a.MapPatch("/episodes/{id:long}", (Hub hub, long id, JsonObject body) =>
        {
            if (hub.Catalog.Episode(id) == null) return NotFound("Episode not found.");
            foreach (var (k, col) in new[] { ("introStartMs", "intro_start_ms"), ("introEndMs", "intro_end_ms"), ("creditsStartMs", "credits_start_ms") })
                if (body.ContainsKey(k)) hub.Db.Exec($"UPDATE episodes SET {col}=? WHERE id=?", Lng(body[k]), id);
            if (body.ContainsKey("title")) hub.Db.Exec("UPDATE episodes SET title=? WHERE id=?", Str(body["title"]), id);
            hub.Catalog.Invalidate();
            return Results.NoContent();
        });
        a.MapDelete("/sources/{id:long}", (Hub hub, long id) =>
        {
            hub.Db.Tx(() => { hub.Db.Exec("DELETE FROM sources WHERE id=?", id); hub.Db.Exec("DELETE FROM media WHERE id NOT IN (SELECT DISTINCT media_id FROM sources)"); });
            hub.Catalog.Invalidate(); return Results.NoContent();
        });

        // ---- storage, cache, backup -------------------------------------------------------
        a.MapGet("/storage", (Hub hub) =>
        {
            var img = new DirectoryInfo(Path.Combine(hub.DataDir, "cache", "images"));
            var files = img.Exists ? img.GetFiles() : [];
            return Results.Json(new
            {
                dataDir = hub.DataDir, databaseBytes = new FileInfo(hub.Db.Path).Length, imageCacheBytes = files.Sum(f => f.Length), imageCacheFiles = files.Length,
                metadataCacheEntries = hub.Db.Long("SELECT COUNT(*) FROM cache"), metadataCacheBytes = hub.Db.Long("SELECT IFNULL(SUM(LENGTH(value)),0) FROM cache"),
            }, J.Opt);
        });
        a.MapPost("/cache/clear", (Hub hub, JsonObject body) =>
        {
            if (Bool(body["images"]) != false) foreach (var f in Directory.EnumerateFiles(Path.Combine(hub.DataDir, "cache", "images"))) { try { File.Delete(f); } catch { } }
            if (Bool(body["metadata"]) != false) hub.Db.Exec("DELETE FROM cache");
            hub.Log("info", "Cache cleared");
            return Results.NoContent();
        });
        a.MapGet("/backup", (Hub hub) =>
        {
            // Fold the write-ahead log into the main file so a plain copy is a complete backup.
            var bytes = hub.Db.Locked(() =>
            {
                hub.Db.Query("PRAGMA wal_checkpoint(TRUNCATE)");
                // SQLite keeps the file open for writing; Windows needs a matching share mode to read it.
                using var fs = new FileStream(hub.Db.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream(); fs.CopyTo(ms); return ms.ToArray();
            });
            return Results.File(bytes, "application/octet-stream", $"djflix-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.djbak");
        });
        a.MapPost("/restore", async (Hub hub, HttpContext c) =>
        {
            using var ms = new MemoryStream(); await c.Request.Body.CopyToAsync(ms);
            var data = ms.ToArray();
            if (data.Length < 100 || System.Text.Encoding.ASCII.GetString(data, 0, 15) != "SQLite format 3") return Err(400, "invalid", "That file is not a DJFlix backup.");
            await File.WriteAllBytesAsync(Path.Combine(hub.DataDir, "restore.pending"), data);
            hub.Log("info", "Backup staged; restarting to apply it"); hub.RestartRequested = true;
            _ = Task.Delay(300).ContinueWith(_ => life.StopApplication());
            return Results.Accepted();
        });
        a.MapPost("/reset", (Hub hub, JsonObject body) =>
        {
            if (Str(body["confirm"]) != "RESET") return Err(400, "confirm", "Send confirm: \"RESET\" to erase all DJFlix data.");
            File.WriteAllText(Path.Combine(hub.DataDir, "reset.pending"), "1"); hub.RestartRequested = true;
            _ = Task.Delay(300).ContinueWith(_ => life.StopApplication());
            return Results.Accepted();
        });
    }

    static IResult Start(Library lib) { _ = Task.Run(lib.ScanAsync); return Results.Accepted(); }
    static async Task<IResult> ScanAndReport(Library lib)
    {
        while (lib.Status.Running) await Task.Delay(100);   // let a scan already in flight finish, then run a fresh one
        await lib.ScanAsync();
        return Results.Json(lib.Status, new System.Text.Json.JsonSerializerOptions(J.Opt) { IncludeFields = true });
    }
}
