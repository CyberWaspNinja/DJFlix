"""Stand-in for TMDB, Real-Debrid and its CDN so the hub can be tested without internet access."""
import json, re, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

def person(n, c): return {"name": n, "character": c, "profile_path": "/" + re.sub(r"\W", "", n) + ".jpg"}
MOVIES = {
    27205: dict(title="Inception", release_date="2010-07-16", runtime=148, genres=["Action", "Science Fiction", "Adventure"], director="Christopher Nolan", cast=[person("Leonardo DiCaprio", "Cobb"), person("Tom Hardy", "Eames")], cert="PG-13", pop=90, vote=8.4, kw=["dream", "heist"], imdb="tt1375666", trailer="YoHD9XEInc0"),
    157336: dict(title="Interstellar", release_date="2014-11-05", runtime=169, genres=["Adventure", "Drama", "Science Fiction"], director="Christopher Nolan", cast=[person("Matthew McConaughey", "Cooper"), person("Michael Caine", "Brand")], cert="PG-13", pop=120, vote=8.5, kw=["space", "time"], imdb="tt0816692", trailer="zSWdZVtXT7E"),
    155: dict(title="The Dark Knight", release_date="2008-07-16", runtime=152, genres=["Action", "Crime", "Thriller"], director="Christopher Nolan", cast=[person("Christian Bale", "Bruce Wayne"), person("Michael Caine", "Alfred")], cert="PG-13", pop=110, vote=8.5, kw=["vigilante"], imdb="tt0468569", trailer=None, collection="The Dark Knight Collection"),
    862: dict(title="Toy Story", release_date="1995-11-22", runtime=81, genres=["Family", "Animation", "Comedy"], director="John Lasseter", cast=[person("Tom Hanks", "Woody")], cert="G", pop=70, vote=8.0, kw=["toy"], imdb="tt0114709", trailer="v-PjgYDrg70"),
    949: dict(title="Heat", release_date="1995-12-15", runtime=170, genres=["Action", "Crime", "Thriller"], director="Michael Mann", cast=[person("Al Pacino", "Hanna"), person("Robert De Niro", "McCauley")], cert="R", pop=40, vote=7.9, kw=["heist"], imdb="tt0113277", trailer=None),
}
SHOWS = {1396: dict(name="Breaking Bad", first_air_date="2008-01-20", genres=["Drama", "Crime"], cast=[person("Bryan Cranston", "Walter White")], creator="Vince Gilligan", cert="TV-MA", pop=200, vote=8.9, imdb="tt0903747", seasons={1: 3, 2: 3})}
STATE = {"rd_mode": "ok", "rd_calls": 0, "unrestrict": 0}
BLOB = bytes(range(256)) * 400   # 102400 bytes of predictable content

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, code, obj, headers=None):
        b = obj if isinstance(obj, bytes) else json.dumps(obj).encode()
        self.send_response(code); self.send_header("Content-Length", str(len(b)))
        for k, v in (headers or {}).items(): self.send_header(k, v)
        self.end_headers(); self.wfile.write(b)

    def do_POST(self):
        u = urlparse(self.path); n = int(self.headers.get("Content-Length", 0)); body = parse_qs(self.rfile.read(n).decode())
        if u.path == "/rd/unrestrict/link":
            if not self.rd_ok(): return
            link = body.get("link", [""])[0]; STATE["unrestrict"] += 1
            if "gone" in link: return self.send(503, {"error": "hoster_unavailable", "error_code": 19})
            name = link.rsplit("/", 1)[-1]
            return self.send(200, {"download": f"http://127.0.0.1:{PORT}/cdn/{name}?n={STATE['unrestrict']}", "filename": name, "filesize": len(BLOB), "mimeType": "video/x-matroska"})
        self.send(404, {})

    def rd_ok(self):
        STATE["rd_calls"] += 1
        if STATE["rd_mode"] == "429once": STATE["rd_mode"] = "ok"; self.send(429, {"error": "too_many_requests"}); return False
        if STATE["rd_mode"] == "down": self.send(503, {"error": "service_unavailable"}); return False
        if self.headers.get("Authorization") != "Bearer rdgood": self.send(401, {"error": "bad_token", "error_code": 8}); return False
        return True

    def do_GET(self):
        u = urlparse(self.path); q = parse_qs(u.query); p = u.path
        if p == "/ctl": STATE["rd_mode"] = q["mode"][0]; return self.send(200, STATE)
        if p == "/state": return self.send(200, STATE)
        if p.startswith("/cdn/"):
            rng = self.headers.get("Range"); data = BLOB; code = 200; hdr = {"Content-Type": "video/x-matroska", "Accept-Ranges": "bytes"}
            if rng:
                a, b = re.match(r"bytes=(\d+)-(\d*)", rng).groups(); a = int(a); b = int(b) if b else len(BLOB) - 1
                data = BLOB[a:b + 1]; code = 206; hdr["Content-Range"] = f"bytes {a}-{b}/{len(BLOB)}"
            return self.send(code, data, hdr)
        if p.startswith("/img/"): return self.send(200, b"\xff\xd8fakejpeg" + p.encode(), {"Content-Type": "image/jpeg"})
        if p.startswith("/rd/"):
            if not self.rd_ok(): return
            if p == "/rd/user": return self.send(200, {"username": "dg", "type": "premium", "expiration": "2027-01-01T00:00:00.000Z", "premium": 999})
            if p == "/rd/torrents":
                if q.get("page", ["1"])[0] != "1": return self.send(200, [])
                return self.send(200, [{"id": "T1", "filename": "Heat.1995.1080p.BluRay", "status": "downloaded"}, {"id": "T2", "filename": "Breaking.Bad.S02.1080p", "status": "downloaded"}, {"id": "T3", "filename": "Still.Downloading", "status": "downloading"}, {"id": "T4", "filename": "Gone.Movie.2001", "status": "downloaded"}])
            if p == "/rd/torrents/info/T1": return self.send(200, {"id": "T1", "filename": "Heat.1995.1080p.BluRay", "files": [{"id": 1, "path": "/Heat.1995.1080p.BluRay.mkv", "bytes": 5000, "selected": 1}, {"id": 2, "path": "/readme.txt", "bytes": 10, "selected": 0}], "links": ["https://real-debrid.com/d/Heat.1995.1080p.BluRay.mkv"]})
            if p == "/rd/torrents/info/T2": return self.send(200, {"id": "T2", "filename": "Breaking.Bad.S02.1080p", "files": [{"id": 1, "path": "/Breaking.Bad.S02E02.1080p.mkv", "bytes": 4000, "selected": 1}, {"id": 2, "path": "/Breaking.Bad.S02E03.1080p.mkv", "bytes": 4000, "selected": 1}], "links": ["https://real-debrid.com/d/Breaking.Bad.S02E02.1080p.mkv", "https://real-debrid.com/d/Breaking.Bad.S02E03.1080p.mkv"]})
            if p == "/rd/torrents/info/T4": return self.send(200, {"id": "T4", "filename": "Gone.Movie.2001", "files": [{"id": 1, "path": "/Gone.Movie.2001.mkv", "bytes": 4000, "selected": 1}], "links": ["https://real-debrid.com/d/gone/Gone.Movie.2001.mkv"]})
            return self.send(404, {"error": "unknown_ressource"})
        if p.startswith("/3/"):
            if q.get("api_key", [""])[0] != "tmdbgood": return self.send(401, {"status_message": "Invalid API key"})
            if p == "/3/configuration": return self.send(200, {"images": {"base_url": "x"}})
            if p == "/3/search/movie":
                t = q["query"][0].lower(); y = q.get("year", [None])[0]
                return self.send(200, {"results": [{"id": i, "title": m["title"], "release_date": m["release_date"], "overview": "o", "poster_path": f"/poster{i}.jpg"} for i, m in MOVIES.items() if t in m["title"].lower() and (not y or m["release_date"].startswith(y))]})
            if p == "/3/search/tv":
                t = q["query"][0].lower()
                return self.send(200, {"results": [{"id": i, "name": s["name"], "first_air_date": s["first_air_date"], "poster_path": f"/poster{i}.jpg"} for i, s in SHOWS.items() if t in s["name"].lower()]})
            m = re.match(r"/3/movie/(\d+)$", p)
            if m and int(m[1]) in MOVIES:
                i = int(m[1]); d = MOVIES[i]
                return self.send(200, {"id": i, "title": d["title"], "original_title": d["title"], "release_date": d["release_date"], "overview": f"Overview of {d['title']}.", "runtime": d["runtime"], "genres": [{"name": g} for g in d["genres"]], "vote_average": d["vote"], "vote_count": 1000, "popularity": d["pop"], "poster_path": f"/poster{i}.jpg", "backdrop_path": f"/backdrop{i}.jpg", "imdb_id": d["imdb"], "belongs_to_collection": {"name": d["collection"]} if d.get("collection") else None, "production_companies": [{"name": "Warner Bros."}], "credits": {"cast": d["cast"], "crew": [{"job": "Director", "name": d["director"]}, {"job": "Producer", "name": "Someone"}]}, "keywords": {"keywords": [{"name": k} for k in d["kw"]]}, "videos": {"results": [{"site": "YouTube", "type": "Trailer", "official": True, "key": d["trailer"]}] if d["trailer"] else []}, "release_dates": {"results": [{"iso_3166_1": "US", "release_dates": [{"certification": d["cert"]}]}]}})
            m = re.match(r"/3/tv/(\d+)$", p)
            if m and int(m[1]) in SHOWS:
                i = int(m[1]); d = SHOWS[i]
                return self.send(200, {"id": i, "name": d["name"], "original_name": d["name"], "first_air_date": d["first_air_date"], "overview": "A chemistry teacher.", "episode_run_time": [47], "number_of_seasons": len(d["seasons"]), "genres": [{"name": g} for g in d["genres"]], "vote_average": d["vote"], "vote_count": 5000, "popularity": d["pop"], "poster_path": f"/poster{i}.jpg", "backdrop_path": f"/backdrop{i}.jpg", "external_ids": {"imdb_id": d["imdb"]}, "created_by": [{"name": d["creator"]}], "credits": {"cast": d["cast"]}, "keywords": {"results": [{"name": "drug"}]}, "videos": {"results": [{"site": "YouTube", "type": "Trailer", "key": "HhesaQXLuRY"}]}, "content_ratings": {"results": [{"iso_3166_1": "US", "rating": d["cert"]}]}, "seasons": [{"season_number": n, "name": f"Season {n}", "overview": "", "poster_path": f"/s{n}.jpg", "air_date": "2008-01-20"} for n in d["seasons"]]})
            m = re.match(r"/3/tv/(\d+)/season/(\d+)$", p)
            if m and int(m[1]) in SHOWS:
                n = int(m[2]); cnt = SHOWS[int(m[1])]["seasons"].get(n, 0)
                return self.send(200, {"episodes": [{"episode_number": e, "name": f"S{n} Episode {e} Title", "overview": "ep", "still_path": f"/still{n}{e}.jpg", "air_date": "2008-02-01", "runtime": 47} for e in range(1, cnt + 1)]})
            return self.send(404, {"status_message": "not found"})
        self.send(404, {})

PORT = int(sys.argv[1])
ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
