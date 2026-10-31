using Avalonia;
using Velopack;

namespace Rewire;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: the Windows installer calls the app with setup arguments and expects it to exit.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // The IDE previewer calls this too, so it must stay public and side-effect free.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
