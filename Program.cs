using Avalonia;

namespace XMLTester;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Console mode for SSH/headless use: the Avalonia GUI needs an X11 display and fails
        // outright without one (confirmed live on Ubuntu 18.04 over plain SSH, 2026-10-14).
        if (args.Contains("--console"))
        {
            ConsoleApp.Run();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
