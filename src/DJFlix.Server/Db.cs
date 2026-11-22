using System.Runtime.InteropServices;
using System.Text;

namespace DJFlix;

/// <summary>A result row. Column names are snake_case as in the schema.</summary>
public sealed class Row : Dictionary<string, object?>
{
    public long L(string k) => TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : 0;
    public long? LN(string k) => TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : null;
    public double D(string k) => TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v) : 0;
    public string S(string k) => TryGetValue(k, out var v) && v != null ? Convert.ToString(v)! : "";
    public string? SN(string k) => TryGetValue(k, out var v) && v != null ? Convert.ToString(v) : null;
    public bool B(string k) => L(k) != 0;
}

/// <summary>
/// Minimal SQLite access over the system library (winsqlite3.dll ships with Windows 10+,
/// libsqlite3 on Linux). One serialized connection in WAL mode: ample for a home hub and
/// it keeps the server free of third-party packages.
/// </summary>
public sealed class Db : IDisposable
{
    const string Lib = "sqlite3";
    static readonly IntPtr Transient = new(-1);
    IntPtr _h;
    readonly object _lock = new();
    public string Path { get; }

    static Db()
    {
        NativeLibrary.SetDllImportResolver(typeof(Db).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            var names = OperatingSystem.IsWindows() ? new[] { "winsqlite3", "sqlite3" }
                : new[] { "libsqlite3.so.0", "libsqlite3", "sqlite3" };
            foreach (var n in names) if (NativeLibrary.TryLoad(n, asm, path, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    [DllImport(Lib)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] v, int n, IntPtr d);
    [DllImport(Lib)] static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
    [DllImport(Lib)] static extern int sqlite3_bind_double(IntPtr stmt, int i, double v);
    [DllImport(Lib)] static extern int sqlite3_bind_null(IntPtr stmt, int i);
    [DllImport(Lib)] static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib)] static extern IntPtr sqlite3_column_name(IntPtr stmt, int i);
    [DllImport(Lib)] static extern int sqlite3_column_type(IntPtr stmt, int i);
    [DllImport(Lib)] static extern long sqlite3_column_int64(IntPtr stmt, int i);
    [DllImport(Lib)] static extern double sqlite3_column_double(IntPtr stmt, int i);
    [DllImport(Lib)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    [DllImport(Lib)] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
    [DllImport(Lib)] static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib)] static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib)] static extern long sqlite3_last_insert_rowid(IntPtr db);

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public Db(string path)
    {
        Path = path;
        const int READWRITE = 2, CREATE = 4, FULLMUTEX = 0x10000;
        if (sqlite3_open_v2(Z(path), out _h, READWRITE | CREATE | FULLMUTEX, IntPtr.Zero) != 0)
            throw new InvalidOperationException("Cannot open database " + path);
        sqlite3_busy_timeout(_h, 5000);
        Query("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("PRAGMA foreign_keys=ON");
    }

    string Err() => Marshal.PtrToStringUTF8(sqlite3_errmsg(_h)) ?? "sqlite error";

    IntPtr Prepare(string sql, object?[] args)
    {
        var b = Encoding.UTF8.GetBytes(sql);
        if (sqlite3_prepare_v2(_h, b, b.Length, out var st, IntPtr.Zero) != 0)
            throw new InvalidOperationException(Err() + " in: " + sql);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case null: sqlite3_bind_null(st, i + 1); break;
                case bool v: sqlite3_bind_int64(st, i + 1, v ? 1 : 0); break;
                case int v: sqlite3_bind_int64(st, i + 1, v); break;
                case long v: sqlite3_bind_int64(st, i + 1, v); break;
                case double v: sqlite3_bind_double(st, i + 1, v); break;
                case float v: sqlite3_bind_double(st, i + 1, v); break;
                default:
                    var t = Encoding.UTF8.GetBytes(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)!);
                    sqlite3_bind_text(st, i + 1, t, t.Length, Transient); break;
            }
        }
        return st;
    }

    public int Exec(string sql, params object?[] args)
    {
        lock (_lock)
        {
            var st = Prepare(sql, args);
            try
            {
                int rc; while ((rc = sqlite3_step(st)) == 100) { }
                if (rc != 101) throw new InvalidOperationException(Err() + " in: " + sql);
                return sqlite3_changes(_h);
            }
            finally { sqlite3_finalize(st); }
        }
    }

    /// <summary>Runs an INSERT and returns the new rowid.</summary>
    public long Insert(string sql, params object?[] args)
    {
        lock (_lock) { Exec(sql, args); return sqlite3_last_insert_rowid(_h); }
    }

    public List<Row> Query(string sql, params object?[] args)
    {
        lock (_lock)
        {
            var st = Prepare(sql, args);
            try
            {
                var rows = new List<Row>();
                int n = sqlite3_column_count(st);
                var names = new string[n];
                for (int i = 0; i < n; i++) names[i] = Marshal.PtrToStringUTF8(sqlite3_column_name(st, i))!;
                int rc;
                while ((rc = sqlite3_step(st)) == 100)
                {
                    var r = new Row();
                    for (int i = 0; i < n; i++)
                    {
                        r[names[i]] = sqlite3_column_type(st, i) switch
                        {
                            1 => sqlite3_column_int64(st, i),
                            2 => sqlite3_column_double(st, i),
                            5 => null,
                            _ => Marshal.PtrToStringUTF8(sqlite3_column_text(st, i), sqlite3_column_bytes(st, i)),
                        };
                    }
                    rows.Add(r);
                }
                if (rc != 101) throw new InvalidOperationException(Err() + " in: " + sql);
                return rows;
            }
            finally { sqlite3_finalize(st); }
        }
    }

    public Row? One(string sql, params object?[] args) => Query(sql, args).FirstOrDefault();
    public long Long(string sql, params object?[] args) { var r = One(sql, args); return r == null ? 0 : Convert.ToInt64(r.Values.First() ?? 0L); }
    public string? Str(string sql, params object?[] args) { var r = One(sql, args); return r?.Values.First()?.ToString(); }

    public void Tx(Action a)
    {
        lock (_lock)
        {
            Exec("BEGIN IMMEDIATE");
            try { a(); Exec("COMMIT"); } catch { Exec("ROLLBACK"); throw; }
        }
    }

    public T Locked<T>(Func<T> f) { lock (_lock) return f(); }

    public void Dispose() { lock (_lock) { if (_h != IntPtr.Zero) { sqlite3_close_v2(_h); _h = IntPtr.Zero; } } }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string? Get(string key) => Str("SELECT value FROM settings WHERE key=?", key);
    public string Get(string key, string def) => Get(key) ?? def;
    public void Set(string key, string? value)
    {
        if (value == null) { Exec("DELETE FROM settings WHERE key=?", key); return; }
        lock (_lock) { if (Exec("UPDATE settings SET value=? WHERE key=?", value, key) == 0) Exec("INSERT INTO settings(key,value) VALUES(?,?)", key, value); }
    }

    public void Migrate()
    {
        foreach (var s in Schema.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Exec(s);
        if (Get("schema.version") == null) Set("schema.version", "1");
    }

    // Kept to features present in older bundled SQLite builds (no UPSERT/RETURNING/STRICT).
    const string Schema = """
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS profiles(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, avatar TEXT, color TEXT, pin_hash TEXT, language TEXT DEFAULT 'en', max_maturity INTEGER DEFAULT 5, prefs_json TEXT DEFAULT '{}', created_at INTEGER);
CREATE TABLE IF NOT EXISTS media(id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL, tmdb_id INTEGER, imdb_id TEXT, title TEXT NOT NULL, original_title TEXT, sort_title TEXT, year INTEGER, release_date TEXT, overview TEXT, genres_json TEXT DEFAULT '[]', cast_json TEXT DEFAULT '[]', directors_json TEXT DEFAULT '[]', studios_json TEXT DEFAULT '[]', keywords_json TEXT DEFAULT '[]', rating REAL, votes INTEGER, popularity REAL, runtime INTEGER, certification TEXT, maturity INTEGER DEFAULT 0, poster TEXT, backdrop TEXT, trailer_key TEXT, seasons_count INTEGER, collection_name TEXT, is_matched INTEGER DEFAULT 0, is_manual INTEGER DEFAULT 0, added_at INTEGER, updated_at INTEGER);
CREATE INDEX IF NOT EXISTS ix_media_tmdb ON media(type, tmdb_id);
CREATE TABLE IF NOT EXISTS seasons(media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, season INTEGER NOT NULL, name TEXT, overview TEXT, poster TEXT, air_date TEXT, PRIMARY KEY(media_id, season));
CREATE TABLE IF NOT EXISTS episodes(id INTEGER PRIMARY KEY AUTOINCREMENT, media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, season INTEGER NOT NULL, episode INTEGER NOT NULL, title TEXT, overview TEXT, still TEXT, runtime INTEGER, air_date TEXT, intro_start_ms INTEGER, intro_end_ms INTEGER, credits_start_ms INTEGER, UNIQUE(media_id, season, episode));
CREATE TABLE IF NOT EXISTS libraries(id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL UNIQUE, last_scan INTEGER, added_at INTEGER);
CREATE TABLE IF NOT EXISTS sources(id INTEGER PRIMARY KEY AUTOINCREMENT, media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, episode_id INTEGER, kind TEXT NOT NULL, provider TEXT, ref TEXT NOT NULL, link TEXT, file_name TEXT, size INTEGER, quality TEXT, is_missing INTEGER DEFAULT 0, library_id INTEGER, added_at INTEGER, UNIQUE(kind, ref));
CREATE INDEX IF NOT EXISTS ix_sources_media ON sources(media_id);
CREATE TABLE IF NOT EXISTS watchlist(profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, added_at INTEGER, PRIMARY KEY(profile_id, media_id));
CREATE TABLE IF NOT EXISTS favourites(profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, added_at INTEGER, PRIMARY KEY(profile_id, media_id));
CREATE TABLE IF NOT EXISTS ratings(profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, rating INTEGER NOT NULL, updated_at INTEGER, PRIMARY KEY(profile_id, media_id));
CREATE TABLE IF NOT EXISTS progress(profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, episode_id INTEGER NOT NULL DEFAULT 0, position_ms INTEGER NOT NULL, duration_ms INTEGER NOT NULL, is_completed INTEGER DEFAULT 0, updated_at INTEGER, device_id TEXT, session_id TEXT, PRIMARY KEY(profile_id, media_id, episode_id));
CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, episode_id INTEGER NOT NULL DEFAULT 0, watched_at INTEGER, position_ms INTEGER, duration_ms INTEGER, is_completed INTEGER DEFAULT 0, device_id TEXT, session_id TEXT);
CREATE INDEX IF NOT EXISTS ix_history_profile ON history(profile_id, watched_at);
CREATE TABLE IF NOT EXISTS search_history(id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, query TEXT NOT NULL, at INTEGER);
CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT, kind TEXT, token_hash TEXT, created_at INTEGER, last_seen INTEGER, last_ip TEXT, is_revoked INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, profile_id INTEGER, media_id INTEGER, episode_id INTEGER NOT NULL DEFAULT 0, source_id INTEGER, device_id TEXT, play_token_hash TEXT, started_at INTEGER, last_at INTEGER, state TEXT, position_ms INTEGER DEFAULT 0, resolved_url TEXT, resolved_at INTEGER);
CREATE TABLE IF NOT EXISTS playlists(id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, name TEXT NOT NULL, created_at INTEGER);
CREATE TABLE IF NOT EXISTS playlist_items(playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE, media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE, position INTEGER, PRIMARY KEY(playlist_id, media_id));
CREATE TABLE IF NOT EXISTS cache(key TEXT PRIMARY KEY, value TEXT, at INTEGER);
""";
}
