using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.RateLimiting;
using DJFlix;

// "--install" / "--uninstall" register the Windows service and firewall rules; the setup program calls them elevated.
if (args.Contains("--install")) return ServiceSetup.Install();
if (args.Contains("--uninstall")) return ServiceSetup.Uninstall();

var dataDir = Environment.GetEnvironmentVariable("DJFLIX_DATA")
    ?? (args.SkipWhile(a => a != "--data").Skip(1).FirstOrDefault())
    ?? (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DJFlix")
                                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".djflix"));

// The host is rebuilt on restart (port change, restore, reset) without the process exiting.
while (true)
{
    var hub = new Hub(dataDir);
    hub.Db.Exec("DELETE FROM devices WHERE id='local'");
    hub.Db.Exec("INSERT INTO devices(id,name,kind,created_at,last_seen) VALUES('local','This PC','windows',?,?)", Db.Now(), Db.Now());

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
#if WINDOWS_SERVICE
    builder.Host.UseWindowsService(o => o.ServiceName = "DJFlix");
#endif
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(k => { k.ListenAnyIP(hub.Port); k.Limits.MaxRequestBodySize = 1L << 30; k.AddServerHeader = false; });
    builder.Services.AddSingleton(hub);
    builder.Services.AddSingleton<Providers>();
    builder.Services.AddSingleton<Library>();
    builder.Services.AddHostedService<LibraryWorker>();
    builder.Services.AddHostedService<DiscoveryResponder>();
    builder.Services.AddHttpClient("upstream", c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("DJFlix/" + Hub.Version); });
    builder.Services.AddHttpClient("stream", c => c.Timeout = Timeout.InfiniteTimeSpan);
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = 429;
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        {
            var p = ctx.Request.Path.Value ?? "";
            if (p.StartsWith("/api/v1/images") || p.StartsWith("/api/v1/stream")) return RateLimitPartition.GetNoLimiter("media");
            return RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1) });
        });
    });

    var app = builder.Build();
    var lastSeen = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

    // 1. Local network only. Anything arriving from a public address is refused outright.
    app.Use(async (ctx, next) =>
    {
        if (!Sec.IsLocalNetwork(ctx.Connection.RemoteIpAddress)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("DJFlix only accepts connections from the local network."); return; }
        await next();
    });

    // 2. Request log. Query strings are never recorded and play tokens are cut from stream paths.
    app.Use(async (ctx, next) =>
    {
        var sw = Stopwatch.StartNew();
        try { await next(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            hub.Log("error", $"{ctx.Request.Method} {Redact(ctx.Request.Path)} failed: {e.GetType().Name}: {e.Message}");
            if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = e is BadHttpRequestException ? 400 : 500; await ctx.Response.WriteAsJsonAsync(new { error = e is BadHttpRequestException ? "bad_request" : "server_error", message = e is BadHttpRequestException ? "The request could not be read." : "DJFlix hit an unexpected error." }); }
            return;
        }
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/") && !path.StartsWith("/api/v1/images") && !path.EndsWith("/progress") && !path.StartsWith("/api/v1/admin/logs") && !path.StartsWith("/api/v1/admin/status"))
            hub.Log(ctx.Response.StatusCode >= 500 ? "error" : ctx.Response.StatusCode >= 400 ? "warn" : "api", $"{ctx.Request.Method} {Redact(ctx.Request.Path)} → {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds} ms) {ClientIp(ctx)}");
    });

    app.UseRateLimiter();

    // 3. Authentication. Paired devices send their token; the desktop app uses the admin token over loopback.
    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/v1/")) { await next(); return; }
        var rest = path["/api/v1".Length..];
        if (rest == "/health" || rest.StartsWith("/pair/") || rest.StartsWith("/images/") || rest.StartsWith("/stream/")) { await next(); return; }

        async Task Deny(int code, string err, string msg) { ctx.Response.StatusCode = code; await ctx.Response.WriteAsJsonAsync(new { error = err, message = msg }); }
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        var token = auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        if (string.IsNullOrEmpty(token)) { await Deny(401, "unauthorized", "This device is not paired with DJFlix."); return; }

        if (Sec.Eq(token, hub.AdminToken))
        {
            if (!Sec.IsLoopback(ctx.Connection.RemoteIpAddress)) { await Deny(403, "forbidden", "Administration is only available on the PC running DJFlix."); return; }
            ctx.Items["admin"] = true; ctx.Items["deviceId"] = "local";
            await next(); return;
        }
        if (rest.StartsWith("/admin/")) { await Deny(403, "forbidden", "Administration is only available on the PC running DJFlix."); return; }

        var dev = hub.Db.One("SELECT id, is_revoked FROM devices WHERE token_hash=?", Sec.Hash(token));
        if (dev == null || dev.B("is_revoked")) { await Deny(401, "device_revoked", "This device is no longer paired. Pair it again from the setup wizard."); return; }
        var id = dev.S("id"); ctx.Items["deviceId"] = id;
        var now = Db.Now();
        if (now - lastSeen.GetValueOrDefault(id) > 15_000) { lastSeen[id] = now; hub.Db.Exec("UPDATE devices SET last_seen=?, last_ip=? WHERE id=?", now, ClientIp(ctx), id); }
        await next();
    });

    var v1 = app.MapGroup("/api/v1");
    Api.MapClient(v1);
    Api.MapAdmin(v1.MapGroup("/admin"), app.Lifetime);
    app.MapGet("/", () => Results.Text($"DJFlix {Hub.Version} is running. Use the DJFlix desktop app or a paired Fire TV to connect."));

    hub.Log("info", $"DJFlix {Hub.Version} listening on port {hub.Port} (LAN address {Hub.LanIp() ?? "unknown"})");
    Console.WriteLine($"DJFlix {Hub.Version} listening on http://{Hub.LanIp() ?? "localhost"}:{hub.Port}  data: {dataDir}");
    try { await app.RunAsync(); }
    finally { await app.DisposeAsync(); hub.Db.Dispose(); }

    if (!hub.RestartRequested) break;
    // As a Windows service the host cannot be rebuilt in-process: the service has just reported "stopped",
    // so hand over to a detached helper that starts it again once the stop has completed.
    if (ServiceSetup.RunningAsService)
    {
        ServiceSetup.StartAgainLater();
        break;
    }
}
return 0;

static string ClientIp(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress;
    return ip == null ? "?" : ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
}

static string Redact(PathString p)
{
    var s = p.Value ?? "";
    return s.StartsWith("/api/v1/stream/") ? "/api/v1/stream/…" : s.StartsWith("/api/v1/pair/status/") ? "/api/v1/pair/status/…" : s;
}

/// <summary>Answers LAN discovery probes so the Fire TV app can find the hub without typing an address.</summary>
public sealed class DiscoveryResponder(Hub hub) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UdpClient udp;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, Hub.DiscoveryPort)) { EnableBroadcast = true }; }
        catch (SocketException e) { hub.Log("warn", $"Discovery is unavailable (UDP {Hub.DiscoveryPort}): {e.Message}"); return; }
        using (udp)
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(ct);
                    if (!Sec.IsLocalNetwork(r.RemoteEndPoint.Address) || Encoding.UTF8.GetString(r.Buffer).Trim() != "DJFLIX_DISCOVER") continue;
                    var reply = Encoding.UTF8.GetBytes(J.Ser(new { service = "djflix", name = hub.ServerName, serverId = hub.ServerId, port = hub.Port, version = Hub.Version, apiVersion = Hub.ApiVersion }));
                    await udp.SendAsync(reply, r.RemoteEndPoint, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { }
            }
    }
}

/// <summary>Registers DJFlix as a Windows service with a LAN-only firewall rule. Run elevated by the setup program.</summary>
public static class ServiceSetup
{
    const string Name = "DJFlix";

    public static bool RunningAsService =>
#if WINDOWS_SERVICE
        Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
#else
        false;
#endif

    static int Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(30000);
        Console.WriteLine($"{Path.GetFileName(file)} {string.Join(' ', args)} -> {p.ExitCode} {output.Trim()}");
        return p.ExitCode;
    }

    public static int Install()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Service installation is only available on Windows."); return 1; }
        var exe = Environment.ProcessPath!;
        Run("sc.exe", "stop", Name); Run("sc.exe", "delete", Name);
        for (int i = 0; i < 20 && Run("sc.exe", "query", Name) == 0; i++) Thread.Sleep(500);   // wait for the old registration to go
        var rc = Run("sc.exe", "create", Name, "binPath=", $"\"{exe}\"", "start=", "auto", "DisplayName=", "DJFlix Media Hub");
        if (rc != 0) return rc;
        Run("sc.exe", "description", Name, "Serves your DJFlix library, profiles and watch progress to this PC and to paired Fire TV devices on the home network.");
        Run("sc.exe", "failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
        // Signed-in users may start and stop the service from the DJFlix dashboard without elevation.
        Run("sc.exe", "sdset", Name, "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)");
        Firewall(false); Firewall(true);
        return Run("sc.exe", "start", Name);
    }

    public static int Uninstall()
    {
        if (!OperatingSystem.IsWindows()) return 1;
        Run("sc.exe", "stop", Name);
        for (int i = 0; i < 20 && Process.GetProcessesByName("DJFlix.Server").Length > 1; i++) Thread.Sleep(500);
        Run("sc.exe", "delete", Name);
        Firewall(false);
        return 0;
    }

    /// <summary>TCP for the API and UDP for discovery, private/domain profiles, local subnet only.</summary>
    static void Firewall(bool add)
    {
        int port = 8674;
        try
        {
            var info = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DJFlix", "hub.json");
            if (File.Exists(info) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(info))?["port"]?.GetValue<int>() is { } p) port = p;
        }
        catch { }
        if (!add)
        {
            Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=DJFlix");
            Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=DJFlix Discovery");
            return;
        }
        Run("netsh.exe", "advfirewall", "firewall", "add", "rule", "name=DJFlix", "dir=in", "action=allow", "protocol=TCP", $"localport={port}", "profile=private,domain", "remoteip=localsubnet");
        Run("netsh.exe", "advfirewall", "firewall", "add", "rule", "name=DJFlix Discovery", "dir=in", "action=allow", "protocol=UDP", $"localport={Hub.DiscoveryPort}", "profile=private,domain", "remoteip=localsubnet");
    }

    public static void StartAgainLater()
    {
        try { Process.Start(new ProcessStartInfo("cmd.exe", "/c for /l %i in (1,1,15) do (ping -n 2 127.0.0.1 >nul & sc start DJFlix >nul && exit)") { CreateNoWindow = true, UseShellExecute = false }); }
        catch { }
    }
}
