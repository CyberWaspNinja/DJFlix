using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DJFlix;

public static class Sec
{
    public static string NewToken(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    public static bool Eq(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(h);
    }
    public static bool VerifyPin(string pin, string stored)
    {
        var p = stored.Split(':'); if (p.Length != 2) return false;
        var h = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(p[0]), 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(p[1]));
    }

    /// <summary>True for loopback, RFC1918, link-local and IPv6 unique-local addresses.</summary>
    public static bool IsLocalNetwork(IPAddress? ip)
    {
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }
    public static bool IsLoopback(IPAddress? ip) => ip != null && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
}

/// <summary>
/// Encrypts provider credentials at rest with AES-GCM. The key lives in the data directory
/// and, on Windows, is itself wrapped with DPAPI (machine scope) so a copied database or
/// key file is useless on another PC.
/// </summary>
public sealed class Secrets
{
    readonly byte[] _key;

    public Secrets(string dataDir)
    {
        var f = System.IO.Path.Combine(dataDir, "secret.key");
        if (File.Exists(f)) _key = Unwrap(File.ReadAllBytes(f));
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(f, Wrap(_key));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string Protect(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plain);
        var ct = new byte[pt.Length]; var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, pt, ct, tag);
        return "v1:" + Convert.ToBase64String(nonce.Concat(tag).Concat(ct).ToArray());
    }

    public string? Unprotect(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("v1:")) return null;
        try
        {
            var raw = Convert.FromBase64String(s[3..]);
            var pt = new byte[raw.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int cb; public IntPtr pb; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob dataIn, string? descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    static byte[] Dpapi(byte[] data, bool protect)
    {
        var pin = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, pin, data.Length);
            var inb = new Blob { cb = data.Length, pb = pin };
            const int UI_FORBIDDEN = 1, LOCAL_MACHINE = 4;
            Blob outb;
            var ok = protect ? CryptProtectData(ref inb, "DJFlix", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN | LOCAL_MACHINE, out outb)
                             : CryptUnprotectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, out outb);
            if (!ok) throw new CryptographicException("DPAPI failed: " + Marshal.GetLastWin32Error());
            var res = new byte[outb.cb]; Marshal.Copy(outb.pb, res, 0, outb.cb); LocalFree(outb.pb);
            return res;
        }
        finally { Marshal.FreeHGlobal(pin); }
    }
    static byte[] Wrap(byte[] k) => OperatingSystem.IsWindows() ? Dpapi(k, true) : k;
    static byte[] Unwrap(byte[] k) => OperatingSystem.IsWindows() ? Dpapi(k, false) : k;
}

public sealed record LogEntry(long At, string Level, string Message);
public sealed record PendingPair(string Id, string Secret, string DeviceName, string Kind, string Ip, long At) { public string? Token; public string? DeviceId; public string State = "pending"; }

/// <summary>Process-wide hub state shared by the API, scanner and providers.</summary>
public sealed class Hub
{
    public const string Version = "1.0.0";
    public const int ApiVersion = 1;
    public const int DiscoveryPort = 47810;

    public string DataDir { get; }
    public Db Db { get; }
    public Secrets Secrets { get; }
    public string AdminToken { get; }
    public string ServerId { get; }
    public long StartedAt { get; } = Db.Now();
    public int Port { get; set; }
    public volatile bool RestartRequested;
    public Catalog Catalog { get; }

    readonly ConcurrentQueue<LogEntry> _logs = new();
    public readonly ConcurrentDictionary<string, PendingPair> Pending = new();
    public readonly ConcurrentDictionary<string, (int n, long at)> PairAttempts = new();
    string? _pairCode; long _pairCodeExp;

    public Hub(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(System.IO.Path.Combine(dataDir, "cache", "images"));
        ApplyPendingRestore();
        Db = new Db(System.IO.Path.Combine(dataDir, "djflix.db"));
        Db.Migrate();
        Secrets = new Secrets(dataDir);
        var tf = System.IO.Path.Combine(dataDir, "admin.token");
        if (!File.Exists(tf))
        {
            File.WriteAllText(tf, Sec.NewToken());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tf, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        AdminToken = File.ReadAllText(tf).Trim();
        ServerId = Db.Get("server.id") ?? Guid.NewGuid().ToString("N");
        Db.Set("server.id", ServerId);
        Port = int.TryParse(Environment.GetEnvironmentVariable("DJFLIX_PORT") ?? Db.Get("server.port"), out var p) ? p : 8674;
        Catalog = new Catalog(this);
        // Lets the desktop app find the hub without knowing the configured port.
        try { File.WriteAllText(System.IO.Path.Combine(dataDir, "hub.json"), J.Ser(new { port = Port, pid = Environment.ProcessId, version = Version })); } catch { }
    }

    void ApplyPendingRestore()
    {
        var db = System.IO.Path.Combine(DataDir, "djflix.db");
        var pending = System.IO.Path.Combine(DataDir, "restore.pending");
        var reset = System.IO.Path.Combine(DataDir, "reset.pending");
        if (!File.Exists(pending) && !File.Exists(reset)) return;
        foreach (var ext in new[] { "", "-wal", "-shm" }) if (File.Exists(db + ext)) File.Delete(db + ext);
        if (File.Exists(pending)) File.Move(pending, db);
        if (File.Exists(reset)) File.Delete(reset);
    }

    public string ServerName => Db.Get("server.name") ?? Environment.MachineName;

    public void Log(string level, string msg)
    {
        _logs.Enqueue(new LogEntry(Db.Now(), level, msg));
        while (_logs.Count > 500) _logs.TryDequeue(out _);
    }
    public IEnumerable<LogEntry> Logs => _logs.Reverse();

    public string? Secret(string key) => Secrets.Unprotect(Db.Get(key));
    public void SetSecret(string key, string? value) => Db.Set(key, string.IsNullOrWhiteSpace(value) ? null : Secrets.Protect(value.Trim()));

    // ---- pairing -------------------------------------------------------------------------
    public (string code, long expiresAt) NewPairCode()
    {
        _pairCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _pairCodeExp = Db.Now() + 10 * 60_000;
        return (_pairCode, _pairCodeExp);
    }
    public (string? code, long expiresAt) CurrentPairCode() => _pairCode != null && Db.Now() < _pairCodeExp ? (_pairCode, _pairCodeExp) : (null, 0);
    public bool ConsumePairCode(string code)
    {
        var (c, _) = CurrentPairCode();
        if (c == null || !Sec.Eq(c, code.Trim())) return false;
        _pairCode = null; return true;   // single use
    }

    public (string deviceId, string token) CreateDevice(string name, string kind, string ip)
    {
        var id = Guid.NewGuid().ToString("N"); var token = Sec.NewToken();
        Db.Exec("INSERT INTO devices(id,name,kind,token_hash,created_at,last_seen,last_ip) VALUES(?,?,?,?,?,?,?)", id, name, kind, Sec.Hash(token), Db.Now(), Db.Now(), ip);
        Log("info", $"Paired device '{name}' ({kind}) from {ip}");
        return (id, token);
    }

    // ---- network -------------------------------------------------------------------------
    public static List<object> Interfaces()
    {
        var list = new List<object>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            foreach (var ua in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                list.Add(new { name = ni.Name, type = ni.NetworkInterfaceType.ToString(), address = ua.Address.ToString(), gateway = gw, hasGateway = gw != null });
        }
        return list;
    }

    /// <summary>The LAN address clients should use: prefer an interface that has a default gateway.</summary>
    public static string? LanIp()
    {
        string? fallback = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            var ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && Sec.IsLocalNetwork(u.Address))?.Address.ToString();
            if (ip == null) continue;
            if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) return ip;
            fallback ??= ip;
        }
        return fallback;
    }
}

public static class J
{
    public static readonly JsonSerializerOptions Opt = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
    public static string Ser(object? o) => JsonSerializer.Serialize(o, Opt);
    public static JsonNode? Parse(string? s) { try { return string.IsNullOrEmpty(s) ? null : JsonNode.Parse(s); } catch { return null; } }
    public static string[] StrArr(string? s) { try { return string.IsNullOrEmpty(s) ? [] : JsonSerializer.Deserialize<string[]>(s) ?? []; } catch { return []; } }

    static string Camel(string k)
    {
        var sb = new StringBuilder(); bool up = false;
        foreach (var c in k) { if (c == '_') { up = true; continue; } sb.Append(up ? char.ToUpperInvariant(c) : c); up = false; }
        return sb.ToString();
    }

    /// <summary>Row → JSON object: snake_case → camelCase, *_json columns inlined, is_/has_ columns as booleans.</summary>
    public static JsonObject Out(Row r)
    {
        var o = new JsonObject();
        foreach (var (k, v) in r)
        {
            if (k.EndsWith("_json")) { o[Camel(k[..^5])] = Parse(v as string); continue; }
            if (k.EndsWith("_hash")) continue;
            var name = Camel(k);
            o[name] = v switch
            {
                null => null,
                long l when k.StartsWith("is_") || k.StartsWith("has_") => l != 0,
                long l => l,
                double d => d,
                _ => v.ToString(),
            };
        }
        return o;
    }
    public static JsonArray Out(IEnumerable<Row> rows) => new(rows.Select(r => (JsonNode)Out(r)).ToArray());
}
