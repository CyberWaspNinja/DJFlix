using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.Storage.Pickers;

namespace DJFlix.Desktop;

public sealed class SettingsPage : PageBase
{
    static readonly (string, string)[] Langs = [("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("it", "Italian"), ("pt", "Portuguese"), ("ar", "Arabic"), ("hi", "Hindi"), ("ml", "Malayalam")];
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static void Pref(object patch) => _ = SavePref(patch);
    static async Task SavePref(object patch) { try { await G.SavePrefs(patch); } catch (Exception e) { G.Window.Toast(e.Message, true); } }
    static ToggleSwitch Toggle(string header, bool on, Action<bool> change) { var t = new ToggleSwitch { Header = header, IsOn = on }; t.Toggled += (_, _) => change(t.IsOn); return t; }

    protected override async Task<UIElement> BuildAsync()
    {
        var col = new StackPanel { Margin = new Thickness(40, 10, 40, 60), Spacing = 8, MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        col.Children.Add(Ui.H1("Settings"));

        // ---- profile --------------------------------------------------------------------------
        col.Children.Add(Ui.H2("Profile"));
        col.Children.Add(Ui.Row(10, Ui.Text($"Signed in as {G.Profile.S("name")}", 15).With(t => t.VerticalAlignment = VerticalAlignment.Center), Ui.Btn("Switch or manage profiles", () => G.Window.ShowProfiles())));
        col.Children.Add(Ui.Text("Name, avatar, colour, PIN, language, maturity limit and favourite genres are edited under Manage profiles.", 12, color: Ui.Faint));

        // ---- playback -------------------------------------------------------------------------
        col.Children.Add(Ui.H2("Playback"));
        var speeds = new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 3, 4 }.Select(s => (s.ToString(System.Globalization.CultureInfo.InvariantCulture), s + "x"));
        col.Children.Add(Ui.Row(12,
            Ui.Combo(speeds, G.PrefNum("playbackSpeed", 1).ToString(System.Globalization.CultureInfo.InvariantCulture), v => Pref(new { playbackSpeed = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) }), "Default speed"),
            Ui.Combo([("5", "5 seconds"), ("10", "10 seconds"), ("15", "15 seconds"), ("30", "30 seconds"), ("60", "60 seconds")], ((int)G.PrefNum("seekSeconds", 10)).ToString(), v => Pref(new { seekSeconds = int.Parse(v) }), "Skip interval"),
            Ui.Combo([("resume", "Resume where I stopped"), ("restart", "Start from the beginning")], G.PrefStr("resumeBehaviour", "resume"), v => Pref(new { resumeBehaviour = v }), "When a title has progress", 240)));
        col.Children.Add(Ui.Row(24,
            Toggle("Autoplay", G.PrefBool("autoplay", true), v => Pref(new { autoplay = v })),
            Toggle("Autoplay next episode", G.PrefBool("autoplayNext", true), v => Pref(new { autoplayNext = v })),
            Toggle("Keep finished titles in Continue Watching", G.PrefBool("keepCompletedInContinue", false), v => Pref(new { keepCompletedInContinue = v }))));
        col.Children.Add(Ui.Row(12,
            Ui.Combo(new[] { ("off", "Off") }.Concat(Langs), G.PrefStr("subtitleLanguage", "off"), v => Pref(new { subtitleLanguage = v }), "Subtitles"),
            Ui.Combo(Langs, G.PrefStr("audioLanguage", "en"), v => Pref(new { audioLanguage = v }), "Audio language"),
            Ui.Combo([("small", "Small"), ("medium", "Medium"), ("large", "Large")], G.PrefStr("subtitleSize", "medium"), v => Pref(new { subtitleSize = v }), "Subtitle size"),
            Ui.Combo([("white", "White"), ("yellow", "Yellow")], G.PrefStr("subtitleColor", "white"), v => Pref(new { subtitleColor = v }), "Subtitle colour"),
            Toggle("Subtitle background", G.PrefBool("subtitleBackground", false), v => Pref(new { subtitleBackground = v }))));

        // ---- library --------------------------------------------------------------------------
        col.Children.Add(Ui.H2("Library"));
        col.Children.Add(Ui.Text("Add folders that contain your own movies and TV episodes. DJFlix reads names such as \"Movie (2010).mkv\" and \"Show S01E02.mkv\". MP4, MKV, AVI, MOV, WebM and M4V are supported; a few audio formats inside MKV/AVI may not play on a Fire TV.", 13, color: Ui.Dim));
        foreach (var l in (await G.Hub.Get("/admin/libraries")).List())
        {
            var lid = l.L("id");
            col.Children.Add(Ui.Card(Ui.Row(12, Ui.Col(2, Ui.Text(l.S("path"), 14, true), Ui.Text($"{l.L("files")} files" + (l.L("missing") > 0 ? $"  ·  {l.L("missing")} missing" : "") + (l.B("available") ? "" : "  ·  folder unavailable") + (l.L("lastScan") > 0 ? $"  ·  scanned {DateTimeOffset.FromUnixTimeMilliseconds(l.L("lastScan")).LocalDateTime:g}" : ""), 12, color: l.B("available") ? Ui.Faint : Ui.Accent)).With(c => c.MinWidth = 520),
                Ui.BtnAsync("Remove", async () => { if (await Ui.Confirm("Remove this folder?", "Its titles leave the library. No files are deleted.", "Remove")) { await G.Hub.Delete($"/admin/libraries/{lid}"); Refresh(); } })), 12));
        }
        var path = new TextBox { PlaceholderText = @"D:\Media\Movies", Width = 420 };
        col.Children.Add(Ui.Row(8, path,
            Ui.BtnAsync("Browse…", async () =>
            {
                var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, G.Window.Hwnd);
                if (await picker.PickSingleFolderAsync() is { } f) path.Text = f.Path;
            }),
            Ui.BtnAsync("Add folder", async () => { await G.Hub.Post("/admin/libraries", new { path = path.Text.Trim() }); G.Window.Toast("Folder added. Scanning…"); Refresh(); }, true),
            Ui.BtnAsync("Scan now", async () => { await G.Hub.Post("/admin/library/scan"); G.Window.Toast("Scan started"); }),
            Ui.Btn("Refresh", Refresh)));
        if (HubHost.ServiceInstalled) col.Children.Add(Ui.Text("The service runs under the Windows system account. Mapped drive letters (for example Z:) are not visible to it; use a local path or a \\\\server\\share path the PC can read.", 12, color: Ui.Faint));
        var scan = await G.Hub.Get("/admin/library/status");
        col.Children.Add(Ui.Text(scan.B("running") ? $"Scanning ({scan.S("phase")})… {scan.L("filesSeen")} files seen, {scan.L("added")} added." : scan.L("finishedAt") > 0 ? $"Last scan: {scan.L("filesSeen")} local files, {scan.L("added")} added, {scan.L("missing")} missing, {scan.L("matched")} newly matched." + (scan.S("providerWarning") is { } w ? "  Note: " + w : "") : "No scan has run yet.", 13, color: Ui.Dim));

        var unmatched = (await G.Hub.Get("/admin/library/unmatched")).List();
        if (unmatched.Count > 0) col.Children.Add(Ui.CardRow($"Unmatched titles ({unmatched.Count}): open one and choose \"Fix match\"", unmatched, Refresh).With(r => r.Margin = new Thickness(-40, 0, -40, 0)));
        var dups = (await G.Hub.Get("/admin/library/duplicates")).List();
        if (dups.Count > 0)
        {
            col.Children.Add(Ui.Text($"Duplicate files ({dups.Count} titles)", 15, true).M(0, 8, 0, 0));
            foreach (var d in dups.Take(20)) col.Children.Add(Ui.Text($"{d.S("title")}:  " + string.Join("   |   ", d.A("files").Select(f => f.S("path"))), 12, color: Ui.Faint));
        }
        var missing = (await G.Hub.Get("/admin/library/missing")).List();
        if (missing.Count > 0)
        {
            col.Children.Add(Ui.Row(10, Ui.Text($"Missing or unavailable files: {missing.Count}", 15, true).With(t => t.VerticalAlignment = VerticalAlignment.Center), Ui.BtnAsync("Remove them from the library", async () => { await G.Hub.Delete("/admin/library/missing"); Refresh(); })).M(0, 8, 0, 0));
            foreach (var m in missing.Take(12)) col.Children.Add(Ui.Text($"{m.S("title")}:  {m.S("path") ?? m.S("fileName")}", 12, color: Ui.Faint));
        }

        // ---- providers ------------------------------------------------------------------------
        col.Children.Add(Ui.H2("Providers"));
        var prov = await G.Hub.Get("/admin/providers");
        var deb = prov.O("debrid"); var meta = prov.O("metadata");
        var token = new PasswordBox { Header = "Real-Debrid API token (from real-debrid.com/apitoken)", Width = 420, PlaceholderText = deb.B("configured") ? "Saved. Type a new token to replace it." : "" };
        col.Children.Add(Ui.Card(Ui.Col(8,
            Ui.Row(10, Ui.Text("Debrid", 16, true), Ui.Combo(prov.O("available").A("debrid").Select(p => (p.S("id")!, p.S("name")!)), deb.S("id") ?? "realdebrid", v => _ = SaveDebrid(new { provider = v }), width: 200)),
            Ui.Text(deb.B("connected") ? $"✓ Connected as {deb.O("account").S("username")}  ·  {deb.O("account").S("plan")}  ·  expires {deb.O("account").S("expiration")?[..10]}" : deb.S("message"), 13, color: deb.B("connected") ? Ui.Hex("#2ECC71") : deb.B("configured") ? Ui.Accent : Ui.Dim),
            Ui.Row(8, token,
                Ui.BtnAsync("Save and test", () => SaveDebrid(new { token = token.Password }), true).With(b => b.VerticalAlignment = VerticalAlignment.Bottom),
                deb.B("configured") ? Ui.BtnAsync("Test connection", () => SaveDebrid(new { })).With(b => b.VerticalAlignment = VerticalAlignment.Bottom) : null,
                deb.B("configured") ? Ui.BtnAsync("Disconnect", async () => { if (await Ui.Confirm("Disconnect the debrid account?", "The saved token is erased and its titles become unavailable until you reconnect.", "Disconnect")) { await G.Hub.Delete("/admin/providers/debrid"); Refresh(); } }).With(b => b.VerticalAlignment = VerticalAlignment.Bottom) : null),
            Toggle("Relay debrid streams through this PC (the TV then only ever talks to the PC; uses more of the PC's bandwidth)", prov.B("proxyStreams"), v => _ = SaveDebrid(new { proxyStreams = v })),
            Ui.Text("The token is encrypted on this PC and is never sent to the Fire TV. DJFlix lists and plays files that are already in your own debrid cloud.", 12, color: Ui.Faint))));

        var key = new PasswordBox { Header = "TMDB API key or read-access token (themoviedb.org → Settings → API)", Width = 420, PlaceholderText = meta.B("configured") ? "Saved. Type a new key to replace it." : "" };
        var settings = await G.Hub.Get("/admin/settings");
        col.Children.Add(Ui.Card(Ui.Col(8,
            Ui.Text("Metadata", 16, true),
            Ui.Text(meta.B("connected") ? "✓ " + meta.S("message") : meta.S("message"), 13, color: meta.B("connected") ? Ui.Hex("#2ECC71") : meta.B("configured") ? Ui.Accent : Ui.Dim),
            Ui.Row(8, key, Ui.BtnAsync("Save and test", async () => { var r = await G.Hub.Put("/admin/providers/metadata", new { apiKey = key.Password }); G.Window.Toast(r.S("message") ?? "", !r.B("connected")); if (r.B("connected")) await G.Hub.Post("/admin/library/scan"); Refresh(); }, true).With(b => b.VerticalAlignment = VerticalAlignment.Bottom),
                meta.B("configured") ? Ui.BtnAsync("Remove key", async () => { await G.Hub.Put("/admin/providers/metadata", new { apiKey = "" }); Refresh(); }).With(b => b.VerticalAlignment = VerticalAlignment.Bottom) : null),
            Ui.Row(12,
                Ui.Combo([("en-US", "English (US)"), ("en-GB", "English (UK)"), ("es-ES", "Spanish"), ("fr-FR", "French"), ("de-DE", "German"), ("it-IT", "Italian"), ("pt-BR", "Portuguese"), ("ar-SA", "Arabic"), ("hi-IN", "Hindi")], settings.S("tmdb.language") ?? "en-US", v => _ = SaveSetting("tmdb.language", v), "Metadata language", 200),
                Ui.Combo([("1", "1 day"), ("7", "7 days"), ("30", "30 days"), ("90", "90 days")], settings.S("cache.metadataDays") ?? "7", v => _ = SaveSetting("cache.metadataDays", v), "Keep metadata for", 160),
                Ui.Combo([("15", "15 minutes"), ("30", "30 minutes"), ("60", "1 hour"), ("360", "6 hours")], settings.S("scan.intervalMinutes") ?? "30", v => _ = SaveSetting("scan.intervalMinutes", v), "Rescan library every", 170)),
            Ui.Text(prov.S("imdbNote"), 12, color: Ui.Faint))));

        // ---- application ----------------------------------------------------------------------
        col.Children.Add(Ui.H2("Application"));
        bool startup = false; try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); startup = k?.GetValue("DJFlix") != null; } catch { }
        col.Children.Add(Ui.Row(24,
            Ui.Combo([("dark", "Dark (cinematic)"), ("light", "Light")], Local.Get("theme", "dark"), v => { Local.Set("theme", v); G.Window.Toast("The theme changes the next time DJFlix starts."); }, "Theme"),
            Toggle("Open DJFlix when I sign in to Windows", startup, on =>
            {
                try { using var k = Registry.CurrentUser.CreateSubKey(RunKey); if (on) k.SetValue("DJFlix", $"\"{Environment.ProcessPath}\""); else k.DeleteValue("DJFlix", false); }
                catch (Exception e) { G.Window.Toast(e.Message, true); }
            })));
        col.Children.Add(Ui.Text(HubHost.ServiceInstalled ? "The DJFlix service itself starts with Windows and keeps serving your Fire TV when this window is closed." : "Portable mode: the hub stops when this window closes. Install DJFlix with the setup program to run it as a Windows service.", 12, color: Ui.Faint));

        var storage = await G.Hub.Get("/admin/storage");
        col.Children.Add(Ui.Text($"Storage:  database {storage.D("databaseBytes") / 1048576.0:0.#} MB  ·  artwork cache {storage.D("imageCacheBytes") / 1048576.0:0.#} MB ({storage.L("imageCacheFiles")} images)  ·  metadata cache {storage.D("metadataCacheBytes") / 1048576.0:0.#} MB  ·  {storage.S("dataDir")}", 13, color: Ui.Dim).M(0, 8, 0, 0));
        col.Children.Add(Ui.Row(8,
            Ui.BtnAsync("Clear cache", async () => { await G.Hub.Post("/admin/cache/clear", new { images = true, metadata = true }); G.Window.Toast("Cache cleared"); Refresh(); }),
            Ui.BtnAsync("Back up…", Backup), Ui.BtnAsync("Restore…", Restore),
            Ui.BtnAsync("Reset DJFlix…", async () =>
            {
                if (await Ui.Prompt("Reset DJFlix", "This erases every profile, the library, watch history, paired devices and provider settings. Your media files are not touched. Type RESET to confirm.", ok: "Erase everything") != "RESET") return;
                await G.Hub.Post("/admin/reset", new { confirm = "RESET" }); await WaitForHub(); G.Profile = null; G.Window.ShowOverlay(new SetupWizard(() => { G.Window.CloseOverlay(); G.Window.ShowProfiles(); }));
            })));
        col.Children.Add(Ui.Text("DJFlix 1.0.0  ·  This product uses the TMDB API but is not endorsed or certified by TMDB. Playback by LibVLC.", 12, color: Ui.Faint).M(0, 14, 0, 0));
        return col;
    }

    async Task SaveDebrid(object body)
    {
        try { var r = await G.Hub.Put("/admin/providers/debrid", body); G.Window.Toast(r.S("message") ?? "Saved", r.B("configured") && !r.B("connected")); if (r.B("connected")) await G.Hub.Post("/admin/library/scan"); Refresh(); }
        catch (Exception e) { G.Window.Toast(e.Message, true); }
    }
    static async Task SaveSetting(string key, string value) { try { await G.Hub.Put("/admin/settings", new JsonObject { [key] = value }); } catch (Exception e) { G.Window.Toast(e.Message, true); } }

    static async Task WaitForHub() { await Task.Delay(2500); for (int i = 0; i < 60; i++) { G.Hub.Configure(); if (await G.Hub.Healthy()) return; await Task.Delay(500); } }

    async Task Backup()
    {
        var picker = new FileSavePicker { SuggestedFileName = $"djflix-backup-{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("DJFlix backup", [".djbak"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, G.Window.Hwnd);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        await File.WriteAllBytesAsync(file.Path, await G.Hub.Download("/admin/backup"));
        G.Window.Toast("Backup saved. It contains your encrypted provider settings; they only work on this PC.");
    }

    async Task Restore()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".djbak");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, G.Window.Hwnd);
        if (await picker.PickSingleFileAsync() is not { } file) return;
        if (!await Ui.Confirm("Restore this backup?", "Everything currently in DJFlix is replaced by the backup. The service restarts.", "Restore")) return;
        await G.Hub.Send(HttpMethod.Post, "/admin/restore", raw: new ByteArrayContent(await File.ReadAllBytesAsync(file.Path)));
        await WaitForHub(); G.Profile = null; G.Window.ShowProfiles();
        G.Window.Toast("Backup restored");
    }
}
