using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DJFlix.Desktop;

/// <summary>Null-safe readers for the hub's JSON.</summary>
public static class JX
{
    public static JsonNode? O(this JsonNode? n, string k) => n is JsonObject o ? o[k] : null;
    public static string? S(this JsonNode? n, string k) => n.O(k) switch { null => null, JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0 ? null : s, var x => x.ToJsonString() };
    public static double D(this JsonNode? n, string k) => n.O(k) is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    public static long L(this JsonNode? n, string k) => (long)n.D(k);
    public static bool B(this JsonNode? n, string k) => n.O(k) is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static List<JsonNode> A(this JsonNode? n, string k) => (n.O(k) as JsonArray).List();
    public static List<JsonNode> List(this JsonNode? n) => n is JsonArray a ? a.Where(x => x != null).Select(x => x!).ToList() : [];
    public static List<string> Strings(this JsonNode? n, string k) => n.A(k).Select(x => x.ToString()).ToList();
}

public sealed class HubException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public bool Unreachable => Status == 0;
}

/// <summary>Talks to the local hub over loopback with the admin token; the same REST API the Fire TV uses.</summary>
public sealed class HubClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    public string Base { get; private set; } = "http://127.0.0.1:8674";
    public int Port { get; private set; } = 8674;

    public static string DataDir => Environment.GetEnvironmentVariable("DJFLIX_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DJFlix");

    /// <summary>Re-reads the port and admin token the hub wrote to its data folder.</summary>
    public bool Configure()
    {
        try
        {
            var info = Path.Combine(DataDir, "hub.json");
            if (File.Exists(info) && JsonNode.Parse(File.ReadAllText(info)) is { } j && j.L("port") > 0) Port = (int)j.L("port");
            Base = $"http://127.0.0.1:{Port}";
            var tf = Path.Combine(DataDir, "admin.token");
            if (!File.Exists(tf)) return false;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(tf).Trim());
            return true;
        }
        catch { return false; }
    }

    public string? Url(string? path) => string.IsNullOrEmpty(path) ? null : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Base + path;

    public async Task<JsonNode?> Send(HttpMethod method, string path, object? body = null, HttpContent? raw = null)
    {
        using var req = new HttpRequestMessage(method, Base + "/api/v1" + path);
        if (raw != null) req.Content = raw;
        else if (body != null) req.Content = new StringContent(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        else if (method != HttpMethod.Get && method != HttpMethod.Delete) req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new HubException(0, "unreachable", "The DJFlix service is not responding."); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync();
            JsonNode? j = null; try { if (text.Length > 0) j = JsonNode.Parse(text); } catch { }
            if (!res.IsSuccessStatusCode) throw new HubException((int)res.StatusCode, j.S("error") ?? "http_" + (int)res.StatusCode, j.S("message") ?? $"DJFlix returned an error (HTTP {(int)res.StatusCode}).");
            return j;
        }
    }
    public Task<JsonNode?> Get(string path) => Send(HttpMethod.Get, path);
    public Task<JsonNode?> Post(string path, object? body = null) => Send(HttpMethod.Post, path, body);
    public Task<JsonNode?> Put(string path, object? body = null) => Send(HttpMethod.Put, path, body);
    public Task<JsonNode?> Patch(string path, object? body) => Send(HttpMethod.Patch, path, body);
    public Task<JsonNode?> Delete(string path) => Send(HttpMethod.Delete, path);
    public async Task<byte[]> Download(string path) => await _http.GetByteArrayAsync(Base + "/api/v1" + path);

    public async Task<bool> Healthy()
    {
        try { using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }; return (await c.GetAsync(Base + "/api/v1/health")).IsSuccessStatusCode; }
        catch { return false; }
    }
}

/// <summary>
/// Starts, stops and finds the hub. Installed: it is the "DJFlix" Windows service and keeps running when this
/// window closes. Portable (no service): the hub runs as a child process for as long as the app is open.
/// </summary>
public static class HubHost
{
    static Process? _child;
    public static string ServerExe => Path.Combine(AppContext.BaseDirectory, "server", "DJFlix.Server.exe");

    static (int code, string output) Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(20000);
            return (p.ExitCode, o);
        }
        catch (Exception e) { return (-1, e.Message); }
    }

    public static bool ServiceInstalled => Run("sc.exe", "query DJFlix").code == 0;
    public static bool ServiceRunning => Run("sc.exe", "query DJFlix").output.Contains("RUNNING");
    public static string Mode => ServiceInstalled ? "Windows service" : "Runs while the app is open";

    public static async Task<(bool ok, string message)> Start()
    {
        if (await G.Hub.Healthy()) return (true, "Already running.");
        if (ServiceInstalled)
        {
            var (code, output) = Run("sc.exe", "start DJFlix");
            if (code != 0 && !output.Contains("1056")) return (false, "Windows would not start the DJFlix service: " + output.Trim());
        }
        else
        {
            if (!File.Exists(ServerExe)) return (false, "The DJFlix service program was not found next to the app. Reinstall DJFlix.");
            try { _child = Process.Start(new ProcessStartInfo(ServerExe) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ServerExe)! }); }
            catch (Exception e) { return (false, "The DJFlix service could not be started: " + e.Message); }
        }
        for (int i = 0; i < 60; i++) { await Task.Delay(500); G.Hub.Configure(); if (await G.Hub.Healthy()) return (true, "Started."); }
        return (false, "The DJFlix service was started but is not answering. Another program may be using its port.");
    }

    public static async Task<(bool ok, string message)> Stop()
    {
        if (ServiceInstalled)
        {
            var (code, output) = Run("sc.exe", "stop DJFlix");
            if (code != 0 && !output.Contains("1062")) return (false, "Windows would not stop the DJFlix service: " + output.Trim());
        }
        else { try { await G.Hub.Post("/admin/shutdown"); } catch { } try { if (_child is { HasExited: false }) { _child.WaitForExit(4000); if (!_child.HasExited) _child.Kill(); } } catch { } _child = null; }
        for (int i = 0; i < 20 && await G.Hub.Healthy(); i++) await Task.Delay(300);
        return (true, "Stopped.");
    }

    public static async Task<(bool ok, string message)> Restart()
    {
        try { await G.Hub.Post("/admin/restart"); } catch (HubException) { return await Start(); }
        await Task.Delay(1500);
        for (int i = 0; i < 60; i++) { G.Hub.Configure(); if (await G.Hub.Healthy()) return (true, "Restarted."); await Task.Delay(500); }
        return await Start();
    }

    /// <summary>In portable mode the hub must not outlive the window.</summary>
    public static void OnAppExit() { try { if (_child is { HasExited: false }) _child.Kill(); } catch { } }
}

/// <summary>Per-user settings of the desktop app itself (theme, window, last profile).</summary>
public static class Local
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DJFlix");
    static readonly string FilePath = Path.Combine(Dir, "desktop.json");
    static JsonObject _data = Load();
    static JsonObject Load() { try { return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); } catch { return new JsonObject(); } }
    public static string Get(string k, string def = "") => _data.S(k) ?? def;
    public static void Set(string k, string v) { _data[k] = v; try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, _data.ToJsonString()); } catch { } }
    public static void Log(string msg) { try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "desktop.log"), $"{DateTime.Now:s} {msg}\n"); } catch { } }
}

/// <summary>App-wide state.</summary>
public static class G
{
    public static readonly HubClient Hub = new();
    public static MainWindow Window = null!;
    public static JsonNode? Profile;
    public static long Pid => Profile.L("id");
    public static JsonNode? Prefs => Profile.O("prefs");
    public static bool PrefBool(string k, bool def) => Prefs.O(k) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
    public static double PrefNum(string k, double def) => Prefs.O(k) is JsonValue v && v.TryGetValue<double>(out var d) ? d : def;
    public static string PrefStr(string k, string def) => Prefs.S(k) ?? def;
    public static async Task SavePrefs(object patch) => Profile = await Hub.Patch($"/profiles/{Pid}", new { prefs = patch });
}
