using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;

namespace DJFlix.Desktop;

static class Qr
{
    public static async Task<Image> Make(string payload, int size = 180)
    {
        using var gen = new QRCodeGenerator();
        var png = new PngByteQRCode(gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M)).GetGraphic(8);
        var bmp = new BitmapImage();
        using var ms = new InMemoryRandomAccessStream();
        using (var w = new DataWriter(ms.GetOutputStreamAt(0))) { w.WriteBytes(png); await w.StoreAsync(); await w.FlushAsync(); w.DetachStream(); }
        ms.Seek(0); await bmp.SetSourceAsync(ms);
        return new Image { Source = bmp, Width = size, Height = size };
    }
}

// ============================= server dashboard ===============================================
public sealed class ServerPage : PageBase
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    JsonNode? _prov; DateTime _lastLive;
    public ServerPage() { _timer.Tick += (_, _) => { if (XamlRoot != null && G.Window.Content.XamlRoot != null && !G.Window.PlayerOpen) Refresh(); }; Loaded += (_, _) => _timer.Start(); Unloaded += (_, _) => _timer.Stop(); }

    static FrameworkElement Status(string label, string value, bool? ok = null) => Ui.Card(Ui.Col(3, Ui.Text(label, 12, color: Ui.Faint), Ui.Row(8, ok == null ? null : new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = ok == true ? Ui.Hex("#2ECC71") : Ui.Accent, VerticalAlignment = VerticalAlignment.Center }, Ui.Text(value, 17, true, maxLines: 2))), 14).With(b => { b.MinWidth = 210; b.MaxWidth = 340; });

    protected override async Task<UIElement> BuildAsync()
    {
        var col = new StackPanel { Margin = new Thickness(40, 10, 40, 40), Spacing = 6 };
        col.Children.Add(Ui.H1("Server"));
        if (!await G.Hub.Healthy())
        {
            col.Children.Add(Ui.Row(12, Status("Service", "Stopped", false), Status("Mode", HubHost.Mode)));
            col.Children.Add(Ui.Text("The DJFlix service is not running. Fire TV devices cannot connect until it is started.", color: Ui.Dim));
            col.Children.Add(Ui.BtnAsync("Start service", async () => { var (ok, msg) = await HubHost.Start(); G.Window.Toast(msg, !ok); Refresh(); }, true));
            return col;
        }
        // Provider accounts are checked against the real services at most once a minute; the page itself refreshes every few seconds.
        var live = _prov == null || (DateTime.Now - _lastLive).TotalSeconds > 60;
        var st = await G.Hub.Get("/admin/status?live=" + (live ? "true" : "false"));
        if (live) { _prov = st.O("providers"); _lastLive = DateTime.Now; }
        var conn = st.O("connection"); var svc = st.O("service"); var prov = _prov; var clients = st.O("clients");
        var up = TimeSpan.FromMilliseconds(svc.D("uptimeMs"));

        col.Children.Add(Ui.Text("Your Media Hub is ready", 20, true));
        var tiles = new WrapPanel { Gap = 10 };
        void Tile(FrameworkElement e) => tiles.Children.Add(e);
        Tile(Status("Service", $"Running  ·  {(int)up.TotalHours}h {up.Minutes}m", true));
        Tile(Status("Server address", conn.S("address") ?? "No network", conn.S("address") != null));
        Tile(Status("Connected devices", $"{clients.A("connected").Count} online  ·  {clients.L("paired")} paired"));
        Tile(Status("Debrid account", prov.O("debrid").B("connected") ? $"{prov.O("debrid").S("name")}: {prov.O("debrid").O("account").S("username")}" : prov.O("debrid").B("configured") ? "Problem" : "Not connected", prov.O("debrid").B("configured") ? prov.O("debrid").B("connected") : null));
        Tile(Status("Metadata", prov.O("metadata").B("connected") ? "TMDB connected" : prov.O("metadata").B("configured") ? "Problem" : "Not configured", prov.O("metadata").B("configured") ? prov.O("metadata").B("connected") : null));
        Tile(Status("Network", st.O("network").B("online") ? "Online" : "Offline", st.O("network").B("online")));
        Tile(Status("Mode", HubHost.Mode));
        Tile(Status("Library", $"{st.O("library").L("movies")} movies  ·  {st.O("library").L("shows")} shows"));
        col.Children.Add(tiles);
        if (prov.O("debrid").B("configured") && !prov.O("debrid").B("connected")) col.Children.Add(Ui.Text("Debrid: " + prov.O("debrid").S("message"), 13, color: Ui.Accent));
        if (prov.O("metadata").B("configured") && !prov.O("metadata").B("connected")) col.Children.Add(Ui.Text("Metadata: " + prov.O("metadata").S("message"), 13, color: Ui.Accent));

        col.Children.Add(Ui.Wrap(8,
            Ui.BtnAsync("Restart service", async () => { var (ok, msg) = await HubHost.Restart(); G.Window.Toast(msg, !ok); Refresh(); }),
            Ui.BtnAsync("Stop service", async () => { if (await Ui.Confirm("Stop the DJFlix service?", "Fire TV devices will be disconnected and this app will not be able to browse or play until it is started again.", "Stop")) { var (ok, msg) = await HubHost.Stop(); G.Window.Toast(msg, !ok); Refresh(); } }),
            Ui.BtnAsync("Scan library now", async () => { await G.Hub.Post("/admin/library/scan"); G.Window.Toast("Library scan started"); }),
            Ui.BtnAsync("Change port…", ChangePort), Ui.Btn("Run setup again", () => G.Window.ShowOverlay(new SetupWizard(() => G.Window.CloseOverlay())))).M(0, 4, 0, 0));

        // ---- pairing ------------------------------------------------------------------------
        col.Children.Add(Ui.H2("Connect a Fire TV"));
        var pair = await G.Hub.Get("/admin/pairing");
        var code = pair.S("code");
        var left = Ui.Col(8,
            Ui.Text("1.  Open DJFlix on the Fire TV. It should find this PC by itself; if not, type the address below.", color: Ui.Dim),
            Ui.Text($"       Address:  {conn.S("lanIp") ?? "—"}        Port:  {conn.L("port")}", 16, true),
            Ui.Text("2.  Type this pairing code on the TV (valid for ten minutes, works once):", color: Ui.Dim),
            code != null ? new TextBlock { Text = code[..3] + " " + code[3..], FontSize = 40, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Ui.Fg, CharacterSpacing = 120 } : Ui.Text("No active code.", 15, color: Ui.Faint),
            Ui.BtnAsync(code == null ? "Generate pairing code" : "New code", async () => { await G.Hub.Post("/admin/pairing/code"); Refresh(); }, code == null),
            Ui.Text("Tip: reserve this PC's IP address in your router's DHCP settings so the address never changes.", 12, color: Ui.Faint));
        left.MaxWidth = 620;
        FrameworkElement qr = conn.S("qrPayload") is { } payload ? Ui.Col(6, await Qr.Make(payload), Ui.Text("Connection details as a QR code", 11, color: Ui.Faint).With(t => t.HorizontalAlignment = HorizontalAlignment.Center)) : new Grid();
        col.Children.Add(Ui.Card(Ui.Row(40, left, qr)));

        foreach (var p in pair.A("pending"))
        {
            var pid = p.S("id");
            col.Children.Add(Ui.Card(Ui.Row(12, Ui.Text($"\"{p.S("deviceName")}\" at {p.S("ip")} is asking to connect.", 15, true).With(t => t.VerticalAlignment = VerticalAlignment.Center),
                Ui.BtnAsync("Approve", async () => { await G.Hub.Post($"/admin/pairing/{pid}/approve"); G.Window.Toast("Device approved"); Refresh(); }, true),
                Ui.BtnAsync("Deny", async () => { await G.Hub.Post($"/admin/pairing/{pid}/deny"); Refresh(); })), 12).With(b => b.BorderBrush = Ui.Accent).With(b => b.BorderThickness = new Thickness(1)));
        }

        // ---- devices ------------------------------------------------------------------------
        col.Children.Add(Ui.H2("Paired devices"));
        var devices = (await G.Hub.Get("/admin/devices")).List().Where(d => !d.B("isRevoked")).ToList();
        if (devices.Count == 0) col.Children.Add(Ui.Text("No devices are paired yet.", color: Ui.Faint));
        var online = clients.A("connected").Select(c => c.S("id")).ToHashSet();
        foreach (var d in devices)
        {
            var id = d.S("id");
            col.Children.Add(Ui.Card(Ui.Row(14,
                new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = online.Contains(id) ? Ui.Hex("#2ECC71") : Ui.Faint, VerticalAlignment = VerticalAlignment.Center },
                Ui.Col(2, Ui.Text(d.S("name"), 15, true), Ui.Text($"{d.S("kind")}  ·  {d.S("lastIp") ?? "never connected"}  ·  last seen {DateTimeOffset.FromUnixTimeMilliseconds(d.L("lastSeen")).LocalDateTime:g}", 12, color: Ui.Faint)).With(c => c.MinWidth = 420),
                Ui.BtnAsync("Rename", async () => { var n = await Ui.Prompt("Rename device", "Name", d.S("name") ?? ""); if (!string.IsNullOrWhiteSpace(n)) { await G.Hub.Patch($"/admin/devices/{id}", new { name = n }); Refresh(); } }),
                Ui.BtnAsync("Unpair", async () => { if (await Ui.Confirm("Unpair this device?", $"\"{d.S("name")}\" will stop working immediately and must be paired again to reconnect.", "Unpair")) { await G.Hub.Delete($"/admin/devices/{id}"); Refresh(); } })), 12));
        }
        var sessions = clients.A("activeSessions");
        if (sessions.Count > 0)
        {
            col.Children.Add(Ui.H2("Now playing"));
            foreach (var s in sessions) col.Children.Add(Ui.Text($"{s.S("device")}  ·  {s.S("profile")}  ·  {s.S("title")}  ·  {Ui.Time(s.L("positionMs"))}  ({s.S("state")})", 13, color: Ui.Dim));
        }

        // ---- firewall & logs ------------------------------------------------------------------
        col.Children.Add(Ui.H2("Windows Firewall"));
        col.Children.Add(Ui.Text("The installer adds a rule that lets devices on your home network (private profile, local subnet only) reach DJFlix. If the TV cannot connect, re-apply it.", 13, color: Ui.Dim));
        col.Children.Add(Ui.BtnAsync("Re-apply firewall rule", async () => { try { await G.Hub.Post("/admin/firewall/apply"); G.Window.Toast("Firewall rule applied"); } catch (HubException e) { var setup = await G.Hub.Get("/admin/setup"); await Ui.Info("Administrator rights needed", e.Message + "\n\nCommand:\n" + setup.O("firewall").S("command")); } }));

        col.Children.Add(Ui.H2("Activity log"));
        var logs = (await G.Hub.Get("/admin/logs?limit=80")).List();
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, Height = 260, TextWrapping = TextWrapping.NoWrap, Text = string.Join("\n", logs.Select(l => $"{DateTimeOffset.FromUnixTimeMilliseconds(l.L("at")).LocalDateTime:HH:mm:ss}  {l.S("level"),-5}  {l.S("message")}")) };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        col.Children.Add(box);
        col.Children.Add(Ui.Text($"Version {svc.S("version")}  ·  Data folder: {svc.S("dataDir")}", 12, color: Ui.Faint));
        return col;
    }

    async Task ChangePort()
    {
        var s = await Ui.Prompt("Change port", "Port number (1024–65535). Paired TVs must be given the new port.", G.Hub.Port.ToString());
        if (s == null) return;
        var r = await G.Hub.Put("/admin/settings", new JsonObject { ["server.port"] = s.Trim() });
        if (r.B("restartRequired")) { await G.Hub.Post("/admin/restart"); await Task.Delay(2500); for (int i = 0; i < 40; i++) { G.Hub.Configure(); if (await G.Hub.Healthy()) break; await Task.Delay(500); } G.Window.Toast("Port changed. Re-apply the firewall rule for the new port."); }
        Refresh();
    }
}

// ============================= first-run wizard ===============================================
public sealed class SetupWizard : UserControl
{
    readonly Action _done; int _step;
    public SetupWizard(Action done) { _done = done; _ = Render(); }

    async Task Render()
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40) };
        body.Children.Add(new TextBlock { Text = "DJFLIX", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.Black, Foreground = Ui.Accent });
        body.Children.Add(Ui.Text($"Setup  ·  step {_step + 1} of 5", 12, color: Ui.Faint));
        void Nav(string next, Func<Task>? onNext = null, bool back = true) => body.Children.Add(Ui.Row(10, back && _step > 0 ? Ui.Btn("Back", () => { _step--; _ = Render(); }) : null,
            Ui.BtnAsync(next, async () => { if (onNext != null) await onNext(); _step++; if (_step > 4) { await G.Hub.Post("/admin/setup/complete"); _done(); } else await Render(); }, true)).M(0, 14, 0, 0));
        try
        {
            var setup = await G.Hub.Get("/admin/setup"); var conn = setup.O("connection");
            switch (_step)
            {
                case 0:
                    body.Children.Add(Ui.H1("Welcome to DJFlix"));
                    body.Children.Add(Ui.Text("This PC is your media hub. It keeps your library, profiles and watch progress, and serves them to this app and to DJFlix on your Fire TV.", 15, color: Ui.Dim));
                    body.Children.Add(Ui.Text("The service is configured and running. Network adapters found on this PC:", 14));
                    foreach (var i in conn.A("interfaces")) body.Children.Add(Ui.Text($"•  {i.S("name")}  —  {i.S("address")}" + (i.B("hasGateway") ? $"   (gateway {i.S("gateway")})" : "   (no gateway)"), 13, color: Ui.Dim));
                    if (conn.S("lanIp") == null) body.Children.Add(Ui.Text("No home-network address was found. Connect this PC to your network before pairing a TV.", color: Ui.Accent));
                    Nav("Continue");
                    break;
                case 1:
                    body.Children.Add(Ui.H1("Address and port"));
                    body.Children.Add(Ui.Text("Your Fire TV connects to this address:", 15, color: Ui.Dim));
                    body.Children.Add(new TextBlock { Text = conn.S("address") ?? "No network address", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Ui.Fg, IsTextSelectionEnabled = true });
                    var port = new NumberBox { Header = "Listening port", Value = conn.L("port"), Minimum = 1024, Maximum = 65535, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
                    body.Children.Add(port);
                    body.Children.Add(Ui.Text("Leave the port as it is unless another program already uses it. Reserve this PC's IP address in your router's DHCP settings so the address does not change.", 13, color: Ui.Faint));
                    Nav("Continue", async () =>
                    {
                        if (double.IsNaN(port.Value) || (int)port.Value == conn.L("port")) return;
                        var r = await G.Hub.Put("/admin/settings", new JsonObject { ["server.port"] = ((int)port.Value).ToString() });
                        if (r.B("restartRequired")) { await G.Hub.Post("/admin/restart"); await Task.Delay(2500); for (int i = 0; i < 40; i++) { G.Hub.Configure(); if (await G.Hub.Healthy()) break; await Task.Delay(500); } }
                    });
                    break;
                case 2:
                    body.Children.Add(Ui.H1("Windows Firewall"));
                    body.Children.Add(Ui.Text("DJFlix only accepts connections from your local network, and every device must be paired. Windows Firewall needs a rule so the TV can reach this PC.", 15, color: Ui.Dim));
                    var result = Ui.Text(HubHost.ServiceInstalled ? "The installer already added the rule. You can re-apply it here." : "Apply the rule now. If Windows refuses, run the command below in an administrator terminal.", 13);
                    body.Children.Add(result);
                    body.Children.Add(Ui.BtnAsync("Apply firewall rule", async () => { try { await G.Hub.Post("/admin/firewall/apply"); result.Text = "✓ Firewall rule applied."; } catch (HubException e) { result.Text = e.Message; } }));
                    body.Children.Add(new TextBox { Text = setup.O("firewall").S("command") ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12 });
                    Nav("Continue");
                    break;
                case 3:
                    body.Children.Add(Ui.H1("Check the service"));
                    var health = await G.Hub.Get("/health");
                    body.Children.Add(Ui.Text($"✓  API reachable on this PC (version {health.S("version")})", 15));
                    body.Children.Add(Ui.Text(conn.S("lanIp") != null ? $"✓  Listening for your network at {conn.S("address")}" : "✕  No home-network address", 15));
                    body.Children.Add(Ui.Text("✓  Pairing is required for every device", 15));
                    body.Children.Add(Ui.Text((health.O("services").B("metadata") ? "✓" : "•") + "  Metadata: " + (health.O("services").B("metadata") ? "connected" : "add a TMDB key in Settings → Providers to get posters and descriptions"), 15));
                    body.Children.Add(Ui.Text((health.O("services").B("debrid") ? "✓" : "•") + "  Debrid: " + (health.O("services").B("debrid") ? "connected" : "optional; connect an account in Settings → Providers"), 15));
                    Nav("Continue");
                    break;
                default:
                    body.Children.Add(Ui.H1("Connect your Fire TV"));
                    var code = (await G.Hub.Post("/admin/pairing/code")).S("code") ?? "";
                    var info = Ui.Col(8,
                        Ui.Text("1.  Install and open DJFlix on the Fire TV.", 15), Ui.Text("2.  It looks for this PC automatically. If it is not found, enter:", 15),
                        Ui.Text($"       Address  {conn.S("lanIp") ?? "—"}      Port  {conn.L("port")}", 18, true),
                        Ui.Text("3.  When asked, type this pairing code:", 15),
                        new TextBlock { Text = code.Length == 6 ? code[..3] + " " + code[3..] : code, FontSize = 44, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Ui.Fg, CharacterSpacing = 120 },
                        Ui.Text("You can do this later from Server → Connect a Fire TV.", 13, color: Ui.Faint));
                    body.Children.Add(Ui.Row(40, info, conn.S("qrPayload") is { } payload ? await Qr.Make(payload, 170) : null));
                    Nav("Finish");
                    break;
            }
        }
        catch (Exception e) { body.Children.Add(Ui.Text(e.Message, color: Ui.Accent)); body.Children.Add(Ui.Btn("Try again", () => _ = Render(), true)); }
        Content = new Grid { Background = Ui.Bg, Children = { new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } };
    }
}
