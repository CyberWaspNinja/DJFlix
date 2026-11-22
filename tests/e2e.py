"""End-to-end test of the DJFlix server against fake upstreams. Usage: python3 e2e.py <path to DJFlix.Server.dll>"""
import json, os, shutil, socket, subprocess, sys, tempfile, time, urllib.request, urllib.error

DLL = sys.argv[1]; PORT = 18674; UP = 19001
ROOT = os.path.realpath(tempfile.mkdtemp(prefix="djflix-")); DATA = os.path.join(ROOT, "data"); MEDIA = os.path.join(ROOT, "media")
BASE = f"http://127.0.0.1:{PORT}/api/v1"
results = []

def check(name, cond, info=""):
    results.append((name, bool(cond)))
    print(("PASS  " if cond else "FAIL  ") + name + (f"   [{info}]" if info and not cond else ""))

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k): return None
opener = urllib.request.build_opener(NoRedirect)

def call(method, path, token=None, body=None, headers=None, raw=False, base=BASE):
    h = dict(headers or {})
    if token: h["Authorization"] = "Bearer " + token
    data = None
    if body is not None:
        data = body if isinstance(body, bytes) else json.dumps(body).encode(); h.setdefault("Content-Type", "application/json")
    req = urllib.request.Request(base + path, data=data, method=method, headers=h)
    try:
        r = opener.open(req, timeout=30); code = r.status; b = r.read(); hd = r.headers
    except urllib.error.HTTPError as e:
        code = e.code; b = e.read(); hd = e.headers
    if raw: return code, b, hd
    try: return code, json.loads(b) if b else None
    except Exception: return code, b

def touch(rel, size=2048, text=None):
    p = os.path.normpath(os.path.join(MEDIA, rel)); os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "wb") as f: f.write(text.encode() if text else bytes(i % 251 for i in range(size)))
    return p

def start_hub():
    env = dict(os.environ, DJFLIX_DATA=DATA, DJFLIX_PORT=str(PORT), DJFLIX_TMDB_BASE=f"http://127.0.0.1:{UP}/3", DJFLIX_RD_BASE=f"http://127.0.0.1:{UP}/rd", DJFLIX_TMDB_IMAGE_BASE=f"http://127.0.0.1:{UP}/img")
    p = subprocess.Popen(["dotnet", DLL], env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    try: wait_up()
    except SystemExit:
        p.terminate(); print(p.communicate(timeout=10)[0].decode(errors="replace")[-4000:]); up.terminate(); raise
    return p

def wait_up():
    for _ in range(60):
        try:
            if call("GET", "/health")[0] == 200: return
        except Exception: pass
        time.sleep(0.2)
    raise SystemExit("hub did not start")

up = subprocess.Popen([sys.executable, os.path.join(os.path.dirname(__file__), "fake_upstream.py"), str(UP)])
inception = touch("Movies/Inception (2010)/Inception.2010.1080p.BluRay.x264.mkv", 50000)
touch("Movies/Inception (2010)/Inception.2010.1080p.BluRay.x264.en.srt", text="1\n00:00:01,000 --> 00:00:02,000\nHello\n")
touch("Movies/Interstellar.2014.2160p.UHD.mkv"); touch("Movies/The.Dark.Knight.2008.720p.mp4"); touch("Movies/Toy Story (1995)/toy story.mp4")
touch("Movies/Home Video Birthday.mov")
for e in (1, 2, 3): touch(f"TV/Breaking Bad/Season 1/Breaking.Bad.S01E0{e}.720p.mkv")
touch("TV/Breaking Bad/Season 2/2x01.mkv")
hub = start_hub()
try:
    ADMIN = open(os.path.join(DATA, "admin.token")).read().strip()
    # ---- 1. server, health, auth ---------------------------------------------------------
    c, h = call("GET", "/health"); check("server starts and reports health", c == 200 and h["apiVersion"] == 1 and h["requiresPairing"])
    check("unauthenticated request is refused", call("GET", "/profiles")[0] == 401)
    check("bad token is refused", call("GET", "/profiles", "nope")[0] == 401)

    # ---- pairing -------------------------------------------------------------------------
    c, pc = call("POST", "/admin/pairing/code", ADMIN); code = pc["code"]
    check("pairing code is generated", c == 200 and len(code) == 6)
    check("wrong pairing code is rejected", call("POST", "/pair/request", body={"deviceName": "Evil", "code": "000000" if code != "000000" else "111111"})[0] == 403)
    c, pr = call("POST", "/pair/request", body={"deviceName": "Living Room Fire TV", "deviceKind": "firetv", "code": code})
    check("pairing with code returns a device token", c == 200 and pr["status"] == "paired" and pr["token"]); TV = pr["token"]; TV_ID = pr["deviceId"]
    check("pairing code is single-use", call("POST", "/pair/request", body={"deviceName": "Again", "code": code})[0] == 403)
    c, pend = call("POST", "/pair/request", body={"deviceName": "Bedroom Stick", "deviceKind": "firetv"})
    check("pairing without code waits for approval", pend["status"] == "pending" and call("GET", f"/pair/status/{pend['requestId']}?secret={pend['secret']}")[1]["status"] == "pending")
    c, plist = call("GET", "/admin/pairing", ADMIN)
    check("pending request is listed for the PC", any(x["deviceName"] == "Bedroom Stick" for x in plist["pending"]))
    call("POST", f"/admin/pairing/{pend['requestId']}/approve", ADMIN)
    c, st = call("GET", f"/pair/status/{pend['requestId']}?secret={pend['secret']}"); TV2 = st.get("token"); TV2_ID = st.get("deviceId")
    check("approved request yields a token exactly once", st["status"] == "paired" and TV2 and call("GET", f"/pair/status/{pend['requestId']}?secret={pend['secret']}")[0] == 404)
    check("paired device can call the API", call("GET", "/profiles", TV)[0] == 200)
    check("paired device cannot reach admin endpoints", call("GET", "/admin/status", TV)[0] == 403 and call("GET", "/admin/devices", TV)[0] == 403)

    # ---- providers -----------------------------------------------------------------------
    c, r = call("PUT", "/admin/providers/metadata", ADMIN, {"apiKey": "wrong"}); check("bad TMDB key is reported", c == 200 and r["connected"] is False)
    c, r = call("PUT", "/admin/providers/metadata", ADMIN, {"apiKey": "tmdbgood"}); check("TMDB connection test passes", r["connected"] is True)
    c, r = call("PUT", "/admin/providers/debrid", ADMIN, {"provider": "realdebrid", "token": "bad"}); check("bad Real-Debrid token is reported", r["connected"] is False and r["errorKind"] == "auth")
    c, r = call("PUT", "/admin/providers/debrid", ADMIN, {"token": "rdgood"}); check("Real-Debrid account status loads", r["connected"] is True and r["account"]["premium"])
    dbbytes = b"".join(open(os.path.join(DATA, f), "rb").read() for f in os.listdir(DATA) if f.startswith("djflix.db"))
    check("provider credentials are encrypted at rest", b"rdgood" not in dbbytes and b"tmdbgood" not in dbbytes)
    c, s = call("GET", "/admin/settings", ADMIN); c2, pv = call("GET", "/admin/providers", ADMIN)
    check("credentials are never returned by the API", "rdgood" not in json.dumps(s) + json.dumps(pv) and "tmdbgood" not in json.dumps(s) + json.dumps(pv))

    # ---- library -------------------------------------------------------------------------
    check("library folder can be added", call("POST", "/admin/libraries", ADMIN, {"path": MEDIA})[0] == 201)
    check("same folder twice is rejected", call("POST", "/admin/libraries", ADMIN, {"path": MEDIA})[0] == 409)
    check("missing folder is rejected", call("POST", "/admin/libraries", ADMIN, {"path": os.path.join(ROOT, "nope")})[0] == 400)
    c, scan = call("POST", "/admin/library/scan?wait=true", ADMIN); check("library scan completes", c == 200 and scan["lastError"] is None, scan)
    c, P = call("POST", "/profiles", TV, {"name": "DG", "prefs": {"favouriteGenres": ["Science Fiction"]}}); PID = P["id"]
    c, K = call("POST", "/profiles", TV, {"name": "Kids", "maxMaturity": 1, "pin": "1234"}); KID = K["id"]
    c, mv = call("GET", f"/movies?profileId={PID}&pageSize=100", TV); c, sh = call("GET", f"/shows?profileId={PID}", TV)
    titles = sorted(x["title"] for x in mv["items"]); M = {x["title"]: x for x in mv["items"]}
    check("movies are matched from local files and debrid cloud", titles == ["Gone Movie", "Heat", "Home Video Birthday", "Inception", "Interstellar", "The Dark Knight", "Toy Story"], titles)
    check("movies and shows are separated", all(x["type"] == "movie" for x in mv["items"]) and [x["title"] for x in sh["items"]] == ["Breaking Bad"] and sh["items"][0]["type"] == "show")
    check("unmatched file falls back to its file name", M["Home Video Birthday"]["isMatched"] is False)
    SHOW = sh["items"][0]["id"]
    c, eps = call("GET", f"/shows/{SHOW}/episodes?profileId={PID}", TV)
    avail = sorted((e["season"], e["episode"]) for e in eps if e["available"])
    check("episodes come from local folders and debrid, with metadata", avail == [(1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (2, 3)] and eps[0]["title"] == "S1 Episode 1 Title", avail)
    c, d = call("GET", f"/media/{M['Inception']['id']}?profileId={PID}", TV)
    check("details include cast, director, IMDb ID, trailer", d["imdbId"] == "tt1375666" and d["directors"] == ["Christopher Nolan"] and d["cast"][0]["name"] == "Leonardo DiCaprio" and d["trailer"]["key"] == "YoHD9XEInc0" and d["card"]["certification"] == "PG-13")
    check("title without trailer reports none", call("GET", f"/media/{M['Heat']['id']}", TV)[1]["trailer"] is None)
    check("local file paths are not exposed to TV clients", all(s["path"] is None for s in d["sources"]) and call("GET", f"/media/{M['Inception']['id']}", ADMIN)[1]["sources"][0]["path"] == inception)
    sim = [x["title"] for x in d["similar"]]; check("similar titles are recommended", sim[:1] == ["Interstellar"] and "The Dark Knight" in sim, sim)
    c, img, hd = call("GET", d["card"]["poster"][len("/api/v1"):], raw=True); check("artwork is proxied and cached", c == 200 and img.startswith(b"\xff\xd8") and os.listdir(os.path.join(DATA, "cache", "images")))
    check("image path traversal is blocked", call("GET", "/images/w342/..%2F..%2Fadmin.token", raw=True)[0] == 404)

    # ---- maturity / PIN ------------------------------------------------------------------
    c, kmv = call("GET", f"/movies?profileId={KID}", TV); kt = sorted(x["title"] for x in kmv["items"])
    check("maturity limit filters the kids profile", kt == ["Toy Story"], kt)
    check("restricted title cannot be played by the kids profile", call("POST", "/playback/sessions", TV, {"profileId": KID, "mediaId": M["Heat"]["id"]})[0] == 403)
    check("PIN verification works", call("POST", f"/profiles/{KID}/verify-pin", TV, {"pin": "0000"})[1]["ok"] is False and call("POST", f"/profiles/{KID}/verify-pin", TV, {"pin": "1234"})[1]["ok"] is True)
    check("PIN-protected profile cannot be edited without the PIN", call("PATCH", f"/profiles/{KID}", TV, {"name": "Hacked"})[0] == 403 and call("PATCH", f"/profiles/{KID}", TV, {"name": "Children", "currentPin": "1234"})[0] == 200)

    # ---- search --------------------------------------------------------------------------
    check("search is partial and case-insensitive", [x["title"] for x in call("GET", f"/search?q=incep&profileId={PID}", TV)[1]["results"]] == ["Inception"])
    r = call("GET", f"/search?q=NOLAN&profileId={PID}", TV)[1]; check("search finds titles by director", len(r["results"]) == 3 and r["people"][0]["name"] == "Christopher Nolan")
    check("search finds titles by actor", {x["title"] for x in call("GET", f"/search?q=caine&profileId={PID}", TV)[1]["results"]} == {"Interstellar", "The Dark Knight"})
    check("search type filter works", [x["title"] for x in call("GET", f"/search?q=b&type=show&profileId={PID}", TV)[1]["results"]] == ["Breaking Bad"])
    check("search by genre and collection", "The Dark Knight Collection" in call("GET", f"/search?q=dark%20knight&profileId={PID}", TV)[1]["collections"] and len(call("GET", f"/search?q=crime&profileId={PID}", TV)[1]["results"]) == 3)
    call("POST", f"/profiles/{PID}/search-history", TV, {"query": "nolan"}); call("POST", f"/profiles/{PID}/search-history", TV, {"query": "nolan"})
    check("recent searches are stored without duplicates and can be cleared", call("GET", f"/profiles/{PID}/search-history", TV)[1] == ["nolan"] and call("DELETE", f"/profiles/{PID}/search-history", TV)[0] == 204 and call("GET", f"/profiles/{PID}/search-history", TV)[1] == [])
    check("filter and sort", [x["title"] for x in call("GET", f"/movies?profileId={PID}&genre=Action&sort=year&order=asc", TV)[1]["items"]] == ["Heat", "The Dark Knight", "Inception"])

    # ---- watchlist -----------------------------------------------------------------------
    for _ in range(2): call("PUT", f"/profiles/{PID}/watchlist/{M['Heat']['id']}", TV)
    call("PUT", f"/profiles/{PID}/watchlist/{SHOW}", TV2)
    wl = call("GET", f"/profiles/{PID}/watchlist", TV)[1]
    check("watchlist prevents duplicates and syncs across devices", sorted(x["title"] for x in wl) == ["Breaking Bad", "Heat"])
    check("watchlists are isolated per profile", call("GET", f"/profiles/{KID}/watchlist", TV)[1] == [])
    check("watchlist filter by type", [x["title"] for x in call("GET", f"/profiles/{PID}/watchlist?type=show", TV)[1]] == ["Breaking Bad"])
    call("DELETE", f"/profiles/{PID}/watchlist/{M['Heat']['id']}", TV); check("watchlist removal works", [x["title"] for x in call("GET", f"/profiles/{PID}/watchlist", TV)[1]] == ["Breaking Bad"])
    call("PUT", f"/profiles/{PID}/ratings/{M['Inception']['id']}", TV, {"rating": 5})
    check("personal rating is stored", call("GET", f"/media/{M['Inception']['id']}?profileId={PID}", TV)[1]["card"]["userRating"] == 5)

    # ---- playback: local -----------------------------------------------------------------
    c, s1 = call("POST", "/playback/sessions", ADMIN, {"profileId": PID, "mediaId": M["Inception"]["id"]})
    check("playback session starts on Windows", c == 201 and s1["kind"] == "local" and s1["streamUrl"].endswith(".mkv") and s1["resumePositionMs"] == 0, s1)
    c, b, hd = call("GET", s1["streamUrl"][len("/api/v1"):], raw=True, headers={"Range": "bytes=100-199"})
    check("stream supports byte ranges (seeking)", c == 206 and len(b) == 100 and b[0] == 100 % 251 and hd["Content-Range"] == "bytes 100-199/50000")
    check("stream rejects a wrong play token", call("GET", s1["streamUrl"][len("/api/v1"):].replace(s1["streamUrl"].split("/")[5], "x" * 32), raw=True)[0] == 401)
    check("sidecar subtitles are offered and served", len(s1["subtitles"]) == 1 and s1["subtitles"][0]["language"] == "en" and b"Hello" in call("GET", s1["subtitles"][0]["url"][len("/api/v1"):], raw=True)[1])
    DUR = 148 * 60000
    c, r = call("PUT", f"/profiles/{PID}/progress", ADMIN, {"mediaId": M["Inception"]["id"], "positionMs": 600000, "durationMs": DUR, "sessionId": s1["sessionId"], "event": "pause"})
    check("progress is saved on pause", r["accepted"] and not r["isCompleted"])
    cw = call("GET", f"/profiles/{PID}/continue-watching", TV)[1]
    check("Continue Watching shows the title on the other device", len(cw) == 1 and cw[0]["title"] == "Inception" and cw[0]["progress"]["positionMs"] == 600000)
    c, s2 = call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": M["Inception"]["id"]})
    check("cross-device resume: Fire TV resumes where Windows paused", s2["resumePositionMs"] == 600000 and s2["hasProgress"])
    check("play from beginning is offered", call("POST", "/playback/sessions", TV2, {"profileId": PID, "mediaId": M["Inception"]["id"], "startOver": True})[1]["resumePositionMs"] == 0)
    c, r = call("PUT", f"/profiles/{PID}/progress", TV, {"mediaId": M["Inception"]["id"], "positionMs": 900000, "durationMs": DUR, "sessionId": s2["sessionId"], "event": "tick"}); check("Fire TV progress is accepted", r["accepted"])
    c, r = call("PUT", f"/profiles/{PID}/progress", ADMIN, {"mediaId": M["Inception"]["id"], "positionMs": 610000, "durationMs": DUR, "sessionId": s1["sessionId"], "event": "tick", "ageMs": 120000})
    check("stale queued update does not overwrite newer progress", r["accepted"] is False and r["reason"] == "stale" and r["current"]["positionMs"] == 900000)
    c, r = call("PUT", f"/profiles/{PID}/progress", ADMIN, {"mediaId": M["Inception"]["id"], "positionMs": 905000, "durationMs": DUR, "sessionId": s1["sessionId"], "event": "tick"})
    check("simultaneous sessions are detected and reported", r["accepted"] and r["otherSession"] and r["otherSession"]["deviceName"] == "Living Room Fire TV", r)
    check("progress is isolated per profile", call("GET", f"/profiles/{KID}/continue-watching", TV)[1] == [])
    c, r = call("PUT", f"/profiles/{PID}/progress", TV, {"mediaId": M["Inception"]["id"], "positionMs": DUR - 60000, "durationMs": DUR, "sessionId": s2["sessionId"], "event": "stop"})
    check("finishing a title marks it completed and removes it from Continue Watching", r["isCompleted"] and call("GET", f"/profiles/{PID}/continue-watching", TV)[1] == [])
    check("completed title restarts from the beginning", call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": M["Inception"]["id"]})[1]["resumePositionMs"] == 0)
    hist = call("GET", f"/profiles/{PID}/history", TV)[1]; check("watch history is recorded once per session", len(hist) == 2 and hist[0]["title"] == "Inception", hist)
    home = call("GET", f"/home?profileId={PID}", TV)[1]; rows = {r["id"]: r for r in home["rows"]}
    check("home has hero and dynamic rows", home["hero"] and {"recent", "trending", "popular_movies", "popular_shows", "recommended", "scifi", "mylist"} <= set(rows), list(rows))
    check("'Because You Watched' row follows history", rows["because"]["title"] == "Because You Watched Inception" and rows["because"]["items"][0]["title"] == "Interstellar")
    check("recommendations exclude watched titles and favour preferred genres", rows["recommended"]["items"][0]["title"] == "Interstellar" and "Inception" not in [x["title"] for x in rows["recommended"]["items"]])
    call("PATCH", f"/profiles/{PID}", TV, {"prefs": {"homeRows": [{"id": "trending", "enabled": True}, {"id": "recent", "enabled": False}]}})
    h2 = call("GET", f"/home?profileId={PID}", TV)[1]; check("home rows can be reordered and hidden", h2["rows"][0]["id"] == "trending" and "recent" not in [r["id"] for r in h2["rows"]])
    check("profile preferences merge instead of overwrite", "favouriteGenres" in call("GET", "/profiles", TV)[1][0]["prefs"])

    # ---- TV: next episode ----------------------------------------------------------------
    E = {(e["season"], e["episode"]): e["id"] for e in eps}
    c, t1 = call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": SHOW})
    check("playing a show starts at the first episode", t1["episode"]["season"] == 1 and t1["episode"]["episode"] == 1 and t1["next"]["episode"] == 2 and t1["previous"] is None)
    call("PUT", f"/profiles/{PID}/progress", TV, {"mediaId": SHOW, "episodeId": E[(1, 1)], "positionMs": 47 * 60000, "durationMs": 47 * 60000, "sessionId": t1["sessionId"], "event": "ended"})
    c, t2 = call("POST", "/playback/sessions", ADMIN, {"profileId": PID, "mediaId": SHOW})
    check("next unwatched episode is offered on the other device", t2["episode"]["episode"] == 2 and t2["resumePositionMs"] == 0)
    call("PUT", f"/profiles/{PID}/progress", ADMIN, {"mediaId": SHOW, "episodeId": E[(1, 2)], "positionMs": 300000, "durationMs": 47 * 60000, "sessionId": t2["sessionId"], "event": "pause"})
    cw = call("GET", f"/profiles/{PID}/continue-watching", TV)[1]
    check("Continue Watching tracks the episode in progress", cw[0]["title"] == "Breaking Bad" and cw[0]["progress"]["episode"] == 2 and cw[0]["progress"]["positionMs"] == 300000)
    call("POST", f"/profiles/{PID}/watched", TV, {"mediaId": SHOW, "season": 1})
    check("marking a season watched moves Up Next to season 2", call("GET", f"/media/{SHOW}?profileId={PID}", TV)[1]["upNext"]["season"] == 2)
    check("progress can be reset", call("DELETE", f"/profiles/{PID}/progress/{SHOW}", TV)[0] == 204 and call("GET", f"/media/{SHOW}?profileId={PID}", TV)[1]["upNext"]["episode"] == 1)

    # ---- playback: debrid ----------------------------------------------------------------
    c, d1 = call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": M["Heat"]["id"]})
    check("debrid playback session is created by the hub", c == 201 and d1["kind"] == "debrid", d1)
    check("no provider credential or restricted link reaches the TV", "rdgood" not in json.dumps(d1) and "real-debrid.com" not in json.dumps(d1))
    c, b, hd = call("GET", d1["streamUrl"][len("/api/v1"):], raw=True)
    check("debrid stream redirects to the provider's playback link", c == 302 and "/cdn/Heat.1995.1080p.BluRay.mkv" in hd["Location"])
    c, b2, hd2 = call("GET", d1["streamUrl"][len("/api/v1"):] + "?refresh=1", raw=True)
    check("expired link is re-resolved on request", c == 302 and hd2["Location"] != hd["Location"])
    call("PUT", "/admin/providers/debrid", ADMIN, {"proxyStreams": True})
    c, b, hd = call("GET", d1["streamUrl"][len("/api/v1"):], raw=True, headers={"Range": "bytes=256-511"})
    check("proxy mode relays ranged bytes through the hub", c == 206 and b == bytes(range(256)) and hd["Content-Range"].startswith("bytes 256-511/"))
    call("PUT", "/admin/providers/debrid", ADMIN, {"proxyStreams": False})
    call("GET", "/ctl?mode=429once", base=f"http://127.0.0.1:{UP}")
    check("provider rate limit is retried", call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": M["Heat"]["id"]})[0] == 201)
    call("GET", "/ctl?mode=down", base=f"http://127.0.0.1:{UP}")
    c, r = call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": M["Heat"]["id"]}); check("provider downtime gives a clear error", c == 503 and r["error"] == "provider_unavailable")
    check("local playback still works while the provider is down", call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": M["Interstellar"]["id"]})[0] == 201)
    call("GET", "/ctl?mode=ok", base=f"http://127.0.0.1:{UP}")
    gone = [x for x in call("GET", f"/movies?profileId={PID}&q=gone", TV)[1]["items"]]
    c, r = call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": gone[0]["id"]}) if gone else (0, {})
    check("removed provider file gives 'expired' and is flagged unavailable", c == 410 and r["error"] == "provider_expired" and call("GET", f"/movies?profileId={PID}&q=gone", TV)[1]["items"] == [], (c, r))
    call("PUT", "/admin/providers/debrid", ADMIN, {"token": "revoked"})
    c, r = call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": M["Heat"]["id"]}); check("provider authentication failure is surfaced", c == 502 and r["error"] == "provider_auth")
    call("PUT", "/admin/providers/debrid", ADMIN, {"token": "rdgood"})

    # ---- library maintenance -------------------------------------------------------------
    touch("Movies/Inception (2010)/Inception.2010.720p.copy.mkv"); os.remove(os.path.join(MEDIA, "Movies/The.Dark.Knight.2008.720p.mp4"))
    call("POST", "/admin/library/scan?wait=true", ADMIN)
    check("duplicate files are identified", any(x["title"] == "Inception" and len(x["files"]) == 2 for x in call("GET", "/admin/library/duplicates", ADMIN)[1]))
    check("missing files are flagged and hidden from browsing", any(x["title"] == "The Dark Knight" for x in call("GET", "/admin/library/missing", ADMIN)[1]) and "The Dark Knight" not in [x["title"] for x in call("GET", f"/movies?profileId={PID}", TV)[1]["items"]])
    check("highest quality local file is preferred", call("POST", "/playback/sessions", TV, {"profileId": PID, "mediaId": M["Inception"]["id"]})[1]["quality"] == "1080p")
    hv = M["Home Video Birthday"]["id"]
    call("PATCH", f"/admin/media/{hv}", ADMIN, {"title": "Birthday 2019", "year": 2019, "genres": ["Family"]})
    check("metadata can be edited manually", call("GET", f"/media/{hv}", TV)[1]["card"]["title"] == "Birthday 2019" and call("GET", f"/media/{hv}", TV)[1]["metadataSource"] == "manual")
    hits = call("GET", "/admin/metadata/search?type=movie&q=toy", ADMIN)[1]; check("manual metadata search works", hits[0]["title"] == "Toy Story")
    st = call("GET", "/admin/library/stats", ADMIN)[1]; check("library statistics", st["shows"] == 1 and st["episodes"] == 6 and st["debridFiles"] >= 3, st)
    ps = call("GET", f"/profiles/{PID}/stats", TV)[1]; check("watch-time statistics", ps["watchTimeMs"] > 0 and ps["moviesCompleted"] == 1 and ps["genres"])

    # ---- dashboard, discovery, revoke ----------------------------------------------------
    c, stt = call("GET", "/admin/status?live=true", ADMIN)
    check("dashboard reports service, clients and providers", stt["service"]["status"] == "running" and stt["clients"]["paired"] == 2 and stt["providers"]["debrid"]["connected"] and stt["connection"]["port"] == PORT, stt)
    logs = json.dumps(call("GET", "/admin/logs?limit=500", ADMIN)[1], ensure_ascii=False); tok = s1["streamUrl"].split("/")[5]
    check("logs contain requests but no tokens or credentials", "/api/v1/stream/…" in logs and tok not in logs and TV not in logs and "rdgood" not in logs and ADMIN not in logs)
    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); u.settimeout(3); u.sendto(b"DJFLIX_DISCOVER", ("127.0.0.1", 47810))
    try: disc = json.loads(u.recvfrom(2048)[0])
    except Exception as e: disc = {"err": str(e)}
    check("LAN discovery answers with the hub's port", disc.get("service") == "djflix" and disc.get("port") == PORT, disc)
    check("device can be revoked", call("DELETE", f"/admin/devices/{TV2_ID}", ADMIN)[0] == 204 and call("GET", "/profiles", TV2)[0] == 401 and call("GET", "/profiles", TV)[0] == 200)
    lim = [call("POST", "/pair/request", body={"deviceName": "x", "code": "999999"})[0] for _ in range(12)]
    check("pairing attempts are rate limited", 429 in lim, lim)

    # ---- persistence, backup, restart ----------------------------------------------------
    c, bak, _ = call("GET", "/admin/backup", ADMIN, raw=True); check("backup downloads a database copy", c == 200 and bak[:15] == b"SQLite format 3")
    check("restart is accepted", call("POST", "/admin/restart", ADMIN)[0] == 202); time.sleep(1.5); wait_up()
    check("hub restarts in place and keeps pairing, profiles and progress", hub.poll() is None and call("GET", "/profiles", TV)[0] == 200 and len(call("GET", "/profiles", TV)[1]) == 2 and call("GET", f"/profiles/{PID}/history", TV)[1])
    check("provider configuration survives restart", call("GET", "/admin/providers", ADMIN)[1]["debrid"]["connected"] is True)
    call("DELETE", f"/profiles/{KID}", ADMIN)
    check("restore is validated", call("POST", "/admin/restore", ADMIN, b"not a backup" * 20)[0] == 400)
    check("restore applies a backup", call("POST", "/admin/restore", ADMIN, bak, headers={"Content-Type": "application/octet-stream"})[0] == 202); time.sleep(1.5); wait_up()
    check("restored data is back after restart", len(call("GET", "/profiles", TV)[1]) == 2)
    hub.terminate(); hub.wait(10); hub = start_hub()
    check("cold start keeps configuration and tokens", call("GET", "/profiles", TV)[0] == 200 and call("GET", f"/movies?profileId={PID}", TV)[1]["total"] >= 4)
except Exception:
    import traceback; traceback.print_exc(); results.append(('test run completed without exception', False))
finally:
    hub.terminate(); up.terminate()
    try: out = hub.communicate(timeout=10)[0].decode()[-1500:]
    except Exception: out = ""
    failed = [n for n, ok in results if not ok]
    print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
    if failed: print("FAILED:", *failed, sep="\n  "); print(out)
    shutil.rmtree(ROOT, ignore_errors=True)
    sys.exit(1 if failed else 0)
