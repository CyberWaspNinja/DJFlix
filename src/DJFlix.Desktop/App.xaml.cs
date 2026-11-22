using Microsoft.UI.Xaml;

namespace DJFlix.Desktop;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        RequestedTheme = Local.Get("theme", "dark") == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark;
        UnhandledException += (_, e) => { e.Handled = true; Local.Log("Unhandled: " + e.Exception); G.Window?.Toast("Something went wrong: " + e.Message, true); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        G.Window = new MainWindow();
        G.Window.Activate();
    }
}
