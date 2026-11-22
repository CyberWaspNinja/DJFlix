using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace DJFlix.Desktop;

/// <summary>Design system: colours, type and the small builders every page uses.</summary>
public static class Ui
{
    public static readonly Color AccentColor = Color.FromArgb(255, 229, 9, 20);
    public static SolidColorBrush Argb(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
    public static SolidColorBrush Hex(string hex)
    {
        try { hex = hex.TrimStart('#'); return Argb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16)); }
        catch { return new SolidColorBrush(AccentColor); }
    }
    public static readonly SolidColorBrush Accent = new(AccentColor);
    public static bool Dark => Application.Current.RequestedTheme == ApplicationTheme.Dark;
    public static SolidColorBrush Bg => Dark ? Argb(255, 11, 11, 13) : Argb(255, 245, 245, 247);
    public static SolidColorBrush Panel => Dark ? Argb(255, 24, 24, 28) : Argb(255, 255, 255, 255);
    public static SolidColorBrush Fg => Dark ? Argb(255, 255, 255, 255) : Argb(255, 20, 20, 22);
    public static SolidColorBrush Dim => Dark ? Argb(255, 179, 179, 184) : Argb(255, 80, 80, 88);
    public static SolidColorBrush Faint => Dark ? Argb(255, 120, 120, 128) : Argb(255, 130, 130, 138);

    public static TextBlock Text(string? s, double size = 14, bool bold = false, Brush? color = null, int maxLines = 0, bool wrap = true)
    {
        var t = new TextBlock { Text = s ?? "", FontSize = size, Foreground = color ?? Fg, TextWrapping = wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        if (maxLines > 0) t.MaxLines = maxLines;
        return t;
    }
    public static TextBlock H1(string s) => new() { Text = s, FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Fg, TextWrapping = TextWrapping.WrapWholeWords };
    public static TextBlock H2(string s) => new() { Text = s, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Fg, Margin = new Thickness(0, 18, 0, 8) };

    public static Button Btn(string label, Action onClick, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = label, CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 8, 16, 8) };
        if (primary)
        {
            b.Background = Accent; b.Foreground = new SolidColorBrush(Colors.White);
            b.Resources["ButtonBackgroundPointerOver"] = Argb(255, 245, 40, 50); b.Resources["ButtonBackgroundPressed"] = Argb(255, 190, 8, 16);
            b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Colors.White); b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Colors.White);
        }
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }
    /// <summary>Button whose handler is async; errors surface as a toast instead of crashing.</summary>
    public static Button BtnAsync(string label, Func<Task> onClick, bool primary = false, string? tip = null)
    {
        Button? b = null;
        b = Btn(label, async () => { b!.IsEnabled = false; try { await onClick(); } catch (Exception e) { G.Window.Toast(e.Message, true); } finally { b.IsEnabled = true; } }, primary, tip);
        return b;
    }

    public static StackPanel Row(double spacing, params UIElement?[] kids) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing }; foreach (var k in kids) if (k != null) p.Children.Add(k); return p; }
    public static WrapPanel Wrap(double gap, params UIElement?[] kids) { var p = new WrapPanel { Gap = gap }; foreach (var k in kids) if (k != null) p.Children.Add(k); return p; }
    public static StackPanel Col(double spacing, params UIElement?[] kids) { var p = new StackPanel { Spacing = spacing }; foreach (var k in kids) if (k != null) p.Children.Add(k); return p; }
    public static T With<T>(this T e, Action<T> f) where T : class { f(e); return e; }
    public static T M<T>(this T e, double l, double t, double r, double b) where T : FrameworkElement { e.Margin = new Thickness(l, t, r, b); return e; }
    public static T InGrid<T>(this T e, int row, int col, int rowSpan = 1, int colSpan = 1) where T : FrameworkElement { Grid.SetRow(e, row); Grid.SetColumn(e, col); Grid.SetRowSpan(e, rowSpan); Grid.SetColumnSpan(e, colSpan); return e; }

    public static Border Card(UIElement child, double pad = 16) => new() { Child = child, Background = Panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(pad) };

    public static FrameworkElement Loading() => new ProgressRing { IsActive = true, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40) };

    /// <summary>Shimmer-free placeholder blocks shown while a row of posters loads.</summary>
    public static FrameworkElement Skeleton(int count = 8)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(40, 20, 0, 0) };
        for (int i = 0; i < count; i++) p.Children.Add(new Border { Width = 150, Height = 225, CornerRadius = new CornerRadius(8), Background = Panel, Opacity = 0.7 });
        return p;
    }

    public static FrameworkElement Empty(string title, string detail, params UIElement[] actions)
    {
        var c = Col(8, Text(title, 20, true).With(t => t.HorizontalAlignment = HorizontalAlignment.Center), Text(detail, 14, color: Dim).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; t.MaxWidth = 520; }));
        if (actions.Length > 0) c.Children.Add(Row(8, actions).With(r => { r.HorizontalAlignment = HorizontalAlignment.Center; r.Margin = new Thickness(0, 10, 0, 0); }));
        c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Center; c.Margin = new Thickness(40, 80, 40, 80);
        return c;
    }

    public static FrameworkElement Error(Exception e, Action retry) => Empty(
        e is HubException { Unreachable: true } ? "DJFlix service is not running" : "Couldn't load this",
        e.Message, Btn("Try again", retry, true), Btn("Server status", () => G.Window.Go(new ServerPage())));

    public static Image Img(string? url, Stretch stretch = Stretch.UniformToFill, int decodeWidth = 0)
    {
        var img = new Image { Stretch = stretch };
        if (G.Hub.Url(url) is { } u) { var bmp = new BitmapImage(new Uri(u)); if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth; img.Source = bmp; }
        return img;
    }

    public static string Meta(JsonNode? c)
    {
        var parts = new List<string>();
        if (c.L("year") > 0) parts.Add(c.L("year").ToString());
        if (c.S("certification") is { } cert) parts.Add(cert);
        if (c.S("type") == "show") { if (c.L("seasonsCount") > 0) parts.Add(c.L("seasonsCount") == 1 ? "1 Season" : $"{c.L("seasonsCount")} Seasons"); }
        else if (c.L("runtime") > 0) parts.Add($"{c.L("runtime") / 60}h {c.L("runtime") % 60}m");
        if (c.D("rating") > 0) parts.Add($"★ {c.D("rating"):0.0}");
        return string.Join("  ·  ", parts);
    }
    public static string Time(long ms) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}"; }

    // ---- dialogs ---------------------------------------------------------------------------
    static ContentDialog Dialog(string title, object content, string primary, string? close = "Cancel") =>
        new() { Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = close ?? "", DefaultButton = ContentDialogButton.Primary, XamlRoot = G.Window.Content.XamlRoot, RequestedTheme = Dark ? ElementTheme.Dark : ElementTheme.Light };

    public static async Task<bool> Confirm(string title, string message, string yes = "OK") =>
        await Dialog(title, Text(message, color: Dim), yes).ShowAsync() == ContentDialogResult.Primary;
    public static async Task Info(string title, string message) => await Dialog(title, Text(message, color: Dim), "OK", null).ShowAsync();
    public static async Task<bool> Show(string title, UIElement content, string primary = "Save") => await Dialog(title, content, primary).ShowAsync() == ContentDialogResult.Primary;

    public static async Task<string?> Prompt(string title, string label, string initial = "", bool secret = false, string ok = "OK")
    {
        Control box = secret ? new PasswordBox { Password = initial, Width = 340 } : new TextBox { Text = initial, Width = 340 };
        var ok2 = await Dialog(title, Col(8, Text(label, color: Dim), box), ok).ShowAsync() == ContentDialogResult.Primary;
        return ok2 ? (box is PasswordBox p ? p.Password : ((TextBox)box).Text) : null;
    }

    // ---- poster cards ----------------------------------------------------------------------
    public static async Task ToggleList(JsonNode c, Action? changed = null)
    {
        var id = c.L("id");
        if (c.B("inWatchlist")) await G.Hub.Delete($"/profiles/{G.Pid}/watchlist/{id}"); else await G.Hub.Put($"/profiles/{G.Pid}/watchlist/{id}");
        G.Window.Toast(c.B("inWatchlist") ? $"Removed \"{c.S("title")}\" from My List" : $"Added \"{c.S("title")}\" to My List");
        changed?.Invoke();
    }
    public static void OpenTrailer(JsonNode? trailer) { if (trailer.S("url") is { } url) G.Window.Go(new TrailerPage(url)); }

    /// <summary>Poster with hover lift, progress bar, watched tick and a right-click menu.</summary>
    public static FrameworkElement Poster(JsonNode c, Action? changed = null, double width = 150)
    {
        var id = c.L("id");
        var grid = new Grid { Width = width, Height = width * 1.5, CornerRadius = new CornerRadius(8), Background = Panel };
        if (c.S("poster") != null) grid.Children.Add(Img(c.S("poster"), decodeWidth: 342));
        else grid.Children.Add(Text(c.S("title"), 14, true).With(t => { t.Margin = new Thickness(10); t.VerticalAlignment = VerticalAlignment.Center; t.TextAlignment = TextAlignment.Center; }));
        if (c.B("watched")) grid.Children.Add(new Border { Child = Text("✓", 12, true, new SolidColorBrush(Colors.White)), Background = Argb(200, 0, 0, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 1, 6, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) });
        var pct = c.O("progress").D("percent");
        if (pct > 0 && !c.B("watched")) grid.Children.Add(new ProgressBar { Value = pct, Maximum = 100, VerticalAlignment = VerticalAlignment.Bottom, Foreground = Accent, Height = 4 });
        if (!c.B("available")) grid.Opacity = 0.45;

        var title = Text(c.S("title"), 13, color: Dim, maxLines: 1, wrap: false).With(t => { t.Width = width; t.Margin = new Thickness(0, 6, 0, 0); });
        var btn = new Button { Content = Col(0, grid, title), Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), CornerRadius = new CornerRadius(8) };
        btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent); btn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
        btn.Click += (_, _) => G.Window.Go(new DetailsPage(id));
        ToolTipService.SetToolTip(btn, $"{c.S("title")}\n{Meta(c)}\n{string.Join(", ", c.Strings("genres").Take(3))}");
        // Lift on hover / keyboard focus.
        btn.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) };
        void Lift(bool up) { btn.CenterPoint = new Vector3((float)width / 2, (float)(width * 0.75), 0); btn.Scale = up ? new Vector3(1.06f) : Vector3.One; Canvas.SetZIndex(btn, up ? 10 : 0); title.Foreground = up ? Fg : Dim; }
        btn.PointerEntered += (_, _) => Lift(true); btn.PointerExited += (_, _) => Lift(false);
        btn.GotFocus += (_, _) => Lift(true); btn.LostFocus += (_, _) => Lift(false);

        var menu = new MenuFlyout();
        void Add(string label, Func<Task> act) { var mi = new MenuFlyoutItem { Text = label }; mi.Click += async (_, _) => { try { await act(); } catch (Exception e) { G.Window.Toast(e.Message, true); } }; menu.Items.Add(mi); }
        if (c.B("available")) Add(c.O("progress").L("positionMs") > 0 && !c.B("watched") ? "Resume" : "Play", () => { G.Window.Play(id); return Task.CompletedTask; });
        if (c.B("hasTrailer")) Add("Watch trailer", async () => OpenTrailer((await G.Hub.Get($"/media/{id}")).O("trailer")));
        Add(c.B("inWatchlist") ? "Remove from My List" : "Add to My List", () => ToggleList(c, changed));
        Add(c.B("watched") ? "Mark as unwatched" : "Mark as watched", async () => { await G.Hub.Post($"/profiles/{G.Pid}/watched", new { mediaId = id, watched = !c.B("watched") }); changed?.Invoke(); });
        Add(c.B("isFavourite") ? "Remove from Favourites" : "Add to Favourites", async () => { if (c.B("isFavourite")) await G.Hub.Delete($"/profiles/{G.Pid}/favourites/{id}"); else await G.Hub.Put($"/profiles/{G.Pid}/favourites/{id}"); changed?.Invoke(); });
        Add("More information", () => { G.Window.Go(new DetailsPage(id)); return Task.CompletedTask; });
        btn.ContextFlyout = menu;
        return btn;
    }

    /// <summary>Horizontally scrolling row of posters with a heading.</summary>
    public static FrameworkElement CardRow(string title, IEnumerable<JsonNode> cards, Action? changed = null)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(40, 8, 40, 14) };
        foreach (var c in cards) strip.Children.Add(Poster(c, changed));
        var sv = new ScrollViewer { Content = strip, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // Let the wheel scroll the page vertically unless Shift is held, as people expect on a desktop.
        return Col(0, Text(title, 18, true).M(40, 14, 0, 0), sv);
    }

    /// <summary>Wrapping, lazily laid-out grid of posters.</summary>
    public static ItemsRepeater PosterGrid(IEnumerable<JsonNode> cards, Action? changed = null) => new()
    {
        Layout = new UniformGridLayout { MinItemWidth = 150, MinItemHeight = 255, MinColumnSpacing = 14, MinRowSpacing = 16, ItemsJustification = UniformGridLayoutItemsJustification.Start },
        ItemsSource = cards.Select(c => (UIElement)Poster(c, changed)).ToList(),
        Margin = new Thickness(40, 8, 40, 30),
    };

    public static ComboBox Combo(IEnumerable<(string id, string label)> options, string selected, Action<string> onChange, string? header = null, double width = 170)
    {
        var cb = new ComboBox { Width = width, Header = header };
        foreach (var (id, label) in options) cb.Items.Add(new ComboBoxItem { Content = label, Tag = id, IsSelected = id == selected });
        if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem i) onChange((string)i.Tag); };
        return cb;
    }
}

/// <summary>Lays children out left to right and wraps to a new line, each child at its own size.</summary>
public sealed class WrapPanel : Panel
{
    public double Gap { get; set; } = 8;
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, y = 0, line = 0, width = 0;
        foreach (var c in Children)
        {
            c.Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > available.Width) { x = 0; y += line + Gap; line = 0; }
            x += d.Width + Gap; line = Math.Max(line, d.Height); width = Math.Max(width, x - Gap);
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), y + line);
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var c in Children)
        {
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > final.Width) { x = 0; y += line + Gap; line = 0; }
            c.Arrange(new Windows.Foundation.Rect(x, y, d.Width, d.Height));
            x += d.Width + Gap; line = Math.Max(line, d.Height);
        }
        return final;
    }
}

/// <summary>Pages that should reload when the user comes back to them.</summary>
public interface IRefresh { void Refresh(); }
