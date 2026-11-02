// See https://aka.ms/new-console-template for more information
using Avalonia;
using System.Text.Json;
using TwinDesk.Core;

namespace TwinDesk.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--check-installation"])
        {
            var engine = EngineDiscovery.Find();
            Console.WriteLine(JsonSerializer.Serialize(new { platform = PairingCode.CurrentPlatform, enginePath = engine, supportedEngineVersion = EngineDiscovery.SupportedVersion }));
            return engine is not null ? 0 : 2;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
